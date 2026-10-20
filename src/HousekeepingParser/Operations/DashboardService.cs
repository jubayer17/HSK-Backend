using System.Globalization;
using Dapper;
using HousekeepingParser.Core;
using HousekeepingParser.Data;
using HousekeepingParser.Functions;

namespace HousekeepingParser.Operations;

public enum DashboardTimeframe
{
    Daily,
    Weekly,
    Monthly,
}

public interface IDashboardService
{
    /// <summary>Figures for the range ending <paramref name="date"/> (max today); null hotel = both.</summary>
    Task<DashboardMetricsDto> GetAsync(DashboardTimeframe timeframe, HotelId? hotel, DateOnly? date, CancellationToken ct);
}

/// <summary>Pure dashboard arithmetic; mirrors the client's demo mode.</summary>
internal static class DashboardMath
{
    /// <summary>Daily, last 7 days, or month-to-date; future dates clamp to today.</summary>
    public static (DateOnly From, DateOnly To) Range(DashboardTimeframe timeframe, DateOnly date, DateOnly today)
    {
        var to = date > today ? today : date;
        var from = timeframe switch
        {
            DashboardTimeframe.Daily => to,
            DashboardTimeframe.Weekly => to.AddDays(-6),
            DashboardTimeframe.Monthly => Berlin.FirstOfMonth(to),
            _ => throw new ArgumentOutOfRangeException(nameof(timeframe)),
        };
        return (from, to);
    }

    /// <summary>PM + APM checks minus flat-rate checks (may be negative).</summary>
    public static int ZuBerechnen(int checksPm, int checksApm, bool flatRateDay, int pauschaleChecks) =>
        checksPm + checksApm - (flatRateDay ? pauschaleChecks : 0);

    /// <summary>Confirmed day cost; missing prices count as 0.</summary>
    public static decimal ActualCost(int gereinigt, int abzuege, int zuBerechnen, decimal? roomPrice, decimal? checkPrice) =>
        (gereinigt - abzuege) * (roomPrice ?? 0) + zuBerechnen * (checkPrice ?? 0);

    /// <summary>Unconfirmed day: planned external rooms × room price.</summary>
    public static decimal PlannedCost(int rooms, decimal? roomPrice) => rooms * (roomPrice ?? 0);

    /// <summary>Hours × rate; a missing rate counts as 0.</summary>
    public static decimal HoursCost(decimal hours, decimal? rate) => hours * (rate ?? 0);

    /// <summary>Clean day of a move: move day if a cleaner was free, else planned day.</summary>
    public static DateOnly CleanedOn(DateOnly moveDate, DateOnly cleanDate, bool cleanerAvailable) => cleanerAvailable ? moveDate : cleanDate;

    public static string DayBasis(bool anyData, bool confirmed) => !anyData ? "none" : confirmed ? "actual" : "planned";

    public static string Basis(int actualDays, int plannedDays) =>
        actualDays > 0 && plannedDays > 0 ? "mixed" : actualDays > 0 ? "actual" : plannedDays > 0 ? "planned" : "none";

    public static decimal Round2(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    /// <summary>€ total; null if a room price is missing or there is no data.</summary>
    public static decimal? Billing(decimal total, bool pricesSet, bool anyDayWithData) => pricesSet && anyDayWithData ? Round2(total) : null;
}

/// <summary>Dashboard: actuals from confirmed closes, else planned from the sheet.</summary>
public sealed class DashboardService(IDbConnectionFactory db, ISettingsService settings, IRoomCheckService checks, IClock clock) : IDashboardService
{
    private static readonly HotelId[] Order = [HotelId.Novotel, HotelId.IbisBudget];
    private static readonly (string Role, Func<Prices, decimal?> Rate)[] PaidRoles =
    [
        ("HD", p => p.Hausdame), ("HDA", p => p.HausdameAssistenz), ("FS", p => p.FloorSupervisor), ("HM", p => p.Hausmann),
    ];

    public async Task<DashboardMetricsDto> GetAsync(DashboardTimeframe timeframe, HotelId? hotel, DateOnly? date, CancellationToken ct)
    {
        var today = clock.Today();
        var (from, to) = DashboardMath.Range(timeframe, date ?? today, today);
        var hotels = hotel is { } only ? new[] { only } : Order;
        var hotelParam = (byte?)hotel;
        var p = (await settings.GetAsync(ct)).Prices;
        var pricesSet = hotels.All(h => p.Room(h) is not null);

        await using var c = await db.OpenAsync(ct);
        var cmd = (string sql, object args) => new CommandDefinition(sql, args, cancellationToken: ct);
        var args = new { from, to, today, hotel = hotelParam };

        var closes = (await c.QueryAsync<CloseRow>(cmd(
            """
            SELECT close_id AS CloseId, report_date AS ReportDate, status AS Status, extra_reinigung_hours AS ExtraReinigungHours
            FROM dbo.daily_close WHERE (report_date >= @from AND report_date <= @to) OR report_date = @today
            """, args))).ToDictionary(r => r.ReportDate);

        var closeHotels = (await c.QueryAsync<CloseHotelRow>(cmd(
            """
            SELECT d.report_date AS ReportDate, h.hotel_id AS HotelId, h.flat_rate_day AS FlatRateDay, h.pauschale_checks AS PauschaleChecks,
                   h.abzuege AS Abzuege, ISNULL(h.checks_pm, 0) AS ChecksPm, ISNULL(h.checks_apm, 0) AS ChecksApm,
                   ISNULL((SELECT SUM(x.gereinigt) FROM dbo.daily_close_cleaner x WHERE x.close_id = h.close_id AND x.hotel_id = h.hotel_id), 0) AS Gereinigt,
                   (SELECT COUNT(*) FROM dbo.daily_close_room r WHERE r.close_id = h.close_id AND r.hotel_id = h.hotel_id
                      AND r.category IN ('LIEGEZIMMER', 'LATE_CHECKOUT')) AS Liegezimmer
            FROM dbo.daily_close d JOIN dbo.daily_close_hotel h ON h.close_id = d.close_id
            WHERE d.status = 'confirmed' AND d.report_date >= @from AND d.report_date <= @to AND (@hotel IS NULL OR h.hotel_id = @hotel)
            """, args))).ToDictionary(r => (r.ReportDate, (HotelId)r.HotelId));

        var supervisors = hotel is null
            ? (await c.QueryAsync<SupervisorRow>(cmd(
                """
                SELECT d.report_date AS ReportDate, s.role AS Role, s.start_time AS StartTime, s.end_time AS EndTime, s.pause_hours AS PauseHours
                FROM dbo.daily_close d JOIN dbo.daily_close_supervisor s ON s.close_id = d.close_id
                WHERE d.status = 'confirmed' AND d.report_date >= @from AND d.report_date <= @to
                """, args))).ToLookup(r => r.ReportDate)
            : Enumerable.Empty<SupervisorRow>().ToLookup(r => r.ReportDate);

        // Latest completed sheet per hotel and day (a re-run replaces earlier ones).
        var sheetRows = (await c.QueryAsync<SheetRow>(cmd(
            """
            WITH latest AS (
                SELECT f.file_id, f.hotel_id, f.report_date,
                       ROW_NUMBER() OVER (PARTITION BY f.hotel_id, f.report_date ORDER BY r.created_at_utc DESC) AS rn
                FROM dbo.phase1_run_file f JOIN dbo.phase1_run r ON r.run_id = f.run_id JOIN dbo.report_summary rs ON rs.file_id = f.file_id
                WHERE f.status = 'completed' AND f.hotel_id IS NOT NULL AND f.report_date >= @from AND f.report_date <= @to
                  AND (@hotel IS NULL OR f.hotel_id = @hotel)
            )
            SELECT l.report_date AS ReportDate, l.hotel_id AS HotelId, s.logic_version AS LogicVersion, s.printed AS Printed,
                   SUM(CASE WHEN s.cleaned = 1 AND x.ext = 1 THEN 1 ELSE 0 END) AS Cleaned,
                   SUM(CASE WHEN s.cleaned = 1 AND x.ext = 1 AND s.final_colour = 'Red' THEN 1 ELSE 0 END) AS Red,
                   SUM(CASE WHEN s.cleaned = 1 AND x.ext = 1 AND s.final_colour = 'Blue' THEN 1 ELSE 0 END) AS Blue,
                   SUM(CASE WHEN s.cleaned = 1 AND x.ext = 1 AND s.final_colour = 'Purple' THEN 1 ELSE 0 END) AS Purple,
                   SUM(CASE WHEN s.cleaned = 1 AND x.ext = 1 AND s.final_colour = 'Green' THEN 1 ELSE 0 END) AS Green,
                   SUM(CASE WHEN s.cleaned = 1 AND x.ext = 1 AND s.final_colour = 'Gold' THEN 1 ELSE 0 END) AS Gold,
                   SUM(CASE WHEN s.cleaned = 0 AND s.final_colour = 'Green' THEN 1 ELSE 0 END) AS GreenNoService,
                   SUM(CASE WHEN s.service_code = 'STAYOVER_BLUE' THEN 1 ELSE 0 END) AS Waiting
            FROM latest l
            LEFT JOIN dbo.room_cleaning_status s ON s.file_id = l.file_id
            CROSS APPLY (SELECT CASE WHEN s.cleaned_by_type IS NULL OR s.cleaned_by_type <> 'HotelEmployee' THEN 1 ELSE 0 END AS ext) x
            WHERE l.rn = 1
            GROUP BY l.report_date, l.hotel_id, s.logic_version, s.printed
            """, args))).ToList();
        var sheets = sheetRows.ToLookup(r => (r.ReportDate, (HotelId)r.HotelId));

        var moves = (await c.QueryAsync<MoveRow>(cmd(
            """
            SELECT hotel_id AS HotelId, move_date AS MoveDate, clean_date AS CleanDate, cleaner_available AS CleanerAvailable, cleaned_by_type AS CleanedByType
            FROM dbo.room_move
            WHERE (@hotel IS NULL OR hotel_id = @hotel)
              AND ((move_date >= @from AND move_date <= @to) OR (clean_date >= @from AND clean_date <= @to) OR clean_date > @today)
            """, args))).ToList();

        var rangeChecks = await c.QuerySingleAsync<(int Pm, int Apm, int Selbst)>(cmd(
            """
            SELECT COUNT(CASE WHEN checker_role = 'ProjectManager' THEN 1 END), COUNT(CASE WHEN checker_role = 'AssistantPM' THEN 1 END),
                   COUNT(CASE WHEN checker_role = 'CleanerSelbstcheck' THEN 1 END)
            FROM dbo.room_check_log WHERE check_date >= @from AND check_date <= @to AND (@hotel IS NULL OR hotel_id = @hotel)
            """, args));

        var activeRhythms = (await c.QueryAsync<(byte Hotel, string Rhythm)>(cmd(
            "SELECT hotel_id, rhythm_type FROM dbo.rhythm_configuration WHERE status = 'ACTIVE'", args)))
            .Where(r => hotels.Contains((HotelId)r.Hotel))
            .Select(r => Enum.TryParse<RhythmType>(r.Rhythm, out var t) ? t : (RhythmType?)null)
            .ToHashSet();

        var usage = await checks.UsageAsync(c, to, hotel);

        // ---------- per day ----------
        int red = 0, blue = 0, purple = 0, green = 0, gold = 0, greenNoService = 0, waiting = 0, liegezimmer = 0;
        int checksBilled = 0, externalRooms = 0, actualDays = 0, plannedDays = 0, confirmed = 0, dayCount = 0;
        decimal total = 0, supervisorHours = 0;
        var logicServices = Logics.All.ToDictionary(l => l, _ => 0);
        var logicCost = Logics.All.ToDictionary(l => l, _ => 0m);
        var days = new List<DashboardDayDto>();

        for (var d = from; d <= to; d = d.AddDays(1))
        {
            dayCount++;
            var isActual = closes.TryGetValue(d, out var close) && close.Status == "confirmed";
            if (isActual) confirmed++;
            decimal dayCost = 0;
            var dayRooms = 0;
            var anyData = false;

            foreach (var h in hotels)
            {
                var rows = sheets[(d, h)].ToList();
                var hasSheet = rows.Count > 0;
                var printed = rows.Where(r => r.Printed == true).ToList();
                red += printed.Sum(r => r.Red);
                blue += printed.Sum(r => r.Blue);
                purple += printed.Sum(r => r.Purple);
                green += printed.Sum(r => r.Green);
                gold += printed.Sum(r => r.Gold);
                greenNoService += printed.Sum(r => r.GreenNoService);
                waiting += printed.Sum(r => r.Waiting);
                foreach (var logic in Logics.All)
                {
                    var services = rows.Where(r => r.LogicVersion == Logics.Version(logic)).Sum(r => r.Cleaned);
                    logicServices[logic] += services;
                    logicCost[logic] += DashboardMath.PlannedCost(services, p.Room(h));
                }

                if (isActual)
                {
                    // A missing hotel part counts as zero.
                    closeHotels.TryGetValue((d, h), out var ch);
                    var zu = ch is null ? 0 : DashboardMath.ZuBerechnen(ch.ChecksPm, ch.ChecksApm, ch.FlatRateDay, ch.PauschaleChecks);
                    dayRooms += ch?.Gereinigt ?? 0;
                    dayCost += DashboardMath.ActualCost(ch?.Gereinigt ?? 0, ch?.Abzuege ?? 0, zu, p.Room(h), p.Check);
                    checksBilled += zu;
                    liegezimmer += ch?.Liegezimmer ?? 0;
                    anyData = true;
                }
                else if (hasSheet)
                {
                    var planned = printed.Sum(r => r.Cleaned);
                    dayRooms += planned;
                    dayCost += DashboardMath.PlannedCost(planned, p.Room(h));
                    anyData = true;
                }
            }

            if (isActual && hotel is null)
            {
                // Shared supervisor hours count only in the "both hotels" view.
                foreach (var (role, rate) in PaidRoles)
                {
                    var s = supervisors[d].FirstOrDefault(x => x.Role == role);
                    var hours = s is null ? 0 : DailyCloseMath.Hours(Time(s.StartTime), Time(s.EndTime), s.PauseHours) ?? 0;
                    supervisorHours += hours;
                    dayCost += DashboardMath.HoursCost(hours, rate(p));
                }
                supervisorHours += close!.ExtraReinigungHours;
                dayCost += DashboardMath.HoursCost(close.ExtraReinigungHours, p.ExtraReinigung);
            }

            if (anyData)
            {
                if (isActual) actualDays++;
                else plannedDays++;
            }
            externalRooms += dayRooms;
            total += dayCost;
            days.Add(new DashboardDayDto(Iso(d), anyData && pricesSet ? DashboardMath.Round2(dayCost) : null, dayRooms, DashboardMath.DayBasis(anyData, isActual)));
        }

        // ---------- room moves ----------
        var roomMoves = moves.Count(m => m.MoveDate >= from && m.MoveDate <= to);
        var internalRooms = moves.Count(m => m.CleanedByType == "HotelEmployee"
            && DashboardMath.CleanedOn(m.MoveDate, m.CleanDate, m.CleanerAvailable) is var on && on >= from && on <= to);
        var movesOpen = today >= from && today <= to ? moves.Count(m => !m.CleanerAvailable && m.CleanDate > today) : 0;
        decimal? internalCost = p.InternalRoom is { } ir ? DashboardMath.Round2(internalRooms * ir) : null;

        var anyDayWithData = actualDays > 0 || plannedDays > 0;
        var financials = new DashboardFinancialsDto(
            DashboardMath.Billing(total, pricesSet, anyDayWithData), externalRooms, internalCost, internalRooms, checksBilled,
            DashboardMath.Round2(supervisorHours), pricesSet);
        var workload = new DashboardWorkloadDto(
            externalRooms + internalRooms, roomMoves, movesOpen, red, blue, purple, green, gold, greenNoService, waiting, liegezimmer);
        var qa = new DashboardQaDto(usage.Month, usage.PmUsed, usage.PmLimit, usage.ApmUsed, usage.ApmLimit, usage.Selbstchecks, usage.Warnings,
            rangeChecks.Pm, rangeChecks.Apm, rangeChecks.Selbst);
        var logicComparison = Logics.All.Select(l => new LogicComparisonDto(
            l.ToString(), logicServices[l], pricesSet ? DashboardMath.Round2(logicCost[l]) : null, activeRhythms.Contains(l))).ToList();
        var todayStatus = closes.TryGetValue(today, out var todayClose) ? todayClose.Status : "new";

        return new DashboardMetricsDto(
            timeframe.ToString().ToLowerInvariant(), Iso(from), Iso(to), hotel is { } hh ? (int)hh : "all",
            DashboardMath.Basis(actualDays, plannedDays), financials, workload, qa, days, logicComparison,
            new DashboardClosesDto(confirmed, dayCount, todayStatus));
    }

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static TimeOnly? Time(TimeSpan? t) => t is { } v ? TimeOnly.FromTimeSpan(v) : null;

    private sealed class CloseRow
    {
        public Guid CloseId { get; set; }
        public DateOnly ReportDate { get; set; }
        public string Status { get; set; } = "";
        public decimal ExtraReinigungHours { get; set; }
    }

    private sealed class CloseHotelRow
    {
        public DateOnly ReportDate { get; set; }
        public byte HotelId { get; set; }
        public bool FlatRateDay { get; set; }
        public int PauschaleChecks { get; set; }
        public int Abzuege { get; set; }
        public int ChecksPm { get; set; }
        public int ChecksApm { get; set; }
        public int Gereinigt { get; set; }
        public int Liegezimmer { get; set; }
    }

    private sealed class SupervisorRow
    {
        public DateOnly ReportDate { get; set; }
        public string Role { get; set; } = "";
        public TimeSpan? StartTime { get; set; }
        public TimeSpan? EndTime { get; set; }
        public decimal PauseHours { get; set; }
    }

    private sealed class SheetRow
    {
        public DateOnly ReportDate { get; set; }
        public byte HotelId { get; set; }
        public byte? LogicVersion { get; set; }
        public bool? Printed { get; set; }
        public int Cleaned { get; set; }
        public int Red { get; set; }
        public int Blue { get; set; }
        public int Purple { get; set; }
        public int Green { get; set; }
        public int Gold { get; set; }
        public int GreenNoService { get; set; }
        public int Waiting { get; set; }
    }

    private sealed class MoveRow
    {
        public byte HotelId { get; set; }
        public DateOnly MoveDate { get; set; }
        public DateOnly CleanDate { get; set; }
        public bool CleanerAvailable { get; set; }
        public string CleanedByType { get; set; } = "";
    }
}
