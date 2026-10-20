using HousekeepingParser.Functions;
using HousekeepingParser.Operations;
using Xunit;

namespace HousekeepingParser.Tests;

/// <summary>Dashboard arithmetic (mirrors the client's mock).</summary>
public class DashboardMathTests
{
    private static readonly DateOnly Today = new(2026, 9, 15);

    [Fact]
    public void Novotel_flat_rate_day_bills_negative_checks()
    {
        // Client sheet 01.09.26: gereinigt 57, PM + APM 23, 30 flat-rate checks.
        var zu = DashboardMath.ZuBerechnen(15, 8, flatRateDay: true, pauschaleChecks: 30);
        Assert.Equal(-7, zu);
        Assert.Equal(381.50m, DashboardMath.ActualCost(57, 0, zu, 7m, 2.5m));
    }

    [Theory]
    [InlineData(23, 0, false, 30, 23)]
    [InlineData(20, 15, true, 30, 5)]
    [InlineData(0, 0, true, 0, 0)]
    public void Zu_berechnen(int pm, int apm, bool flat, int pauschale, int expected) =>
        Assert.Equal(expected, DashboardMath.ZuBerechnen(pm, apm, flat, pauschale));

    [Fact]
    public void Abzuege_reduce_billed_rooms_but_missing_prices_count_as_zero()
    {
        Assert.Equal(55m * 7m + 4m * 2.5m, DashboardMath.ActualCost(57, 2, 4, 7m, 2.5m));
        Assert.Equal(57m * 7m, DashboardMath.ActualCost(57, 0, 10, 7m, null));
        Assert.Equal(0m, DashboardMath.ActualCost(57, 0, 0, null, null));
    }

    [Fact]
    public void Planned_and_hours_cost()
    {
        Assert.Equal(312m, DashboardMath.PlannedCost(48, 6.5m));
        Assert.Equal(0m, DashboardMath.PlannedCost(48, null));
        Assert.Equal(7.5m * 22m, DashboardMath.HoursCost(7.5m, 22m));
        Assert.Equal(0m, DashboardMath.HoursCost(7.5m, null));
    }

    [Theory]
    [InlineData(0, 0, "none")]
    [InlineData(3, 0, "actual")]
    [InlineData(0, 2, "planned")]
    [InlineData(1, 1, "mixed")]
    public void Range_basis(int actual, int planned, string expected) => Assert.Equal(expected, DashboardMath.Basis(actual, planned));

    [Theory]
    [InlineData(false, false, "none")]
    [InlineData(false, true, "none")]
    [InlineData(true, true, "actual")]
    [InlineData(true, false, "planned")]
    public void Day_basis(bool anyData, bool confirmed, string expected) => Assert.Equal(expected, DashboardMath.DayBasis(anyData, confirmed));

    [Theory]
    [InlineData("2026-09-10", DashboardTimeframe.Daily, "2026-09-10", "2026-09-10")]
    [InlineData("2026-09-10", DashboardTimeframe.Weekly, "2026-09-04", "2026-09-10")]
    [InlineData("2026-09-03", DashboardTimeframe.Weekly, "2026-08-28", "2026-09-03")]
    [InlineData("2026-09-10", DashboardTimeframe.Monthly, "2026-09-01", "2026-09-10")]
    [InlineData("2026-10-20", DashboardTimeframe.Monthly, "2026-09-01", "2026-09-15")] // future date clamped to today
    [InlineData("2026-09-01", DashboardTimeframe.Monthly, "2026-09-01", "2026-09-01")]
    public void Ranges(string date, DashboardTimeframe timeframe, string from, string to)
    {
        var (f, t) = DashboardMath.Range(timeframe, DateOnly.Parse(date), Today);
        Assert.Equal(DateOnly.Parse(from), f);
        Assert.Equal(DateOnly.Parse(to), t);
    }

    [Fact]
    public void Room_move_cleaning_day()
    {
        var move = new DateOnly(2026, 9, 10);
        Assert.Equal(move, DashboardMath.CleanedOn(move, move.AddDays(1), cleanerAvailable: true));
        Assert.Equal(move.AddDays(1), DashboardMath.CleanedOn(move, move.AddDays(1), cleanerAvailable: false));
    }

    [Fact]
    public void Rounding_and_billing()
    {
        Assert.Equal(10.13m, DashboardMath.Round2(10.125m));
        Assert.Equal(381.5m, DashboardMath.Round2(381.499m));
        Assert.Equal(381.50m, DashboardMath.Billing(381.5m, pricesSet: true, anyDayWithData: true));
        Assert.Null(DashboardMath.Billing(381.5m, pricesSet: false, anyDayWithData: true));
        Assert.Null(DashboardMath.Billing(0m, pricesSet: true, anyDayWithData: false));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("all", null)]
    [InlineData("1", (byte)1)]
    [InlineData("2", (byte)2)]
    public void Hotel_filter(string? value, byte? expected) => Assert.Equal(expected, (byte?)DashboardFunctions.ParseHotel(value));

    [Theory]
    [InlineData("3")]
    [InlineData("novotel")]
    [InlineData("-1")]
    public void Unknown_hotel_is_rejected(string value) =>
        Assert.Equal(400, Assert.Throws<Core.BusinessRuleException>(() => DashboardFunctions.ParseHotel(value)).Status);

    [Fact]
    public void Timeframe_and_date_parsing()
    {
        Assert.Equal(DashboardTimeframe.Weekly, DashboardFunctions.ParseTimeframe("weekly"));
        Assert.Equal(DashboardTimeframe.Daily, DashboardFunctions.ParseTimeframe(null));
        Assert.Throws<Core.BusinessRuleException>(() => DashboardFunctions.ParseTimeframe("yearly"));
        Assert.Null(DashboardFunctions.ParseDate(""));
        Assert.Equal(new DateOnly(2026, 9, 1), DashboardFunctions.ParseDate("2026-09-01"));
        Assert.Throws<Core.BusinessRuleException>(() => DashboardFunctions.ParseDate("01.09.2026"));
    }
}
