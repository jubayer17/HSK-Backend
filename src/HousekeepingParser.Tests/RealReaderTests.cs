using System.Security.Cryptography;
using System.Text.Json;
using HousekeepingParser.Parsing;
using HousekeepingParser.Runs;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace HousekeepingParser.Tests;

public class RealReaderTests
{
    public static string SampleDirectory => Environment.GetEnvironmentVariable("HSK_SAMPLE_DIR")
        ?? Path.Combine(FindRepoRoot(), "samples", "opera");
    public const string FinalSample = "task_sheet_report_1914628800.PDF";
    public const string FinalExpected = "task_sheet_report_1914628800.expected.json";

    public static TheoryData<string, string> Samples => new()
    {
        { "task_sheet_report_1909957450.PDF", Fixtures.Novotel0710 },
        { "task_sheet_report_1912057967.PDF", Fixtures.Novotel0828 },
        { "task_sheet_report_1912132626.PDF", Fixtures.Novotel0830 },
        { "task_sheet_report_1912144053.PDF", Fixtures.Ibis0830 },
    };

    [SampleFileTheory]
    [MemberData(nameof(Samples))]
    public async Task Dotnet_reader_matches_real_report_room_fields(string file, string expected)
    {
        var (pdf, _) = PdfBytes.RepairLineEndings(await File.ReadAllBytesAsync(Path.Combine(SampleDirectory, file)));
        var result = await Extractor().ExtractAsync(pdf, "private-fixture.pdf", default);
        var actual = TaskSheetParser.Parse(result.Pages);
        var reference = Fixtures.Report(expected);
        // Room parsing can succeed while the upload is still rejected: verify the complete real-PDF header too.
        Assert.True(actual.IsTaskSheetReport);
        Assert.Equal(reference.Header, actual.Header);
        Assert.Equal(reference.Summary, actual.Summary);
        Assert.Equal(reference.Rooms.Count, actual.Rooms.Count);
        Assert.Empty(actual.InvalidDataPages);
        foreach (var room in reference.Rooms)
        {
            var parsed = Assert.Single(actual.Rooms, r => r.SheetNo == room.SheetNo && r.RoomNo == room.RoomNo);
            Assert.Equal((room.RoomStatus, room.Credits, room.ReservationStatus, room.Arrival, room.Departure, room.Adults, room.Children ?? 0),
                (parsed.RoomStatus, parsed.Credits, parsed.ReservationStatus, parsed.Arrival, parsed.Departure, parsed.Adults, parsed.Children ?? 0));
        }
        Assert.NotEmpty(result.Quality!);
        Assert.Contains(result.Quality!, q => q.Parser == "Camelot.Stream");
    }

    [SampleFileTheory]
    [MemberData(nameof(Samples))]
    public async Task Pdfpig_fallback_also_reads_real_report(string file, string expected)
    {
        var (pdf, _) = PdfBytes.RepairLineEndings(await File.ReadAllBytesAsync(Path.Combine(SampleDirectory, file)));
        var result = await Extractor(true).ExtractAsync(pdf, "private-fixture.pdf", default);
        var actual = TaskSheetParser.Parse(result.Pages);
        Assert.True(actual.IsTaskSheetReport);
        Assert.Equal(Fixtures.Report(expected).Header, actual.Header);
        Assert.Equal(Fixtures.Report(expected).Summary, actual.Summary);
        Assert.Equal(Fixtures.Report(expected).Rooms.Count, actual.Rooms.Count);
        Assert.All(result.Quality!, q => Assert.Equal("PdfPig.GetWords", q.Parser));
    }

    [FinalSampleFact]
    public async Task Required_october_sample_matches_independently_verified_fields()
    {
        var path = Path.Combine(SampleDirectory, FinalSample);
        var expectedPath = Path.Combine(SampleDirectory, FinalExpected);
        // Strict acceptance fails for missing inputs. Never substitute old PDFs or parser-generated expectations.
        Assert.True(File.Exists(path), $"Required sample missing: {path}");
        Assert.True(File.Exists(expectedPath), $"Independently checked expected fields missing: {expectedPath}");
        var original = await File.ReadAllBytesAsync(path);
        var expected = JsonSerializer.Deserialize<FinalSampleExpectation>(await File.ReadAllTextAsync(expectedPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal(expected.Sha256.ToUpperInvariant(), Convert.ToHexString(SHA256.HashData(original)));
        Assert.Equal(56, expected.Rooms.Count);
        var (pdf, _) = PdfBytes.RepairLineEndings(original);
        var report = TaskSheetParser.Parse((await Extractor().ExtractAsync(pdf, "required-fixture.pdf", default)).Pages);
        Assert.Empty(report.InvalidDataPages);
        Assert.Equal(28, report.Rooms.Count(r => r.Position.Page == 2));
        Assert.Equal(28, report.Rooms.Count(r => r.Position.Page == 4));
        Assert.Equal(56, report.Rooms.Count);
        Assert.Equal(109, report.Summary!.TotalRooms);
        Assert.Equal(expected.Rooms.OrderBy(r => r.Position.Page).ThenBy(r => r.RoomNo),
            report.Rooms.OrderBy(r => r.Position.Page).ThenBy(r => r.RoomNo));
        Assert.Contains(MorningRunPipeline.TotalsWarnings(report), w => w.Code == "ROOM_COUNT");
    }

    internal static DotNetPdfExtractor Extractor(bool fallback = false) => new(
        Options.Create(new PdfParserOptions { Timeout = TimeSpan.FromSeconds(90), ForcePdfPigFallback = fallback }),
        NullLogger<DotNetPdfExtractor>.Instance, new TelemetryClient(new TelemetryConfiguration()), new MemorySink());

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HousekeepingParser.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Backend repository root not found.");
    }

    private sealed record FinalSampleExpectation(string Sha256, IReadOnlyList<ParsedRoom> Rooms);
    private sealed class MemorySink : IPdfProcessingAuditSink
    {
        public Task RecordAsync(PdfProcessingAuditRecord record, CancellationToken ct) => Task.CompletedTask;
    }
}

public sealed class SampleFileTheoryAttribute : TheoryAttribute
{
    public SampleFileTheoryAttribute()
    {
        var files = new[] { "1909957450", "1912057967", "1912132626", "1912144053" };
        if (files.Any(n => !File.Exists(Path.Combine(RealReaderTests.SampleDirectory, $"task_sheet_report_{n}.PDF"))))
            Skip = "Private regression PDFs absent from samples/opera; no real-PDF result is claimed.";
    }
}

public sealed class FinalSampleFactAttribute : FactAttribute
{
    public FinalSampleFactAttribute()
    {
        var strict = Environment.GetEnvironmentVariable("HSK_REQUIRE_FINAL_SAMPLE") == "true";
        if (!strict && (!File.Exists(Path.Combine(RealReaderTests.SampleDirectory, RealReaderTests.FinalSample))
            || !File.Exists(Path.Combine(RealReaderTests.SampleDirectory, RealReaderTests.FinalExpected))))
            Skip = "FINAL ACCEPTANCE PENDING: required October PDF and independently verified fields are missing. Run tools/Test-FinalSample.ps1 for strict acceptance.";
    }
}
