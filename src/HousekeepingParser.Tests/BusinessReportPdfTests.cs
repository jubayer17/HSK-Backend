using HousekeepingParser.Core;
using HousekeepingParser.Parsing;
using HousekeepingParser.Reports;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Xunit;

namespace HousekeepingParser.Tests;

public class BusinessReportPdfTests
{
    private static readonly IBusinessReportRenderer Renderer = new PdfSharpBusinessReportRenderer();

    private static readonly DateTime Generated = new(2026, 9, 1, 7, 20, 0);

    /// <summary>The client's own example: Novotel, 01.09.2026.</summary>
    public static TaskSheetReportModel TaskSheetSample(int extraCleaners = 0)
    {
        var cleaners = new List<CleanerLine>
        {
            new("Cleaner A", 25, 5, 5, 1, new TimeOnly(8, 0), new TimeOnly(14, 47), 0.5m),
            new("Cleaner B", 16, 10, 2, 0, new TimeOnly(8, 0), new TimeOnly(15, 0), null),
            new("Cleaner C", 16, 11, 7, 1, new TimeOnly(8, 0), new TimeOnly(14, 23), null),
        };
        // Listed but not working that day: empty rows, as on paper.
        cleaners.AddRange(Enumerable.Range(4, 12 + extraCleaners).Select(i => new CleanerLine(i <= 26 ? $"Cleaner {(char)('A' + i - 1)}" : $"Cleaner {i}", null, null, null, null, null, null, null)));

        return new TaskSheetReportModel(
            HotelId.Novotel, new DateOnly(2026, 9, 1), Generated,
            new ReportSummary(3, 99, 36, 99, 0, 196m),
            [
                new("HD", "Bruno", new TimeOnly(6, 30), new TimeOnly(15, 45)),
                new("HDA", "Manuela", new TimeOnly(7, 15), new TimeOnly(15, 43)),
                new("FS", "Sofia", null, null),
                new("RPH FS", "Frederico", new TimeOnly(8, 22), new TimeOnly(15, 43)),
                new("HM", "Tauvir", null, null),
                new("RPH HM", null, null, null),
            ],
            cleaners,
            new DailyTotals(57, 26, 14, 2, 0, 0, 99),
            30, "5T/W", true, 23, -7,
            "Maria Richter", new DateTime(2026, 9, 1, 16, 5, 0));
    }

    public static HandoverModel HandoverSample(int liegezimmer = 12, bool approved = true)
    {
        static IReadOnlyList<string> Rooms(int start, int count) => Enumerable.Range(0, count).Select(i => (start + i * 3).ToString()).ToList();
        var lieg = Enumerable.Range(0, liegezimmer).Select(i => (101 + (i / 26) * 100 + i % 26).ToString()).ToList();
        return new HandoverModel(
            HotelId.Novotel, new DateOnly(2026, 9, 1), Generated, "Frederico",
            Rooms(101, 26), Rooms(201, 14), Rooms(302, 2), Rooms(410, 3), lieg,
            "Zimmer 214 Gast reist erst morgen ab, Wäsche bitte an der Rezeption abholen. Minibar im 4. OG wird am Nachmittag aufgefüllt.",
            approved ? "Maria Richter" : null, approved ? new DateTime(2026, 9, 1, 16, 5, 0) : null);
    }

    public static MonthlyStatementModel MonthlySample()
    {
        var month = new DateOnly(2026, 9, 1);
        var rows = new List<MonthlyRowModel>();
        for (var day = 1; day <= 31; day++)
        {
            var inMonth = day <= 30;
            rows.Add(day switch
            {
                1 => new(1, true, new(57, 23, -7, 0), new(90, 24, -6, 0), null, null, 8m, 8.75m, null, "BdS GR"),
                2 => new(2, true, new(61, 30, 0, 0), new(84, 30, 0, 0), 7.5m, null, 8m, 8.75m, null, null),
                3 => new(3, true, new(48, 26, -4, 1), new(77, 22, -8, 0), 7.5m, 6m, 8m, 8.5m, 0m, null),
                4 => new(4, true, new(66, 35, 5, 0), new(95, 31, 1, 2), 7.5m, 6m, 8m, 8.75m, 2.5m, "Grundreinigung 3. OG"),
                5 => new(5, true, new(52, 28, -2, 0), new(70, 30, 0, 0), null, null, 7.25m, 8.75m, null, null),
                _ => new(day, inMonth, null, null, null, null, null, null, null, null),
            });
        }
        var totals = new MonthlyTotals(new(284, 142, -8, 1), new(416, 137, -13, 2), 22.5m, 12m, 39.25m, 43.5m, 2.5m);
        return new MonthlyStatementModel("Novotel / Ibis Budget Münster", "4900", month, "Sep. 26", rows, totals, 5,
            new DateTime(2026, 9, 6, 8, 15, 0), "Maria Richter", new DateTime(2026, 9, 6, 8, 10, 0));
    }

    private static PdfDocument Open(byte[] pdf, string name)
    {
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));
        // Visual check: HSK_REPORT_OUT=/some/folder dotnet test …
        var outDir = Environment.GetEnvironmentVariable("HSK_REPORT_OUT");
        if (!string.IsNullOrEmpty(outDir)) File.WriteAllBytes(Path.Combine(outDir, name), pdf);
        return PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.Import);
    }

    [Fact]
    public void Task_sheet_report_is_one_portrait_page()
    {
        using var doc = Open(Renderer.TaskSheetReport(TaskSheetSample()), "task-sheet-report.pdf");

        Assert.Equal(1, doc.PageCount);
        Assert.True(doc.Pages[0].Height.Point > doc.Pages[0].Width.Point, "portrait A4");
        Assert.Equal(595, doc.Pages[0].Width.Point, 0);
        Assert.Contains("Task Sheet Report", doc.Info.Title);
        Assert.Contains("Novotel Münster City", doc.Info.Title);
        Assert.Contains("01.09.2026", doc.Info.Title);
    }

    [Fact]
    public void Task_sheet_report_continues_on_a_second_page_with_many_cleaners()
    {
        using var doc = Open(Renderer.TaskSheetReport(TaskSheetSample(extraCleaners: 20)), "task-sheet-report-long.pdf");
        Assert.Equal(2, doc.PageCount);
    }

    [Fact]
    public void Task_sheet_report_renders_without_opera_summary_or_approval()
    {
        var model = TaskSheetSample() with { OperaSummary = null, ApprovedBy = null, ApprovedAtLocal = null, FlatRateDay = false, PauschaleChecks = 0 };
        using var doc = Open(Renderer.TaskSheetReport(model), "task-sheet-report-draft.pdf");
        Assert.Equal(1, doc.PageCount);
    }

    [Fact]
    public void Handover_fits_on_one_page_for_typical_data()
    {
        var model = HandoverSample() with
        {
            NoService = Enumerable.Range(0, 45).Select(i => (101 + i).ToString()).ToList(),
            RoteKarte = Enumerable.Range(0, 15).Select(i => (201 + i).ToString()).ToList(),
            KeinService = Enumerable.Range(0, 15).Select(i => (301 + i).ToString()).ToList(),
            LateCheckOut = Enumerable.Range(0, 10).Select(i => (401 + i).ToString()).ToList(),
            Liegezimmer = Enumerable.Range(0, 30).Select(i => (501 + i).ToString()).ToList(),
        };
        using var doc = Open(Renderer.Handover(model), "handover-full.pdf");
        Assert.Equal(1, doc.PageCount);
        Assert.Contains("Übergabe", doc.Info.Title);
    }

    [Fact]
    public void Handover_sample_is_one_page()
    {
        using var doc = Open(Renderer.Handover(HandoverSample(approved: false)), "handover.pdf");
        Assert.Equal(1, doc.PageCount);
    }

    [Fact]
    public void Handover_with_more_than_30_liegezimmer_gets_a_separate_list()
    {
        using var doc = Open(Renderer.Handover(HandoverSample(liegezimmer: 40)), "handover-40-liegezimmer.pdf");
        Assert.Equal(2, doc.PageCount);
    }

    [Fact]
    public void Handover_grows_onto_more_pages_instead_of_clipping()
    {
        var model = HandoverSample() with { NoService = Enumerable.Range(0, 150).Select(i => (100 + i).ToString()).ToList() };
        using var doc = Open(Renderer.Handover(model), "handover-overflow.pdf");
        Assert.Equal(2, doc.PageCount);
    }

    [Fact]
    public void Monthly_statement_is_one_landscape_page()
    {
        using var doc = Open(Renderer.MonthlyStatement(MonthlySample()), "leistungsnachweis.pdf");

        Assert.Equal(1, doc.PageCount);
        Assert.True(doc.Pages[0].Width.Point > doc.Pages[0].Height.Point, "landscape A4");
        Assert.Equal(842, doc.Pages[0].Width.Point, 0);
        Assert.Contains("Leistungsnachweis", doc.Info.Title);
        Assert.Contains("Sep. 26", doc.Info.Title);
    }
}
