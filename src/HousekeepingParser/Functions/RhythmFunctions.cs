using System.Globalization;
using HousekeepingParser.Core;
using HousekeepingParser.Rhythm;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace HousekeepingParser.Functions;

public sealed class RhythmFunctions(Api api, IRhythmService rhythms)
{
    [Function("RhythmGet")]
    public Task<IActionResult> Get([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "rhythm")] HttpRequest req) =>
        api.RunAsync(req, async _ => Api.Ok((await rhythms.GetAllAsync(req.HttpContext.RequestAborted)).Select(ToDto)));

    [Function("RhythmSet")]
    public Task<IActionResult> Set([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "rhythm")] HttpRequest req) =>
        api.RunAsync(req, async user =>
        {
            var body = await Api.ReadJsonAsync<SetRhythmRequest>(req);
            if (!body.Confirmed) throw BusinessRuleException.BadRequest("The change must be confirmed.");
            var hotel = ParseHotel(body.HotelId);
            if (!Enum.TryParse<RhythmType>(body.Rhythm, out var rhythm) || !Enum.IsDefined(rhythm))
                throw BusinessRuleException.BadRequest("Unknown rhythm.");
            if (!DateOnly.TryParseExact(body.EffectiveFrom, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from))
                throw BusinessRuleException.BadRequest("The start date is not a valid date.");
            var updated = await rhythms.SetAsync(user, hotel, rhythm, from, body.Reason, req.HttpContext.RequestAborted);
            return Api.Ok(ToDto(updated));
        });

    [Function("RhythmCancel")]
    public Task<IActionResult> Cancel(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "rhythm/{hotelId:int}/scheduled")] HttpRequest req, int hotelId) =>
        api.RunAsync(req, async user =>
        {
            var body = req.ContentLength is > 0 ? await Api.ReadJsonAsync<CancelRhythmRequest>(req) : new CancelRhythmRequest(null, false);
            if (!body.Confirmed) throw BusinessRuleException.BadRequest("The cancellation must be confirmed.");
            var updated = await rhythms.CancelScheduledAsync(user, ParseHotel(hotelId), body.Reason, req.HttpContext.RequestAborted);
            return Api.Ok(ToDto(updated));
        });

    [Function("RhythmHistory")]
    public Task<IActionResult> History([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "rhythm/history")] HttpRequest req) =>
        api.RunAsync(req, async _ => Api.Ok((await rhythms.HistoryAsync(req.HttpContext.RequestAborted)).Select(h =>
            new RhythmHistoryEntryDto(h.Id.ToString(), (int)h.HotelId, h.Action, h.Rhythm.ToString(), h.EffectiveFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                h.Reason, h.By, h.At.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)))));

    internal static HotelId ParseHotel(int id) =>
        Enum.IsDefined(typeof(HotelId), (byte)id) && id is 1 or 2 ? (HotelId)id : throw BusinessRuleException.BadRequest("Unknown hotel.");

    private static HotelRhythmDto ToDto(HotelRhythm r) => new((int)r.HotelId, Period(r.Active), Period(r.Scheduled));

    private static RhythmPeriodDto? Period(RhythmPeriod? p) =>
        p is null ? null : new(p.Rhythm.ToString(), p.EffectiveFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), p.Reason);
}
