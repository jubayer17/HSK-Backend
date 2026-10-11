using System.Globalization;
using Dapper;
using HousekeepingParser.Coloring;
using HousekeepingParser.Core;
using HousekeepingParser.Data;
using HousekeepingParser.Parsing;
using HousekeepingParser.PdfMarking;
using HousekeepingParser.Rhythm;
using HousekeepingParser.Storage;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HousekeepingParser.Runs;

/// <summary>
/// The steps of one morning run (plan 9.1). Each method is one Durable activity and is safe to run
/// twice. Logs carry ids, counts and outcome codes only — never PDF text (plan 5.3).
/// </summary>
public sealed class MorningRunPipeline(
    IDbConnectionFactory db,
    RunStore store,
    IBlobStore blobs,
    ICompliantPdfExtractor extractor,
    IColorRuleService colours,
    IRhythmService rhythms,
    IPdfMarker marker,
    IOptions<ValidationOptions> validation,
    ILogger<MorningRunPipeline> log)
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    public async Task<IReadOnlyList<Guid>> ListFilesAsync(Guid runId, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        return (await c.QueryAsync<Guid>("SELECT file_id FROM dbo.phase1_run_file WHERE run_id = @runId ORDER BY upload_order", new { runId })).ToList();
    }

    // ---------- read + single-file checks (checks 1, 2, 3, 5) ----------

    public async Task<ReadOutcome> ReadFileAsync(Guid fileId, CancellationToken ct)
    {
        var file = await store.GetFileAsync(fileId, ct);
        if (file.Status == "rejected") return new ReadOutcome(fileId, file.UploadOrder, false, null, null, null);
        await store.StepAsync(fileId, StepKeys.Read, "running");

        var bytes = await blobs.DownloadAsync(Containers.Incoming, file.BlobName, ct);
        if (!PdfBytes.LooksLikePdf(bytes))
            return await RejectAsync(file, StepKeys.Read, "NOT_PDF", "This file is not a hotel task sheet: it is not a PDF.", ct);

        var (pdf, repaired) = PdfBytes.RepairLineEndings(bytes);
        PdfExtractionResult extraction;
        try
        {
            extraction = await ExtractWithOneRetryAsync(pdf, file.BlobName, ct);
        }
        catch (OpenDataLoaderTimeoutException)
        {
            return await RejectAsync(file, StepKeys.Read, "READ_TIMEOUT",
                "Reading this file took too long and was stopped. Please export the task sheet from Opera again.", ct);
        }
        catch (OpenDataLoaderException)
        {
            return await RejectAsync(file, StepKeys.Read, "READ_FAILED",
                "This file could not be read as a PDF. Please export the task sheet from Opera again.", ct);
        }

        var report = TaskSheetParser.Parse(extraction.Pages);
        await using (var c = await db.OpenAsync(ct))
        {
            await c.ExecuteAsync(
                "UPDATE dbo.phase1_run_file SET correlation_id = @cid, line_endings_repaired = @repaired WHERE file_id = @fileId",
                new { cid = extraction.CorrelationId, repaired, fileId });
        }
        await store.StepAsync(fileId, StepKeys.Read, "done",
            $"{report.Rooms.Count} rooms on {extraction.Pages.Count} pages" + (repaired ? " (damaged line endings repaired)" : ""));
        log.LogInformation("Read file {FileId}: {Rooms} rooms, {Pages} pages, repaired={Repaired}", fileId, report.Rooms.Count, extraction.Pages.Count, repaired);

        await store.StepAsync(fileId, StepKeys.Check, "running");
        if (!report.IsTaskSheetReport)
            return await RejectAsync(file, StepKeys.Check, "NOT_TASK_SHEET",
                "This file is not an Opera task sheet: the title \"Task Sheet Report\" is missing.", ct);
        if (report.Header is not { } header)
            return await RejectAsync(file, StepKeys.Check, "NO_HEADER",
                "The hotel name and print date at the top of the sheet could not be read.", ct);
        if (header.HotelId is not { } hotel)
            return await RejectAsync(file, StepKeys.Check, "UNKNOWN_HOTEL",
                $"The hotel printed on this sheet (\"{RunStore.Trim(header.PrintedHotelName, 60)}\") is not Novotel or ibis budget Münster City.", ct);

        if (validation.Value.EnforceSheetAge)
        {
            var publishedUtc = Berlin.ToUtc(header.PublishedAtLocal);
            var age = file.RunCreatedAtUtc - publishedUtc;
            if (age > TimeSpan.FromHours(validation.Value.MaxSheetAgeHours) || age < TimeSpan.FromHours(-1))
                return await RejectAsync(file, StepKeys.Check, "TOO_OLD",
                    $"This sheet was printed on {header.PublishedAtLocal.ToString("dd.MM.yyyy HH:mm", German)}, more than " +
                    $"{validation.Value.MaxSheetAgeHours} hours before the upload. Please print today's task sheet.", ct);
        }

        if (report.Summary is null)
            return await RejectAsync(file, StepKeys.Check, "NO_SUMMARY",
                "The totals page at the end (\"Report Summary\") could not be read. Please check that the whole task sheet was exported.", ct);

        await using (var c = await db.OpenAsync(ct))
        {
            await c.ExecuteAsync(
                "UPDATE dbo.phase1_run_file SET hotel_id = @hotel, report_date = @date, published_at_local = @published WHERE file_id = @fileId",
                new { hotel = (byte)hotel, date = header.ReportDate, published = header.PublishedAtLocal, fileId });
        }
        return new ReadOutcome(fileId, file.UploadOrder, true, hotel, header.PublishedAtLocal, report);
    }

    private async Task<PdfExtractionResult> ExtractWithOneRetryAsync(byte[] pdf, string blobName, CancellationToken ct)
    {
        try
        {
            return await extractor.ExtractAsync(pdf, blobName, ct);
        }
        catch (OpenDataLoaderTimeoutException)
        {
            log.LogWarning("Reader timed out, retrying once");
            return await extractor.ExtractAsync(pdf, blobName, ct); // runbook: retry once, then refuse
        }
    }

    // ---------- cross-file checks (check 4 and "one sheet per hotel") ----------

    public async Task<IReadOnlyList<Guid>> ResolveAsync(Guid runId, IReadOnlyList<ReadOutcome> outcomes, CancellationToken ct)
    {
        var accepted = new List<Guid>();
        var seen = new HashSet<HotelId>();
        foreach (var o in outcomes.Where(o => o.Ok).OrderBy(o => o.UploadOrder))
        {
            var file = await store.GetFileAsync(o.FileId, ct);
            if (file.Status is "checked" or "completed")
            {
                accepted.Add(o.FileId); // activity retried after it already passed
                seen.Add(o.HotelId!.Value);
                continue;
            }
            var hotel = o.HotelId!.Value;
            if (!seen.Add(hotel))
            {
                await RejectAsync(file, StepKeys.Check, "SAME_HOTEL",
                    $"Both files are {Hotels.ShortName(hotel)} task sheets. Please upload one sheet for Novotel and one for ibis budget.", ct);
                continue;
            }

            var printed = o.PublishedAtLocal!.Value.ToString("dd.MM.yyyy HH:mm", German);
            var duplicateMessage = $"This {Hotels.ShortName(hotel)} task sheet (printed {printed}) has already been processed. The same sheet is never used twice.";
            await using var c = await db.OpenAsync(ct);
            var duplicate = await c.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM dbo.phase1_run_file WHERE hotel_id = @hotel AND published_at_local = @published AND status IN ('checked', 'completed') AND file_id <> @fileId",
                new { hotel = (byte)hotel, published = o.PublishedAtLocal, fileId = o.FileId });
            if (duplicate > 0)
            {
                await RejectAsync(file, StepKeys.Check, "DUPLICATE", duplicateMessage, ct);
                continue;
            }
            try
            {
                await c.ExecuteAsync("UPDATE dbo.phase1_run_file SET status = 'checked' WHERE file_id = @fileId", new { fileId = o.FileId });
            }
            catch (SqlException ex) when (ex.Number is 2601 or 2627)
            {
                await RejectAsync(file, StepKeys.Check, "DUPLICATE", duplicateMessage, ct); // a parallel run took it first
                continue;
            }
            await store.StepAsync(o.FileId, StepKeys.Check, "done", $"{Hotels.PrintedNames[hotel]}, printed {printed}");
            accepted.Add(o.FileId);
        }
        return accepted;
    }

    // ---------- save ----------

    public async Task SaveAsync(ReadOutcome outcome, CancellationToken ct)
    {
        var report = outcome.Report!;
        var hotel = outcome.HotelId!.Value;
        var header = report.Header!;
        var summary = report.Summary!;
        var fileId = outcome.FileId;
        await store.StepAsync(fileId, StepKeys.Save, "running");

        await using var c = await db.OpenAsync(ct);
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
        await c.ExecuteAsync(
            """
            DELETE FROM dbo.run_warning WHERE file_id = @fileId;
            DELETE FROM dbo.room_cleaning_status WHERE file_id = @fileId;
            DELETE FROM dbo.room_rows WHERE file_id = @fileId;
            DELETE FROM dbo.task_sheets WHERE file_id = @fileId;
            DELETE FROM dbo.report_summary WHERE file_id = @fileId;
            """, new { fileId }, tx);

        await c.ExecuteAsync(
            """
            INSERT INTO dbo.task_sheets (file_id, hotel_id, sheet_no, sheet_date, task_codes, auto_generated, instructions,
                total_rooms, departure_rooms, total_adults, total_children, total_credits, clean_rooms, dirty_rooms, first_page, last_page)
            VALUES (@FileId, @HotelId, @SheetNo, @SheetDate, @TaskCodes, @AutoGenerated, @Instructions,
                @Rooms, @Departures, @Adults, @Children, @Credits, @Clean, @Dirty, @FirstPage, @LastPage)
            """,
            report.Sheets.Select(s => new
            {
                FileId = fileId, HotelId = (byte)hotel, s.SheetNo, s.SheetDate, s.TaskCodes, s.AutoGenerated,
                Instructions = RunStore.Trim(s.Instructions, 1000),
                s.Totals?.Rooms, Departures = s.Totals?.DepartureRooms, s.Totals?.Adults, s.Totals?.Children, s.Totals?.Credits,
                s.Totals?.Clean, s.Totals?.Dirty, FirstPage = (short)s.FirstPage, LastPage = (short)s.LastPage,
            }), tx);

        await c.ExecuteAsync(
            """
            INSERT INTO dbo.room_rows (file_id, hotel_id, report_date, sheet_no, room_no, room_status, credits, reservation_status,
                arrival_date, departure_date, adults, children, tasks, row_marker, page_no, x_left, y_top, y_bottom)
            VALUES (@FileId, @HotelId, @ReportDate, @SheetNo, @RoomNo, @RoomStatus, @Credits, @ReservationStatus,
                @Arrival, @Departure, @Adults, @Children, @Tasks, @Marker, @Page, @Left, @Top, @Bottom)
            """,
            report.Rooms.Select(r => new
            {
                FileId = fileId, HotelId = (byte)hotel, header.ReportDate, r.SheetNo, r.RoomNo, RoomStatus = RunStore.Trim(r.RoomStatus, 4) ?? "",
                r.Credits, ReservationStatus = RunStore.Trim(r.ReservationStatus, 30) ?? "", r.Arrival, r.Departure,
                Adults = (byte?)r.Adults, Children = (byte?)r.Children, Tasks = RunStore.Trim(r.Tasks, 200) ?? "", r.Marker,
                Page = (short)r.Position.Page, Left = Math.Round((decimal)r.Position.Left, 2), Top = Math.Round((decimal)r.Position.Top, 2),
                Bottom = Math.Round((decimal)r.Position.Bottom, 2),
            }), tx);

        await c.ExecuteAsync(
            """
            INSERT INTO dbo.report_summary (file_id, total_task_sheets, total_rooms, departure_rooms, dirty_rooms, clean_rooms, total_credits)
            VALUES (@fileId, @TotalTaskSheets, @TotalRooms, @DepartureRooms, @DirtyRooms, @CleanRooms, @TotalCredits)
            """,
            new { fileId, summary.TotalTaskSheets, summary.TotalRooms, summary.DepartureRooms, summary.DirtyRooms, summary.CleanRooms, summary.TotalCredits }, tx);

        await c.ExecuteAsync("UPDATE dbo.phase1_run_file SET parsed_rooms = @n WHERE file_id = @fileId", new { n = report.Rooms.Count, fileId }, tx);

        // The guest wishes of this run now belong to the sheet's report date.
        var run = await c.QuerySingleAsync<Guid>("SELECT run_id FROM dbo.phase1_run_file WHERE file_id = @fileId", new { fileId }, tx);
        await c.ExecuteAsync("UPDATE dbo.pre_phase_requests SET request_date = @date WHERE run_id = @run AND hotel_id = @hotel",
            new { date = header.ReportDate, run, hotel = (byte)hotel }, tx);

        var warnings = TotalsWarnings(report).Concat(report.Warnings.Select(w => (w.Code, w.Message))).ToList();
        foreach (var (code, message) in warnings) await store.WarnAsync(fileId, code, message, c, tx);

        var countMatches = report.Rooms.Count == summary.TotalRooms;
        await store.StepAsync(fileId, StepKeys.Save, countMatches ? "done" : "warning",
            countMatches
                ? $"{report.Rooms.Count} of {summary.TotalRooms} rooms, totals match"
                : $"{report.Rooms.Count} rooms read, the totals page says {summary.TotalRooms}", c, tx);
        await tx.CommitAsync(ct);
    }

    /// <summary>Check 6 and the per-sheet cross-check. A mismatch is a warning; the run continues (core workflow).</summary>
    internal static IEnumerable<(string Code, string Message)> TotalsWarnings(TaskSheetReport report)
    {
        var s = report.Summary!;
        if (report.Rooms.Count != s.TotalRooms)
            yield return ("ROOM_COUNT", $"{report.Rooms.Count} rooms were read, but the totals page says {s.TotalRooms}. Please check the marked PDF.");
        if (report.Sheets.Count != s.TotalTaskSheets)
            yield return ("SHEET_COUNT", $"{report.Sheets.Count} task sheets were found, but the totals page says {s.TotalTaskSheets}.");

        var dirty = report.Rooms.Count(r => r.RoomStatus == "DI");
        var clean = report.Rooms.Count(r => r.RoomStatus == "CL");
        if (dirty != s.DirtyRooms || clean != s.CleanRooms)
            yield return ("DIRTY_CLEAN", $"Dirty / clean rooms read: {dirty} / {clean}; the totals page says {s.DirtyRooms} / {s.CleanRooms}.");

        foreach (var sheet in report.Sheets.Where(x => x.Totals?.Rooms is not null))
        {
            var n = report.Rooms.Count(r => r.SheetNo == sheet.SheetNo);
            if (n != sheet.Totals!.Rooms)
                yield return ("SHEET_ROOMS", $"Task sheet {sheet.SheetNo}: {n} rooms read, the sheet's own total says {sheet.Totals.Rooms}.");
        }
    }

    // ---------- colour ----------

    public async Task ColourAsync(Guid fileId, CancellationToken ct)
    {
        await store.StepAsync(fileId, StepKeys.Colour, "running");
        var file = await store.GetFileAsync(fileId, ct);
        var hotel = (HotelId)file.HotelId!.Value;
        var reportDate = file.ReportDate!.Value;

        await using var c = await db.OpenAsync(ct);
        var rooms = (await c.QueryAsync<RoomRow>(
            """
            SELECT sheet_no AS SheetNo, room_no AS RoomNo, room_status AS RoomStatus, reservation_status AS ReservationStatus,
                   arrival_date AS Arrival, departure_date AS Departure
            FROM dbo.room_rows WHERE file_id = @fileId
            """, new { fileId })).ToList();

        // This run's wishes, plus "daily cleaning for the whole stay" entered on an earlier day of the same stay.
        var requests = (await c.QueryAsync<RequestRow>(
            """
            SELECT q.room_no AS RoomNo, q.request_type AS RequestType, q.colour AS Colour, q.request_date AS RequestDate, q.run_id AS RunId
            FROM dbo.pre_phase_requests q JOIN dbo.phase1_run r ON r.run_id = q.run_id
            WHERE q.hotel_id = @hotel AND r.status <> 'failed'
              AND (q.run_id = @runId OR (q.request_type = 'DAILY_FULL' AND q.request_date < @reportDate))
            """, new { hotel = (byte)hotel, runId = file.RunId, reportDate })).ToList();

        await c.ExecuteAsync("DELETE FROM dbo.room_cleaning_status WHERE file_id = @fileId", new { fileId });
        await c.ExecuteAsync("DELETE FROM dbo.run_warning WHERE file_id = @fileId AND code LIKE 'COLOUR[_]%'", new { fileId });

        var onSheet = rooms.Select(r => r.RoomNo).ToHashSet();
        foreach (var missing in requests.Where(q => q.RunId == file.RunId && !onSheet.Contains(q.RoomNo)).Select(q => q.RoomNo).Distinct())
            await store.WarnAsync(fileId, "COLOUR_REQUEST_ROOM", $"A guest request was entered for room {missing}, but the room is not on this task sheet.", c);

        var rows = new List<object>();
        var coloured = 0;
        foreach (var room in rooms)
        {
            var mine = requests
                .Where(q => q.RoomNo == room.RoomNo)
                .Where(q => q.RunId == file.RunId || (room.Arrival is { } a && q.RequestDate >= a)) // carried-over wish only within the same stay
                .Select(q => new GuestRequest(Enum.Parse<RequestType>(q.RequestType), q.Colour is null ? null : Enum.Parse<ColourName>(q.Colour)))
                .ToList();

            RhythmType? guestRhythm = null;
            if (string.Equals(room.ReservationStatus.Trim(), "Stayover", StringComparison.OrdinalIgnoreCase)
                && room.Arrival is { } arrival && arrival < reportDate)
            {
                guestRhythm = await rhythms.GuestRhythmAsync(c, hotel, room.RoomNo, arrival, room.Departure);
            }

            var decision = colours.Decide(new ColourInput(room.RoomNo, room.RoomStatus, room.ReservationStatus, room.Arrival, reportDate, guestRhythm, mine));
            if (decision.Warning is not null) await store.WarnAsync(fileId, "COLOUR_STATUS", decision.Warning, c);
            if (decision.Colour is not null) coloured++;

            rows.Add(new
            {
                fileId, hotel = (byte)hotel, reportDate, room.SheetNo, room.RoomNo,
                baseColour = decision.BaseColour?.ToString(), finalColour = decision.Colour?.ToString(), decision.ServiceCode,
                reason = RunStore.Trim(decision.Reason, 200), decision.OverrideReason, rhythm = decision.Rhythm?.ToString(),
                cycle = (byte?)decision.CyclePosition, nights = (short?)decision.Nights, decision.Cleaned,
            });
        }

        await c.ExecuteAsync(
            """
            INSERT INTO dbo.room_cleaning_status (file_id, hotel_id, report_date, sheet_no, room_no, base_colour, final_colour, service_code,
                reason, override_reason, assigned_rhythm, cycle_position, nights, cleaned, logic_version)
            VALUES (@fileId, @hotel, @reportDate, @SheetNo, @RoomNo, @baseColour, @finalColour, @ServiceCode,
                @reason, @OverrideReason, @rhythm, @cycle, @nights, @Cleaned, 1)
            """, rows);

        await store.StepAsync(fileId, StepKeys.Colour, "done", $"{coloured} rooms get a marker, {rooms.Count - coloured} without");
    }

    // ---------- mark ----------

    public async Task MarkAsync(Guid fileId, CancellationToken ct)
    {
        await store.StepAsync(fileId, StepKeys.Mark, "running");
        var file = await store.GetFileAsync(fileId, ct);

        List<DotRow> dots;
        await using (var c = await db.OpenAsync(ct))
        {
            dots = (await c.QueryAsync<DotRow>(
                """
                SELECT r.page_no AS Page, r.x_left AS XLeft, r.y_top AS YTop, r.y_bottom AS YBottom, s.final_colour AS Colour
                FROM dbo.room_rows r
                JOIN dbo.room_cleaning_status s ON s.file_id = r.file_id AND s.sheet_no = r.sheet_no AND s.room_no = r.room_no AND s.logic_version = 1
                WHERE r.file_id = @fileId AND s.final_colour IS NOT NULL
                """, new { fileId })).ToList();
        }

        var source = await blobs.DownloadAsync(Containers.Incoming, file.BlobName, ct)
            ?? throw new InvalidOperationException("Uploaded PDF is missing.");
        var (pdf, _) = PdfBytes.RepairLineEndings(source);

        byte[] marked;
        try
        {
            marked = marker.AddDots(pdf, dots.Select(d => new DotPlacement(d.Page, (double)d.XLeft, (double)d.YTop, (double)d.YBottom, Enum.Parse<ColourName>(d.Colour))).ToList());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError("Marking failed for file {FileId}: {Error}", fileId, ex.GetType().Name);
            await store.StepAsync(fileId, StepKeys.Mark, "failed", "The coloured markers could not be added to this PDF.");
            await store.WarnAsync(fileId, "MARK_FAILED", "The coloured markers could not be added to the PDF. The room colours above are still correct.");
            return;
        }

        var markedName = $"{file.RunId:N}/{fileId:N}.pdf";
        await blobs.UploadAsync(Containers.Marked, markedName, marked, ct);
        await blobs.MoveAsync(Containers.Incoming, Containers.Archive, file.BlobName, ct);
        await using (var c = await db.OpenAsync(ct))
        {
            await c.ExecuteAsync("UPDATE dbo.phase1_run_file SET marked_blob_name = @markedName WHERE file_id = @fileId", new { markedName, fileId });
        }
        await store.StepAsync(fileId, StepKeys.Mark, "done", $"{dots.Count} markers added");
    }

    // ---------- finish ----------

    public async Task FinishAsync(Guid runId, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        await c.ExecuteAsync(
            """
            UPDATE dbo.phase1_run_file SET status = 'completed' WHERE run_id = @runId AND status = 'checked';
            UPDATE dbo.phase1_run SET status = 'completed', completed_at_utc = SYSUTCDATETIME(),
                report_date = COALESCE((SELECT MAX(report_date) FROM dbo.phase1_run_file WHERE run_id = @runId AND status = 'completed'), report_date)
            WHERE run_id = @runId;
            """, new { runId });
        log.LogInformation("Run {RunId} completed", runId);
    }

    public async Task FailAsync(Guid runId, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        await c.ExecuteAsync(
            """
            UPDATE dbo.phase1_run_step SET status = 'failed', message = N'Stopped by a technical error', updated_at_utc = SYSUTCDATETIME()
            WHERE status = 'running' AND file_id IN (SELECT file_id FROM dbo.phase1_run_file WHERE run_id = @runId);
            UPDATE dbo.phase1_run_file SET status = 'rejected', reject_code = 'TECHNICAL',
                reject_reason = N'Processing stopped because of a technical error. Please start a new run; if it happens again, contact support.'
            WHERE run_id = @runId AND status IN ('processing', 'checked');
            UPDATE dbo.phase1_run SET status = 'failed', completed_at_utc = SYSUTCDATETIME() WHERE run_id = @runId;
            """, new { runId });
        log.LogError("Run {RunId} failed", runId);
    }

    private async Task<ReadOutcome> RejectAsync(RunStore.FileRow file, string step, string code, string reason, CancellationToken ct)
    {
        await using (var c = await db.OpenAsync(ct))
        {
            await c.ExecuteAsync(
                """
                UPDATE dbo.phase1_run_file SET status = 'rejected', reject_code = @code, reject_reason = @reason WHERE file_id = @fileId;
                DELETE FROM dbo.room_cleaning_status WHERE file_id = @fileId;
                DELETE FROM dbo.room_rows WHERE file_id = @fileId;
                DELETE FROM dbo.task_sheets WHERE file_id = @fileId;
                DELETE FROM dbo.report_summary WHERE file_id = @fileId;
                """, new { code, reason, fileId = file.FileId });
        }
        await store.StepAsync(file.FileId, step, "failed", reason);
        await blobs.MoveAsync(Containers.Incoming, Containers.Failed, file.BlobName, ct);
        log.LogWarning("File {FileId} rejected: {Code}", file.FileId, code);
        return new ReadOutcome(file.FileId, file.UploadOrder, false, null, null, null);
    }

    private sealed class RoomRow
    {
        public string SheetNo { get; set; } = "";
        public string RoomNo { get; set; } = "";
        public string RoomStatus { get; set; } = "";
        public string ReservationStatus { get; set; } = "";
        public DateOnly? Arrival { get; set; }
        public DateOnly? Departure { get; set; }
    }

    private sealed class RequestRow
    {
        public string RoomNo { get; set; } = "";
        public string RequestType { get; set; } = "";
        public string? Colour { get; set; }
        public DateOnly RequestDate { get; set; }
        public Guid RunId { get; set; }
    }

    private sealed class DotRow
    {
        public short Page { get; set; }
        public decimal XLeft { get; set; }
        public decimal YTop { get; set; }
        public decimal YBottom { get; set; }
        public string Colour { get; set; } = "";
    }
}
