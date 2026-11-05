using HousekeepingParser.Coloring;
using HousekeepingParser.PdfMarking;
using HousekeepingParser.Runs;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace HousekeepingParser.Tests;

public sealed partial class DatabaseTests
{
    [RealPdfDockerFact]
    public async Task Real_opera_pdfs_pass_upload_checks_and_produce_marked_downloads()
    {
        // Legacy JSON snapshots hid the real reader's split header. Exercise the actual upload
        // pipeline with private PDFs and a throw-away database, without changing users' run history.
        var e = await CreateAsync();
        await SetBothRhythms(e);
        var uploads = new[] { "task_sheet_report_1912132626.PDF", "task_sheet_report_1912144053.PDF" }
            .Select(file => new UploadedFile(file, File.ReadAllBytes(Path.Combine(RealReaderTests.SampleDirectory, file)))).ToList();
        var runId = await e.Runs.CreateAsync(e.Operations, uploads, [], default);
        var pipeline = new MorningRunPipeline(e.Db, new RunStore(e.Db), e.Blobs, RealReaderTests.Extractor(),
            new ColorRuleService(), e.Rhythms, new PdfSharpMarker(), new PdfSharpReportRenderer(), e.Clock,
            Options.Create(new ValidationOptions { EnforceSheetAge = false }), NullLogger<MorningRunPipeline>.Instance);
        var outcomes = new List<ReadOutcome>();
        foreach (var file in await pipeline.ListFilesAsync(runId, default))
            outcomes.Add(await pipeline.ReadFileAsync(file, default));
        Assert.All(outcomes, outcome => Assert.True(outcome.Ok));
        var accepted = await pipeline.ResolveAsync(runId, outcomes, default);
        Assert.Equal(2, accepted.Count);
        foreach (var outcome in outcomes) await pipeline.SaveAsync(outcome, default);
        foreach (var file in accepted)
        {
            await pipeline.ColourAsync(file, default);
            await pipeline.MarkAsync(file, default);
        }
        await pipeline.FinishAsync(runId, default);

        var run = (await e.Runs.GetAsync(runId, default))!;
        Assert.Equal("completed", run.Status);
        Assert.Equal("2026-08-30", run.ReportDate);
        Assert.Equal(new[] { 112, 116 }, run.Hotels.Select(h => h.ParsedRooms!.Value).OrderBy(n => n));
        Assert.All(run.Hotels, hotel =>
        {
            Assert.Equal("completed", hotel.Status);
            Assert.Null(hotel.RejectReason);
            Assert.True(hotel.MarkedPdfAvailable);
            Assert.All(hotel.Steps, step => Assert.Equal("done", step.Status));
        });
    }
}

public sealed class RealPdfDockerFactAttribute : FactAttribute
{
    public RealPdfDockerFactAttribute()
    {
        if (!DockerCheck.Available) Skip = "Docker is unavailable; real-PDF SQL pipeline was not tested.";
        else if (new[] { "1912132626", "1912144053" }.Any(id =>
            !File.Exists(Path.Combine(RealReaderTests.SampleDirectory, $"task_sheet_report_{id}.PDF"))))
            Skip = "Private Opera PDFs are absent; real-PDF SQL pipeline was not tested.";
    }
}
