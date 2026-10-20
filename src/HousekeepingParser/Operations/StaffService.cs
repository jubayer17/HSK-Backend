using Dapper;
using HousekeepingParser.Accounts;
using HousekeepingParser.Audit;
using HousekeepingParser.Core;
using HousekeepingParser.Data;
using Microsoft.Data.SqlClient;

namespace HousekeepingParser.Operations;

public sealed record StaffMember(Guid Id, HotelId? HotelId, string Name, string Company, string Role, int? MonthlyCheckLimit, bool Active);

public interface IStaffService
{
    Task<IReadOnlyList<StaffMember>> ListAsync(CancellationToken ct);
    Task<StaffMember> SaveAsync(CurrentUser user, Guid? id, StaffMember value, CancellationToken ct);
}

/// <summary>Staff list with monthly check limits.</summary>
public sealed class StaffService(IDbConnectionFactory db, IAuditLog audit) : IStaffService
{
    public static readonly string[] Roles =
        ["Cleaner", "Hausdame", "HausdameAssistenz", "FloorSupervisor", "Hausmann", "RphFloorSupervisor", "RphHausmann", "ProjectManager", "AssistantPM"];

    public async Task<IReadOnlyList<StaffMember>> ListAsync(CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        return (await c.QueryAsync<Row>(
            """
            SELECT staff_id AS Id, hotel_id AS HotelId, display_name AS Name, company AS Company, role AS Role,
                   monthly_check_limit AS MonthlyCheckLimit, is_active AS Active
            FROM dbo.staff_member ORDER BY role, display_name
            """)).Select(r => r.ToModel()).ToList();
    }

    public async Task<StaffMember> SaveAsync(CurrentUser user, Guid? id, StaffMember v, CancellationToken ct)
    {
        if (!user.IsDirector) throw BusinessRuleException.Forbidden("Only the Hotel Director can change the staff list.");
        var name = v.Name.Trim();
        if (name.Length == 0) throw BusinessRuleException.BadRequest("Enter the name.");
        if (name.Length > 100) throw BusinessRuleException.BadRequest("The name is too long.");
        if (v.Company is not ("External" or "Hotel")) throw BusinessRuleException.BadRequest("Unknown company.");
        if (!Roles.Contains(v.Role)) throw BusinessRuleException.BadRequest("Unknown role.");
        if (v.MonthlyCheckLimit is < 0) throw BusinessRuleException.BadRequest("The check limit must be 0 or more.");
        var limit = v.Role is "ProjectManager" or "AssistantPM" ? v.MonthlyCheckLimit : null;

        await using var c = await db.OpenAsync(ct);
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
        var staffId = id ?? Guid.NewGuid();
        var args = new { staffId, hotel = (byte?)v.HotelId, name, v.Company, v.Role, limit, v.Active, user = user.User.UserId };
        if (id is null)
        {
            await c.ExecuteAsync(
                """
                INSERT INTO dbo.staff_member (staff_id, hotel_id, display_name, company, role, monthly_check_limit, is_active, created_by)
                VALUES (@staffId, @hotel, @name, @Company, @Role, @limit, @Active, @user)
                """, args, tx);
        }
        else
        {
            var n = await c.ExecuteAsync(
                """
                UPDATE dbo.staff_member SET hotel_id = @hotel, display_name = @name, company = @Company, role = @Role,
                    monthly_check_limit = @limit, is_active = @Active, updated_at_utc = SYSUTCDATETIME()
                WHERE staff_id = @staffId
                """, args, tx);
            if (n == 0) throw BusinessRuleException.NotFound("Staff member not found.");
        }
        await audit.ConfirmationAsync(c, tx, user, id is null ? "STAFF_ADD" : "STAFF_UPDATE", "staff_member", staffId,
            new { name, v.Role, v.Company, hotelId = (int?)v.HotelId, limit, v.Active });
        await tx.CommitAsync(ct);
        return new StaffMember(staffId, v.HotelId, name, v.Company, v.Role, limit, v.Active);
    }

    private sealed class Row
    {
        public Guid Id { get; set; }
        public byte? HotelId { get; set; }
        public string Name { get; set; } = "";
        public string Company { get; set; } = "";
        public string Role { get; set; } = "";
        public int? MonthlyCheckLimit { get; set; }
        public bool Active { get; set; }

        public StaffMember ToModel() => new(Id, (HotelId?)HotelId, Name, Company, Role, MonthlyCheckLimit, Active);
    }
}
