namespace HousekeepingParser.Core;

public enum ColourName
{
    Red,
    Green,
    Purple,
    Blue,
    Gold,
}

public enum RhythmType
{
    TwoTages,
    ThreeTages,
    /// <summary>Logic 3: Green daily; cleaned on request if a cleaner is free.</summary>
    OneNight,
}

/// <summary>logic_version: 1 = 2-Tages, 2 = 3-Tages, 3 = 1-Night.</summary>
public static class Logics
{
    public static readonly RhythmType[] All = [RhythmType.TwoTages, RhythmType.ThreeTages, RhythmType.OneNight];

    public static byte Version(RhythmType rhythm) => rhythm switch
    {
        RhythmType.TwoTages => 1,
        RhythmType.ThreeTages => 2,
        RhythmType.OneNight => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(rhythm)),
    };

    public static RhythmType FromVersion(int version) => All[version - 1];
}

public static class Colours
{
    /// <summary>Marker colours from the Phase 1 handoff (Gold for special requests).</summary>
    public static readonly IReadOnlyDictionary<ColourName, string> Hex = new Dictionary<ColourName, string>
    {
        [ColourName.Red] = "#E74C3C",
        [ColourName.Green] = "#2ECC71",
        [ColourName.Purple] = "#9B59B6",
        [ColourName.Blue] = "#3498DB",
        [ColourName.Gold] = "#D4A017",
    };
}
