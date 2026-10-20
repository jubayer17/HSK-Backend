using Dapper;
using HousekeepingParser.Data;
using HousekeepingParser.Rhythm;
using HousekeepingParser.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace HousekeepingParser.Functions;

public sealed class SystemFunctions(IDbConnectionFactory db, IRhythmService rhythms, IBlobStore blobs, ILogger<SystemFunctions> log)
{
    /// <summary>No data, used by the pipeline smoke test (plan 13).</summary>
    [Function("Health")]
    public async Task<IActionResult> Health([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health")] HttpRequest req)
    {
        try
        {
            await using var c = await db.OpenAsync(req.HttpContext.RequestAborted);
            await c.ExecuteScalarAsync<int>("SELECT 1");
            return Api.Ok(new { status = "ok" });
        }
        catch (Exception ex)
        {
            log.LogError("Health check failed: {ExceptionType}", ex.GetType().Name);
            return Api.Ok(new { status = "unavailable" }, 503);
        }
    }

    /// <summary>1st of the month, 00:05 Berlin: a planned rhythm change becomes active (plan 14).</summary>
    [Function("RhythmActivation")]
    public async Task RhythmActivation([TimerTrigger("0 5 0 1 * *")] TimerInfo timer, FunctionContext ctx)
    {
        var n = await rhythms.ActivateDueAsync(ctx.CancellationToken);
        log.LogInformation("Rhythm activation: {Count} planned change(s) started", n);
    }

    /// <summary>Daily 03:30 Berlin: retention clean-up (plan 5.5).</summary>
    [Function("RetentionCleanup")]
    public async Task RetentionCleanup([TimerTrigger("0 30 3 * * *")] TimerInfo timer, FunctionContext ctx)
    {
        var now = DateTimeOffset.UtcNow;
        var failed = await blobs.DeleteOlderThanAsync(Containers.Failed, now.AddDays(-30), ctx.CancellationToken);
        var incoming = await blobs.DeleteOlderThanAsync(Containers.Incoming, now.AddDays(-90), ctx.CancellationToken);
        await using var c = await db.OpenAsync(ctx.CancellationToken);
        var logs = await c.ExecuteAsync("DELETE FROM dbo.PdfProcessingLog WHERE OccurredAtUtc < DATEADD(DAY, -90, SYSUTCDATETIME())");
        var sessions = await c.ExecuteAsync("DELETE FROM dbo.app_session WHERE expires_at_utc < DATEADD(DAY, -7, SYSUTCDATETIME())");
        // Privacy: employee names on room moves are kept 30 days only.
        var names = await c.ExecuteAsync(
            "UPDATE dbo.room_move SET employee_name = NULL WHERE employee_name IS NOT NULL AND created_at_utc < DATEADD(DAY, -30, SYSUTCDATETIME())");
        var outbox = await blobs.DeleteOlderThanAsync(Containers.Outbox, now.AddDays(-90), ctx.CancellationToken);
        log.LogInformation("Retention: {Names} room-move employee names cleared, {Outbox} outbox emails removed", names, outbox);
        log.LogInformation("Retention: {Failed} refused PDFs, {Incoming} stale uploads, {Logs} processing log rows, {Sessions} sessions removed",
            failed, incoming, logs, sessions);
    }
}
