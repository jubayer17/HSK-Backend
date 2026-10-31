using Dapper;
using HousekeepingParser.Accounts;
using HousekeepingParser.Audit;
using HousekeepingParser.Core;
using HousekeepingParser.Data;
using HousekeepingParser.Parsing;
using Microsoft.Data.SqlClient;

namespace HousekeepingParser.Operations;

/// <summary>Opera "Report Summary" of the day's latest sheet, and its file.</summary>
public sealed record CloseSheet(Guid FileId, ReportSummary Summary, DateTime? PublishedAtLocal);

public sealed record CloseHotelView(CloseHotel Input, CloseSheet? Sheet, CloseChecks Checks, int RoomMoves, CloseTotals Totals)
{
    public int? TotalRooms { get; init; }

    /// <summary>Flat-rate days saved for the other days of this Monday–Sunday week.</summary>
    public int OtherFlatRateDays { get; init; }
}

public sealed record DailyCloseView(
    DateOnly Date,
    /// <summary>"new", "draft" or "confirmed".</summary>
    string Status,
    CloseInput Input,
    IReadOnlyList<CloseHotelView> Hotels,
    IReadOnlyList<CloseIssue> Issues,
    string? UpdatedBy,
    DateTime? UpdatedAtUtc,
    string? ConfirmedBy,
    DateTime? ConfirmedAtUtc)
{
    public CloseHotelView Hotel(HotelId id) => Hotels.Single(h => h.Input.Hotel == id);
}

public sealed record DailyCloseListItem(DateOnly Date, string Status, string? ConfirmedBy, DateTime? ConfirmedAtUtc, IReadOnlyDictionary<HotelId, int> Rooms);

public interface IDailyCloseService
{
    Task<DailyCloseView> GetAsync(DateOnly date, CancellationToken ct);
    Task<IReadOnlyList<DailyCloseListItem>> ListAsync(DateOnly month, CancellationToken ct);
    Task<DailyCloseView> SaveAsync(CurrentUser user, DateOnly date, CloseInput input, CancellationToken ct);
    Task<DailyCloseView> ConfirmAsync(CurrentUser user, DateOnly date, CloseInput input, CancellationToken ct);
    Task<DailyCloseView> ReopenAsync(CurrentUser user, DateOnly date, string? reason, CancellationToken ct);
}

/// <summary>Daily close: entered once, confirmed, then locked as a snapshot.</summary>
public sealed class DailyCloseService(IDbConnectionFactory db, IRoomCheckService checks, IAuditLog audit, IClock clock) : IDailyCloseService
{
    private static readonly HotelId[] Order = [HotelId.Novotel, HotelId.IbisBudget];

    public async Task<DailyCloseView> GetAsync(DateOnly date, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        return await LoadAsync(c, null, date);
    }

    public async Task<IReadOnlyList<DailyCloseListItem>> ListAsync(DateOnly month, CancellationToken ct)
    {
        var from = Berlin.FirstOfMonth(month);
        var to = from.AddMonths(1);
        await using var c = await db.OpenAsync(ct);
        var closes = (await c.QueryAsync<HeadRow>(
            """
            SELECT d.close_id AS CloseId, d.report_date AS ReportDate, d.status AS Status, cu.display_label AS ConfirmedBy, d.confirmed_at_utc AS ConfirmedAtUtc,
                   d.comment AS Comment, d.extra_reinigung_hours AS ExtraReinigungHours, uu.display_label AS UpdatedBy, d.updated_at_utc AS UpdatedAtUtc
            FROM dbo.daily_close d JOIN dbo.app_user uu ON uu.user_id = d.updated_by LEFT JOIN dbo.app_user cu ON cu.user_id = d.confirmed_by
            WHERE d.report_date >= @from AND d.report_date < @to
            """, new { from, to })).ToDictionary(r => r.ReportDate);
        var rooms = (await c.QueryAsync<(DateOnly Date, byte Hotel, int Rooms)>(
            """
            SELECT d.report_date, x.hotel_id, SUM(x.gereinigt) FROM dbo.daily_close_cleaner x JOIN dbo.daily_close d ON d.close_id = x.close_id
            WHERE d.report_date >= @from AND d.report_date < @to GROUP BY d.report_date, x.hotel_id
            """, new { from, to })).ToLookup(r => r.Date);

        var today = clock.Today();
        var list = new List<DailyCloseListItem>();
        for (var d = from; d < to && d <= today; d = d.AddDays(1))
        {
            closes.TryGetValue(d, out var h);
            list.Add(new DailyCloseListItem(d, h?.Status ?? "new", h?.ConfirmedBy, h?.ConfirmedAtUtc,
                rooms[d].ToDictionary(r => (HotelId)r.Hotel, r => r.Rooms)));
        }
        list.Reverse();
        return list;
    }

    public async Task<DailyCloseView> SaveAsync(CurrentUser user, DateOnly date, CloseInput input, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
        await WriteAsync(c, tx, user, date, Normalise(input));
        await tx.CommitAsync(ct);
        return await LoadAsync(c, null, date);
    }

    public async Task<DailyCloseView> ConfirmAsync(CurrentUser user, DateOnly date, CloseInput input, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
        var closeId = await WriteAsync(c, tx, user, date, Normalise(input));
        var view = await LoadAsync(c, tx, date);
        var blocking = view.Issues.FirstOrDefault(i => i.IsError);
        if (blocking is not null) throw BusinessRuleException.Conflict($"The daily close cannot be confirmed yet: {blocking.Message}");

        foreach (var h in view.Hotels)
        {
            await c.ExecuteAsync(
                """
                UPDATE dbo.daily_close_hotel SET total_rooms_sheet = @total, checks_pm = @Pm, checks_apm = @Apm, checks_selbst = @Selbst
                WHERE close_id = @closeId AND hotel_id = @hotel
                """, new { closeId, hotel = (byte)h.Input.Hotel, total = h.Sheet?.Summary.TotalRooms, h.Checks.Pm, h.Checks.Apm, h.Checks.Selbst }, tx);
        }
        await c.ExecuteAsync(
            "UPDATE dbo.daily_close SET status = 'confirmed', confirmed_by = @user, confirmed_at_utc = SYSUTCDATETIME() WHERE close_id = @closeId",
            new { closeId, user = user.User.UserId }, tx);
        await audit.ConfirmationAsync(c, tx, user, "DAILY_CLOSE_CONFIRM", "daily_close", closeId, new
        {
            date,
            hotels = view.Hotels.Select(h => new { hotelId = (int)h.Input.Hotel, h.Totals, totalRooms = h.Sheet?.Summary.TotalRooms }),
            warnings = view.Issues.Where(i => !i.IsError).Select(i => i.Message),
        });
        await tx.CommitAsync(ct);
        return await LoadAsync(c, null, date);
    }

    public async Task<DailyCloseView> ReopenAsync(CurrentUser user, DateOnly date, string? reason, CancellationToken ct)
    {
        if (!user.IsDirector) throw BusinessRuleException.Forbidden("Only the Hotel Director can reopen a confirmed day.");
        await using var c = await db.OpenAsync(ct);
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
        var closeId = await c.ExecuteScalarAsync<Guid?>(
            "SELECT close_id FROM dbo.daily_close WITH (UPDLOCK) WHERE report_date = @date AND status = 'confirmed'", new { date }, tx)
            ?? throw BusinessRuleException.Conflict("This day is not confirmed.");
        await c.ExecuteAsync(
            """
            UPDATE dbo.daily_close SET status = 'draft', confirmed_by = NULL, confirmed_at_utc = NULL, updated_by = @user, updated_at_utc = SYSUTCDATETIME()
            WHERE close_id = @closeId;
            UPDATE dbo.daily_close_hotel SET total_rooms_sheet = NULL, checks_pm = NULL, checks_apm = NULL, checks_selbst = NULL WHERE close_id = @closeId;
            """, new { closeId, user = user.User.UserId }, tx);
        var clean = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim()[..Math.Min(500, reason.Trim().Length)];
        await audit.DirectorActionAsync(c, tx, user, "DAILY_CLOSE_REOPEN", null, new { date }, clean);
        await tx.CommitAsync(ct);
        return await LoadAsync(c, null, date);
    }

    // ---------- writing ----------

    private static CloseInput Normalise(CloseInput input)
    {
        var supervisors = SupervisorRoles.All.Select(r =>
        {
            var s = input.Supervisors.FirstOrDefault(x => x.Role == r.Role);
            return s is null
                ? new CloseSupervisor(r.Role, null, "", null, null, 0)
                : s with { Name = Trim(s.Name, 100), PauseHours = Math.Max(0, s.PauseHours) };
        }).ToList();
        var hotels = Order.Select(id =>
        {
            var h = input.Hotels.FirstOrDefault(x => x.Hotel == id)
                ?? throw BusinessRuleException.BadRequest($"The daily close has no part for {Hotels.ShortName(id)}.");
            if (h.Cleaners.Count > 80) throw BusinessRuleException.BadRequest("Too many cleaner lines.");
            return h with
            {
                RichterStaffName = Trim(h.RichterStaffName, 100),
                Informationen = h.Informationen.Length > 2000 ? h.Informationen[..2000] : h.Informationen,
                Cleaners = h.Cleaners.Select(c => c with { Name = Trim(c.Name, 100), PauseHours = Math.Max(0, c.PauseHours) }).ToList(),
                Rooms = Enum.GetValues<RoomCategory>().ToDictionary(cat => cat, cat => (IReadOnlyList<string>)h.RoomsOf(cat)
                    .Select(r => r.Trim()).Where(r => r.Length is > 0 and <= 4).Distinct().OrderBy(r => int.TryParse(r, out var n) ? n : int.MaxValue).ToList()),
            };
        }).ToList();
        return new CloseInput(Trim(input.Comment, 300), Math.Round(input.ExtraReinigungHours, 2), supervisors, hotels);
    }

    private static string Trim(string? s, int max)
    {
        var t = (s ?? "").Trim();
        return t.Length > max ? t[..max] : t;
    }

    private async Task<Guid> WriteAsync(SqlConnection c, SqlTransaction tx, CurrentUser user, DateOnly date, CloseInput input)
    {
        if (date > clock.Today()) throw BusinessRuleException.BadRequest("A daily close cannot be entered for a future day.");
        var head = await c.QuerySingleOrDefaultAsync<(Guid CloseId, string Status)>(
            "SELECT close_id, status FROM dbo.daily_close WITH (UPDLOCK, HOLDLOCK) WHERE report_date = @date", new { date }, tx);
        if (head.Status == "confirmed") throw BusinessRuleException.Conflict("This day is already confirmed. The Hotel Director can reopen it.");
        foreach (var r in input.Hotels.SelectMany(h => h.Rooms.Values.SelectMany(x => x).Select(room => (h.Hotel, room))))
        {
            if (!RoomInventory.Contains(r.Hotel, r.room)) throw BusinessRuleException.BadRequest($"{Hotels.ShortName(r.Hotel)}: room {r.room} does not exist.");
        }

        var closeId = head.CloseId;
        var p = new { date, comment = NullIfEmpty(input.Comment), extra = input.ExtraReinigungHours, user = user.User.UserId };
        if (closeId == Guid.Empty)
        {
            closeId = Guid.NewGuid();
            await c.ExecuteAsync(
                "INSERT INTO dbo.daily_close (close_id, report_date, status, comment, extra_reinigung_hours, updated_by) VALUES (@closeId, @date, 'draft', @comment, @extra, @user)",
                new { closeId, p.date, p.comment, p.extra, p.user }, tx);
        }
        else
        {
            await c.ExecuteAsync(
                """
                UPDATE dbo.daily_close SET comment = @comment, extra_reinigung_hours = @extra, updated_by = @user, updated_at_utc = SYSUTCDATETIME()
                WHERE close_id = @closeId;
                DELETE FROM dbo.daily_close_room WHERE close_id = @closeId;
                DELETE FROM dbo.daily_close_cleaner WHERE close_id = @closeId;
                DELETE FROM dbo.daily_close_supervisor WHERE close_id = @closeId;
                DELETE FROM dbo.daily_close_hotel WHERE close_id = @closeId;
                """, new { closeId, p.comment, p.extra, p.user }, tx);
        }

        await c.ExecuteAsync(
            """
            INSERT INTO dbo.daily_close_supervisor (close_id, role, staff_id, person_name, start_time, end_time, pause_hours)
            VALUES (@closeId, @Role, @StaffId, @name, @Start, @End, @PauseHours)
            """, input.Supervisors.Select(s => new { closeId, s.Role, s.StaffId, name = NullIfEmpty(s.Name), Start = Time(s.Start), End = Time(s.End), s.PauseHours }), tx);
        foreach (var h in input.Hotels)
        {
            var hotel = (byte)h.Hotel;
            await c.ExecuteAsync(
                """
                INSERT INTO dbo.daily_close_hotel (close_id, hotel_id, richter_staff_name, flat_rate_day, pauschale_checks, sauber, abzuege, informationen)
                VALUES (@closeId, @hotel, @name, @FlatRateDay, @PauschaleChecks, @Sauber, @Abzuege, @info)
                """, new { closeId, hotel, name = NullIfEmpty(h.RichterStaffName), h.FlatRateDay, PauschaleChecks = Math.Max(0, h.PauschaleChecks),
                    Sauber = Math.Max(0, h.Sauber), Abzuege = Math.Max(0, h.Abzuege), info = NullIfEmpty(h.Informationen) }, tx);
            await c.ExecuteAsync(
                """
                INSERT INTO dbo.daily_close_cleaner (close_id, hotel_id, line_no, staff_id, cleaner_name, gereinigt, ns, rk, ks, start_time, end_time, pause_hours)
                VALUES (@closeId, @hotel, @line, @StaffId, @Name, @Gereinigt, @Ns, @Rk, @Ks, @Start, @End, @PauseHours)
                """, h.Cleaners.Select((x, i) => new
                {
                    closeId, hotel, line = (short)(i + 1), x.StaffId, x.Name, Gereinigt = Math.Max(0, x.Gereinigt), Ns = Math.Max(0, x.Ns),
                    Rk = Math.Max(0, x.Rk), Ks = Math.Max(0, x.Ks), Start = Time(x.Start), End = Time(x.End), x.PauseHours,
                }), tx);
            await c.ExecuteAsync(
                "INSERT INTO dbo.daily_close_room (close_id, hotel_id, category, room_no) VALUES (@closeId, @hotel, @cat, @room)",
                h.Rooms.SelectMany(kv => kv.Value.Select(room => new { closeId, hotel, cat = kv.Key.ToString(), room }))
                    .GroupBy(x => x.room).Select(g => g.First()), tx);
        }
        return closeId;
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static TimeSpan? Time(TimeOnly? t) => t?.ToTimeSpan();

    // ---------- reading ----------

    private async Task<DailyCloseView> LoadAsync(SqlConnection c, SqlTransaction? tx, DateOnly date)
    {
        var head = await c.QuerySingleOrDefaultAsync<HeadRow>(
            """
            SELECT d.close_id AS CloseId, d.report_date AS ReportDate, d.status AS Status, cu.display_label AS ConfirmedBy, d.confirmed_at_utc AS ConfirmedAtUtc,
                   d.comment AS Comment, d.extra_reinigung_hours AS ExtraReinigungHours, uu.display_label AS UpdatedBy, d.updated_at_utc AS UpdatedAtUtc
            FROM dbo.daily_close d JOIN dbo.app_user uu ON uu.user_id = d.updated_by LEFT JOIN dbo.app_user cu ON cu.user_id = d.confirmed_by
            WHERE d.report_date = @date
            """, new { date }, tx);

        var sheets = new Dictionary<HotelId, CloseSheet?>();
        foreach (var h in Order) sheets[h] = await SheetAsync(c, tx, h, date);

        CloseInput input;
        var snapshots = new Dictionary<HotelId, (int? Total, CloseChecks? Checks)>();
        if (head is null)
        {
            var settings = await SettingsService.LoadAsync(c, tx);
            var flat = date.DayOfWeek is >= DayOfWeek.Monday and <= DayOfWeek.Friday;
            var hotels = new List<CloseHotel>();
            foreach (var h in Order)
            {
                // No Service pre-filled: rooms shown Green without a card.
                var ns = sheets[h] is { } s
                    ? (await c.QueryAsync<string>(
                        "SELECT room_no FROM dbo.room_cleaning_status WHERE file_id = @file AND printed = 1 AND final_colour = 'Green' AND cleaned = 0",
                        new { file = s.FileId }, tx)).Distinct().OrderBy(r => int.TryParse(r, out var n) ? n : 0).ToList()
                    : [];
                // Liegezimmer für Folgetag pre-filled: rooms moved out today without a cleaner (cleaned tomorrow).
                var liegezimmer = (await c.QueryAsync<string>(
                        "SELECT room_no FROM dbo.room_move WHERE hotel_id = @hotel AND move_date = @date AND clean_date > @date",
                        new { hotel = (byte)h, date }, tx))
                    .Distinct().Where(r => !ns.Contains(r)).OrderBy(r => int.TryParse(r, out var n) ? n : 0).ToList();
                hotels.Add(new CloseHotel(h, "", flat, settings.PauschaleChecks, 0, 0, "", [],
                    Enum.GetValues<RoomCategory>().ToDictionary(cat => cat, cat => (IReadOnlyList<string>)(cat switch
                    {
                        RoomCategory.NS => ns,
                        RoomCategory.LIEGEZIMMER => liegezimmer,
                        _ => [],
                    }))));
            }
            input = new CloseInput("", 0, SupervisorRoles.All.Select(r => new CloseSupervisor(r.Role, null, "", null, null, 0.5m)).ToList(), hotels);
        }
        else
        {
            var supervisors = (await c.QueryAsync<SupervisorRow>(
                "SELECT role AS Role, staff_id AS StaffId, person_name AS Name, start_time AS StartTime, end_time AS EndTime, pause_hours AS PauseHours FROM dbo.daily_close_supervisor WHERE close_id = @id",
                new { id = head.CloseId }, tx)).ToList();
            var hotelRows = (await c.QueryAsync<HotelRow>(
                """
                SELECT hotel_id AS HotelId, richter_staff_name AS RichterStaffName, flat_rate_day AS FlatRateDay, pauschale_checks AS PauschaleChecks,
                       sauber AS Sauber, abzuege AS Abzuege, informationen AS Informationen, total_rooms_sheet AS TotalRoomsSheet,
                       checks_pm AS ChecksPm, checks_apm AS ChecksApm, checks_selbst AS ChecksSelbst
                FROM dbo.daily_close_hotel WHERE close_id = @id
                """, new { id = head.CloseId }, tx)).ToList();
            var cleaners = (await c.QueryAsync<CleanerRow>(
                """
                SELECT hotel_id AS HotelId, staff_id AS StaffId, cleaner_name AS Name, gereinigt AS Gereinigt, ns AS Ns, rk AS Rk, ks AS Ks,
                       start_time AS StartTime, end_time AS EndTime, pause_hours AS PauseHours
                FROM dbo.daily_close_cleaner WHERE close_id = @id ORDER BY line_no
                """, new { id = head.CloseId }, tx)).ToLookup(x => (HotelId)x.HotelId);
            var rooms = (await c.QueryAsync<(byte Hotel, string Category, string Room)>(
                "SELECT hotel_id, category, room_no FROM dbo.daily_close_room WHERE close_id = @id", new { id = head.CloseId }, tx)).ToList();

            input = new CloseInput(
                head.Comment ?? "",
                head.ExtraReinigungHours,
                SupervisorRoles.All.Select(r => supervisors.FirstOrDefault(s => s.Role == r.Role)?.ToModel() ?? new CloseSupervisor(r.Role, null, "", null, null, 0)).ToList(),
                Order.Select(id =>
                {
                    var hr = hotelRows.FirstOrDefault(x => x.HotelId == (byte)id);
                    snapshots[id] = (hr?.TotalRoomsSheet, hr?.ChecksPm is { } pm ? new CloseChecks(pm, hr.ChecksApm ?? 0, hr.ChecksSelbst ?? 0) : null);
                    return new CloseHotel(id, hr?.RichterStaffName ?? "", hr?.FlatRateDay ?? false, hr?.PauschaleChecks ?? 0, hr?.Sauber ?? 0, hr?.Abzuege ?? 0,
                        hr?.Informationen ?? "", cleaners[id].Select(x => x.ToModel()).ToList(),
                        Enum.GetValues<RoomCategory>().ToDictionary(cat => cat, cat => (IReadOnlyList<string>)rooms
                            .Where(r => r.Hotel == (byte)id && r.Category == cat.ToString()).Select(r => r.Room)
                            .OrderBy(r => int.TryParse(r, out var n) ? n : 0).ToList()));
                }).ToList());
        }

        var confirmed = head?.Status == "confirmed";
        var views = new List<CloseHotelView>();
        foreach (var h in input.Hotels)
        {
            var snap = confirmed ? snapshots.GetValueOrDefault(h.Hotel) : default;
            var counts = snap.Checks ?? await checks.CountAsync(c, tx, h.Hotel, date);
            var moves = await c.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM dbo.room_move WHERE hotel_id = @hotel AND (move_date = @date OR clean_date = @date)", new { hotel = (byte)h.Hotel, date }, tx);
            var weekStart = DailyCloseMath.WeekStart(date);
            var otherFlat = await c.ExecuteScalarAsync<int>(
                """
                SELECT COUNT(*) FROM dbo.daily_close d JOIN dbo.daily_close_hotel h ON h.close_id = d.close_id
                WHERE h.hotel_id = @hotel AND h.flat_rate_day = 1 AND d.report_date BETWEEN @from AND @to AND d.report_date <> @date
                """, new { hotel = (byte)h.Hotel, from = weekStart, to = weekStart.AddDays(6), date }, tx);
            views.Add(new CloseHotelView(h, sheets[h.Hotel], counts, moves, DailyCloseMath.Totals(h, counts))
            {
                TotalRooms = confirmed ? snap.Total : sheets[h.Hotel]?.Summary.TotalRooms,
                OtherFlatRateDays = otherFlat,
            });
        }
        var issues = DailyCloseMath.Issues(input, views.Select(v => (v.Input.Hotel, v.TotalRooms, v.Totals)).ToList(),
            views.ToDictionary(v => v.Input.Hotel, v => v.OtherFlatRateDays));

        return new DailyCloseView(date, head?.Status ?? "new", input, views, issues, head?.UpdatedBy, head?.UpdatedAtUtc, head?.ConfirmedBy, head?.ConfirmedAtUtc);
    }

    /// <summary>The day's latest completed sheet for the hotel.</summary>
    internal static async Task<CloseSheet?> SheetAsync(SqlConnection c, SqlTransaction? tx, HotelId hotel, DateOnly date)
    {
        var row = await c.QueryFirstOrDefaultAsync<SheetRow>(
            """
            SELECT TOP (1) f.file_id AS FileId, f.published_at_local AS PublishedAtLocal, s.total_task_sheets AS TotalTaskSheets, s.total_rooms AS TotalRooms,
                   s.departure_rooms AS DepartureRooms, s.dirty_rooms AS DirtyRooms, s.clean_rooms AS CleanRooms, s.total_credits AS TotalCredits
            FROM dbo.phase1_run_file f JOIN dbo.phase1_run r ON r.run_id = f.run_id JOIN dbo.report_summary s ON s.file_id = f.file_id
            WHERE f.hotel_id = @hotel AND f.report_date = @date AND f.status = 'completed'
            ORDER BY r.created_at_utc DESC
            """, new { hotel = (byte)hotel, date }, tx);
        return row is null
            ? null
            : new CloseSheet(row.FileId, new ReportSummary(row.TotalTaskSheets, row.TotalRooms, row.DepartureRooms, row.DirtyRooms, row.CleanRooms, row.TotalCredits), row.PublishedAtLocal);
    }

    private sealed class HeadRow
    {
        public Guid CloseId { get; set; }
        public DateOnly ReportDate { get; set; }
        public string Status { get; set; } = "";
        public string? ConfirmedBy { get; set; }
        public DateTime? ConfirmedAtUtc { get; set; }
        public string? Comment { get; set; }
        public decimal ExtraReinigungHours { get; set; }
        public string? UpdatedBy { get; set; }
        public DateTime? UpdatedAtUtc { get; set; }
    }

    private sealed class SheetRow
    {
        public Guid FileId { get; set; }
        public DateTime? PublishedAtLocal { get; set; }
        public int TotalTaskSheets { get; set; }
        public int TotalRooms { get; set; }
        public int DepartureRooms { get; set; }
        public int DirtyRooms { get; set; }
        public int CleanRooms { get; set; }
        public decimal TotalCredits { get; set; }
    }

    private sealed class SupervisorRow
    {
        public string Role { get; set; } = "";
        public Guid? StaffId { get; set; }
        public string? Name { get; set; }
        public TimeSpan? StartTime { get; set; }
        public TimeSpan? EndTime { get; set; }
        public decimal PauseHours { get; set; }

        public CloseSupervisor ToModel() =>
            new(Role, StaffId, Name ?? "", StartTime is { } s ? TimeOnly.FromTimeSpan(s) : null, EndTime is { } e ? TimeOnly.FromTimeSpan(e) : null, PauseHours);
    }

    private sealed class HotelRow
    {
        public byte HotelId { get; set; }
        public string? RichterStaffName { get; set; }
        public bool FlatRateDay { get; set; }
        public int PauschaleChecks { get; set; }
        public int Sauber { get; set; }
        public int Abzuege { get; set; }
        public string? Informationen { get; set; }
        public int? TotalRoomsSheet { get; set; }
        public int? ChecksPm { get; set; }
        public int? ChecksApm { get; set; }
        public int? ChecksSelbst { get; set; }
    }

    private sealed class CleanerRow
    {
        public byte HotelId { get; set; }
        public Guid? StaffId { get; set; }
        public string Name { get; set; } = "";
        public int Gereinigt { get; set; }
        public int Ns { get; set; }
        public int Rk { get; set; }
        public int Ks { get; set; }
        public TimeSpan? StartTime { get; set; }
        public TimeSpan? EndTime { get; set; }
        public decimal PauseHours { get; set; }

        public CloseCleaner ToModel() =>
            new(StaffId, Name, Gereinigt, Ns, Rk, Ks, StartTime is { } s ? TimeOnly.FromTimeSpan(s) : null, EndTime is { } e ? TimeOnly.FromTimeSpan(e) : null, PauseHours);
    }
}
