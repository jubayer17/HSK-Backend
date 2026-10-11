using System.Reflection;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HousekeepingParser.Data;

/// <summary>Runs the numbered SQL scripts from migrations/ once each, in order (plan 8).</summary>
public sealed partial class DbMigrator(IOptions<DatabaseOptions> options, ILogger<DbMigrator> log)
{
    public async Task MigrateAsync(CancellationToken ct = default)
    {
        var cs = options.Value.ConnectionString;
        if (options.Value.CreateIfMissing) await CreateDatabaseIfMissingAsync(cs, ct);

        await using var connection = new SqlConnection(cs);
        await connection.OpenAsync(ct);
        await connection.ExecuteAsync("""
            IF OBJECT_ID('dbo.SchemaVersions') IS NULL
                CREATE TABLE dbo.SchemaVersions (
                    script_name VARCHAR(200) NOT NULL CONSTRAINT PK_SchemaVersions PRIMARY KEY,
                    applied_at_utc DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME());
            """);
        var applied = (await connection.QueryAsync<string>("SELECT script_name FROM dbo.SchemaVersions")).ToHashSet();

        foreach (var (name, sql) in Scripts())
        {
            if (applied.Contains(name)) continue;
            await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(ct);
            foreach (var batch in BatchSeparator().Split(sql).Where(b => b.Trim().Length > 0))
                await connection.ExecuteAsync(new CommandDefinition(batch, transaction: tx, commandTimeout: 120, cancellationToken: ct));
            await connection.ExecuteAsync("INSERT INTO dbo.SchemaVersions (script_name) VALUES (@name)", new { name }, tx);
            await tx.CommitAsync(ct);
            log.LogInformation("Applied migration {Script}", name);
        }
    }

    public static IEnumerable<(string Name, string Sql)> Scripts()
    {
        var asm = Assembly.GetExecutingAssembly();
        foreach (var resource in asm.GetManifestResourceNames().Where(n => n.EndsWith(".sql", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var stream = asm.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            var name = resource.Split('.')[^2]; // "HousekeepingParser.Migrations.0001_core.sql" -> "0001_core"
            yield return (name, reader.ReadToEnd());
        }
    }

    private static async Task CreateDatabaseIfMissingAsync(string cs, CancellationToken ct)
    {
        var builder = new SqlConnectionStringBuilder(cs);
        var database = builder.InitialCatalog;
        if (string.IsNullOrEmpty(database)) return;
        builder.InitialCatalog = "master";
        await using var master = new SqlConnection(builder.ConnectionString);
        await master.OpenAsync(ct);
        await master.ExecuteAsync($"IF DB_ID(@database) IS NULL CREATE DATABASE [{database.Replace("]", "]]")}]", new { database });
    }

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex BatchSeparator();
}
