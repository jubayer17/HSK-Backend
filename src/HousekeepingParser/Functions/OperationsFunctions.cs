using System.Globalization;
using HousekeepingParser.Core;
using HousekeepingParser.Operations;
using HousekeepingParser.Reports;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace HousekeepingParser.Functions;

/// <summary>Staff, room move/check, daily close, report and settings endpoints.</summary>
public sealed class OperationsFunctions(
    Api api,
    IStaffService staff,
    IRoomMoveService moves,
    IRoomCheckService checks,
    IDailyCloseService closes,
    IReportService reports,
    ISettingsService settings,
    IEmailSender email,
    IClock clock,
    ILogger<OperationsFunctions> log)
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ---------- staff ----------

    [Function("StaffList")]
    public Task<IActionResult> StaffList([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "staff")] HttpRequest req) =>
        api.RunAsync(req, async _ => Api.Ok((await staff.ListAsync(req.HttpContext.RequestAborted)).Select(ToDto)));

    [Function("StaffAdd")]
    public Task<IActionResult> StaffAdd([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "staff")] HttpRequest req) =>
        api.RunAsync(req, async user => Api.Ok(ToDto(await staff.SaveAsync(user, null, await ReadStaffAsync(req), req.HttpContext.RequestAborted)), 201));

    [Function("StaffUpdate")]
    public Task<IActionResult> StaffUpdate([HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "staff/{id}")] HttpRequest req, string id) =>
        api.RunAsync(req, async user =>
        {
            if (!Guid.TryParse(id, out var staffId)) return Api.Error(404, "Staff member not found.");
            return Api.Ok(ToDto(await staff.SaveAsync(user, staffId, await ReadStaffAsync(req), req.HttpContext.RequestAborted)));
        });

    private static async Task<StaffMember> ReadStaffAsync(HttpRequest req)
    {
        var b = await Api.ReadJsonAsync<SaveStaffRequest>(req);
        if (!b.Confirmed) throw BusinessRuleException.BadRequest("The entry must be confirmed.");
        HotelId? hotel = b.HotelId is { } h ? RhythmFunctions.ParseHotel(h) : null;
        return new StaffMember(Guid.Empty, hotel, b.Name ?? "", b.Company ?? "", b.Role ?? "", b.MonthlyCheckLimit, b.Active);
    }

    private static StaffMemberDto ToDto(StaffMember s) =>
        new(s.Id.ToString(), (int?)s.HotelId, s.Name, s.Company, s.Role, s.MonthlyCheckLimit, s.Active);

    // ---------- room moves ----------

    [Function("RoomMoveList")]
    public Task<IActionResult> RoomMoveList([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "room-moves")] HttpRequest req) =>
        api.RunAsync(req, async _ =>
        {
            var date = OptionalDate(req.Query["date"]);
            var today = clock.Today();
            return Api.Ok((await moves.ListAsync(date, req.HttpContext.RequestAborted)).Select(m => ToDto(m, today)));
        });

    [Function("RoomMoveAdd")]
    public Task<IActionResult> RoomMoveAdd([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "room-moves")] HttpRequest req) =>
        api.RunAsync(req, async user =>
        {
            var b = await Api.ReadJsonAsync<CreateRoomMoveRequest>(req);
            if (!b.Confirmed) throw BusinessRuleException.BadRequest("The room move must be confirmed.");
            var m = await moves.AddAsync(user, ToModel(b.Move ?? throw BusinessRuleException.BadRequest("The room move could not be read.")), req.HttpContext.RequestAborted);
            log.LogInformation("Room move added for hotel {Hotel}", (int)m.HotelId);
            return Api.Ok(ToDto(m, clock.Today()), 201);
        });

    internal static NewRoomMove ToModel(NewRoomMoveDto d) =>
        new(RhythmFunctions.ParseHotel(d.HotelId), d.RoomNo ?? "", d.ToRoomNo, d.CleanerAvailable, d.CleanedByType ?? "", d.EmployeeName, d.Note);

    private static RoomMoveDto ToDto(RoomMove m, DateOnly today) =>
        new(m.Id.ToString(), (int)m.HotelId, m.RoomNo, m.ToRoomNo, m.CleanerAvailable, m.CleanedByType, m.EmployeeName, m.Note,
            Date(m.MoveDate), Date(m.CleanDate), m.Status(today), m.CreatedBy, Iso(m.CreatedAtUtc));

    // ---------- room checks ----------

    [Function("ChecksGet")]
    public Task<IActionResult> ChecksGet([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "checks")] HttpRequest req) =>
        api.RunAsync(req, async _ => Api.Ok(ToDto(await checks.OverviewAsync(OptionalDate(req.Query["date"]) ?? clock.Today(), req.HttpContext.RequestAborted))));

    [Function("ChecksAdd")]
    public Task<IActionResult> ChecksAdd([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "checks")] HttpRequest req) =>
        api.RunAsync(req, async user =>
        {
            var b = await Api.ReadJsonAsync<NewRoomChecksRequest>(req);
            if (!b.Confirmed) throw BusinessRuleException.BadRequest("The checks must be confirmed.");
            Guid? staffId = Guid.TryParse(b.StaffId, out var s) ? s : null;
            var entry = new NewRoomChecks(RhythmFunctions.ParseHotel(b.HotelId), OptionalDate(b.Date) ?? clock.Today(), staffId, b.Role ?? "",
                b.CheckedBy ?? "", b.Rooms ?? [], b.Count);
            return Api.Ok(ToDto(await checks.AddAsync(user, entry, req.HttpContext.RequestAborted)), 201);
        });

    [Function("ChecksDelete")]
    public Task<IActionResult> ChecksDelete([HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "checks/{id}")] HttpRequest req, string id) =>
        api.RunAsync(req, async user =>
        {
            if (!long.TryParse(id, NumberStyles.None, Inv, out var checkId)) return Api.Error(404, "Entry not found.");
            return Api.Ok(ToDto(await checks.DeleteAsync(user, checkId, req.HttpContext.RequestAborted)));
        });

    private static ChecksOverviewDto ToDto(ChecksOverview o) => new(
        Date(o.Date),
        o.Entries.Select(e => new RoomCheckDto(e.Id.ToString(Inv), (int)e.HotelId, Date(e.CheckDate), e.RoomNo, e.Role, e.StaffId?.ToString(), e.CheckedBy, Iso(e.CreatedAtUtc))).ToList(),
        o.Today.Select(t => new HotelChecksDto((int)t.Hotel, t.Checks.Pm, t.Checks.Apm, t.Checks.Selbst)).ToList(),
        ToDto(o.Month));

    internal static CheckUsageDto ToDto(CheckUsage u) => new(u.Month, u.PmUsed, u.PmLimit, u.ApmUsed, u.ApmLimit, u.Selbstchecks, u.Warnings);

    // ---------- daily close ----------

    [Function("DailyCloseList")]
    public Task<IActionResult> DailyCloseList([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "daily-close")] HttpRequest req) =>
        api.RunAsync(req, async _ =>
        {
            var month = OptionalMonth(req.Query["month"]) ?? Berlin.FirstOfMonth(clock.Today());
            var list = await closes.ListAsync(month, req.HttpContext.RequestAborted);
            var emailed = (await reports.EmailLogAsync(null, month, month.AddMonths(1).AddDays(-1), 1000, req.HttpContext.RequestAborted))
                .Where(e => e.Kind != ReportKind.MONTHLY).Select(e => e.Period).ToHashSet();
            return Api.Ok(list.Select(d => new DailyCloseListItemDto(Date(d.Date), d.Status, d.ConfirmedBy, d.ConfirmedAtUtc is { } c ? Iso(c) : null,
                d.Rooms.ToDictionary(r => ((int)r.Key).ToString(Inv), r => r.Value), emailed.Contains(d.Date))));
        });

    [Function("DailyCloseGet")]
    public Task<IActionResult> DailyCloseGet([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "daily-close/{date}")] HttpRequest req, string date) =>
        api.RunAsync(req, async _ => Api.Ok(await ToDtoAsync(await closes.GetAsync(RequiredDate(date), req.HttpContext.RequestAborted), req.HttpContext.RequestAborted)));

    [Function("DailyCloseSave")]
    public Task<IActionResult> DailyCloseSave([HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "daily-close/{date}")] HttpRequest req, string date) =>
        api.RunAsync(req, async user =>
        {
            var input = ToModel(await Api.ReadJsonAsync<DailyCloseInputDto>(req));
            var view = await closes.SaveAsync(user, RequiredDate(date), input, req.HttpContext.RequestAborted);
            return Api.Ok(await ToDtoAsync(view, req.HttpContext.RequestAborted));
        });

    /// <summary>Save, confirm (lock), send reports; a failed email keeps the lock.</summary>
    [Function("DailyCloseConfirm")]
    public Task<IActionResult> DailyCloseConfirm([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "daily-close/{date}/confirm")] HttpRequest req, string date) =>
        api.RunAsync(req, async user =>
        {
            var body = await Api.ReadJsonAsync<DailyCloseInputDto>(req);
            if (!body.Confirmed) throw BusinessRuleException.BadRequest("The daily close must be confirmed.");
            var day = RequiredDate(date);
            var view = await closes.ConfirmAsync(user, day, ToModel(body), req.HttpContext.RequestAborted);
            try
            {
                await reports.SendDailyAsync(day, user, req.HttpContext.RequestAborted);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError("Daily reports for {Date} could not be sent: {Error}", day, ex.GetType().Name);
            }
            return Api.Ok(await ToDtoAsync(view, req.HttpContext.RequestAborted));
        });

    [Function("DailyCloseReopen")]
    public Task<IActionResult> DailyCloseReopen([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "daily-close/{date}/reopen")] HttpRequest req, string date) =>
        api.RunAsync(req, async user =>
        {
            var b = await Api.ReadJsonAsync<ReopenRequest>(req);
            if (!b.Confirmed) throw BusinessRuleException.BadRequest("Reopening must be confirmed.");
            return Api.Ok(await ToDtoAsync(await closes.ReopenAsync(user, RequiredDate(date), b.Reason, req.HttpContext.RequestAborted), req.HttpContext.RequestAborted));
        });

    private static CloseInput ToModel(DailyCloseInputDto d)
    {
        var hotels = (d.Hotels ?? []).Select(h =>
        {
            var hotel = RhythmFunctions.ParseHotel(h.HotelId);
            var r = h.Rooms;
            var rooms = new Dictionary<RoomCategory, IReadOnlyList<string>>
            {
                [RoomCategory.NS] = r?.NS ?? [],
                [RoomCategory.RK] = r?.RK ?? [],
                [RoomCategory.KS] = r?.KS ?? [],
                [RoomCategory.LATE_CHECKOUT] = r?.LATE_CHECKOUT ?? [],
                [RoomCategory.LIEGEZIMMER] = r?.LIEGEZIMMER ?? [],
            };
            return new CloseHotel(hotel, h.RichterStaffName ?? "", h.FlatRateDay, h.PauschaleChecks, h.Sauber, h.Abzuege, h.Informationen ?? "",
                (h.Cleaners ?? []).Select(c => new CloseCleaner(Guid.TryParse(c.StaffId, out var s) ? s : null, c.Name ?? "", c.Gereinigt, c.Ns, c.Rk, c.Ks,
                    Time(c.Start), Time(c.End), c.PauseHours)).ToList(),
                rooms);
        }).ToList();
        var supervisors = (d.Supervisors ?? []).Where(s => SupervisorRoles.IsKnown(s.Role)).Select(s =>
            new CloseSupervisor(s.Role, Guid.TryParse(s.StaffId, out var id) ? id : null, s.Name ?? "", Time(s.Start), Time(s.End), s.PauseHours)).ToList();
        return new CloseInput(d.Comment ?? "", d.ExtraReinigungHours, supervisors, hotels);
    }

    private async Task<DailyCloseDto> ToDtoAsync(DailyCloseView v, CancellationToken ct)
    {
        var emails = v.Status == "new" ? [] : (await reports.EmailLogAsync(null, v.Date, v.Date, 20, ct)).Where(e => e.Kind != ReportKind.MONTHLY);
        return new DailyCloseDto(
            Date(v.Date), v.Status, v.Input.Comment, v.Input.ExtraReinigungHours,
            v.Input.Supervisors.Select(s => new CloseSupervisorDto(s.Role, s.StaffId?.ToString(), s.Name, TimeText(s.Start), TimeText(s.End), s.PauseHours)).ToList(),
            v.Hotels.Select(h => new CloseHotelDto(
                (int)h.Input.Hotel, h.Input.RichterStaffName, h.Input.FlatRateDay, h.Input.PauschaleChecks, h.Input.Sauber, h.Input.Abzuege, h.Input.Informationen,
                h.Input.Cleaners.Select(c => new CloseCleanerDto(c.StaffId?.ToString(), c.Name, c.Gereinigt, c.Ns, c.Rk, c.Ks, TimeText(c.Start), TimeText(c.End), c.PauseHours)).ToList(),
                new CloseRoomsDto(h.Input.RoomsOf(RoomCategory.NS), h.Input.RoomsOf(RoomCategory.RK), h.Input.RoomsOf(RoomCategory.KS),
                    h.Input.RoomsOf(RoomCategory.LATE_CHECKOUT), h.Input.RoomsOf(RoomCategory.LIEGEZIMMER)),
                h.Sheet is { } s
                    ? new CloseSheetDto(s.Summary.TotalTaskSheets, h.TotalRooms ?? s.Summary.TotalRooms, s.Summary.DepartureRooms, s.Summary.DirtyRooms,
                        s.Summary.CleanRooms, s.Summary.TotalCredits, s.PublishedAtLocal is { } p ? Iso(Berlin.ToUtc(p)) : null)
                    : null,
                new CloseChecksDto(h.Checks.Pm, h.Checks.Apm, h.Checks.Selbst), h.RoomMoves,
                new CloseTotalsDto(h.Totals.Gereinigt, h.Totals.Ns, h.Totals.Rk, h.Totals.Ks, h.Totals.Sauber, h.Totals.Liegezimmer, h.Totals.Summe,
                    h.Totals.ChecksActual, h.Totals.ZuBerechnen))).ToList(),
            v.Issues.Select(i => new CloseIssueDto(i.Level, (int?)i.Hotel, i.Message)).ToList(),
            v.UpdatedBy, v.UpdatedAtUtc is { } u ? Iso(u) : null, v.ConfirmedBy, v.ConfirmedAtUtc is { } c ? Iso(c) : null,
            emails.Select(ToDto).ToList());
    }

    // ---------- reports ----------

    /// <summary>"task-sheet.pdf" or "handover.pdf"; never cached.</summary>
    [Function("ReportDailyPdf")]
    public Task<IActionResult> ReportDailyPdf(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "reports/daily/{date}/{hotelId:int}/{kind}")] HttpRequest req, string date, int hotelId, string kind) =>
        api.RunAsync(req, async _ =>
        {
            var k = kind switch
            {
                "task-sheet.pdf" => DailyReportKind.TaskSheet,
                "handover.pdf" => DailyReportKind.Handover,
                _ => throw BusinessRuleException.NotFound("Report not found."),
            };
            var (pdf, name) = await reports.DailyPdfAsync(RequiredDate(date), RhythmFunctions.ParseHotel(hotelId), k, req.HttpContext.RequestAborted);
            return Pdf(req, pdf, name);
        });

    [Function("ReportDailySend")]
    public Task<IActionResult> ReportDailySend([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "reports/daily/{date}/send")] HttpRequest req, string date) =>
        api.RunAsync(req, async user =>
        {
            if (!(await Api.ReadJsonAsync<ConfirmOnlyRequest>(req)).Confirmed) throw BusinessRuleException.BadRequest("Sending must be confirmed.");
            return Api.Ok((await reports.SendDailyAsync(RequiredDate(date), user, req.HttpContext.RequestAborted)).Select(ToDto));
        });

    /// <summary>"2026-09" returns data; "2026-09.pdf" returns the PDF.</summary>
    [Function("ReportMonthly")]
    public Task<IActionResult> ReportMonthly([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "reports/monthly/{month}")] HttpRequest req, string month) =>
        api.RunAsync(req, async _ =>
        {
            var pdf = month.EndsWith(".pdf", StringComparison.Ordinal);
            var first = OptionalMonth(pdf ? month[..^4] : month) ?? throw BusinessRuleException.BadRequest("The month is not valid.");
            if (pdf)
            {
                var (bytes, name) = await reports.MonthlyPdfAsync(first, req.HttpContext.RequestAborted);
                return Pdf(req, bytes, name);
            }
            var m = await reports.MonthlyAsync(first, req.HttpContext.RequestAborted);
            var emails = (await reports.EmailLogAsync(ReportKind.MONTHLY, first, first, 20, req.HttpContext.RequestAborted)).Select(ToDto).ToList();
            return Api.Ok(ToDto(m, emails));
        });

    [Function("ReportMonthlySend")]
    public Task<IActionResult> ReportMonthlySend([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "reports/monthly/{month}/send")] HttpRequest req, string month) =>
        api.RunAsync(req, async user =>
        {
            if (!(await Api.ReadJsonAsync<ConfirmOnlyRequest>(req)).Confirmed) throw BusinessRuleException.BadRequest("Sending must be confirmed.");
            var first = OptionalMonth(month) ?? throw BusinessRuleException.BadRequest("The month is not valid.");
            return Api.Ok((await reports.SendMonthlyAsync(first, user, req.HttpContext.RequestAborted)).Select(ToDto));
        });

    [Function("ReportEmails")]
    public Task<IActionResult> ReportEmails([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "reports/emails")] HttpRequest req) =>
        api.RunAsync(req, async _ => Api.Ok((await reports.EmailLogAsync(null, null, null, 100, req.HttpContext.RequestAborted)).Select(ToDto)));

    private static IActionResult Pdf(HttpRequest req, byte[] pdf, string name)
    {
        var download = req.Query.ContainsKey("download");
        req.HttpContext.Response.Headers.ContentDisposition = $"{(download ? "attachment" : "inline")}; filename=\"{name}\"";
        req.HttpContext.Response.Headers["X-Content-Type-Options"] = "nosniff";
        return new FileContentResult(pdf, "application/pdf");
    }

    private static MonthlyStatementDto ToDto(MonthlyStatement m, IReadOnlyList<EmailLogEntryDto> emails)
    {
        var model = m.Model;
        static MonthlyHotelCellsDto? Cells(MonthlyHotelCells? c) => c is null ? null : new(c.Zimmer, c.Gecheckt, c.ZuBerechnen, c.Abzuege);
        var rows = model.Rows.Select((r, i) => new MonthlyRowDto(
            r.Day, r.InMonth ? Date(new DateOnly(model.Month.Year, model.Month.Month, r.Day)) : null, m.DayStatus[i], Cells(r.Novotel), Cells(r.Ibis),
            r.Hausmann, r.FloorSupervisor, r.HausdameAssistenz, r.Hausdame, r.ExtraReinigung, r.Kommentar)).ToList();
        var t = model.Totals;
        return new MonthlyStatementDto(model.Month.ToString("yyyy-MM", Inv), model.Objekt, model.Kostenstelle, model.Zeitraum, rows,
            new MonthlyTotalsDto(Cells(t.Novotel)!, Cells(t.Ibis)!, t.Hausmann, t.FloorSupervisor, t.HausdameAssistenz, t.Hausdame, t.ExtraReinigung),
            model.ConfirmedDays, DateTime.DaysInMonth(model.Month.Year, model.Month.Month), emails);
    }

    private static EmailLogEntryDto ToDto(EmailLogEntry e) =>
        new(e.Id.ToString(), e.Kind.ToString(), Date(e.Period), e.Recipients, e.Status, e.Detail, Iso(e.AtUtc));

    // ---------- settings ----------

    [Function("SettingsGet")]
    public Task<IActionResult> SettingsGet([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "settings")] HttpRequest req) =>
        api.RunAsync(req, async _ => Api.Ok(ToDto(await settings.GetAsync(req.HttpContext.RequestAborted))));

    [Function("SettingsSave")]
    public Task<IActionResult> SettingsSave([HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "settings")] HttpRequest req) =>
        api.RunAsync(req, async user =>
        {
            var b = await Api.ReadJsonAsync<SaveSettingsRequest>(req);
            if (!b.Confirmed) throw BusinessRuleException.BadRequest("The change must be confirmed.");
            var p = b.Prices ?? new PricesDto(null, null, null, null, null, null, null, null, null);
            var recipients = Enum.GetValues<ReportKind>().ToDictionary(k => k, k =>
                (IReadOnlyList<string>)(b.Recipients?.GetValueOrDefault(k.ToString()) ?? []).Select(e => e.Trim()).Where(e => e.Length > 0).ToList());
            var model = new AppSettings(b.Objekt ?? "", b.Kostenstelle ?? "", b.PauschaleChecks, b.PauschaleLabel ?? "", b.EmailEnabled,
                new Prices(p.RoomNovotel, p.RoomIbis, p.Check, p.Hausdame, p.HausdameAssistenz, p.FloorSupervisor, p.Hausmann, p.ExtraReinigung, p.InternalRoom),
                recipients);
            return Api.Ok(ToDto(await settings.SaveAsync(user, model, req.HttpContext.RequestAborted)));
        });

    private AppSettingsDto ToDto(AppSettings s) => new(
        s.Objekt, s.Kostenstelle, s.PauschaleChecks, s.PauschaleLabel, s.EmailEnabled,
        new PricesDto(s.Prices.RoomNovotel, s.Prices.RoomIbis, s.Prices.Check, s.Prices.Hausdame, s.Prices.HausdameAssistenz, s.Prices.FloorSupervisor,
            s.Prices.Hausmann, s.Prices.ExtraReinigung, s.Prices.InternalRoom),
        s.Recipients.ToDictionary(r => r.Key.ToString(), r => r.Value),
        email.Transport);

    // ---------- timers ----------

    /// <summary>1st of the month, 06:00 Berlin: send last month's Leistungsnachweis.</summary>
    [Function("MonthlyStatementEmail")]
    public async Task MonthlyStatementEmail([TimerTrigger("0 0 6 1 * *")] TimerInfo timer, FunctionContext ctx)
    {
        var previous = Berlin.FirstOfMonth(clock.Today()).AddMonths(-1);
        var sent = await reports.EmailLogAsync(ReportKind.MONTHLY, previous, previous, 5, ctx.CancellationToken);
        if (sent.Any(e => e.Status is "sent" or "outbox"))
        {
            log.LogInformation("Leistungsnachweis {Month} was already sent", previous);
            return;
        }
        var result = await reports.SendMonthlyAsync(previous, null, ctx.CancellationToken);
        log.LogInformation("Leistungsnachweis {Month}: {Status}", previous, result[0].Status);
    }

    // ---------- parsing ----------

    private static string Date(DateOnly d) => d.ToString("yyyy-MM-dd", Inv);

    private static string Iso(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", Inv);

    private static string? TimeText(TimeOnly? t) => t?.ToString("HH:mm", Inv);

    private static TimeOnly? Time(string? s) =>
        TimeOnly.TryParseExact(s ?? "", "HH:mm", Inv, DateTimeStyles.None, out var t) ? t : null;

    internal static DateOnly? OptionalDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return DateOnly.TryParseExact(s, "yyyy-MM-dd", Inv, DateTimeStyles.None, out var d) ? d : throw BusinessRuleException.BadRequest("The date is not valid.");
    }

    private static DateOnly RequiredDate(string s) => OptionalDate(s) ?? throw BusinessRuleException.BadRequest("The date is not valid.");

    private static DateOnly? OptionalMonth(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return DateOnly.TryParseExact($"{s}-01", "yyyy-MM-dd", Inv, DateTimeStyles.None, out var d) ? d : throw BusinessRuleException.BadRequest("The month is not valid.");
    }
}
