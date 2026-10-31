using HousekeepingParser.Coloring;
using HousekeepingParser.Core;
using HousekeepingParser.Operations;
using HousekeepingParser.Reports;
using Xunit;

namespace HousekeepingParser.Tests;

/// <summary>1-Night colour logic test cases.</summary>
public class OneNightLogicTests
{
    private readonly ColorRuleService _rules = new();

    private ColourDecision Decide(string checkIn, string report, string status, bool cleaner = false, bool request = false, bool pending = false, RoomMoveMark? move = null)
    {
        var requests = request ? new[] { new GuestRequest(RequestType.CARD) } : [];
        return _rules.Decide(new ColourInput("214", "DI", status, DateOnly.Parse(checkIn), DateOnly.Parse(report), RhythmType.OneNight, requests)
        {
            CleanerAvailable = cleaner,
            PendingService = pending,
            RoomMove = move,
        });
    }

    [Theory]
    // check-in, report, status, cleaner, request, pending → colour, code, cleaned
    [InlineData("2026-08-30", "2026-08-30", "Stayover", false, false, false, ColourName.Red, "ARRIVAL_RED", false)]
    [InlineData("2026-08-29", "2026-08-30", "Stayover", false, false, false, ColourName.Green, "STAYOVER_GREEN", false)]
    [InlineData("2026-08-29", "2026-08-30", "Stayover", true, true, false, ColourName.Green, "STAYOVER_GREEN_CLEANED", true)]
    [InlineData("2026-08-29", "2026-08-30", "Stayover", false, true, false, ColourName.Blue, "STAYOVER_BLUE", false)]
    [InlineData("2026-08-29", "2026-09-01", "Stayover", true, false, true, ColourName.Green, "STAYOVER_GREEN_CLEANED", true)]
    [InlineData("2026-08-29", "2026-09-01", "Stayover", false, false, true, ColourName.Blue, "STAYOVER_BLUE", false)]
    [InlineData("2026-08-29", "2026-08-30", "Departed", false, false, false, ColourName.Red, "DEPARTURE_RED", true)]
    [InlineData("2026-08-30", "2026-08-30", "Day Use", false, false, false, ColourName.Red, "DEPARTURE_RED", true)]
    [InlineData("2026-08-29", "2026-08-30", "Due Out", false, false, false, ColourName.Red, "DEPARTURE_RED", true)]
    public void Spec_test_table(string checkIn, string report, string status, bool cleaner, bool request, bool pending, ColourName colour, string code, bool cleaned)
    {
        var d = Decide(checkIn, report, status, cleaner, request, pending);
        Assert.Equal(colour, d.Colour);
        Assert.Equal(code, d.ServiceCode);
        Assert.Equal(cleaned, d.Cleaned);
    }

    [Fact]
    public void Room_move_is_red_and_counts_as_a_full_clean()
    {
        var d = Decide("2026-08-29", "2026-08-30", "Stayover", move: new RoomMoveMark(RoomMoveSide.MovedOut, true, "External", false));
        Assert.Equal(ColourName.Red, d.Colour);
        Assert.Equal("ROOM_MOVE_RED", d.ServiceCode);
        Assert.True(d.Cleaned);
        Assert.Equal("External", d.CleanedByType);
    }

    [Fact]
    public void Special_request_overrides_a_pending_blue()
    {
        var d = _rules.Decide(new ColourInput("214", "DI", "Stayover", new DateOnly(2026, 8, 29), new DateOnly(2026, 9, 1), RhythmType.OneNight,
            [new GuestRequest(RequestType.SPECIAL, ColourName.Blue)]) { PendingService = true });
        Assert.Equal(ColourName.Blue, d.Colour);
        Assert.Equal("SPECIAL", d.ServiceCode);
        Assert.True(d.Cleaned);
    }

    [Fact]
    public void No_cleaner_answer_means_no_cleaner()
    {
        var d = Decide("2026-08-29", "2026-08-30", "Stayover", request: true);
        Assert.Equal(ColourName.Blue, d.Colour);
    }
}

/// <summary>Room moves in the 2-/3-Tages rhythms.</summary>
public class RoomMoveRuleTests
{
    private readonly ColorRuleService _rules = new();

    private ColourDecision Move(RoomMoveMark mark, params GuestRequest[] requests) =>
        _rules.Decide(new ColourInput("214", "DI", "Stayover", new DateOnly(2026, 8, 28), new DateOnly(2026, 8, 30), RhythmType.TwoTages, requests) { RoomMove = mark });

    [Fact]
    public void Cleaner_available_cleans_today()
    {
        var d = Move(new RoomMoveMark(RoomMoveSide.MovedOut, true, "HotelEmployee", false));
        Assert.Equal((ColourName.Red, "ROOM_MOVE_RED", true, "HotelEmployee"), (d.Colour!.Value, d.ServiceCode, d.Cleaned, d.CleanedByType));
    }

    [Fact]
    public void No_cleaner_waits_for_tomorrow_and_is_red_then()
    {
        var today = Move(new RoomMoveMark(RoomMoveSide.MovedOut, false, "External", false));
        Assert.Equal(ColourName.Red, today.Colour);
        Assert.False(today.Cleaned);
        Assert.Contains("tomorrow", today.Reason);

        var tomorrow = Move(new RoomMoveMark(RoomMoveSide.MovedOut, true, "External", true));
        Assert.True(tomorrow.Cleaned);
        Assert.Contains("yesterday", tomorrow.Reason);
    }

    [Fact]
    public void Room_moved_into_is_red_but_not_cleaned()
    {
        var d = Move(new RoomMoveMark(RoomMoveSide.MovedIn, false, "External", false));
        Assert.Equal((ColourName.Red, "ROOM_MOVE_IN", false), (d.Colour!.Value, d.ServiceCode, d.Cleaned));
    }

    [Fact]
    public void Daily_cleaning_does_not_replace_the_room_move_clean()
    {
        var d = Move(new RoomMoveMark(RoomMoveSide.MovedOut, true, "External", false), new GuestRequest(RequestType.DAILY_TODAY));
        Assert.Equal("ROOM_MOVE_RED", d.ServiceCode);
    }
}

/// <summary>Client's Task Sheet 01.09.26 and September Leistungsnachweis.</summary>
public class DailyCloseMathTests
{
    private static CloseHotel Novotel0109(bool flat = true) => new(
        HotelId.Novotel, "Frederico", flat, 30, 0, 0, "",
        [
            new CloseCleaner(null, "Cleaner A", 25, 5, 5, 1, new TimeOnly(8, 0), new TimeOnly(14, 47), 0.5m),
            new CloseCleaner(null, "Cleaner B", 16, 10, 2, 0, new TimeOnly(8, 0), new TimeOnly(15, 0), 0.5m),
            new CloseCleaner(null, "Cleaner C", 16, 11, 7, 1, new TimeOnly(8, 0), new TimeOnly(14, 23), 0.5m),
        ],
        Enum.GetValues<RoomCategory>().ToDictionary(c => c, _ => (IReadOnlyList<string>)[]));

    [Fact]
    public void Totals_match_the_paper_sheet()
    {
        var t = DailyCloseMath.Totals(Novotel0109(), new CloseChecks(15, 8, 3));
        Assert.Equal((57, 26, 14, 2, 0, 0, 99), (t.Gereinigt, t.Ns, t.Rk, t.Ks, t.Sauber, t.Liegezimmer, t.Summe));
        Assert.Equal(23, t.ChecksActual);
        Assert.Equal(-7, t.ZuBerechnen); // 23 − 30, "zu berechnen: -7"
    }

    [Fact]
    public void No_flat_rate_on_a_day_that_is_not_a_flat_rate_day()
    {
        var t = DailyCloseMath.Totals(Novotel0109(flat: false), new CloseChecks(20, 9, 0));
        Assert.Equal(29, t.ZuBerechnen); // Leistungsnachweis day 4: 29 to bill
    }

    [Fact]
    public void Hours_are_end_minus_start_minus_break()
    {
        Assert.Equal(8.75m, DailyCloseMath.Hours(new TimeOnly(6, 30), new TimeOnly(15, 45), 0.5m)); // Hausdame, 01.09.26
        Assert.Null(DailyCloseMath.Hours(new TimeOnly(15, 0), new TimeOnly(8, 0), 0));
        Assert.Null(DailyCloseMath.Hours(null, new TimeOnly(8, 0), 0));
    }

    [Fact]
    public void Sum_must_equal_the_opera_total()
    {
        var input = new CloseInput("", 0, [], [Novotel0109()]);
        var totals = DailyCloseMath.Totals(input.Hotels[0], new CloseChecks(0, 0, 0));
        Assert.DoesNotContain(DailyCloseMath.Issues(input, [(HotelId.Novotel, 99, totals)]), i => i.IsError);
        var wrong = DailyCloseMath.Issues(input, [(HotelId.Novotel, 100, totals)]);
        Assert.Contains(wrong, i => i.IsError && i.Message.Contains("100 rooms"));
    }

    [Fact]
    public void A_room_in_two_lists_is_an_error()
    {
        var h = Novotel0109() with
        {
            Rooms = new Dictionary<RoomCategory, IReadOnlyList<string>> { [RoomCategory.NS] = ["214"], [RoomCategory.RK] = ["214"] },
        };
        var issues = DailyCloseMath.Issues(new CloseInput("", 0, [], [h]), []);
        Assert.Contains(issues, i => i.IsError && i.Message.Contains("room 214"));
    }

    [Fact]
    public void A_sixth_flat_rate_day_in_one_week_is_a_warning()
    {
        var input = new CloseInput("", 0, [], [Novotel0109()]);
        var fifth = DailyCloseMath.Issues(input, [], new Dictionary<HotelId, int> { [HotelId.Novotel] = 4 });
        Assert.DoesNotContain(fifth, i => i.Message.Contains("5T/W"));

        var sixth = DailyCloseMath.Issues(input, [], new Dictionary<HotelId, int> { [HotelId.Novotel] = 5 });
        Assert.Contains(sixth, i => i.Level == "warning" && i.Message.Contains("flat-rate day 6"));

        var notFlat = new CloseInput("", 0, [], [Novotel0109(flat: false)]);
        Assert.DoesNotContain(DailyCloseMath.Issues(notFlat, [], new Dictionary<HotelId, int> { [HotelId.Novotel] = 5 }), i => i.Message.Contains("5T/W"));
    }

    [Theory]
    [InlineData("2026-09-07", "2026-09-07")] // Monday
    [InlineData("2026-09-10", "2026-09-07")]
    [InlineData("2026-09-13", "2026-09-07")] // Sunday
    public void Week_runs_monday_to_sunday(string date, string monday) =>
        Assert.Equal(DateOnly.Parse(monday), DailyCloseMath.WeekStart(DateOnly.Parse(date)));

    [Fact]
    public void Zeitraum_is_written_like_the_paper_form() => Assert.Equal("Sep. 26", ReportService.Zeitraum(new DateOnly(2026, 9, 1)));

    [Fact]
    public void Cleaner_list_keeps_everyone_on_the_roster()
    {
        var roster = new[]
        {
            new StaffMember(Guid.NewGuid(), HotelId.Novotel, "Asmo I.", "External", "Cleaner", null, true),
            new StaffMember(Guid.NewGuid(), HotelId.Novotel, "Daniel D.", "External", "Cleaner", null, true),
        };
        var lines = ReportService.CleanerLines([new CloseCleaner(roster[0].Id, "Asmo I.", 25, 5, 5, 1, null, null, 0.5m)], roster);
        Assert.Equal(2, lines.Count);
        Assert.Equal(25, lines[0].Gereinigt);
        Assert.Null(lines[1].Gereinigt); // listed, did not work: empty row
    }
}
