using HousekeepingParser.Core;

namespace HousekeepingParser.Operations;

/// <summary>Room lists of the "Übergabe Etage – Empfang" form, in printed order.</summary>
public enum RoomCategory
{
    NS,
    RK,
    KS,
    LATE_CHECKOUT,
    LIEGEZIMMER,
}

public static class SupervisorRoles
{
    /// <summary>The six Task Sheet lines with their printed labels, in order.</summary>
    public static readonly (string Role, string Label)[] All =
    [
        ("HD", "HD"), ("HDA", "HDA"), ("FS", "FS"), ("RPH_FS", "RPH FS"), ("HM", "HM"), ("RPH_HM", "RPH HM"),
    ];

    public static bool IsKnown(string role) => All.Any(r => r.Role == role);
}

public sealed record CloseSupervisor(string Role, Guid? StaffId, string Name, TimeOnly? Start, TimeOnly? End, decimal PauseHours);

public sealed record CloseCleaner(Guid? StaffId, string Name, int Gereinigt, int Ns, int Rk, int Ks, TimeOnly? Start, TimeOnly? End, decimal PauseHours);

public sealed record CloseHotel(
    HotelId Hotel,
    string RichterStaffName,
    bool FlatRateDay,
    int PauschaleChecks,
    int Sauber,
    int Abzuege,
    string Informationen,
    IReadOnlyList<CloseCleaner> Cleaners,
    IReadOnlyDictionary<RoomCategory, IReadOnlyList<string>> Rooms)
{
    public IReadOnlyList<string> RoomsOf(RoomCategory c) => Rooms.TryGetValue(c, out var r) ? r : [];
}

public sealed record CloseInput(string Comment, decimal ExtraReinigungHours, IReadOnlyList<CloseSupervisor> Supervisors, IReadOnlyList<CloseHotel> Hotels);

public sealed record CloseChecks(int Pm, int Apm, int Selbst);

public sealed record CloseTotals(int Gereinigt, int Ns, int Rk, int Ks, int Sauber, int Liegezimmer, int Summe, int ChecksActual, int ZuBerechnen);

public sealed record CloseIssue(string Level, HotelId? Hotel, string Message)
{
    public bool IsError => Level == "error";
}

/// <summary>Daily close arithmetic; mirrors the client's closeMath.ts.</summary>
public static class DailyCloseMath
{
    /// <summary>End − start − break, 2 decimals; null if missing or not positive.</summary>
    public static decimal? Hours(TimeOnly? start, TimeOnly? end, decimal pauseHours)
    {
        if (start is null || end is null) return null;
        var minutes = (end.Value.ToTimeSpan() - start.Value.ToTimeSpan()).TotalMinutes;
        if (minutes <= 0) return null;
        return Math.Round((decimal)minutes / 60m - pauseHours, 2, MidpointRounding.AwayFromZero);
    }

    public static decimal HoursOf(CloseInput input, string role)
    {
        var s = input.Supervisors.FirstOrDefault(x => x.Role == role);
        return s is null ? 0 : Hours(s.Start, s.End, s.PauseHours) ?? 0;
    }

    public static CloseTotals Totals(CloseHotel h, CloseChecks checks)
    {
        var gereinigt = h.Cleaners.Sum(c => c.Gereinigt);
        var ns = h.Cleaners.Sum(c => c.Ns);
        var rk = h.Cleaners.Sum(c => c.Rk);
        var ks = h.Cleaners.Sum(c => c.Ks);
        var liegezimmer = h.RoomsOf(RoomCategory.LATE_CHECKOUT).Count + h.RoomsOf(RoomCategory.LIEGEZIMMER).Count;
        var actual = checks.Pm + checks.Apm;
        return new CloseTotals(gereinigt, ns, rk, ks, h.Sauber, liegezimmer, gereinigt + ns + rk + ks + h.Sauber + liegezimmer,
            actual, actual - (h.FlatRateDay ? h.PauschaleChecks : 0));
    }

    private static readonly IReadOnlyDictionary<RoomCategory, string> Labels = new Dictionary<RoomCategory, string>
    {
        [RoomCategory.NS] = "No Service",
        [RoomCategory.RK] = "Rote Karte",
        [RoomCategory.KS] = "Kein Service",
        [RoomCategory.LATE_CHECKOUT] = "Late Check-out Liegezimmer",
        [RoomCategory.LIEGEZIMMER] = "Liegezimmer für Folgetag",
    };

    /// <summary>Errors stop the confirmation; warnings are shown but allowed.</summary>
    public static IReadOnlyList<CloseIssue> Issues(CloseInput input, IReadOnlyList<(HotelId Hotel, int? TotalRooms, CloseTotals Totals)> context)
    {
        var issues = new List<CloseIssue>();
        void Err(HotelId? h, string m) => issues.Add(new CloseIssue("error", h, m));
        void Warn(HotelId? h, string m) => issues.Add(new CloseIssue("warning", h, m));

        foreach (var s in input.Supervisors)
        {
            var label = s.Role.Replace('_', ' ');
            if (s.Start is not null && s.End is not null && Hours(s.Start, s.End, s.PauseHours) is null)
                Err(null, $"{label}: the end time must be after the start time and longer than the break.");
            if ((s.Start is not null || s.End is not null) && string.IsNullOrWhiteSpace(s.Name))
                Err(null, $"{label}: enter the name for the times given.");
        }
        if (input.ExtraReinigungHours < 0) Err(null, "Extra Reinigung hours cannot be negative.");

        foreach (var h in input.Hotels)
        {
            var shortName = Hotels.ShortName(h.Hotel);
            for (var i = 0; i < h.Cleaners.Count; i++)
            {
                var c = h.Cleaners[i];
                var who = string.IsNullOrWhiteSpace(c.Name) ? $"Cleaner line {i + 1}" : c.Name.Trim();
                if (string.IsNullOrWhiteSpace(c.Name)) Err(h.Hotel, $"{shortName}: cleaner line {i + 1} has no name.");
                if (c.Gereinigt < 0 || c.Ns < 0 || c.Rk < 0 || c.Ks < 0) Err(h.Hotel, $"{shortName}, {who}: room numbers must be whole numbers, 0 or more.");
                if (c.Start is not null && c.End is not null && Hours(c.Start, c.End, c.PauseHours) is null)
                    Err(h.Hotel, $"{shortName}, {who}: the end time must be after the start time.");
            }

            var seen = new Dictionary<string, RoomCategory>();
            foreach (var cat in Enum.GetValues<RoomCategory>())
            {
                foreach (var room in h.RoomsOf(cat))
                {
                    if (!RoomInventory.Contains(h.Hotel, room)) Err(h.Hotel, $"{shortName}: room {room} does not exist.");
                    if (seen.TryGetValue(room, out var before))
                        Err(h.Hotel, $"{shortName}: room {room} is listed under both {Labels[before]} and {Labels[cat]}.");
                    seen[room] = cat;
                }
            }

            var ctx = context.FirstOrDefault(c => c.Hotel == h.Hotel);
            if (ctx != default)
            {
                var t = ctx.Totals;
                if (ctx.TotalRooms is null)
                    Warn(h.Hotel, $"{shortName}: no task sheet was processed for this day, so the room total cannot be checked.");
                else if (t.Summe != ctx.TotalRooms)
                    Err(h.Hotel, $"{shortName}: Gereinigt + No Service + Rote Karten + Kein Service + Sauber + Liegezimmer = {t.Summe}, but the Opera sheet has {ctx.TotalRooms} rooms.");

                foreach (var (cat, total) in new[] { (RoomCategory.NS, t.Ns), (RoomCategory.RK, t.Rk), (RoomCategory.KS, t.Ks) })
                {
                    if (h.RoomsOf(cat).Count != total)
                        Warn(h.Hotel, $"{shortName}: {h.RoomsOf(cat).Count} rooms listed as {Labels[cat]}, the cleaners' total is {total}.");
                }
            }
            if (h.RoomsOf(RoomCategory.LIEGEZIMMER).Count > 30)
                Warn(h.Hotel, $"{shortName}: more than 30 Liegezimmer. The extra rooms go on a second page of the handover form.");
            if (string.IsNullOrWhiteSpace(h.RichterStaffName)) Warn(h.Hotel, $"{shortName}: \"Name MA Richter\" for the handover form is empty.");
            if (h.FlatRateDay && h.PauschaleChecks <= 0) Warn(h.Hotel, $"{shortName}: flat-rate day without flat-rate checks.");
        }
        return issues;
    }
}
