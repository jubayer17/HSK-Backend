using HousekeepingParser.Parsing;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace HousekeepingParser.Tests;

/// <summary>
/// Runs the real reader (Java + OpenDataLoader JAR) through the client's wrapper on the real sample
/// files. Those files contain staff names and are never committed, so this only runs on a machine
/// that has them in the repository root and the JAR in server/tools/opendataloader (see README).
/// </summary>
public class RealReaderTests
{
    private static readonly string Root = FindRepoRoot();
    private static readonly string Jar = Path.Combine(Root, "server", "tools", "opendataloader", "opendataloader-pdf.jar");

    public static TheoryData<string, int, int> Samples => new()
    {
        { "task_sheet_report_1909957450.PDF", 103, 6 },
        { "task_sheet_report_1912057967.PDF", 74, 4 },
        { "task_sheet_report_1912132626.PDF", 112, 5 },
        { "task_sheet_report_1912144053.PDF", 116, 4 },
    };

    [SampleFileTheory]
    [MemberData(nameof(Samples))]
    public async Task Real_sample_files_give_the_totals(string file, int rooms, int sheets)
    {
        var path = Path.Combine(Root, file);
        if (!File.Exists(path) || !File.Exists(Jar)) return; // nothing to check on this machine

        var sink = new MemorySink();
        var extractor = new CompliantOpenDataLoaderExtractor(
            Options.Create(new OpenDataLoaderOptions { JavaExecutable = JavaPath(), JarPath = Jar, Timeout = TimeSpan.FromSeconds(90) }),
            NullLogger<CompliantOpenDataLoaderExtractor>.Instance,
            new TelemetryClient(new TelemetryConfiguration()),
            sink);

        var (pdf, _) = PdfBytes.RepairLineEndings(await File.ReadAllBytesAsync(path));
        var result = await extractor.ExtractAsync(pdf, "fixture.pdf", CancellationToken.None);
        var report = TaskSheetParser.Parse(result.Pages);

        Assert.Equal(rooms, report.Rooms.Count);
        Assert.Equal(sheets, report.Sheets.Count);
        Assert.Equal(rooms, report.Summary!.TotalRooms);
        Assert.All(result.Pages, p => Assert.True(p.Height > 700, "page sizes come from the PDF"));
        Assert.Equal("SUCCESS", Assert.Single(sink.Records).Outcome);
        Assert.False(Directory.EnumerateDirectories(Path.GetTempPath(), "odl-" + result.CorrelationId).Any(), "temp folder is shredded");
    }

    [SampleFileTheory]
    [InlineData("task_sheet_report_1912144053.PDF")]
    [InlineData("task_sheet_report_1909957450.PDF")]
    public async Task Real_sample_can_be_marked_without_changing_the_pages(string file)
    {
        var path = Path.Combine(Root, file);
        if (!File.Exists(path) || !File.Exists(Jar)) return;

        var extractor = new CompliantOpenDataLoaderExtractor(
            Options.Create(new OpenDataLoaderOptions { JavaExecutable = JavaPath(), JarPath = Jar, Timeout = TimeSpan.FromSeconds(90) }),
            NullLogger<CompliantOpenDataLoaderExtractor>.Instance, new TelemetryClient(new TelemetryConfiguration()), new MemorySink());
        var (pdf, _) = PdfBytes.RepairLineEndings(await File.ReadAllBytesAsync(path));
        var report = TaskSheetParser.Parse((await extractor.ExtractAsync(pdf, "fixture.pdf", CancellationToken.None)).Pages);
        var palette = Enum.GetValues<HousekeepingParser.Core.ColourName>();
        var dots = report.Rooms.Select((r, i) => new HousekeepingParser.PdfMarking.DotPlacement(
            r.Position.Page, r.Position.Left, r.Position.Top, r.Position.Bottom, palette[i % palette.Length])).ToList();

        var marked = new HousekeepingParser.PdfMarking.PdfSharpMarker().AddDots(pdf, dots);

        Assert.Equal(PdfPageSizes.Read(pdf).Count, PdfPageSizes.Read(marked).Count);
        // Optional visual check on a developer machine: HSK_MARK_OUT=/some/folder dotnet test …
        var outDir = Environment.GetEnvironmentVariable("HSK_MARK_OUT");
        if (!string.IsNullOrEmpty(outDir)) await File.WriteAllBytesAsync(Path.Combine(outDir, "marked-" + Path.GetFileNameWithoutExtension(file) + ".pdf"), marked);
    }

    private static string JavaPath()
    {
        var home = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrEmpty(home) && File.Exists(Path.Combine(home, "bin", "java"))) return Path.Combine(home, "bin", "java");
        return "java";
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "server", "migrations"))) dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }

    private sealed class MemorySink : IPdfProcessingAuditSink
    {
        public List<PdfProcessingAuditRecord> Records { get; } = [];

        public Task RecordAsync(PdfProcessingAuditRecord record, CancellationToken ct)
        {
            Records.Add(record);
            return Task.CompletedTask;
        }
    }
}

/// <summary>Skipped unless the real sample PDFs and the reader JAR are present.</summary>
public sealed class SampleFileTheoryAttribute : TheoryAttribute
{
    public SampleFileTheoryAttribute()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "server", "migrations"))) dir = dir.Parent;
        var ok = dir is not null
            && File.Exists(Path.Combine(dir.FullName, "server", "tools", "opendataloader", "opendataloader-pdf.jar"))
            && File.Exists(Path.Combine(dir.FullName, "task_sheet_report_1912144053.PDF"));
        if (!ok) Skip = "Real sample PDFs or the OpenDataLoader JAR are not on this machine.";
    }
}
