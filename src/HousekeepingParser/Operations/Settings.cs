using System.Globalization;
using System.Net.Mail;
using Dapper;
using HousekeepingParser.Accounts;
using HousekeepingParser.Audit;
using HousekeepingParser.Core;
using HousekeepingParser.Data;
using Microsoft.Data.SqlClient;

namespace HousekeepingParser.Operations;

public enum ReportKind
{
    DAILY_TASK_SHEET,
    DAILY_HANDOVER,
    MONTHLY,
}

/// <summary>Contract prices. Null = not agreed; the dashboard hides € amounts.</summary>
public sealed record Prices(
    decimal? RoomNovotel,
    decimal? RoomIbis,
    decimal? Check,
    decimal? Hausdame,
    decimal? HausdameAssistenz,
    decimal? FloorSupervisor,
    decimal? Hausmann,
    decimal? ExtraReinigung,
    decimal? InternalRoom)
{
    public decimal? Room(HotelId hotel) => hotel == HotelId.Novotel ? RoomNovotel : RoomIbis;
}

public sealed record AppSettings(
    string Objekt,
    string Kostenstelle,
    int PauschaleChecks,
    string PauschaleLabel,
    bool EmailEnabled,
    Prices Prices,
    IReadOnlyDictionary<ReportKind, IReadOnlyList<string>> Recipients);

public interface ISettingsService
{
    Task<AppSettings> GetAsync(CancellationToken ct);
    Task<AppSettings> SaveAsync(CurrentUser user, AppSettings settings, CancellationToken ct);
}

/// <summary>App settings and report recipients; Director only, audited.</summary>
public sealed class SettingsService(IDbConnectionFactory db, IAuditLog audit) : ISettingsService
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static readonly (string Key, Func<Prices, decimal?> Get)[] PriceKeys =
    [
        ("price_room_novotel", p => p.RoomNovotel),
        ("price_room_ibis", p => p.RoomIbis),
        ("price_check", p => p.Check),
        ("rate_hausdame", p => p.Hausdame),
        ("rate_hausdame_assistenz", p => p.HausdameAssistenz),
        ("rate_floor_supervisor", p => p.FloorSupervisor),
        ("rate_hausmann", p => p.Hausmann),
        ("rate_extra_reinigung", p => p.ExtraReinigung),
        ("cost_internal_room", p => p.InternalRoom),
    ];

    public async Task<AppSettings> GetAsync(CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        return await LoadAsync(c, null);
    }

    internal static async Task<AppSettings> LoadAsync(SqlConnection c, SqlTransaction? tx)
    {
        var values = (await c.QueryAsync<(string Key, string? Value)>("SELECT setting_key, setting_value FROM dbo.app_setting", transaction: tx))
            .ToDictionary(r => r.Key, r => r.Value);
        string Text(string key, string fallback) => values.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;
        decimal? Money(string key) => values.TryGetValue(key, out var v) && decimal.TryParse(v, NumberStyles.Number, Inv, out var d) ? d : null;

        var recipients = (await c.QueryAsync<(string Kind, string Email)>(
                "SELECT report_kind, email FROM dbo.report_recipient ORDER BY email", transaction: tx))
            .GroupBy(r => r.Kind).ToDictionary(g => Enum.Parse<ReportKind>(g.Key), g => (IReadOnlyList<string>)g.Select(x => x.Email).ToList());
        foreach (var k in Enum.GetValues<ReportKind>()) recipients.TryAdd(k, []);

        return new AppSettings(
            Text("objekt", "Novotel / Ibis Budget Münster"),
            Text("kostenstelle", "4900"),
            int.TryParse(Text("pauschale_checks", "30"), Inv, out var p) ? p : 30,
            values.GetValueOrDefault("pauschale_label") ?? "",
            !string.Equals(Text("email_enabled", "true"), "false", StringComparison.OrdinalIgnoreCase),
            new Prices(Money("price_room_novotel"), Money("price_room_ibis"), Money("price_check"), Money("rate_hausdame"),
                Money("rate_hausdame_assistenz"), Money("rate_floor_supervisor"), Money("rate_hausmann"), Money("rate_extra_reinigung"),
                Money("cost_internal_room")),
            recipients);
    }

    public async Task<AppSettings> SaveAsync(CurrentUser user, AppSettings s, CancellationToken ct)
    {
        if (!user.IsDirector) throw BusinessRuleException.Forbidden("Only the Hotel Director can change prices and recipients.");
        if (string.IsNullOrWhiteSpace(s.Objekt) || s.Objekt.Length > 200) throw BusinessRuleException.BadRequest("Enter the Objekt (up to 200 characters).");
        if (string.IsNullOrWhiteSpace(s.Kostenstelle) || s.Kostenstelle.Length > 40) throw BusinessRuleException.BadRequest("Enter the Kostenstelle.");
        if (s.PauschaleChecks is < 0 or > 1000) throw BusinessRuleException.BadRequest("Flat-rate checks must be between 0 and 1000.");
        foreach (var (key, get) in PriceKeys)
        {
            if (get(s.Prices) is { } v && (v < 0 || v > 100_000)) throw BusinessRuleException.BadRequest("Prices must be numbers, 0 or more.");
        }
        foreach (var e in s.Recipients.Values.SelectMany(x => x))
        {
            if (!IsEmail(e)) throw BusinessRuleException.BadRequest($"\"{e}\" is not a valid email address.");
        }

        await using var c = await db.OpenAsync(ct);
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
        var before = await LoadAsync(c, tx);
        var rows = new List<(string Key, string? Value)>
        {
            ("objekt", s.Objekt.Trim()),
            ("kostenstelle", s.Kostenstelle.Trim()),
            ("pauschale_checks", s.PauschaleChecks.ToString(Inv)),
            ("pauschale_label", s.PauschaleLabel.Trim()),
            ("email_enabled", s.EmailEnabled ? "true" : "false"),
        };
        rows.AddRange(PriceKeys.Select(k => (k.Key, k.Get(s.Prices) is { } v ? Math.Round(v, 2).ToString(Inv) : null)));
        foreach (var (key, value) in rows)
        {
            await c.ExecuteAsync(
                """
                UPDATE dbo.app_setting SET setting_value = @value, updated_by = @user, updated_at_utc = SYSUTCDATETIME() WHERE setting_key = @key;
                IF @@ROWCOUNT = 0 INSERT INTO dbo.app_setting (setting_key, setting_value, updated_by) VALUES (@key, @value, @user);
                """, new { key, value, user = user.User.UserId }, tx);
        }
        await c.ExecuteAsync("DELETE FROM dbo.report_recipient", transaction: tx);
        foreach (var (kind, list) in s.Recipients)
        {
            foreach (var email in list.Select(e => e.Trim().ToLowerInvariant()).Distinct())
                await c.ExecuteAsync("INSERT INTO dbo.report_recipient (report_kind, email, created_by) VALUES (@kind, @email, @user)",
                    new { kind = kind.ToString(), email, user = user.User.UserId }, tx);
        }
        await audit.DirectorActionAsync(c, tx, user, "SETTINGS_SAVE", null, new { before, after = s }, null);
        await tx.CommitAsync(ct);
        return await GetAsync(ct);
    }

    public static bool IsEmail(string e)
    {
        if (string.IsNullOrWhiteSpace(e) || e.Length > 254 || e.Contains(' ')) return false;
        return MailAddress.TryCreate(e, out var a) && a.Address == e && a.Host.Contains('.');
    }
}
