using System.Globalization;
using Dapper;
using HousekeepingParser.Accounts;
using HousekeepingParser.Core;
using HousekeepingParser.Data;
using HousekeepingParser.Operations;
using Microsoft.Extensions.Logging;

namespace HousekeepingParser.Reports;

public enum DailyReportKind
{
    TaskSheet,
    Handover,
}

public sealed record EmailLogEntry(Guid Id, ReportKind Kind, DateOnly Period, IReadOnlyList<string> Recipients, string Status, string? Detail, DateTime AtUtc);

/// <summary>The Leistungsnachweis with each day's state.</summary>
public sealed record MonthlyStatement(MonthlyStatementModel Model, IReadOnlyList<string> DayStatus);

public interface IReportService
{
    Task<(byte[] Pdf, string FileName)> DailyPdfAsync(DateOnly date, HotelId hotel, DailyReportKind kind, CancellationToken ct);
    Task<MonthlyStatement> MonthlyAsync(DateOnly month, CancellationToken ct);
    Task<(byte[] Pdf, string FileName)> MonthlyPdfAsync(DateOnly month, CancellationToken ct);
    Task<IReadOnlyList<EmailLogEntry>> SendDailyAsync(DateOnly date, CurrentUser? user, CancellationToken ct);
    Task<IReadOnlyList<EmailLogEntry>> SendMonthlyAsync(DateOnly month, CurrentUser? user, CancellationToken ct);
    Task<IReadOnlyList<EmailLogEntry>> EmailLogAsync(ReportKind? kind, DateOnly? from, DateOnly? to, int take, CancellationToken ct);
}

/// <summary>Builds the report PDFs from the daily close and emails them.</summary>
public sealed class ReportService(
    IDbConnectionFactory db,
    IDailyCloseService closes,
    IStaffService staff,
    ISettingsService settings,
    IBusinessReportRenderer renderer,
    IEmailSender email,
    IClock clock,
    ILogger<ReportService> log) : IReportService
{
    private static readonly HotelId[] Order = [HotelId.Novotel, HotelId.IbisBudget];
    private static readonly string[] MonthsDe = ["Jan.", "Feb.", "März", "Apr.", "Mai", "Juni", "Juli", "Aug.", "Sep.", "Okt.", "Nov.", "Dez."];

    public static string Zeitraum(DateOnly month) => $"{MonthsDe[month.Month - 1]} {month:yy}";

    private static string Slug(HotelId h) => h == HotelId.Novotel ? "novotel" : "ibis-budget";

    // ---------- daily ----------

    public async Task<(byte[] Pdf, string FileName)> DailyPdfAsync(DateOnly date, HotelId hotel, DailyReportKind kind, CancellationToken ct)
    {
        var view = await closes.GetAsync(date, ct);
        if (view.Status == "new") throw BusinessRuleException.NotFound("There is no daily close for this day yet.");
        return await RenderDailyAsync(view, hotel, kind, ct);
    }

    private async Task<(byte[] Pdf, string FileName)> RenderDailyAsync(DailyCloseView view, HotelId hotel, DailyReportKind kind, CancellationToken ct)
    {
        var h = view.Hotel(hotel);
        var now = Berlin.ToLocal(clock.UtcNow);
        DateTime? approvedAt = view.ConfirmedAtUtc is { } c ? Berlin.ToLocal(c) : null;
        if (kind == DailyReportKind.Handover)
        {
            var model = new HandoverModel(hotel, view.Date, now, Empty(h.Input.RichterStaffName),
                h.Input.RoomsOf(RoomCategory.NS), h.Input.RoomsOf(RoomCategory.RK), h.Input.RoomsOf(RoomCategory.KS),
                h.Input.RoomsOf(RoomCategory.LATE_CHECKOUT), h.Input.RoomsOf(RoomCategory.LIEGEZIMMER), Empty(h.Input.Informationen),
                view.ConfirmedBy, approvedAt);
            return (renderer.Handover(model), $"uebergabe-{Slug(hotel)}-{view.Date:yyyy-MM-dd}.pdf");
        }

        var s = await settings.GetAsync(ct);
        var roster = (await staff.ListAsync(ct))
            .Where(p => p.Active && p.Role == "Cleaner" && (p.HotelId is null || p.HotelId == hotel)).OrderBy(p => p.Name, StringComparer.CurrentCulture).ToList();
        var model2 = new TaskSheetReportModel(
            hotel, view.Date, now, h.Sheet?.Summary,
            SupervisorRoles.All.Select(r =>
            {
                var x = view.Input.Supervisors.FirstOrDefault(v => v.Role == r.Role);
                return new SupervisorLine(r.Label, Empty(x?.Name ?? ""), x?.Start, x?.End);
            }).ToList(),
            CleanerLines(h.Input.Cleaners, roster),
            new DailyTotals(h.Totals.Gereinigt, h.Totals.Ns, h.Totals.Rk, h.Totals.Ks, h.Totals.Sauber, h.Totals.Liegezimmer, h.Totals.Summe),
            h.Input.FlatRateDay ? h.Input.PauschaleChecks : 0, s.PauschaleLabel, h.Input.FlatRateDay, h.Totals.ChecksActual, h.Totals.ZuBerechnen,
            view.ConfirmedBy, approvedAt);
        return (renderer.TaskSheetReport(model2), $"task-sheet-report-{Slug(hotel)}-{view.Date:yyyy-MM-dd}.pdf");
    }

    /// <summary>All listed cleaners; empty rows for those who did not work.</summary>
    internal static IReadOnlyList<CleanerLine> CleanerLines(IReadOnlyList<CloseCleaner> worked, IReadOnlyList<StaffMember> roster)
    {
        var lines = new List<CleanerLine>();
        var used = new HashSet<int>();
        foreach (var p in roster)
        {
            var i = worked.ToList().FindIndex(w => (w.StaffId == p.Id || string.Equals(w.Name, p.Name, StringComparison.OrdinalIgnoreCase)));
            if (i >= 0 && used.Add(i)) lines.Add(Line(worked[i]));
            else lines.Add(new CleanerLine(p.Name, null, null, null, null, null, null, null));
        }
        for (var i = 0; i < worked.Count; i++)
            if (!used.Contains(i)) lines.Add(Line(worked[i]));
        return lines;

        static CleanerLine Line(CloseCleaner w) => new(w.Name, w.Gereinigt, w.Ns, w.Rk, w.Ks, w.Start, w.End, w.PauseHours);
    }

    private static string? Empty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

    public async Task<IReadOnlyList<EmailLogEntry>> SendDailyAsync(DateOnly date, CurrentUser? user, CancellationToken ct)
    {
        var view = await closes.GetAsync(date, ct);
        if (view.Status != "confirmed") throw BusinessRuleException.Conflict("The daily close for this day is not confirmed yet.");
        var s = await settings.GetAsync(ct);
        var dateText = date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
        var results = new List<EmailLogEntry>();

        foreach (var (kind, reportKind, subject) in new[]
        {
            (DailyReportKind.TaskSheet, ReportKind.DAILY_TASK_SHEET, $"Task Sheet Report {dateText} – Novotel & ibis budget Münster City"),
            (DailyReportKind.Handover, ReportKind.DAILY_HANDOVER, $"Übergabe Etage – Empfang {dateText} – Novotel & ibis budget"),
        })
        {
            var attachments = new List<EmailAttachment>();
            foreach (var hotel in Order)
            {
                var (pdf, name) = await RenderDailyAsync(view, hotel, kind, ct);
                attachments.Add(new EmailAttachment(name, pdf));
            }
            var body = $"""
                Guten Tag,

                anbei {(kind == DailyReportKind.TaskSheet ? "der Task Sheet Report" : "die Übergabe Etage – Empfang")} vom {dateText} für Novotel Münster City und ibis budget Münster City.
                Freigegeben von {view.ConfirmedBy}.

                Diese E-Mail wurde automatisch von HSK Automation erstellt.
                """;
            results.Add(await DeliverAsync(s, reportKind, date, subject, body, attachments, user, ct));
        }
        return results;
    }

    // ---------- monthly ----------

    public async Task<MonthlyStatement> MonthlyAsync(DateOnly month, CancellationToken ct)
    {
        var first = Berlin.FirstOfMonth(month);
        var days = DateTime.DaysInMonth(first.Year, first.Month);
        var s = await settings.GetAsync(ct);

        Dictionary<DateOnly, string> statuses;
        await using (var c = await db.OpenAsync(ct))
        {
            statuses = (await c.QueryAsync<(DateOnly Date, string Status)>(
                "SELECT report_date, status FROM dbo.daily_close WHERE report_date >= @first AND report_date < @next",
                new { first, next = first.AddMonths(1) })).ToDictionary(r => r.Date, r => r.Status);
        }

        var rows = new List<MonthlyRowModel>();
        var status = new List<string>();
        DateTime? lastConfirmed = null;
        for (var day = 1; day <= 31; day++)
        {
            if (day > days)
            {
                rows.Add(new MonthlyRowModel(day, false, null, null, null, null, null, null, null, null));
                status.Add("outside");
                continue;
            }
            var date = new DateOnly(first.Year, first.Month, day);
            var st = statuses.GetValueOrDefault(date);
            if (st != "confirmed")
            {
                rows.Add(new MonthlyRowModel(day, true, null, null, null, null, null, null, null, null));
                status.Add(st is null ? "open" : "draft");
                continue;
            }
            var view = await closes.GetAsync(date, ct);
            if (view.ConfirmedAtUtc > lastConfirmed || lastConfirmed is null) lastConfirmed = view.ConfirmedAtUtc;
            MonthlyHotelCells Cells(HotelId id)
            {
                var h = view.Hotel(id);
                return new MonthlyHotelCells(h.Totals.Gereinigt, h.Totals.ChecksActual, h.Totals.ZuBerechnen, h.Input.Abzuege);
            }
            rows.Add(new MonthlyRowModel(day, true, Cells(HotelId.Novotel), Cells(HotelId.IbisBudget),
                DailyCloseMath.HoursOf(view.Input, "HM"), DailyCloseMath.HoursOf(view.Input, "FS"), DailyCloseMath.HoursOf(view.Input, "HDA"),
                DailyCloseMath.HoursOf(view.Input, "HD"), view.Input.ExtraReinigungHours, Empty(view.Input.Comment)));
            status.Add("confirmed");
        }

        var filled = rows.Where(r => r.Novotel is not null).ToList();
        MonthlyHotelCells Sum(Func<MonthlyRowModel, MonthlyHotelCells?> pick) => new(
            filled.Sum(r => pick(r)!.Zimmer), filled.Sum(r => pick(r)!.Gecheckt), filled.Sum(r => pick(r)!.ZuBerechnen), filled.Sum(r => pick(r)!.Abzuege));
        var totals = new MonthlyTotals(Sum(r => r.Novotel), Sum(r => r.Ibis), filled.Sum(r => r.Hausmann ?? 0), filled.Sum(r => r.FloorSupervisor ?? 0),
            filled.Sum(r => r.HausdameAssistenz ?? 0), filled.Sum(r => r.Hausdame ?? 0), filled.Sum(r => r.ExtraReinigung ?? 0));
        var complete = filled.Count == days;
        var model = new MonthlyStatementModel(s.Objekt, s.Kostenstelle, first, Zeitraum(first), rows, totals, filled.Count, Berlin.ToLocal(clock.UtcNow),
            complete ? "HSK Automation (alle Tage freigegeben)" : null, complete && lastConfirmed is { } lc ? Berlin.ToLocal(lc) : null);
        return new MonthlyStatement(model, status);
    }

    public async Task<(byte[] Pdf, string FileName)> MonthlyPdfAsync(DateOnly month, CancellationToken ct)
    {
        var m = await MonthlyAsync(month, ct);
        return (renderer.MonthlyStatement(m.Model), $"leistungsnachweis-{month:yyyy-MM}.pdf");
    }

    public async Task<IReadOnlyList<EmailLogEntry>> SendMonthlyAsync(DateOnly month, CurrentUser? user, CancellationToken ct)
    {
        var first = Berlin.FirstOfMonth(month);
        var m = await MonthlyAsync(first, ct);
        var (pdf, name) = (renderer.MonthlyStatement(m.Model), $"leistungsnachweis-{first:yyyy-MM}.pdf");
        var s = await settings.GetAsync(ct);
        var days = DateTime.DaysInMonth(first.Year, first.Month);
        var body = $"""
            Guten Tag,

            anbei der Leistungsnachweis {m.Model.Zeitraum} für {s.Objekt}, Kostenstelle {s.Kostenstelle}.
            {m.Model.ConfirmedDays} von {days} Tagen sind freigegeben.{(m.Model.ConfirmedDays < days ? " Nicht freigegebene Tage sind leer." : "")}

            Diese E-Mail wurde automatisch von HSK Automation erstellt.
            """;
        return [await DeliverAsync(s, ReportKind.MONTHLY, first, $"Leistungsnachweis {m.Model.Zeitraum} – {s.Objekt}", body, [new EmailAttachment(name, pdf)], user, ct)];
    }

    // ---------- email ----------

    private async Task<EmailLogEntry> DeliverAsync(
        AppSettings s, ReportKind kind, DateOnly period, string subject, string body, IReadOnlyList<EmailAttachment> attachments, CurrentUser? user, CancellationToken ct)
    {
        var to = s.Recipients.GetValueOrDefault(kind) ?? [];
        string status;
        string? detail;
        if (!s.EmailEnabled)
        {
            (status, detail) = ("skipped", "Email is switched off in the settings");
        }
        else if (to.Count == 0)
        {
            (status, detail) = ("skipped", "No recipients set for this report");
        }
        else
        {
            try
            {
                await email.SendAsync(new EmailMessage(to, subject, body, attachments), ct);
                (status, detail) = email.Transport == "smtp" ? ("sent", null) : ("outbox", "Saved to the outbox: no mail server is set up yet");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError("Report email {Kind} for {Period} failed: {Error}", kind, period, ex.GetType().Name);
                (status, detail) = ("failed", $"Sending failed ({ex.GetType().Name}). Use “Send again” later.");
            }
        }

        var id = Guid.NewGuid();
        await using var c = await db.OpenAsync(ct);
        await c.ExecuteAsync(
            """
            INSERT INTO dbo.report_email_log (email_id, report_kind, period, recipients, status, detail, triggered_by)
            VALUES (@id, @kind, @period, @recipients, @status, @detail, @user)
            """, new { id, kind = kind.ToString(), period, recipients = string.Join(", ", to), status, detail, user = user?.User.UserId });
        return new EmailLogEntry(id, kind, period, to, status, detail, clock.UtcNow);
    }

    public async Task<IReadOnlyList<EmailLogEntry>> EmailLogAsync(ReportKind? kind, DateOnly? from, DateOnly? to, int take, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        return (await c.QueryAsync<(Guid Id, string Kind, DateOnly Period, string Recipients, string Status, string? Detail, DateTime At)>(
            """
            SELECT TOP (@take) email_id, report_kind, period, recipients, status, detail, at_utc FROM dbo.report_email_log
            WHERE (@kind IS NULL OR report_kind = @kind) AND (@from IS NULL OR period >= @from) AND (@to IS NULL OR period <= @to)
            ORDER BY at_utc DESC
            """, new { take, kind = kind?.ToString(), from, to }))
            .Select(r => new EmailLogEntry(r.Id, Enum.Parse<ReportKind>(r.Kind), r.Period,
                r.Recipients.Split(", ", StringSplitOptions.RemoveEmptyEntries), r.Status, r.Detail, r.At))
            .ToList();
    }
}
