using Dapper;
using HousekeepingParser.Accounts;
using HousekeepingParser.Audit;
using HousekeepingParser.Core;
using HousekeepingParser.Data;
using Microsoft.Data.SqlClient;

namespace HousekeepingParser.Operations;

public sealed record RoomCheck(long Id, HotelId HotelId, DateOnly CheckDate, string? RoomNo, string Role, Guid? StaffId, string CheckedBy, DateTime CreatedAtUtc);

public sealed record NewRoomChecks(HotelId HotelId, DateOnly Date, Guid? StaffId, string Role, string CheckedBy, IReadOnlyList<string> Rooms, int? Count);

public sealed record CheckUsage(string Month, int PmUsed, int? PmLimit, int ApmUsed, int? ApmLimit, int Selbstchecks, IReadOnlyList<string> Warnings);

public sealed record ChecksOverview(DateOnly Date, IReadOnlyList<RoomCheck> Entries, IReadOnlyList<(HotelId Hotel, CloseChecks Checks)> Today, CheckUsage Month);

public interface IRoomCheckService
{
    Task<ChecksOverview> OverviewAsync(DateOnly date, CancellationToken ct);
    Task<ChecksOverview> AddAsync(CurrentUser user, NewRoomChecks entry, CancellationToken ct);
    Task<ChecksOverview> DeleteAsync(CurrentUser user, long id, CancellationToken ct);
    Task<CheckUsage> UsageAsync(SqlConnection c, DateOnly anyDayOfMonth, HotelId? hotel);
    Task<CloseChecks> CountAsync(SqlConnection c, SqlTransaction? tx, HotelId hotel, DateOnly date);
}

/// <summary>Room checks; monthly limit = active PM/APM limits, warning from 90 %.</summary>
public sealed class RoomCheckService(IDbConnectionFactory db, IAuditLog audit, IClock clock) : IRoomCheckService
{
    public static readonly string[] Roles = ["ProjectManager", "AssistantPM", "CleanerSelbstcheck"];

    public async Task<ChecksOverview> OverviewAsync(DateOnly date, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        var entries = (await c.QueryAsync<Row>(
            """
            SELECT TOP (1000) check_id AS Id, hotel_id AS HotelId, check_date AS CheckDate, room_no AS RoomNo, checker_role AS Role,
                   staff_id AS StaffId, checked_by_name AS CheckedBy, created_at_utc AS CreatedAtUtc
            FROM dbo.room_check_log WHERE check_date = @date ORDER BY created_at_utc DESC, check_id DESC
            """, new { date })).Select(r => r.ToModel()).ToList();
        var today = new List<(HotelId, CloseChecks)>();
        foreach (var h in new[] { HotelId.Novotel, HotelId.IbisBudget }) today.Add((h, await CountAsync(c, null, h, date)));
        return new ChecksOverview(date, entries, today, await UsageAsync(c, date, null));
    }

    public async Task<ChecksOverview> AddAsync(CurrentUser user, NewRoomChecks e, CancellationToken ct)
    {
        if (!Roles.Contains(e.Role)) throw BusinessRuleException.BadRequest("Choose who checked: PM, APM or a cleaner.");
        var name = e.CheckedBy.Trim();
        if (name.Length == 0) throw BusinessRuleException.BadRequest("Choose who checked.");
        if (name.Length > 100) throw BusinessRuleException.BadRequest("The name is too long.");
        if (e.Date > clock.Today()) throw BusinessRuleException.BadRequest("Checks cannot be logged for a future day.");
        var rooms = e.Rooms.Select(r => r.Trim()).Where(r => r.Length > 0).Distinct().ToList();
        foreach (var r in rooms)
            if (!RoomInventory.Contains(e.HotelId, r)) throw BusinessRuleException.BadRequest($"Room {r} does not exist at {Hotels.ShortName(e.HotelId)}.");
        var count = rooms.Count > 0 ? rooms.Count : e.Count ?? 0;
        if (count is < 1 or > 300) throw BusinessRuleException.BadRequest("Enter the rooms checked, or a number between 1 and 300.");

        await using var c = await db.OpenAsync(ct);
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
        await EnsureDayOpenAsync(c, tx, e.Date);
        if (e.StaffId is { } sid && await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.staff_member WHERE staff_id = @sid", new { sid }, tx) == 0)
            throw BusinessRuleException.BadRequest("The chosen person is not in the staff list.");
        var rows = Enumerable.Range(0, count).Select(i => new
        {
            hotel = (byte)e.HotelId, e.Date, room = i < rooms.Count ? rooms[i] : null, e.Role, e.StaffId, name, user = user.User.UserId,
        });
        await c.ExecuteAsync(
            """
            INSERT INTO dbo.room_check_log (hotel_id, check_date, room_no, checker_role, staff_id, checked_by_name, created_by)
            VALUES (@hotel, @Date, @room, @Role, @StaffId, @name, @user)
            """, rows, tx);
        await audit.ConfirmationAsync(c, tx, user, "ROOM_CHECKS_ADD", "room_check_log", null,
            new { hotelId = (int)e.HotelId, date = e.Date, e.Role, count, rooms });
        await tx.CommitAsync(ct);
        return await OverviewAsync(e.Date, ct);
    }

    public async Task<ChecksOverview> DeleteAsync(CurrentUser user, long id, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
        var date = await c.ExecuteScalarAsync<DateOnly?>("SELECT check_date FROM dbo.room_check_log WITH (UPDLOCK) WHERE check_id = @id", new { id }, tx)
            ?? throw BusinessRuleException.NotFound("Entry not found.");
        if (date != clock.Today()) throw BusinessRuleException.BadRequest("Only today's entries can be removed.");
        await EnsureDayOpenAsync(c, tx, date);
        await c.ExecuteAsync("DELETE FROM dbo.room_check_log WHERE check_id = @id", new { id }, tx);
        await audit.ConfirmationAsync(c, tx, user, "ROOM_CHECK_DELETE", "room_check_log", null, new { id, date });
        await tx.CommitAsync(ct);
        return await OverviewAsync(date, ct);
    }

    private static async Task EnsureDayOpenAsync(SqlConnection c, SqlTransaction tx, DateOnly date)
    {
        var status = await c.ExecuteScalarAsync<string?>("SELECT status FROM dbo.daily_close WHERE report_date = @date", new { date }, tx);
        if (status == "confirmed") throw BusinessRuleException.Conflict("The daily close for this day is already confirmed.");
    }

    public async Task<CloseChecks> CountAsync(SqlConnection c, SqlTransaction? tx, HotelId hotel, DateOnly date)
    {
        var r = await c.QuerySingleAsync<(int Pm, int Apm, int Selbst)>(
            """
            SELECT COUNT(CASE WHEN checker_role = 'ProjectManager' THEN 1 END), COUNT(CASE WHEN checker_role = 'AssistantPM' THEN 1 END),
                   COUNT(CASE WHEN checker_role = 'CleanerSelbstcheck' THEN 1 END)
            FROM dbo.room_check_log WHERE hotel_id = @hotel AND check_date = @date
            """, new { hotel = (byte)hotel, date }, tx);
        return new CloseChecks(r.Pm, r.Apm, r.Selbst);
    }

    public async Task<CheckUsage> UsageAsync(SqlConnection c, DateOnly anyDayOfMonth, HotelId? hotel)
    {
        var from = Berlin.FirstOfMonth(anyDayOfMonth);
        var to = from.AddMonths(1);
        var used = await c.QuerySingleAsync<(int Pm, int Apm, int Selbst)>(
            """
            SELECT COUNT(CASE WHEN checker_role = 'ProjectManager' THEN 1 END), COUNT(CASE WHEN checker_role = 'AssistantPM' THEN 1 END),
                   COUNT(CASE WHEN checker_role = 'CleanerSelbstcheck' THEN 1 END)
            FROM dbo.room_check_log WHERE check_date >= @from AND check_date < @to AND (@hotel IS NULL OR hotel_id = @hotel)
            """, new { from, to, hotel = (byte?)hotel });
        var limits = (await c.QueryAsync<(string Role, int Total, int People)>(
            """
            SELECT role, SUM(monthly_check_limit), COUNT(*) FROM dbo.staff_member
            WHERE is_active = 1 AND monthly_check_limit IS NOT NULL AND role IN ('ProjectManager', 'AssistantPM')
            GROUP BY role
            """)).ToDictionary(r => r.Role, r => (int?)r.Total);
        var pmLimit = limits.GetValueOrDefault("ProjectManager");
        var apmLimit = limits.GetValueOrDefault("AssistantPM");
        return new CheckUsage(from.ToString("yyyy-MM"), used.Pm, pmLimit, used.Apm, apmLimit, used.Selbst,
            [.. Warnings("PM", used.Pm, pmLimit), .. Warnings("APM", used.Apm, apmLimit)]);
    }

    /// <summary>"External company is near PM check limit for this month".</summary>
    public static IEnumerable<string> Warnings(string label, int used, int? limit)
    {
        if (limit is not > 0) yield break;
        if (used >= limit) yield return $"External company has reached the {label} check limit for this month ({used} of {limit}).";
        else if (used >= limit * 0.9) yield return $"External company is near the {label} check limit for this month ({used} of {limit}).";
    }

    private sealed class Row
    {
        public long Id { get; set; }
        public byte HotelId { get; set; }
        public DateOnly CheckDate { get; set; }
        public string? RoomNo { get; set; }
        public string Role { get; set; } = "";
        public Guid? StaffId { get; set; }
        public string CheckedBy { get; set; } = "";
        public DateTime CreatedAtUtc { get; set; }

        public RoomCheck ToModel() => new(Id, (HotelId)HotelId, CheckDate, RoomNo, Role, StaffId, CheckedBy, CreatedAtUtc);
    }
}
