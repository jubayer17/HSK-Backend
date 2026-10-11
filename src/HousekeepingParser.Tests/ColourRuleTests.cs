using HousekeepingParser.Coloring;
using HousekeepingParser.Core;
using Xunit;

namespace HousekeepingParser.Tests;

/// <summary>The priority list (plan 9.5) and both rhythm tables (plan 9.4).</summary>
public class ColourRuleTests
{
    private static readonly DateOnly Today = new(2026, 8, 30);
    private readonly ColorRuleService _rules = new();

    private ColourDecision Stay(int nights, RhythmType rhythm, params GuestRequest[] requests) =>
        _rules.Decide(new ColourInput("214", "DI", "Stayover", Today.AddDays(-nights), Today, rhythm, requests));

    [Theory]
    // night: 0 1 2 3 4 5 6 7 8  →  R G P B G P B G P
    [InlineData(0, ColourName.Red, "ARRIVAL_0")]
    [InlineData(1, ColourName.Green, "SERVICE_1")]
    [InlineData(2, ColourName.Purple, "SERVICE_2")]
    [InlineData(3, ColourName.Blue, "SERVICE_3")]
    [InlineData(4, ColourName.Green, "SERVICE_1")]
    [InlineData(5, ColourName.Purple, "SERVICE_2")]
    [InlineData(6, ColourName.Blue, "SERVICE_3")]
    [InlineData(7, ColourName.Green, "SERVICE_1")]
    [InlineData(8, ColourName.Purple, "SERVICE_2")]
    public void Two_tages_rhythm(int nights, ColourName colour, string code)
    {
        var d = Stay(nights, RhythmType.TwoTages);
        Assert.Equal(colour, d.Colour);
        Assert.Equal(code, d.ServiceCode);
        Assert.Equal(nights, d.Nights);
    }

    [Theory]
    // night: 0 1 2 3 4 5 6 7 8  →  R G G P B G G P B
    [InlineData(0, ColourName.Red)]
    [InlineData(1, ColourName.Green)]
    [InlineData(2, ColourName.Green)]
    [InlineData(3, ColourName.Purple)]
    [InlineData(4, ColourName.Blue)]
    [InlineData(5, ColourName.Green)]
    [InlineData(6, ColourName.Green)]
    [InlineData(7, ColourName.Purple)]
    [InlineData(8, ColourName.Blue)]
    public void Three_tages_rhythm(int nights, ColourName colour) =>
        Assert.Equal(colour, Stay(nights, RhythmType.ThreeTages).Colour);

    [Fact]
    public void Green_without_card_is_shown_but_not_cleaned()
    {
        var d = Stay(1, RhythmType.TwoTages);
        Assert.Equal(ColourName.Green, d.Colour);
        Assert.False(d.Cleaned);
        Assert.Contains("only if a card", d.Reason);
    }

    [Fact]
    public void Green_with_card_is_cleaned()
    {
        var d = Stay(1, RhythmType.TwoTages, new GuestRequest(RequestType.CARD));
        Assert.Equal(ColourName.Green, d.Colour);
        Assert.True(d.Cleaned);
    }

    [Fact]
    public void Card_does_not_change_a_purple_or_blue_day()
    {
        var d = Stay(2, RhythmType.TwoTages, new GuestRequest(RequestType.CARD));
        Assert.Equal(ColourName.Purple, d.Colour);
        Assert.Equal("SERVICE_2", d.ServiceCode);
    }

    [Fact]
    public void Special_request_beats_everything()
    {
        var d = _rules.Decide(new ColourInput("101", "DI", "Due Out", Today.AddDays(-2), Today, null,
            [new GuestRequest(RequestType.DAILY_FULL), new GuestRequest(RequestType.SPECIAL, ColourName.Gold)]));
        Assert.Equal(ColourName.Gold, d.Colour);
        Assert.Equal("SPECIAL", d.ServiceCode);
        Assert.Equal(ColourName.Red, d.BaseColour);
    }

    [Theory]
    [InlineData(RequestType.DAILY_FULL, ColourName.Blue, "BLUE_DAILY_FULL")]
    [InlineData(RequestType.DAILY_TODAY, ColourName.Blue, "BLUE_DAILY_TODAY")]
    [InlineData(RequestType.ROOM_SPECIFIC, ColourName.Purple, "PURPLE_ROOM_SPECIFIC")]
    public void Guest_wishes_override_the_rhythm(RequestType type, ColourName colour, string code)
    {
        var d = Stay(1, RhythmType.TwoTages, new GuestRequest(type));
        Assert.Equal(colour, d.Colour);
        Assert.Equal(code, d.ServiceCode);
        Assert.True(d.Cleaned);
    }

    [Fact]
    public void Daily_cleaning_beats_one_area_request()
    {
        var d = Stay(3, RhythmType.TwoTages, new GuestRequest(RequestType.ROOM_SPECIFIC), new GuestRequest(RequestType.DAILY_TODAY));
        Assert.Equal("BLUE_DAILY_TODAY", d.ServiceCode);
    }

    [Theory]
    [InlineData("Departed", "DEPARTURE_RED")]
    [InlineData("Departure", "DEPARTURE_RED")]
    [InlineData("Due Out", "DUEOUT_RED")]
    [InlineData("Arrived", "ARRIVAL_0")]
    [InlineData("Arrival", "ARRIVAL_0")]
    [InlineData("Day Use", "DAYUSE_RED")]
    public void Departures_and_arrivals_are_red(string status, string code)
    {
        var d = _rules.Decide(new ColourInput("101", "DI", status, Today.AddDays(-1), Today, null, []));
        Assert.Equal(ColourName.Red, d.Colour);
        Assert.Equal(code, d.ServiceCode);
    }

    [Fact]
    public void Clean_room_without_booking_gets_no_dot()
    {
        var d = _rules.Decide(new ColourInput("117", "CL", "", null, Today, null, []));
        Assert.Null(d.Colour);
        Assert.Equal("CLEAN_ROOM_012", d.ServiceCode);
        Assert.False(d.Cleaned);
    }

    [Fact]
    public void Arrival_after_report_date_is_skipped_with_warning()
    {
        var d = _rules.Decide(new ColourInput("101", "DI", "Stayover", Today.AddDays(2), Today, RhythmType.TwoTages, []));
        Assert.Null(d.Colour);
        Assert.NotNull(d.Warning);
    }

    [Fact]
    public void Unknown_status_gets_no_dot_and_a_warning()
    {
        var d = _rules.Decide(new ColourInput("101", "DI", "Walk In", Today, Today, null, []));
        Assert.Null(d.Colour);
        Assert.Equal("UNKNOWN_STATUS", d.ServiceCode);
        Assert.NotNull(d.Warning);
    }
}
