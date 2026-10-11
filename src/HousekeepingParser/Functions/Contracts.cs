namespace HousekeepingParser.Functions;

// HTTP shapes. Kept identical to client/src/lib/types.ts (camelCase JSON, enums as strings, hotelId as a number).

public sealed record UserDto(string Username, string DisplayName, string Role);

public sealed record RhythmPeriodDto(string Rhythm, string EffectiveFrom, string? Reason);

public sealed record HotelRhythmDto(int HotelId, RhythmPeriodDto? Active, RhythmPeriodDto? Scheduled);

public sealed record RhythmHistoryEntryDto(string Id, int HotelId, string Action, string Rhythm, string EffectiveFrom, string? Reason, string By, string At);

public sealed record RunStepDto(string Key, string Label, string Status, string? Message);

public sealed record ReportSummaryDto(int TotalTaskSheets, int TotalRooms, int DepartureRooms, int DirtyRooms, int CleanRooms, decimal TotalCredits);

public sealed record RoomResultDto(
    string RoomNo,
    string SheetNo,
    string RoomStatus,
    string ReservationStatus,
    string? Arrival,
    string? Departure,
    int? Adults,
    int? Children,
    int? Nights,
    string? Colour,
    string ServiceCode,
    string Reason,
    bool Cleaned);

public sealed record HotelRunResultDto(
    int? HotelId,
    string FileName,
    string Status,
    string? RejectReason,
    IReadOnlyList<RunStepDto> Steps,
    string? PublishedAt,
    ReportSummaryDto? Summary,
    int? ParsedRooms,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<RoomResultDto> Rooms,
    bool MarkedPdfAvailable);

public sealed record RunDto(
    string Id,
    string CreatedAt,
    string CreatedBy,
    string ReportDate,
    string Status,
    IReadOnlyList<HotelRunResultDto> Hotels);

public sealed record LoginRequest(string? Username, string? Password);

public sealed record SetRhythmRequest(int HotelId, string? Rhythm, string? EffectiveFrom, string? Reason, bool Confirmed);

public sealed record CancelRhythmRequest(string? Reason, bool Confirmed);

public sealed record PrePhaseRequestDto(int HotelId, string? RoomNo, string? Type, string? Colour, string? Note);

public sealed record StartRunResponse(string Id);

public sealed record ErrorDto(string Error);
