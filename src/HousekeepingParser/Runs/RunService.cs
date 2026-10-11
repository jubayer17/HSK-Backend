using System.Globalization;
using System.Security.Cryptography;
using Dapper;
using HousekeepingParser.Accounts;
using HousekeepingParser.Audit;
using HousekeepingParser.Coloring;
using HousekeepingParser.Core;
using HousekeepingParser.Data;
using HousekeepingParser.Functions;
using HousekeepingParser.Rhythm;
using HousekeepingParser.Storage;
using Microsoft.Data.SqlClient;

namespace HousekeepingParser.Runs;

public interface IRunService
{
    /// <summary>Checks, stores and records a confirmed upload. The caller then starts the orchestration.</summary>
    Task<Guid> CreateAsync(CurrentUser user, IReadOnlyList<UploadedFile> files, IReadOnlyList<NewRequest> requests, CancellationToken ct);
    Task SetInstanceAsync(Guid runId, string instanceId, CancellationToken ct);
    Task<RunDto?> GetAsync(Guid runId, CancellationToken ct);
    Task<IReadOnlyList<RunDto>> ListAsync(int take, CancellationToken ct);
    Task<(Stream Pdf, string FileName)?> OpenMarkedPdfAsync(Guid runId, HotelId hotel, CancellationToken ct);
}

public sealed class RunService(IDbConnectionFactory db, IBlobStore blobs, IRhythmService rhythms, IAuditLog audit, IClock clock) : IRunService
{
    public const int MaxPdfBytes = 20 * 1024 * 1024;

    public async Task<Guid> CreateAsync(CurrentUser user, IReadOnlyList<UploadedFile> files, IReadOnlyList<NewRequest> requests, CancellationToken ct)
    {
        // Step 0: no run without an active rhythm for both hotels.
        var missing = (await rhythms.GetAllAsync(ct)).Where(r => r.Active is null).Select(r => Hotels.ShortName(r.HotelId)).ToList();
        if (missing.Count > 0)
            throw BusinessRuleException.Conflict($"{string.Join(" and ", missing)} has no active cleaning rhythm. The Hotel Director must set it first.");

        if (files.Count != 2) throw BusinessRuleException.BadRequest("Please upload exactly two task sheets: one for Novotel and one for ibis budget.");
        foreach (var f in files)
        {
            if (f.Bytes.Length == 0) throw BusinessRuleException.BadRequest($"\"{f.FileName}\" is empty.");
            if (f.Bytes.Length > MaxPdfBytes) throw BusinessRuleException.BadRequest($"\"{f.FileName}\" is larger than 20 MB.");
        }
        ValidateRequests(requests);

        var runId = Guid.NewGuid();
        var today = clock.Today();
        var stored = new List<(Guid FileId, string Blob, UploadedFile File, int Order)>();
        for (var i = 0; i < files.Count; i++)
        {
            var fileId = Guid.NewGuid();
            // The blob name never contains the upload name (wrapper document: data minimisation).
            stored.Add((fileId, $"{runId:N}/{fileId:N}.pdf", files[i], i + 1));
        }
        foreach (var s in stored) await blobs.UploadAsync(Containers.Incoming, s.Blob, s.File.Bytes, ct);

        await using var c = await db.OpenAsync(ct);
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
        await c.ExecuteAsync(
            "INSERT INTO dbo.phase1_run (run_id, report_date, status, created_by, session_id) VALUES (@runId, @today, 'processing', @userId, @sessionId)",
            new { runId, today, userId = user.User.UserId, sessionId = user.SessionId }, tx);

        foreach (var s in stored)
        {
            await c.ExecuteAsync(
                """
                INSERT INTO dbo.phase1_run_file (file_id, run_id, upload_order, file_label, byte_count, sha256, blob_name, status)
                VALUES (@FileId, @runId, @Order, @label, @bytes, @sha, @Blob, 'processing')
                """,
                new { s.FileId, runId, Order = (byte)s.Order, label = RunStore.Trim(Path.GetFileName(s.File.FileName), 260), bytes = s.File.Bytes.Length, sha = SHA256.HashData(s.File.Bytes), s.Blob }, tx);
            foreach (var (key, _) in StepKeys.All)
            {
                await c.ExecuteAsync(
                    "INSERT INTO dbo.phase1_run_step (file_id, step_key, status, message) VALUES (@FileId, @key, @status, @message)",
                    new { s.FileId, key, status = key == StepKeys.Rhythm ? "done" : "pending", message = key == StepKeys.Rhythm ? "Active rhythm set for both hotels" : null }, tx);
            }
        }

        foreach (var r in requests)
        {
            await c.ExecuteAsync(
                """
                INSERT INTO dbo.pre_phase_requests (run_id, hotel_id, room_no, request_type, colour, note, request_date, created_by)
                VALUES (@runId, @hotel, @RoomNo, @type, @colour, @note, @today, @userId)
                """,
                new { runId, hotel = (byte)r.HotelId, r.RoomNo, type = r.Type.ToString(), colour = r.Colour?.ToString(), note = RunStore.Trim(r.Note, 200), today, userId = user.User.UserId }, tx);
        }

        await audit.ConfirmationAsync(c, tx, user, "RUN_START", "phase1_run", runId, new
        {
            files = stored.Select(s => new { s.File.FileName, bytes = s.File.Bytes.Length }),
            requests = requests.Select(r => new { hotelId = (int)r.HotelId, r.RoomNo, type = r.Type.ToString(), colour = r.Colour?.ToString(), r.Note }),
        });
        await tx.CommitAsync(ct);
        return runId;
    }

    private static void ValidateRequests(IReadOnlyList<NewRequest> requests)
    {
        if (requests.Count > 300) throw BusinessRuleException.BadRequest("Too many guest requests.");
        var seen = new HashSet<(HotelId, string, RequestType)>();
        foreach (var r in requests)
        {
            if (!RoomInventory.Contains(r.HotelId, r.RoomNo))
                throw BusinessRuleException.BadRequest($"Room {r.RoomNo} does not exist at {Hotels.ShortName(r.HotelId)}.");
            if (r.Type == RequestType.SPECIAL && r.Colour is null)
                throw BusinessRuleException.BadRequest($"Choose a colour for the special request in room {r.RoomNo}.");
            if (r.Type != RequestType.SPECIAL && r.Colour is not null)
                throw BusinessRuleException.BadRequest("Only a special request can have its own colour.");
            if (!seen.Add((r.HotelId, r.RoomNo, r.Type)))
                throw BusinessRuleException.BadRequest($"The request for room {r.RoomNo} was entered twice.");
        }
    }

    public async Task SetInstanceAsync(Guid runId, string instanceId, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        await c.ExecuteAsync("UPDATE dbo.phase1_run SET instance_id = @instanceId WHERE run_id = @runId", new { runId, instanceId });
    }

    public async Task<RunDto?> GetAsync(Guid runId, CancellationToken ct) =>
        (await LoadAsync(new[] { runId }, includeRooms: true, ct)).SingleOrDefault();

    public async Task<IReadOnlyList<RunDto>> ListAsync(int take, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        var ids = (await c.QueryAsync<Guid>("SELECT TOP (@take) run_id FROM dbo.phase1_run ORDER BY created_at_utc DESC", new { take })).ToList();
        return await LoadAsync(ids, includeRooms: false, ct);
    }

    public async Task<(Stream Pdf, string FileName)?> OpenMarkedPdfAsync(Guid runId, HotelId hotel, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        var row = await c.QuerySingleOrDefaultAsync<(string Blob, DateTime? Date)>(
            "SELECT marked_blob_name, report_date FROM dbo.phase1_run_file WHERE run_id = @runId AND hotel_id = @hotel AND status = 'completed' AND marked_blob_name IS NOT NULL",
            new { runId, hotel = (byte)hotel });
        if (row.Blob is null) return null;
        var stream = await blobs.OpenReadAsync(Containers.Marked, row.Blob, ct);
        if (stream is null) return null;
        var date = row.Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";
        return (stream, $"housekeeping-plan-{(hotel == HotelId.Novotel ? "novotel" : "ibis-budget")}-{date}.pdf");
    }

    // ---------- reading a run for the screens ----------

    private async Task<IReadOnlyList<RunDto>> LoadAsync(IReadOnlyList<Guid> runIds, bool includeRooms, CancellationToken ct)
    {
        if (runIds.Count == 0) return [];
        await using var c = await db.OpenAsync(ct);
        var p = new { ids = runIds };

        var runs = (await c.QueryAsync<RunRow>(
            """
            SELECT r.run_id AS RunId, r.created_at_utc AS CreatedAtUtc, u.display_label AS CreatedBy, r.report_date AS ReportDate, r.status AS Status
            FROM dbo.phase1_run r JOIN dbo.app_user u ON u.user_id = r.created_by WHERE r.run_id IN @ids
            """, p)).ToList();
        var files = (await c.QueryAsync<FileRow>(
            """
            SELECT f.file_id AS FileId, f.run_id AS RunId, f.upload_order AS UploadOrder, f.file_label AS FileLabel, f.hotel_id AS HotelId,
                   f.status AS Status, f.reject_reason AS RejectReason, f.published_at_local AS PublishedAtLocal, f.parsed_rooms AS ParsedRooms,
                   f.marked_blob_name AS MarkedBlobName,
                   s.total_task_sheets AS TotalTaskSheets, s.total_rooms AS TotalRooms, s.departure_rooms AS DepartureRooms,
                   s.dirty_rooms AS DirtyRooms, s.clean_rooms AS CleanRooms, s.total_credits AS TotalCredits
            FROM dbo.phase1_run_file f LEFT JOIN dbo.report_summary s ON s.file_id = f.file_id
            WHERE f.run_id IN @ids
            """, p)).ToList();
        var steps = (await c.QueryAsync<StepRow>(
            "SELECT s.file_id AS FileId, s.step_key AS StepKey, s.status AS Status, s.message AS Message FROM dbo.phase1_run_step s JOIN dbo.phase1_run_file f ON f.file_id = s.file_id WHERE f.run_id IN @ids",
            p)).ToLookup(s => s.FileId);
        var warnings = (await c.QueryAsync<(Guid FileId, string Message)>(
            "SELECT w.file_id, w.message FROM dbo.run_warning w JOIN dbo.phase1_run_file f ON f.file_id = w.file_id WHERE f.run_id IN @ids ORDER BY w.created_at_utc",
            p)).ToLookup(w => w.FileId, w => w.Message);
        var rooms = includeRooms
            ? (await c.QueryAsync<RoomRow>(
                """
                SELECT r.file_id AS FileId, r.room_no AS RoomNo, r.sheet_no AS SheetNo, r.room_status AS RoomStatus, r.reservation_status AS ReservationStatus,
                       r.arrival_date AS Arrival, r.departure_date AS Departure, r.adults AS Adults, r.children AS Children,
                       s.nights AS Nights, s.final_colour AS Colour, s.service_code AS ServiceCode, s.reason AS Reason, s.cleaned AS Cleaned
                FROM dbo.room_rows r JOIN dbo.phase1_run_file f ON f.file_id = r.file_id
                LEFT JOIN dbo.room_cleaning_status s ON s.file_id = r.file_id AND s.sheet_no = r.sheet_no AND s.room_no = r.room_no AND s.logic_version = 1
                WHERE f.run_id IN @ids
                ORDER BY TRY_CAST(r.room_no AS INT), r.sheet_no
                """, p)).ToLookup(r => r.FileId)
            : null;

        return runIds.Select(id => runs.FirstOrDefault(r => r.RunId == id)).Where(r => r is not null).Select(run =>
        {
            var hotels = files.Where(f => f.RunId == run!.RunId).OrderBy(f => f.UploadOrder).Select(f =>
            {
                var status = f.Status switch { "rejected" => "rejected", "completed" => "completed", _ => "processing" };
                var stepList = StepKeys.All.Select(k =>
                {
                    var s = steps[f.FileId].FirstOrDefault(x => x.StepKey == k.Key);
                    return new RunStepDto(k.Key, k.Label, s?.Status ?? "pending", s?.Message);
                }).ToList();
                return new HotelRunResultDto(
                    f.HotelId,
                    f.FileLabel,
                    status,
                    f.RejectReason,
                    stepList,
                    f.PublishedAtLocal is { } pub ? Iso(Berlin.ToUtc(pub)) : null,
                    f.TotalRooms is null ? null : new ReportSummaryDto(f.TotalTaskSheets!.Value, f.TotalRooms.Value, f.DepartureRooms!.Value, f.DirtyRooms!.Value, f.CleanRooms!.Value, f.TotalCredits!.Value),
                    f.ParsedRooms,
                    warnings[f.FileId].ToList(),
                    status == "completed" && rooms is not null
                        ? rooms[f.FileId].Select(r => new RoomResultDto(r.RoomNo, r.SheetNo, r.RoomStatus, r.ReservationStatus, DateText(r.Arrival), DateText(r.Departure),
                            r.Adults, r.Children, r.Nights, r.Colour, r.ServiceCode ?? "", r.Reason ?? "", r.Cleaned ?? false)).ToList()
                        : [],
                    status == "completed" && f.MarkedBlobName is not null);
            }).ToList();

            return new RunDto(run!.RunId.ToString(), Iso(run.CreatedAtUtc), run.CreatedBy,
                DateText(run.ReportDate) ?? DateText(DateOnly.FromDateTime(Berlin.ToLocal(run.CreatedAtUtc)))!, run.Status, hotels);
        }).ToList();
    }

    private static string Iso(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    private static string? DateText(DateOnly? d) => d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private sealed class RunRow
    {
        public Guid RunId { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public string CreatedBy { get; set; } = "";
        public DateOnly? ReportDate { get; set; }
        public string Status { get; set; } = "";
    }

    private sealed class FileRow
    {
        public Guid FileId { get; set; }
        public Guid RunId { get; set; }
        public byte UploadOrder { get; set; }
        public string FileLabel { get; set; } = "";
        public byte? HotelId { get; set; }
        public string Status { get; set; } = "";
        public string? RejectReason { get; set; }
        public DateTime? PublishedAtLocal { get; set; }
        public int? ParsedRooms { get; set; }
        public string? MarkedBlobName { get; set; }
        public int? TotalTaskSheets { get; set; }
        public int? TotalRooms { get; set; }
        public int? DepartureRooms { get; set; }
        public int? DirtyRooms { get; set; }
        public int? CleanRooms { get; set; }
        public decimal? TotalCredits { get; set; }
    }

    private sealed class StepRow
    {
        public Guid FileId { get; set; }
        public string StepKey { get; set; } = "";
        public string Status { get; set; } = "";
        public string? Message { get; set; }
    }

    private sealed class RoomRow
    {
        public Guid FileId { get; set; }
        public string RoomNo { get; set; } = "";
        public string SheetNo { get; set; } = "";
        public string RoomStatus { get; set; } = "";
        public string ReservationStatus { get; set; } = "";
        public DateOnly? Arrival { get; set; }
        public DateOnly? Departure { get; set; }
        public byte? Adults { get; set; }
        public byte? Children { get; set; }
        public short? Nights { get; set; }
        public string? Colour { get; set; }
        public string? ServiceCode { get; set; }
        public string? Reason { get; set; }
        public bool? Cleaned { get; set; }
    }
}
