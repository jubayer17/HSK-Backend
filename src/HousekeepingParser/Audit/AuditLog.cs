using System.Text.Json;
using Dapper;
using HousekeepingParser.Accounts;
using HousekeepingParser.Core;
using HousekeepingParser.Data;
using HousekeepingParser.Parsing;
using Microsoft.Data.SqlClient;

namespace HousekeepingParser.Audit;

public interface IAuditLog
{
    /// <summary>Rule 5: the confirmation itself is logged, in the same transaction as the write.</summary>
    Task ConfirmationAsync(SqlConnection c, SqlTransaction tx, CurrentUser user, string action, string entityType, Guid? entityId, object confirmedValues);

    Task DirectorActionAsync(SqlConnection c, SqlTransaction tx, CurrentUser user, string action, HotelId? hotel, object details, string? reason);
}

public sealed class SqlAuditLog : IAuditLog
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public Task ConfirmationAsync(SqlConnection c, SqlTransaction tx, CurrentUser user, string action, string entityType, Guid? entityId, object confirmedValues) =>
        c.ExecuteAsync(
            """
            INSERT INTO dbo.UserConfirmationLog (session_id, user_id, action, entity_type, entity_id, confirmed_values, confirmations)
            VALUES (@sessionId, @userId, @action, @entityType, @entityId, @values, 2)
            """,
            new { sessionId = user.SessionId, userId = user.User.UserId, action, entityType, entityId, values = JsonSerializer.Serialize(confirmedValues, Json) },
            tx);

    public Task DirectorActionAsync(SqlConnection c, SqlTransaction tx, CurrentUser user, string action, HotelId? hotel, object details, string? reason) =>
        c.ExecuteAsync(
            "INSERT INTO dbo.DirectorAuditLog (user_id, action, hotel_id, details, reason) VALUES (@userId, @action, @hotel, @details, @reason)",
            new { userId = user.User.UserId, action, hotel = (byte?)hotel, details = JsonSerializer.Serialize(details, Json), reason },
            tx);
}

/// <summary>dbo.PdfProcessingLog writer for the client's reader wrapper. Metadata only, never PDF text.</summary>
public sealed class SqlPdfProcessingAuditSink(IDbConnectionFactory db) : IPdfProcessingAuditSink
{
    public async Task RecordAsync(PdfProcessingAuditRecord r, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        await c.ExecuteAsync(
            """
            INSERT INTO dbo.PdfProcessingLog (CorrelationId, SourceBlobName, Outcome, DurationMs, ByteCount, ErrorDetail, OccurredAtUtc)
            VALUES (@CorrelationId, @SourceBlobName, @Outcome, @DurationMs, @ByteCount, @ErrorDetail, @OccurredAtUtc)
            """, r);
    }
}
