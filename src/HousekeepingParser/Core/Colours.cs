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
