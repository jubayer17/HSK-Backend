using HousekeepingParser.Core;

namespace HousekeepingParser.Coloring;

public enum RequestType
{
    /// <summary>goGreen / Black Card / Make My Room card shown: a Green room is cleaned.</summary>
    CARD,
    DAILY_FULL,
    DAILY_TODAY,
    ROOM_SPECIFIC,
    SPECIAL,
}

public sealed record GuestRequest(RequestType Type, ColourName? Colour = null);

public sealed record ColourInput(
    string RoomNo,
    string RoomStatus,
    string ReservationStatus,
    DateOnly? Arrival,
    DateOnly ReportDate,
    /// <summary>The rhythm frozen at the guest's check-in (rule 9); null when the room has no stay.</summary>
    RhythmType? GuestRhythm,
    IReadOnlyList<GuestRequest> Requests);

public sealed record ColourDecision(
    ColourName? Colour,
    /// <summary>The colour before any guest request was applied.</summary>
    ColourName? BaseColour,
    string ServiceCode,
    string Reason,
    string? OverrideReason,
    int? Nights,
    int? CyclePosition,
    RhythmType? Rhythm,
    /// <summary>False for a Green room without a card: shown Green, not cleaned (cost 0).</summary>
    bool Cleaned,
    /// <summary>Set when the row needs a warning on the results screen.</summary>
    string? Warning = null);

public interface IColorRuleService
{
    ColourDecision Decide(ColourInput input);
}

public static class RhythmCycles
{
    public static readonly IReadOnlyDictionary<RhythmType, ColourName[]> Cycles = new Dictionary<RhythmType, ColourName[]>
    {
        [RhythmType.TwoTages] = [ColourName.Green, ColourName.Purple, ColourName.Blue],
        [RhythmType.ThreeTages] = [ColourName.Green, ColourName.Green, ColourName.Purple, ColourName.Blue],
    };

    /// <summary>Night 0 is always Red; after that position = (nights − 1) % cycle length (plan 9.4).</summary>
    public static (ColourName Colour, int Position) ColourForNight(RhythmType rhythm, int nights)
    {
        if (nights < 1) throw new ArgumentOutOfRangeException(nameof(nights));
        var cycle = Cycles[rhythm];
        var pos = (nights - 1) % cycle.Length;
        return (cycle[pos], pos);
    }

    public static string ServiceCode(ColourName colour) => colour switch
    {
        ColourName.Green => "SERVICE_1",
        ColourName.Purple => "SERVICE_2",
        ColourName.Blue => "SERVICE_3",
        _ => "SERVICE",
    };
}

/// <summary>
/// The priority list from plan 9.5, checked top to bottom; the first rule that fits decides.
/// Pure: rooms and wishes in, colours out, no database calls.
/// Decisions D2 (client defaults until confirmed): Arrived / Arrival → Red, Day Use → Red, no guest → no dot.
/// </summary>
public sealed class ColorRuleService : IColorRuleService
{
    public ColourDecision Decide(ColourInput input)
    {
        var baseDecision = DecideByStatus(input);
        var has = (RequestType t) => input.Requests.FirstOrDefault(r => r.Type == t);

        if (has(RequestType.SPECIAL) is { } special)
        {
            var colour = special.Colour ?? ColourName.Gold;
            return baseDecision with
            {
                Colour = colour,
                ServiceCode = "SPECIAL",
                Reason = "Special request",
                OverrideReason = "SPECIAL",
                Cleaned = true,
            };
        }
        if (has(RequestType.DAILY_FULL) is not null)
            return baseDecision with { Colour = ColourName.Blue, ServiceCode = "BLUE_DAILY_FULL", Reason = "Daily cleaning (whole stay)", OverrideReason = "DAILY_FULL", Cleaned = true };
        if (has(RequestType.DAILY_TODAY) is not null)
            return baseDecision with { Colour = ColourName.Blue, ServiceCode = "BLUE_DAILY_TODAY", Reason = "Daily cleaning (today)", OverrideReason = "DAILY_TODAY", Cleaned = true };
        if (has(RequestType.ROOM_SPECIFIC) is not null)
            return baseDecision with { Colour = ColourName.Purple, ServiceCode = "PURPLE_ROOM_SPECIFIC", Reason = "Request for one area of the room", OverrideReason = "ROOM_SPECIFIC", Cleaned = true };

        if (baseDecision.Colour == ColourName.Green && has(RequestType.CARD) is not null)
            return baseDecision with { Reason = $"Night {baseDecision.Nights}, card shown", Cleaned = true };

        return baseDecision;
    }

    private static ColourDecision DecideByStatus(ColourInput input)
    {
        int? nights = input.Arrival is { } a ? input.ReportDate.DayNumber - a.DayNumber : null;
        ColourDecision Red(string code, string reason) =>
            new(ColourName.Red, ColourName.Red, code, reason, null, nights, null, input.GuestRhythm, true);

        var status = input.ReservationStatus.Trim();
        switch (status.ToLowerInvariant())
        {
            case "departed":
            case "departure":
                return Red("DEPARTURE_RED", "Guest has checked out");
            case "due out":
                return Red("DUEOUT_RED", "Guest leaves today");
            case "arrived":
            case "arrival":
                return Red("ARRIVAL_0", "Arrival today");
            case "day use":
                return Red("DAYUSE_RED", "Day use");
            case "stayover":
                return Stayover(input, nights, Red);
            case "":
                return new ColourDecision(null, null, "CLEAN_ROOM_012", "No booking on the sheet", null, null, null, null, false);
            default:
                return new ColourDecision(null, null, "UNKNOWN_STATUS", $"Unknown status \"{status}\"", null, nights, null, null, false,
                    $"Room {input.RoomNo}: reservation status \"{status}\" is unknown, no colour assigned.");
        }
    }

    private static ColourDecision Stayover(ColourInput input, int? nights, Func<string, string, ColourDecision> red)
    {
        if (nights is null)
            return new ColourDecision(null, null, "NO_ARRIVAL_DATE", "Stayover without arrival date", null, null, null, null, false,
                $"Room {input.RoomNo}: stayover without an arrival date, no colour assigned.");
        if (nights < 0)
            return new ColourDecision(null, null, "ARRIVAL_IN_FUTURE", "Arrival date after the report date", null, nights, null, null, false,
                $"Room {input.RoomNo}: arrival date is after the report date, room skipped.");
        if (nights == 0) return red("ARRIVAL_0", "Arrival night");

        var rhythm = input.GuestRhythm ?? throw new InvalidOperationException("A stayover needs the guest's rhythm.");
        var (colour, pos) = RhythmCycles.ColourForNight(rhythm, nights.Value);
        var cleaned = colour != ColourName.Green;
        var reason = colour == ColourName.Green ? $"Night {nights}, only if a card is shown" : $"Night {nights} of stay";
        return new ColourDecision(colour, colour, RhythmCycles.ServiceCode(colour), reason, null, nights, pos, rhythm, cleaned);
    }
}
