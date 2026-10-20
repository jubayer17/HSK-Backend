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

public enum RoomMoveSide
{
    /// <summary>Room the guest left: full clean.</summary>
    MovedOut,
    /// <summary>Room the guest moved into: already clean.</summary>
    MovedIn,
}

/// <summary>A room move touching this room today.</summary>
public sealed record RoomMoveMark(
    RoomMoveSide Side,
    /// <summary>Cleaned today (cleaner available, or carried over).</summary>
    bool CleanedToday,
    /// <summary>"External" or "HotelEmployee".</summary>
    string CleanedByType,
    /// <summary>Moved yesterday without a cleaner; cleaned this morning.</summary>
    bool CarriedOver);

public sealed record ColourInput(
    string RoomNo,
    string RoomStatus,
    string ReservationStatus,
    DateOnly? Arrival,
    DateOnly ReportDate,
    /// <summary>Stayover: rhythm frozen at check-in; else the hotel's active one.</summary>
    RhythmType? GuestRhythm,
    IReadOnlyList<GuestRequest> Requests)
{
    /// <summary>1-Night: cleaner has capacity today (no answer = no).</summary>
    public bool CleanerAvailable { get; init; }

    /// <summary>1-Night: last day Blue and not cleaned, or left as Liegezimmer.</summary>
    public bool PendingService { get; init; }

    public RoomMoveMark? RoomMove { get; init; }
}

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
    string? Warning = null)
{
    /// <summary>"HotelEmployee" if staff clean a room move; else external.</summary>
    public string? CleanedByType { get; init; }
}

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
/// Room moves rank right after special requests: the room left gets a full clean.
/// </summary>
public sealed class ColorRuleService : IColorRuleService
{
    public ColourDecision Decide(ColourInput input)
    {
        var oneNight = input.GuestRhythm == RhythmType.OneNight;
        if (input.Requests.FirstOrDefault(r => r.Type == RequestType.SPECIAL) is { } special)
        {
            var baseDecision = input.RoomMove is { } m ? RoomMove(input, m) : oneNight ? OneNight(input) : DecideByStatus(input);
            return baseDecision with
            {
                // Special request without a colour: Green in 1-Night, Gold otherwise.
                Colour = special.Colour ?? (oneNight ? ColourName.Green : ColourName.Gold),
                ServiceCode = "SPECIAL",
                Reason = "Special request",
                OverrideReason = "SPECIAL",
                Cleaned = true,
                CleanedByType = null,
            };
        }
        if (input.RoomMove is { } move) return RoomMove(input, move);

        return oneNight ? OneNight(input) : Rhythm(input);
    }

    private static int? Nights(ColourInput input) => input.Arrival is { } a ? input.ReportDate.DayNumber - a.DayNumber : null;

    private static ColourDecision RoomMove(ColourInput input, RoomMoveMark move)
    {
        var nights = Nights(input);
        if (move.Side == RoomMoveSide.MovedIn)
            return new ColourDecision(ColourName.Red, ColourName.Red, "ROOM_MOVE_IN", "Room move: guest moved in, room is ready", "ROOM_MOVE",
                nights, null, input.GuestRhythm, false);

        var by = move.CleanedByType == "HotelEmployee" ? "hotel employee" : "cleaning company";
        var reason = move.CarriedOver
            ? $"Room move yesterday, cleaned this morning by the {by}"
            : move.CleanedToday
                ? $"Room move, cleaned today by the {by}"
                : "Room move, no cleaner today: cleaned tomorrow morning";
        return new ColourDecision(ColourName.Red, ColourName.Red, "ROOM_MOVE_RED", reason, "ROOM_MOVE", nights, null, input.GuestRhythm, move.CleanedToday)
        {
            CleanedByType = move.CleanedToday ? move.CleanedByType : null,
        };
    }

    // ---------- Logic 1 and 2: 2-Tages / 3-Tages rhythm ----------

    private static ColourDecision Rhythm(ColourInput input)
    {
        var baseDecision = DecideByStatus(input);
        var has = (RequestType t) => input.Requests.FirstOrDefault(r => r.Type == t);

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
        var nights = Nights(input);
        ColourDecision Red(string code, string reason) =>
            new(ColourName.Red, ColourName.Red, code, reason, null, nights, null, input.GuestRhythm, true);

        var status = input.ReservationStatus.Trim();
        switch (status.ToLowerInvariant())
        {
            case "departed":
            case "departure":
                return Red("DEPARTURE_RED", "Guest has checked out");
            case "due out":
            case "dueout":
                return Red("DUEOUT_RED", "Guest leaves today");
            case "arrived":
            case "arrival":
                return Red("ARRIVAL_0", "Arrival today");
            case "day use":
                return Red("DAYUSE_RED", "Day use");
            case "stayover":
                return Stayover(input, nights, Red);
            case "":
                return NoBooking();
            default:
                return UnknownStatus(input, status, nights);
        }
    }

    private static ColourDecision Stayover(ColourInput input, int? nights, Func<string, string, ColourDecision> red)
    {
        if (StayoverProblem(input, nights) is { } problem) return problem;
        if (nights == 0) return red("ARRIVAL_0", "Arrival night");

        var rhythm = input.GuestRhythm ?? throw new InvalidOperationException("A stayover needs the guest's rhythm.");
        var (colour, pos) = RhythmCycles.ColourForNight(rhythm, nights!.Value);
        var cleaned = colour != ColourName.Green;
        var reason = colour == ColourName.Green ? $"Night {nights}, only if a card is shown" : $"Night {nights} of stay";
        return new ColourDecision(colour, colour, RhythmCycles.ServiceCode(colour), reason, null, nights, pos, rhythm, cleaned);
    }

    // ---------- Logic 3: 1 Night Color Logic ----------

    /// <summary>Departure before arrival (turnover = full clean), then pending, then request.</summary>
    private static ColourDecision OneNight(ColourInput input)
    {
        var nights = Nights(input);
        ColourDecision Make(ColourName colour, string code, string reason, bool cleaned, string? overrideReason = null) =>
            new(colour, colour, code, reason, overrideReason, nights, null, RhythmType.OneNight, cleaned);

        var status = input.ReservationStatus.Trim().ToLowerInvariant();
        switch (status)
        {
            case "departed":
            case "departure":
                return Make(ColourName.Red, "DEPARTURE_RED", "Guest has checked out: full departure clean", true);
            case "due out":
            case "dueout":
                return Make(ColourName.Red, "DEPARTURE_RED", "Guest leaves today: full departure clean", true);
            case "day use":
                return Make(ColourName.Red, "DEPARTURE_RED", "Day use: full departure clean", true);
            case "arrived":
            case "arrival":
                return Make(ColourName.Red, "ARRIVAL_RED", "Arrival today, room is ready", false);
            case "stayover":
                break;
            case "":
                return NoBooking();
            default:
                return UnknownStatus(input, input.ReservationStatus.Trim(), nights);
        }

        if (StayoverProblem(input, nights) is { } problem) return problem;
        if (nights == 0) return Make(ColourName.Red, "ARRIVAL_RED", "Arrival night, room is ready", false);

        if (input.PendingService)
        {
            return input.CleanerAvailable
                ? Make(ColourName.Green, "STAYOVER_GREEN_CLEANED", "Waiting service done today", true, "PENDING")
                : Make(ColourName.Blue, "STAYOVER_BLUE", "Still waiting: no cleaner today", false, "PENDING");
        }

        if (input.Requests.Count > 0)
        {
            return input.CleanerAvailable
                ? Make(ColourName.Green, "STAYOVER_GREEN_CLEANED", "Guest asked for service, cleaner available", true, "REQUEST")
                : Make(ColourName.Blue, "STAYOVER_BLUE", "Guest asked for service, no cleaner: waiting", false, "REQUEST");
        }

        return Make(ColourName.Green, "STAYOVER_GREEN", $"Night {nights}, no service requested", false);
    }

    // ---------- shared ----------

    private static ColourDecision? StayoverProblem(ColourInput input, int? nights)
    {
        if (nights is null)
            return new ColourDecision(null, null, "NO_ARRIVAL_DATE", "Stayover without arrival date", null, null, null, null, false,
                $"Room {input.RoomNo}: stayover without an arrival date, no colour assigned.");
        if (nights < 0)
            return new ColourDecision(null, null, "ARRIVAL_IN_FUTURE", "Arrival date after the report date", null, nights, null, null, false,
                $"Room {input.RoomNo}: arrival date is after the report date, room skipped.");
        return null;
    }

    private static ColourDecision NoBooking() =>
        new(null, null, "CLEAN_ROOM_012", "No booking on the sheet", null, null, null, null, false);

    private static ColourDecision UnknownStatus(ColourInput input, string status, int? nights) =>
        new(null, null, "UNKNOWN_STATUS", $"Unknown status \"{status}\"", null, nights, null, null, false,
            $"Room {input.RoomNo}: reservation status \"{status}\" is unknown, no colour assigned.");
}
