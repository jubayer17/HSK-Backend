using HousekeepingParser.Core;

namespace HousekeepingParser.Operations;

public sealed record BillingDay(HotelId Hotel, DateOnly Date, int Checks, int Allowance);
public sealed record WeeklyBillingTotal(HotelId Hotel, DateOnly WeekStart, DateOnly From, DateOnly To,
    int ConfirmedDays, int Checks, int Allowance, int BillableChecks);

public static class WeeklyBillingService
{
    // Confirmed by the user: keep signed amounts (23 - 30 = -7); do not clamp or invent carry-over rules.
    public static int DailyAmount(int checks, bool flatRateDay, int allowance) => checks - (flatRateDay ? allowance : 0);

    public static DateOnly Monday(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    public static IReadOnlyList<WeeklyBillingTotal> Aggregate(IEnumerable<BillingDay> days) => days
        .GroupBy(d => (d.Hotel, Week: Monday(d.Date)))
        .Select(g => new WeeklyBillingTotal(g.Key.Hotel, g.Key.Week, g.Min(d => d.Date), g.Max(d => d.Date),
            g.Count(), g.Sum(d => d.Checks), g.Sum(d => d.Allowance), g.Sum(d => d.Checks) - g.Sum(d => d.Allowance)))
        .OrderBy(w => w.WeekStart).ThenBy(w => w.Hotel).ToList();

    public static IReadOnlyList<WeeklyBillingTotal> Month(DateOnly month, IEnumerable<BillingDay> days)
    {
        var first = new DateOnly(month.Year, month.Month, 1);
        // Filter before grouping: a week crossing months contributes only this month's confirmed days.
        // Missing/unconfirmed days incur neither checks nor an assumed allowance.
        return Aggregate(days.Where(d => d.Date >= first && d.Date < first.AddMonths(1)));
    }
}
