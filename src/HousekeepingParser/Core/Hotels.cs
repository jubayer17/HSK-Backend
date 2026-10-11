namespace HousekeepingParser.Core;

/// <summary>HotelMaster ids from the client spec: 1 = ibis budget, 2 = Novotel (conflict C6: the name on the PDF decides).</summary>
public enum HotelId : byte
{
    IbisBudget = 1,
    Novotel = 2,
}

public static class Hotels
{
    public static readonly IReadOnlyDictionary<HotelId, string> PrintedNames = new Dictionary<HotelId, string>
    {
        [HotelId.Novotel] = "Novotel Münster City",
        [HotelId.IbisBudget] = "ibis budget Münster City",
    };

    public static string ShortName(HotelId id) => id == HotelId.Novotel ? "Novotel" : "ibis budget";

    /// <summary>Maps the hotel name printed in the PDF header to a hotel. Never uses the file name (rule 14).</summary>
    public static HotelId? FromPrintedName(string printed)
    {
        var normalised = Normalise(printed);
        foreach (var (id, name) in PrintedNames)
        {
            if (normalised == Normalise(name)) return id;
        }
        return null;
    }

    private static string Normalise(string s) =>
        string.Join(' ', s.Normalize(System.Text.NormalizationForm.FormC)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToLowerInvariant();
}

/// <summary>Room inventory from the Room Grid blueprint: 117 Novotel rooms, 123 ibis budget rooms.</summary>
public static class RoomInventory
{
    private static readonly IReadOnlyDictionary<HotelId, (int From, int To)[]> Ranges = new Dictionary<HotelId, (int, int)[]>
    {
        [HotelId.Novotel] = [(101, 126), (201, 226), (301, 326), (401, 426), (501, 513)],
        [HotelId.IbisBudget] = [(101, 128), (201, 228), (301, 328), (401, 428), (501, 511)],
    };

    public static IEnumerable<string> Rooms(HotelId hotel) =>
        Ranges[hotel].SelectMany(r => Enumerable.Range(r.From, r.To - r.From + 1)).Select(n => n.ToString());

    public static bool Contains(HotelId hotel, string roomNo) =>
        int.TryParse(roomNo, out var n) && Ranges[hotel].Any(r => n >= r.From && n <= r.To);
}
