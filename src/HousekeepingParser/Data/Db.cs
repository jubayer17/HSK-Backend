using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace HousekeepingParser.Data;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>
    /// In Azure: "Server=tcp:….database.windows.net;Database=…;Authentication=Active Directory Default"
    /// (Managed Identity, no password — rule 12). Locally: SQL Server in Docker.
    /// </summary>
    public string ConnectionString { get; set; } = "";

    /// <summary>Local development only: create the database if it does not exist.</summary>
    public bool CreateIfMissing { get; set; }

    /// <summary>Run the numbered scripts in migrations/ at start-up (the pipeline runs them in Azure).</summary>
    public bool MigrateOnStartup { get; set; } = true;
}

public interface IDbConnectionFactory
{
    Task<SqlConnection> OpenAsync(CancellationToken ct = default);
}

public sealed class SqlConnectionFactory : IDbConnectionFactory
{
    private readonly string _connectionString;
    private readonly SqlRetryLogicBaseProvider _retry;

    public SqlConnectionFactory(IOptions<DatabaseOptions> options)
    {
        _connectionString = options.Value.ConnectionString;
        // Built-in retry for short outages (plan 4).
        _retry = SqlConfigurableRetryFactory.CreateExponentialRetryProvider(new SqlRetryLogicOption
        {
            NumberOfTries = 4,
            DeltaTime = TimeSpan.FromSeconds(1),
            MaxTimeInterval = TimeSpan.FromSeconds(15),
        });
        DapperSetup.Register();
    }

    public async Task<SqlConnection> OpenAsync(CancellationToken ct = default)
    {
        var connection = new SqlConnection(_connectionString) { RetryLogicProvider = _retry };
        await connection.OpenAsync(ct);
        return connection;
    }
}

public static class DapperSetup
{
    private static int _registered;

    public static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1) return;
        SqlMapper.AddTypeHandler(new DateOnlyHandler());
        SqlMapper.AddTypeHandler(new NullableDateOnlyHandler());
    }

    private sealed class DateOnlyHandler : SqlMapper.TypeHandler<DateOnly>
    {
        public override void SetValue(IDbDataParameter parameter, DateOnly value)
        {
            parameter.DbType = DbType.Date;
            parameter.Value = value.ToDateTime(TimeOnly.MinValue);
        }

        public override DateOnly Parse(object value) => value switch
        {
            DateOnly d => d,
            DateTime dt => DateOnly.FromDateTime(dt),
            _ => DateOnly.Parse(value.ToString()!),
        };
    }

    private sealed class NullableDateOnlyHandler : SqlMapper.TypeHandler<DateOnly?>
    {
        public override void SetValue(IDbDataParameter parameter, DateOnly? value)
        {
            parameter.DbType = DbType.Date;
            parameter.Value = value is { } d ? d.ToDateTime(TimeOnly.MinValue) : DBNull.Value;
        }

        public override DateOnly? Parse(object value) => value switch
        {
            null or DBNull => null,
            DateOnly d => d,
            DateTime dt => DateOnly.FromDateTime(dt),
            _ => DateOnly.Parse(value.ToString()!),
        };
    }
}
