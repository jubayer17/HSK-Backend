using HousekeepingParser.Core;
using HousekeepingParser.Parsing;
using Xunit;

namespace HousekeepingParser.Tests;

public class HeaderParsingTests
{
    [Fact]
    public void Separate_print_time_above_title_is_a_valid_opera_header()
    {
        // Real Opera PDFs have a 5.3-point baseline difference between title and print time.
        var report = Parse(Hotel(), Line("07:23", 560, 759.64), Title());

        Assert.True(report.IsTaskSheetReport);
        Assert.NotNull(report.Header);
        Assert.Equal(HotelId.Novotel, report.Header.HotelId);
        Assert.Equal(new DateTime(2026, 7, 10, 7, 23, 0), report.Header.PublishedAtLocal);
    }

    [Fact]
    public void Combined_title_and_time_from_legacy_reader_remains_valid()
    {
        var report = Parse(Hotel(), Line("Task Sheet Report 07:23", 244, 754.34));

        Assert.True(report.IsTaskSheetReport);
        Assert.Equal(new DateTime(2026, 7, 10, 7, 23, 0), report.Header!.PublishedAtLocal);
    }

    [Fact]
    public void Missing_time_does_not_claim_the_title_is_missing()
    {
        var report = Parse(Hotel(), Title());

        Assert.True(report.IsTaskSheetReport);
        Assert.Null(report.Header);
    }

    [Theory]
    [InlineData("99:99", 560, 759.64)] // Invalid clock value must not throw.
    [InlineData("07:23", 560, 720)] // A table time is too far below the title.
    [InlineData("07:23", 20, 759.64)] // A left-hand time is not the print time.
    public void Unrelated_or_invalid_time_cannot_complete_the_header(string time, double x, double y)
    {
        var report = Parse(Hotel(), Line(time, x, y), Title());

        Assert.True(report.IsTaskSheetReport);
        Assert.Null(report.Header);
    }

    [Fact]
    public void Title_mentioned_in_body_does_not_make_another_document_an_opera_report()
    {
        var report = Parse(Hotel(), Line("Task Sheet Report 07:23", 244, 400));

        Assert.False(report.IsTaskSheetReport);
        Assert.Null(report.Header);
    }

    [Fact]
    public void Ambiguous_print_times_do_not_complete_the_header()
    {
        var report = Parse(Hotel(), Line("07:23", 560, 759.64), Line("08:23", 510, 759.64), Title());

        Assert.True(report.IsTaskSheetReport);
        Assert.Null(report.Header);
    }

    private static TaskSheetReport Parse(params PdfLine[] lines) => TaskSheetParser.Parse([
        new PdfPage(1, 612, 792, lines.OrderByDescending(l => l.CentreY).ToArray())]);

    private static PdfLine Hotel() => Line("Novotel Münster City 10.07.26", 3, 776.52);
    private static PdfLine Title() => Line("Task Sheet Report", 244, 754.34);
    private static PdfLine Line(string text, double x, double y) => new(
        text, y + 4, y - 4, x, x + text.Length * 5,
        [new PdfWord(text, x, x + text.Length * 5, y + 4, y - 4)]);
}
