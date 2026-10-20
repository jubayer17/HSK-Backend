using System.Globalization;
using HousekeepingParser.Core;
using HousekeepingParser.Operations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace HousekeepingParser.Functions;

public sealed class DashboardFunctions(Api api, IDashboardService dashboard)
{
    /// <summary>GET /api/dashboard?timeframe=daily|weekly|monthly&amp;hotel=all|1|2&amp;date=YYYY-MM-DD</summary>
    [Function("DashboardGet")]
    public Task<IActionResult> Get([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "dashboard")] HttpRequest req) =>
        api.RunAsync(req, async _ =>
        {
            var timeframe = ParseTimeframe(req.Query["timeframe"].ToString());
            var hotel = ParseHotel(req.Query["hotel"].ToString());
            var date = ParseDate(req.Query["date"].ToString());
            return Api.Ok(await dashboard.GetAsync(timeframe, hotel, date, req.HttpContext.RequestAborted));
        });

    internal static DashboardTimeframe ParseTimeframe(string? value) => (value ?? "").Trim().ToLowerInvariant() switch
    {
        "" or "daily" => DashboardTimeframe.Daily,
        "weekly" => DashboardTimeframe.Weekly,
        "monthly" => DashboardTimeframe.Monthly,
        _ => throw BusinessRuleException.BadRequest("Unknown timeframe. Use daily, weekly or monthly."),
    };

    /// <summary>"all" or empty = both hotels.</summary>
    internal static HotelId? ParseHotel(string? value)
    {
        var v = (value ?? "").Trim();
        if (v.Length == 0 || v.Equals("all", StringComparison.OrdinalIgnoreCase)) return null;
        return int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? RhythmFunctions.ParseHotel(id)
            : throw BusinessRuleException.BadRequest("Unknown hotel.");
    }

    internal static DateOnly? ParseDate(string? value)
    {
        var v = (value ?? "").Trim();
        if (v.Length == 0) return null;
        return DateOnly.TryParseExact(v, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : throw BusinessRuleException.BadRequest("The date is not a valid date.");
    }
}
