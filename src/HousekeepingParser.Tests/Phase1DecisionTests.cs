using HousekeepingParser.Coloring;
using HousekeepingParser.Core;
using HousekeepingParser.Operations;
using HousekeepingParser.Parsing;
using HousekeepingParser.Runs;
using Xunit;

namespace HousekeepingParser.Tests;

public class Phase1DecisionTests
{
    private static TaskSheetReport EditFirstRow(Func<PdfLine, PdfLine> edit)
    {
        var pages = Fixtures.Pages(Fixtures.Ibis0830).ToList();
        var first = Fixtures.Report(Fixtures.Ibis0830).Rooms.First();
        var page = pages.Single(p => p.PageNumber == first.Position.Page);
        var lines = page.Lines.Select(l => l.Words.Any(w => w.Left >= 10 && w.Left < 38 && w.Text == first.RoomNo) ? edit(l) : l).ToList();
        pages[pages.IndexOf(page)] = page with { Lines = lines };
        return TaskSheetParser.Parse(pages);
    }

    private static PdfLine Words(PdfLine line, IEnumerable<PdfWord> words)
    {
        var list = words.OrderBy(w => w.Left).ToList();
        return line with { Words = list, Text = string.Join(' ', list.Select(w => w.Text)) };
    }

    [Fact]
    public void Missing_child_count_is_zero_but_incomplete_fields_stay_null()
    {
        var report = EditFirstRow(l => Words(l, l.Words.Where(w => w.Left < 239 || w.Left >= 260)));
        Assert.Equal(0, report.Rooms.First().Children);
        var incomplete = EditFirstRow(l => Words(l, l.Words.Where(w => w.Left < 94.5)));
        var row = incomplete.Rooms.First();
        Assert.Null(row.Arrival);
        Assert.Null(row.Departure);
        Assert.Null(row.Adults);
        Assert.Null(row.Children);
    }

    [Fact]
    public void Known_task_typo_is_normalized_without_changing_colour_inputs()
    {
        var report = EditFirstRow(l => Words(l, l.Words.Where(w => w.Left < 260)
            .Append(new PdfWord("Tasks:GLCL", 270, 330, l.Top, l.Bottom))));
        Assert.Equal("Tasks:GLC", report.Rooms.First().Tasks);
    }

    [Fact]
    public void First_page_with_room_numbers_only_is_not_a_data_sheet()
    {
        var original = Fixtures.Pages(Fixtures.Ibis0830);
        var first = original[0];
        var word = new PdfWord("103", 15, 30, 400, 390);
        var cover = first with { Lines = first.Lines.Take(2).Append(new PdfLine("103", 400, 390, 15, 30, [word])).ToList() };
        var pages = new[] { cover }.Concat(original.Select(p => p with { PageNumber = p.PageNumber + 1 })).ToList();
        var report = TaskSheetParser.Parse(pages);
        Assert.Equal(Fixtures.Report(Fixtures.Ibis0830).Rooms.Count, report.Rooms.Count);
        Assert.DoesNotContain(report.Rooms, r => r.Position.Page == 1);
        Assert.Empty(report.InvalidDataPages);
    }

    [Fact]
    public void Missing_required_data_heading_is_a_layout_failure()
    {
        var pages = Fixtures.Pages(Fixtures.Ibis0830).ToList();
        pages[0] = pages[0] with { Lines = pages[0].Lines.Select(l => Words(l, l.Words.Where(w => w.Text != "Resv"))).ToList() };
        Assert.Contains(1, TaskSheetParser.Parse(pages).InvalidDataPages);
    }

    [Fact]
    public void Credits_and_departure_mismatches_warn_without_rejecting_report()
    {
        var report = Fixtures.Report(Fixtures.Ibis0830);
        report = report with { Summary = report.Summary! with { TotalCredits = report.Summary.TotalCredits + 1, DepartureRooms = report.Summary.DepartureRooms + 1 } };
        var warnings = MorningRunPipeline.TotalsWarnings(report).ToList();
        Assert.Contains(warnings, w => w.Code == "CREDITS_TOTAL");
        Assert.Contains(warnings, w => w.Code == "DEPARTURE_COUNT");
        Assert.True(report.IsTaskSheetReport);
    }

    [Theory]
    [InlineData(RhythmType.TwoTages)]
    [InlineData(RhythmType.ThreeTages)]
    [InlineData(RhythmType.OneNight)]
    public void Room_move_always_beats_special_requests_even_without_a_cleaner(RhythmType logic)
    {
        var decision = new ColorRuleService().Decide(new ColourInput("214", "DI", "Due Out", new DateOnly(2026, 10, 20),
            new DateOnly(2026, 10, 24), logic, [new GuestRequest(RequestType.SPECIAL, ColourName.Gold)])
            { RoomMove = new RoomMoveMark(RoomMoveSide.MovedOut, false, "External", false) });
        Assert.Equal(ColourName.Red, decision.Colour);
        Assert.Equal("ROOM_MOVE_RED", decision.ServiceCode);
        Assert.False(decision.Cleaned);
    }

    [Fact]
    public void Weekly_billing_keeps_negative_results_and_separates_hotels()
    {
        var days = new[] { new BillingDay(HotelId.Novotel, new DateOnly(2026, 10, 19), 23, 30),
            new BillingDay(HotelId.Novotel, new DateOnly(2026, 10, 20), 45, 30),
            new BillingDay(HotelId.IbisBudget, new DateOnly(2026, 10, 19), 10, 30),
            new BillingDay(HotelId.Novotel, new DateOnly(2026, 10, 25), 5, 0) };
        var weeks = WeeklyBillingService.Aggregate(days);
        var novotel = Assert.Single(weeks, w => w.Hotel == HotelId.Novotel);
        Assert.Equal(new DateOnly(2026, 10, 19), novotel.WeekStart);
        Assert.Equal(13, novotel.BillableChecks);
        Assert.Equal(-20, Assert.Single(weeks, w => w.Hotel == HotelId.IbisBudget).BillableChecks);
    }

    [Fact]
    public void Month_boundary_does_not_bill_the_same_day_twice()
    {
        var days = new[] { new BillingDay(HotelId.Novotel, new DateOnly(2026, 8, 31), 23, 30),
            new BillingDay(HotelId.Novotel, new DateOnly(2026, 9, 1), 45, 30),
            new BillingDay(HotelId.Novotel, new DateOnly(2026, 9, 7), 3, 0) };
        Assert.Equal(-7, WeeklyBillingService.Month(new DateOnly(2026, 8, 1), days).Sum(w => w.BillableChecks));
        var september = WeeklyBillingService.Month(new DateOnly(2026, 9, 1), days);
        Assert.Equal(18, september.Sum(w => w.BillableChecks));
        Assert.Equal(2, september.Count);
        Assert.Equal(new DateOnly(2026, 9, 1), september[0].From);
    }
}
