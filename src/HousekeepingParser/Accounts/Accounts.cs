using System.Collections.Concurrent;
using System.Security.Cryptography;
using Dapper;
using HousekeepingParser.Core;
using HousekeepingParser.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HousekeepingParser.Accounts;

public enum Role
{
    Director,
    Operations,
}

public sealed record AppUser(Guid UserId, string Username, string DisplayName, Role Role);

/// <summary>The signed-in user of the current request, plus the session for the audit trail.</summary>
public sealed record CurrentUser(AppUser User, Guid SessionId)
{
    public bool IsDirector => User.Role == Role.Director;
}

public sealed class AuthOptions
{
    public const string SectionName = "Auth";
    public const string CookieName = "hsk_session";

    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromHours(12);

    /// <summary>Secure cookie flag. Off only for plain-http local development.</summary>
    public bool SecureCookie { get; set; } = true;

    /// <summary>
    /// Stage 1 simple login (plan page 1; Stage 2 switches to Microsoft Entra sign-in). Seeded into
    /// app_user once, when the table is empty. Passwords come from Key Vault / local.settings.json, never code.
    /// </summary>
    public List<SeedUser> SeedUsers { get; set; } = [];
}

public sealed class SeedUser
{
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Role { get; set; } = "";
    public string Password { get; set; } = "";
}

public static class PasswordHasher
{
    private const int Iterations = 210_000;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256" || !int.TryParse(parts[1], out var iterations)) return false;
        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}

public interface IAuthService
{
    Task<(AppUser User, string Token, DateTime ExpiresUtc)> LoginAsync(string username, string password, CancellationToken ct);
    Task<CurrentUser?> ResolveAsync(string? token, CancellationToken ct);
    Task LogoutAsync(string? token, CancellationToken ct);
    Task SeedUsersAsync(CancellationToken ct);
}

public sealed class AuthService(
    IDbConnectionFactory db,
    IOptions<AuthOptions> options,
    IClock clock,
    ILogger<AuthService> log) : IAuthService
{
    private const int MaxFailures = 5;
    private static readonly TimeSpan LockTime = TimeSpan.FromMinutes(2);
    private static readonly ConcurrentDictionary<string, (int Failures, DateTime LockedUntilUtc)> Attempts = new();

    private const string WrongLogin = "Wrong username or password.";

    public async Task<(AppUser User, string Token, DateTime ExpiresUtc)> LoginAsync(string username, string password, CancellationToken ct)
    {
        var key = username.Trim().ToLowerInvariant();
        if (Attempts.TryGetValue(key, out var a) && a.LockedUntilUtc > clock.UtcNow)
            throw new BusinessRuleException(429, "Too many failed sign-in attempts. Please wait two minutes and try again.");

        await using var c = await db.OpenAsync(ct);
        var row = await c.QuerySingleOrDefaultAsync<UserRow>(
            "SELECT user_id AS UserId, username AS Username, display_label AS DisplayName, role AS Role, password_hash AS PasswordHash " +
            "FROM dbo.app_user WHERE username = @key AND is_active = 1", new { key });

        if (row is null || !PasswordHasher.Verify(password, row.PasswordHash))
        {
            var failures = (a.LockedUntilUtc > clock.UtcNow ? 0 : a.Failures) + 1;
            Attempts[key] = (failures, failures >= MaxFailures ? clock.UtcNow + LockTime : DateTime.MinValue);
            log.LogWarning("Failed sign-in attempt");
            throw new BusinessRuleException(401, WrongLogin);
        }
        Attempts.TryRemove(key, out _);

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var expires = clock.UtcNow + options.Value.SessionLifetime;
        await c.ExecuteAsync(
            "INSERT INTO dbo.app_session (token_hash, user_id, expires_at_utc) VALUES (@hash, @userId, @expires)",
            new { hash = HashToken(token), userId = row.UserId, expires });
        return (row.ToUser(), token, expires);
    }

    public async Task<CurrentUser?> ResolveAsync(string? token, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 100) return null;
        await using var c = await db.OpenAsync(ct);
        var row = await c.QuerySingleOrDefaultAsync<SessionRow>(
            """
            SELECT s.session_id AS SessionId, u.user_id AS UserId, u.username AS Username, u.display_label AS DisplayName,
                   u.role AS Role, u.password_hash AS PasswordHash
            FROM dbo.app_session s JOIN dbo.app_user u ON u.user_id = s.user_id
            WHERE s.token_hash = @hash AND s.revoked_at_utc IS NULL AND s.expires_at_utc > @now AND u.is_active = 1
            """, new { hash = HashToken(token), now = clock.UtcNow });
        return row is null ? null : new CurrentUser(row.ToUser(), row.SessionId);
    }

    public async Task LogoutAsync(string? token, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token)) return;
        await using var c = await db.OpenAsync(ct);
        await c.ExecuteAsync("UPDATE dbo.app_session SET revoked_at_utc = SYSUTCDATETIME() WHERE token_hash = @hash AND revoked_at_utc IS NULL",
            new { hash = HashToken(token) });
    }

    public async Task SeedUsersAsync(CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        if (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.app_user") > 0) return;
        foreach (var u in options.Value.SeedUsers.Where(u => u.Username.Length > 0 && u.Password.Length >= 4))
        {
            if (!Enum.TryParse<Role>(u.Role, out var role)) continue;
            await c.ExecuteAsync(
                "INSERT INTO dbo.app_user (username, display_label, role, password_hash) VALUES (@username, @display, @role, @hash)",
                new { username = u.Username.ToLowerInvariant(), display = u.DisplayName, role = role.ToString(), hash = PasswordHasher.Hash(u.Password) });
            log.LogInformation("Seeded sign-in account with role {Role}", role);
        }
    }

    private static byte[] HashToken(string token) => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));

    private class UserRow
    {
        public Guid UserId { get; set; }
        public string Username { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Role { get; set; } = "";
        public string PasswordHash { get; set; } = "";
        public AppUser ToUser() => new(UserId, Username, DisplayName, Enum.Parse<Role>(Role));
    }

    private sealed class SessionRow : UserRow
    {
        public Guid SessionId { get; set; }
    }
}
