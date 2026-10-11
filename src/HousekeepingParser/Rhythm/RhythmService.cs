using Dapper;
using HousekeepingParser.Accounts;
using HousekeepingParser.Audit;
using HousekeepingParser.Core;
using HousekeepingParser.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace HousekeepingParser.Rhythm;

public sealed record RhythmPeriod(RhythmType Rhythm, DateOnly EffectiveFrom, string? Reason);

public sealed record HotelRhythm(HotelId HotelId, RhythmPeriod? Active, RhythmPeriod? Scheduled);

public sealed record RhythmHistoryEntry(
    Guid Id, HotelId HotelId, string Action, RhythmType Rhythm, DateOnly EffectiveFrom, string? Reason, string By, DateTime At);

public interface IRhythmService
{
    Task<IReadOnlyList<HotelRhythm>> GetAllAsync(CancellationToken ct);
    Task<HotelRhythm> SetAsync(CurrentUser user, HotelId hotel, RhythmType rhythm, DateOnly effectiveFrom, string? reason, CancellationToken ct);
    Task<HotelRhythm> CancelScheduledAsync(CurrentUser user, HotelId hotel, string? reason, CancellationToken ct);
    Task<IReadOnlyList<RhythmHistoryEntry>> HistoryAsync(CancellationToken ct);

    /// <summary>Turns a planned change whose 1st of the month has come into the active rhythm (timer at 00:05, and on every read).</summary>
    Task<int> ActivateDueAsync(CancellationToken ct);

    /// <summary>The guest's frozen rhythm (rule 9): found, or created with the rhythm valid on the arrival date.</summary>
    Task<RhythmType> GuestRhythmAsync(SqlConnection c, HotelId hotel, string roomNo, DateOnly arrival, DateOnly? departure);
}

/// <summary>Rules from plan 9.6 and rule 10, checked here (API) and again by constraints in the database.</summary>
public sealed class RhythmService(IDbConnectionFactory db, IAuditLog audit, IClock clock, ILogger<RhythmService> log) : IRhythmService
{
    private static readonly HotelId[] DisplayOrder = [HotelId.Novotel, HotelId.IbisBudget];

    public async Task<IReadOnlyList<HotelRhythm>> GetAllAsync(CancellationToken ct)
    {
        await ActivateDueAsync(ct);
        await using var c = await db.OpenAsync(ct);
        var rows = (await c.QueryAsync<ConfigRow>(
            "SELECT config_id AS ConfigId, hotel_id AS HotelId, rhythm_type AS RhythmType, effective_from AS EffectiveFrom, status AS Status, reason AS Reason " +
            "FROM dbo.rhythm_configuration WHERE status IN ('ACTIVE', 'SCHEDULED')")).ToList();
        return DisplayOrder.Select(h => ToHotelRhythm(h, rows)).ToList();
    }

    public async Task<HotelRhythm> SetAsync(CurrentUser user, HotelId hotel, RhythmType rhythm, DateOnly effectiveFrom, string? reason, CancellationToken ct)
    {
        RequireDirector(user, "change the cleaning rhythm");
        if (effectiveFrom.Day != 1) throw BusinessRuleException.BadRequest("A rhythm can only start on the 1st of a month.");
        await ActivateDueAsync(ct);

        var thisMonth = Berlin.FirstOfMonth(clock.Today());
        await using var c = await db.OpenAsync(ct);
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
        var rows = (await c.QueryAsync<ConfigRow>(
            "SELECT config_id AS ConfigId, hotel_id AS HotelId, rhythm_type AS RhythmType, effective_from AS EffectiveFrom, status AS Status, reason AS Reason " +
            "FROM dbo.rhythm_configuration WITH (UPDLOCK, HOLDLOCK) WHERE hotel_id = @hotel AND status IN ('ACTIVE', 'SCHEDULED')",
            new { hotel = (byte)hotel }, tx)).ToList();

        var active = rows.SingleOrDefault(r => r.Status == "ACTIVE");
        if (rows.Any(r => r.Status == "SCHEDULED"))
            throw BusinessRuleException.Conflict("There is already a planned change for this hotel. Cancel it first.");

        string status;
        if (active is null)
        {
            if (effectiveFrom < thisMonth) throw BusinessRuleException.BadRequest("The start date cannot be in the past.");
            status = effectiveFrom == thisMonth ? "ACTIVE" : "SCHEDULED";
        }
        else
        {
            if (effectiveFrom <= thisMonth) throw BusinessRuleException.BadRequest("A change must start on the 1st of a future month.");
            status = "SCHEDULED";
        }

        var id = Guid.NewGuid();
        var cleanReason = Clean(reason);
        await c.ExecuteAsync(
            """
            INSERT INTO dbo.rhythm_configuration (config_id, hotel_id, rhythm_type, effective_from, status, reason, created_by, activated_at_utc)
            VALUES (@id, @hotel, @rhythm, @effectiveFrom, @status, @reason, @userId, CASE WHEN @status = 'ACTIVE' THEN SYSUTCDATETIME() END)
            """,
            new { id, hotel = (byte)hotel, rhythm = rhythm.ToString(), effectiveFrom, status, reason = cleanReason, userId = user.User.UserId }, tx);

        var values = new { hotelId = hotel, rhythm, effectiveFrom, reason = cleanReason };
        await audit.ConfirmationAsync(c, tx, user, "RHYTHM_SET", "rhythm_configuration", id, values);
        await audit.DirectorActionAsync(c, tx, user, status == "ACTIVE" ? "RHYTHM_SET" : "RHYTHM_SCHEDULED", hotel, values, cleanReason);
        await tx.CommitAsync(ct);

        log.LogInformation("Rhythm {Status} for hotel {Hotel} from {EffectiveFrom}", status, (byte)hotel, effectiveFrom);
        return (await GetAllAsync(ct)).Single(r => r.HotelId == hotel);
    }

    public async Task<HotelRhythm> CancelScheduledAsync(CurrentUser user, HotelId hotel, string? reason, CancellationToken ct)
    {
        RequireDirector(user, "cancel a rhythm change");
        await ActivateDueAsync(ct);
        var today = clock.Today();

        await using var c = await db.OpenAsync(ct);
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
        var scheduled = await c.QuerySingleOrDefaultAsync<ConfigRow>(
            "SELECT config_id AS ConfigId, hotel_id AS HotelId, rhythm_type AS RhythmType, effective_from AS EffectiveFrom, status AS Status, reason AS Reason " +
            "FROM dbo.rhythm_configuration WITH (UPDLOCK, HOLDLOCK) WHERE hotel_id = @hotel AND status = 'SCHEDULED'",
            new { hotel = (byte)hotel }, tx)
            ?? throw BusinessRuleException.NotFound("There is no planned change to cancel.");

        if (today >= scheduled.EffectiveFrom)
            throw BusinessRuleException.Conflict("A planned change can only be cancelled until the day before it starts.");

        var cleanReason = Clean(reason);
        await c.ExecuteAsync("UPDATE dbo.rhythm_configuration SET status = 'CANCELLED' WHERE config_id = @id", new { id = scheduled.ConfigId }, tx);
        await c.ExecuteAsync(
            "INSERT INTO dbo.rhythm_change_cancellation (config_id, reason, cancelled_by) VALUES (@id, @reason, @userId)",
            new { id = scheduled.ConfigId, reason = cleanReason, userId = user.User.UserId }, tx);

        var values = new { hotelId = hotel, rhythm = Enum.Parse<RhythmType>(scheduled.RhythmType), effectiveFrom = scheduled.EffectiveFrom, reason = cleanReason };
        await audit.ConfirmationAsync(c, tx, user, "RHYTHM_CANCEL", "rhythm_configuration", scheduled.ConfigId, values);
        await audit.DirectorActionAsync(c, tx, user, "RHYTHM_CANCELLED", hotel, values, cleanReason);
        await tx.CommitAsync(ct);

        return (await GetAllAsync(ct)).Single(r => r.HotelId == hotel);
    }

    public async Task<IReadOnlyList<RhythmHistoryEntry>> HistoryAsync(CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        var rows = await c.QueryAsync<HistoryRow>(
            """
            SELECT TOP (200) a.audit_id AS Id, a.hotel_id AS HotelId, a.action AS Action,
                   JSON_VALUE(a.details, '$.rhythm') AS Rhythm, JSON_VALUE(a.details, '$.effectiveFrom') AS EffectiveFrom,
                   a.reason AS Reason, CASE WHEN a.action = 'RHYTHM_ACTIVATED' THEN N'System' ELSE u.display_label END AS ByLabel,
                   a.at_utc AS At
            FROM dbo.DirectorAuditLog a JOIN dbo.app_user u ON u.user_id = a.user_id
            WHERE a.action LIKE 'RHYTHM[_]%'
            ORDER BY a.at_utc DESC
            """);
        return rows.Select(r => new RhythmHistoryEntry(
            r.Id,
            (HotelId)r.HotelId,
            r.Action switch
            {
                "RHYTHM_SET" => "set",
                "RHYTHM_SCHEDULED" => "scheduled",
                "RHYTHM_CANCELLED" => "cancelled",
                _ => "activated",
            },
            Enum.Parse<RhythmType>(r.Rhythm),
            DateOnly.Parse(r.EffectiveFrom[..10]),
            r.Reason,
            r.ByLabel,
            DateTime.SpecifyKind(r.At, DateTimeKind.Utc))).ToList();
    }

    public async Task<int> ActivateDueAsync(CancellationToken ct)
    {
        var today = clock.Today();
        await using var c = await db.OpenAsync(ct);
        var due = (await c.QueryAsync<ConfigRow>(
            "SELECT config_id AS ConfigId, hotel_id AS HotelId, rhythm_type AS RhythmType, effective_from AS EffectiveFrom, status AS Status, reason AS Reason, created_by AS CreatedBy " +
            "FROM dbo.rhythm_configuration WHERE status = 'SCHEDULED' AND effective_from <= @today", new { today })).ToList();

        foreach (var row in due)
        {
            await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
            var changed = await c.ExecuteAsync(
                """
                UPDATE dbo.rhythm_configuration SET status = 'SUPERSEDED' WHERE hotel_id = @hotel AND status = 'ACTIVE';
                UPDATE dbo.rhythm_configuration SET status = 'ACTIVE', activated_at_utc = SYSUTCDATETIME()
                WHERE config_id = @id AND status = 'SCHEDULED';
                """, new { hotel = row.HotelId, id = row.ConfigId }, tx);
            if (changed > 0)
            {
                await c.ExecuteAsync(
                    "INSERT INTO dbo.DirectorAuditLog (user_id, action, hotel_id, details, reason) VALUES (@userId, 'RHYTHM_ACTIVATED', @hotel, @details, N'Planned change started')",
                    new
                    {
                        userId = row.CreatedBy,
                        hotel = row.HotelId,
                        details = System.Text.Json.JsonSerializer.Serialize(new { rhythm = row.RhythmType, effectiveFrom = row.EffectiveFrom.ToString("yyyy-MM-dd") }),
                    }, tx);
            }
            await tx.CommitAsync(ct);
            log.LogInformation("Activated planned rhythm for hotel {Hotel}", row.HotelId);
        }
        return due.Count;
    }

    public async Task<RhythmType> GuestRhythmAsync(SqlConnection c, HotelId hotel, string roomNo, DateOnly arrival, DateOnly? departure)
    {
        var p = new { hotel = (byte)hotel, roomNo, arrival, departure };
        var existing = await c.QuerySingleOrDefaultAsync<string>(
            "SELECT rhythm_type FROM dbo.guest_rhythm_assignment WHERE hotel_id = @hotel AND room_no = @roomNo AND arrival_date = @arrival", p);
        if (existing is not null)
        {
            // Only the check-out date may move (stay extension); the rhythm never changes.
            await c.ExecuteAsync(
                "UPDATE dbo.guest_rhythm_assignment SET departure_date = @departure WHERE hotel_id = @hotel AND room_no = @roomNo AND arrival_date = @arrival AND ISNULL(departure_date, '19000101') <> ISNULL(@departure, '19000101')", p);
            return Enum.Parse<RhythmType>(existing);
        }

        // The rhythm that was valid on the arrival date. A guest who arrived before the first set-up gets the current one.
        var config = await c.QueryFirstOrDefaultAsync<ConfigRow>(
            """
            SELECT TOP (1) config_id AS ConfigId, rhythm_type AS RhythmType FROM dbo.rhythm_configuration
            WHERE hotel_id = @hotel AND status IN ('ACTIVE', 'SUPERSEDED') AND effective_from <= @arrival
            ORDER BY effective_from DESC
            """, p)
            ?? await c.QueryFirstOrDefaultAsync<ConfigRow>(
                "SELECT TOP (1) config_id AS ConfigId, rhythm_type AS RhythmType FROM dbo.rhythm_configuration WHERE hotel_id = @hotel AND status = 'ACTIVE'", p)
            ?? throw BusinessRuleException.Conflict($"No active cleaning rhythm for {Hotels.ShortName(hotel)}.");

        try
        {
            await c.ExecuteAsync(
                "INSERT INTO dbo.guest_rhythm_assignment (hotel_id, room_no, arrival_date, departure_date, rhythm_type, config_id) VALUES (@hotel, @roomNo, @arrival, @departure, @rhythm, @configId)",
                new { p.hotel, roomNo, arrival, departure, rhythm = config.RhythmType, configId = config.ConfigId });
            return Enum.Parse<RhythmType>(config.RhythmType);
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            // Another run created it at the same moment: use that row.
            return Enum.Parse<RhythmType>(await c.QuerySingleAsync<string>(
                "SELECT rhythm_type FROM dbo.guest_rhythm_assignment WHERE hotel_id = @hotel AND room_no = @roomNo AND arrival_date = @arrival", p));
        }
    }

    private static HotelRhythm ToHotelRhythm(HotelId hotel, List<ConfigRow> rows)
    {
        RhythmPeriod? Period(string status) => rows.Where(r => r.HotelId == (byte)hotel && r.Status == status)
            .Select(r => new RhythmPeriod(Enum.Parse<RhythmType>(r.RhythmType), r.EffectiveFrom, r.Reason)).SingleOrDefault();
        return new HotelRhythm(hotel, Period("ACTIVE"), Period("SCHEDULED"));
    }

    private static void RequireDirector(CurrentUser user, string what)
    {
        if (!user.IsDirector) throw BusinessRuleException.Forbidden($"Only the Hotel Director can {what}.");
    }

    private static string? Clean(string? reason)
    {
        var r = reason?.Trim();
        return string.IsNullOrEmpty(r) ? null : r.Length > 500 ? r[..500] : r;
    }

    private sealed class ConfigRow
    {
        public Guid ConfigId { get; set; }
        public byte HotelId { get; set; }
        public string RhythmType { get; set; } = "";
        public DateOnly EffectiveFrom { get; set; }
        public string Status { get; set; } = "";
        public string? Reason { get; set; }
        public Guid CreatedBy { get; set; }
    }

    private sealed class HistoryRow
    {
        public Guid Id { get; set; }
        public byte HotelId { get; set; }
        public string Action { get; set; } = "";
        public string Rhythm { get; set; } = "";
        public string EffectiveFrom { get; set; } = "";
        public string? Reason { get; set; }
        public string ByLabel { get; set; } = "";
        public DateTime At { get; set; }
    }
}
