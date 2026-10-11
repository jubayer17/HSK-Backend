using HousekeepingParser.Runs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;

namespace HousekeepingParser.Functions;

/// <summary>
/// The morning run as one Durable Functions orchestration (plan 9.1): both PDFs are read in parallel
/// (fan-out / fan-in), progress is saved after each step, and failed steps are retried.
/// </summary>
public sealed class MorningRunOrchestration(MorningRunPipeline pipeline)
{
    private static readonly TaskOptions Retry = TaskOptions.FromRetryPolicy(new RetryPolicy(3, TimeSpan.FromSeconds(5)));

    [Function(nameof(MorningRun))]
    public static async Task MorningRun([OrchestrationTrigger] TaskOrchestrationContext context)
    {
        var runId = context.GetInput<Guid>();
        var log = context.CreateReplaySafeLogger(nameof(MorningRun));
        try
        {
            var files = await context.CallActivityAsync<List<Guid>>(nameof(ListRunFiles), runId, Retry);
            var read = await Task.WhenAll(files.Select(f => context.CallActivityAsync<ReadOutcome>(nameof(ReadFile), f, Retry)));
            var accepted = await context.CallActivityAsync<List<Guid>>(nameof(ResolveFiles), new ResolveInput(runId, read.ToList()), Retry);
            var toSave = read.Where(r => accepted.Contains(r.FileId)).ToList();

            await Task.WhenAll(toSave.Select(r => context.CallActivityAsync(nameof(SaveFile), r, Retry)));
            await Task.WhenAll(accepted.Select(f => context.CallActivityAsync(nameof(ColourFile), f, Retry)));
            await Task.WhenAll(accepted.Select(f => context.CallActivityAsync(nameof(MarkFile), f, Retry)));
            await context.CallActivityAsync(nameof(FinishRun), runId, Retry);
        }
        catch (TaskFailedException)
        {
            log.LogError("Run {RunId} stopped after a failed step", runId);
            await context.CallActivityAsync(nameof(FailRun), runId, Retry);
        }
    }

    public sealed record ResolveInput(Guid RunId, List<ReadOutcome> Outcomes);

    [Function(nameof(ListRunFiles))]
    public async Task<List<Guid>> ListRunFiles([ActivityTrigger] Guid runId, FunctionContext ctx) =>
        (await pipeline.ListFilesAsync(runId, ctx.CancellationToken)).ToList();

    [Function(nameof(ReadFile))]
    public Task<ReadOutcome> ReadFile([ActivityTrigger] Guid fileId, FunctionContext ctx) =>
        pipeline.ReadFileAsync(fileId, ctx.CancellationToken);

    [Function(nameof(ResolveFiles))]
    public async Task<List<Guid>> ResolveFiles([ActivityTrigger] ResolveInput input, FunctionContext ctx) =>
        (await pipeline.ResolveAsync(input.RunId, input.Outcomes, ctx.CancellationToken)).ToList();

    [Function(nameof(SaveFile))]
    public Task SaveFile([ActivityTrigger] ReadOutcome outcome, FunctionContext ctx) =>
        pipeline.SaveAsync(outcome, ctx.CancellationToken);

    [Function(nameof(ColourFile))]
    public Task ColourFile([ActivityTrigger] Guid fileId, FunctionContext ctx) =>
        pipeline.ColourAsync(fileId, ctx.CancellationToken);

    [Function(nameof(MarkFile))]
    public Task MarkFile([ActivityTrigger] Guid fileId, FunctionContext ctx) =>
        pipeline.MarkAsync(fileId, ctx.CancellationToken);

    [Function(nameof(FinishRun))]
    public Task FinishRun([ActivityTrigger] Guid runId, FunctionContext ctx) =>
        pipeline.FinishAsync(runId, ctx.CancellationToken);

    [Function(nameof(FailRun))]
    public Task FailRun([ActivityTrigger] Guid runId, FunctionContext ctx) =>
        pipeline.FailAsync(runId, ctx.CancellationToken);
}
