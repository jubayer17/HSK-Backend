using Dapper;
using HousekeepingParser.Accounts;
using HousekeepingParser.Audit;
using HousekeepingParser.Core;
using HousekeepingParser.Data;
using Microsoft.Data.SqlClient;

namespace HousekeepingParser.Operations;

public sealed record NewRoomMove(HotelId HotelId, string RoomNo, string? ToRoomNo, bool CleanerAvailable, string CleanedByType, string? EmployeeName, string? Note);

public sealed record RoomMove(
    Guid Id, HotelId HotelId, string RoomNo, string? ToRoomNo, bool CleanerAvailable, string CleanedByType, string? EmployeeName, string? Note,
    DateOnly MoveDate, DateOnly CleanDate, string CreatedBy, DateTime CreatedAtUtc)
{
    /// <summary>"cleaned" (move day), "tomorrow" (waiting) or "carried" (next morning).</summary>
    public string Status(DateOnly today) => CleanerAvailable ? "cleaned" : CleanDate > today ? "tomorrow" : "carried";
}

public interface IRoomMoveService
{
    Task<IReadOnlyList<RoomMove>> ListAsync(DateOnly? date, CancellationToken ct);
    Task<RoomMove> AddAsync(CurrentUser user, NewRoomMove move, CancellationToken ct);

    /// <summary>Saves the morning upload's moves inside the run-start transaction.</summary>
    Task InsertAsync(SqlConnection c, SqlTransaction tx, CurrentUser user, NewRoomMove move, DateOnly date, Guid? runId);
}

/// <summary>Room moves: the room left is Red (today, or next morning if no cleaner).</summary>
public sealed class RoomMoveService(IDbConnectionFactory db, IAuditLog audit, IClock clock) : IRoomMoveService
{
    public static NewRoomMove Validate(NewRoomMove m)
    {
        var shortName = Hotels.ShortName(m.HotelId);
        var room = m.RoomNo.Trim();
        var to = string.IsNullOrWhiteSpace(m.ToRoomNo) ? null : m.ToRoomNo.Trim();
        if (!RoomInventory.Contains(m.HotelId, room)) throw BusinessRuleException.BadRequest($"Room {(room.Length > 0 ? room : "?")} does not exist at {shortName}.");
        if (to is not null && !RoomInventory.Contains(m.HotelId, to)) throw BusinessRuleException.BadRequest($"Room {to} does not exist at {shortName}.");
        if (to == room) throw BusinessRuleException.BadRequest("The guest must move to a different room.");
        if (m.CleanedByType is not ("External" or "HotelEmployee")) throw BusinessRuleException.BadRequest("Choose who cleans the room.");
        var employee = m.CleanedByType == "HotelEmployee" ? m.EmployeeName?.Trim() : null;
        if (m.CleanedByType == "HotelEmployee" && string.IsNullOrEmpty(employee))
            throw BusinessRuleException.BadRequest("Enter the name of the hotel employee who cleans the room.");
        if (employee is { Length: > 100 }) throw BusinessRuleException.BadRequest("The employee name is too long.");
        var note = string.IsNullOrWhiteSpace(m.Note) ? null : m.Note.Trim();
        return m with { RoomNo = room, ToRoomNo = to, EmployeeName = employee, Note = note is { Length: > 200 } ? note[..200] : note };
    }

    public async Task<IReadOnlyList<RoomMove>> ListAsync(DateOnly? date, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        return (await c.QueryAsync<Row>(
            """
            SELECT TOP (500) m.move_id AS Id, m.hotel_id AS HotelId, m.room_no AS RoomNo, m.to_room_no AS ToRoomNo, m.cleaner_available AS CleanerAvailable,
                   m.cleaned_by_type AS CleanedByType, m.employee_name AS EmployeeName, m.note AS Note, m.move_date AS MoveDate, m.clean_date AS CleanDate,
                   u.display_label AS CreatedBy, m.created_at_utc AS CreatedAtUtc
            FROM dbo.room_move m JOIN dbo.app_user u ON u.user_id = m.created_by
            WHERE @date IS NULL OR m.move_date = @date OR m.clean_date = @date
            ORDER BY m.created_at_utc DESC
            """, new { date })).Select(r => r.ToModel()).ToList();
    }

    public async Task<RoomMove> AddAsync(CurrentUser user, NewRoomMove move, CancellationToken ct)
    {
        var m = Validate(move);
        var today = clock.Today();
        await using var c = await db.OpenAsync(ct);
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
        var id = await InsertCoreAsync(c, tx, user, m, today, null);
        await tx.CommitAsync(ct);
        return new RoomMove(id, m.HotelId, m.RoomNo, m.ToRoomNo, m.CleanerAvailable, m.CleanedByType, m.EmployeeName, m.Note,
            today, m.CleanerAvailable ? today : today.AddDays(1), user.User.DisplayName, clock.UtcNow);
    }

    public async Task InsertAsync(SqlConnection c, SqlTransaction tx, CurrentUser user, NewRoomMove move, DateOnly date, Guid? runId) =>
        await InsertCoreAsync(c, tx, user, Validate(move), date, runId);

    private async Task<Guid> InsertCoreAsync(SqlConnection c, SqlTransaction tx, CurrentUser user, NewRoomMove m, DateOnly date, Guid? runId)
    {
        var id = Guid.NewGuid();
        await c.ExecuteAsync(
            """
            INSERT INTO dbo.room_move (move_id, hotel_id, move_date, clean_date, room_no, to_room_no, cleaner_available, cleaned_by_type,
                employee_name, note, run_id, created_by)
            VALUES (@id, @hotel, @date, @cleanDate, @RoomNo, @ToRoomNo, @CleanerAvailable, @CleanedByType, @EmployeeName, @Note, @runId, @user)
            """,
            new
            {
                id, hotel = (byte)m.HotelId, date, cleanDate = m.CleanerAvailable ? date : date.AddDays(1), m.RoomNo, m.ToRoomNo,
                m.CleanerAvailable, m.CleanedByType, m.EmployeeName, m.Note, runId, user = user.User.UserId,
            }, tx);
        // No employee name in the audit: names are deleted after 30 days.
        await audit.ConfirmationAsync(c, tx, user, "ROOM_MOVE_ADD", "room_move", id,
            new { hotelId = (int)m.HotelId, m.RoomNo, m.ToRoomNo, m.CleanerAvailable, m.CleanedByType, date });
        return id;
    }

    private sealed class Row
    {
        public Guid Id { get; set; }
        public byte HotelId { get; set; }
        public string RoomNo { get; set; } = "";
        public string? ToRoomNo { get; set; }
        public bool CleanerAvailable { get; set; }
        public string CleanedByType { get; set; } = "";
        public string? EmployeeName { get; set; }
        public string? Note { get; set; }
        public DateOnly MoveDate { get; set; }
        public DateOnly CleanDate { get; set; }
        public string CreatedBy { get; set; } = "";
        public DateTime CreatedAtUtc { get; set; }

        public RoomMove ToModel() => new(Id, (HotelId)HotelId, RoomNo, ToRoomNo, CleanerAvailable, CleanedByType, EmployeeName, Note,
            MoveDate, CleanDate, CreatedBy, CreatedAtUtc);
    }
}
