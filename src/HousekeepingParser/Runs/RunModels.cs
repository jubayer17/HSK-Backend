using HousekeepingParser.Coloring;
using HousekeepingParser.Core;
using HousekeepingParser.Parsing;

namespace HousekeepingParser.Runs;

public static class StepKeys
{
    public const string Rhythm = "rhythm";
    public const string Read = "read";
    public const string Check = "check";
    public const string Save = "save";
    public const string Colour = "colour";
    public const string Mark = "mark";

    public static readonly (string Key, string Label)[] All =
    [
        (Rhythm, "Check cleaning rhythm"),
        (Read, "Read the PDF"),
        (Check, "Check the file"),
        (Save, "Save rooms and totals"),
        (Colour, "Assign colours"),
        (Mark, "Create the PDF report"),
    ];
}

public sealed class ValidationOptions
{
    public const string SectionName = "Validation";

    /// <summary>Check 3: the sheet must be printed within this many hours before the upload. Switch off only for tests with old sample files.</summary>
    public bool EnforceSheetAge { get; set; } = true;
    public int MaxSheetAgeHours { get; set; } = 24;
}

public sealed record UploadedFile(string FileName, byte[] Bytes);

public sealed record NewRequest(HotelId HotelId, string RoomNo, RequestType Type, ColourName? Colour, string? Note);

/// <summary>Output of the read step, passed through the orchestration to the save step. Contains no names.</summary>
public sealed record ReadOutcome(
    Guid FileId,
    int UploadOrder,
    bool Ok,
    HotelId? HotelId,
    DateTime? PublishedAtLocal,
    TaskSheetReport? Report);
