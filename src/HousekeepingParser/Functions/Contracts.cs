using System.Text.Json.Serialization;

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

// ---------- Stage 2 ----------

public sealed record NewRoomMoveDto(int HotelId, string? RoomNo, string? ToRoomNo, bool CleanerAvailable, string? CleanedByType, string? EmployeeName, string? Note);

public sealed record CreateRoomMoveRequest(NewRoomMoveDto? Move, bool Confirmed);

public sealed record RoomMoveDto(
    string Id, int HotelId, string RoomNo, string? ToRoomNo, bool CleanerAvailable, string CleanedByType, string? EmployeeName, string? Note,
    string MoveDate, string CleanDate, string Status, string CreatedBy, string CreatedAt);

public sealed record CleanerAvailabilityDto(int HotelId, bool Available);

public sealed record StaffMemberDto(string Id, int? HotelId, string Name, string Company, string Role, int? MonthlyCheckLimit, bool Active);

public sealed record SaveStaffRequest(int? HotelId, string? Name, string? Company, string? Role, int? MonthlyCheckLimit, bool Active, bool Confirmed);

public sealed record RoomCheckDto(string Id, int HotelId, string CheckDate, string? RoomNo, string Role, string? StaffId, string CheckedBy, string CreatedAt);

public sealed record NewRoomChecksRequest(int HotelId, string? Date, string? StaffId, string? Role, string? CheckedBy, IReadOnlyList<string>? Rooms, int? Count, bool Confirmed);

public sealed record CheckUsageDto(string Month, int PmUsed, int? PmLimit, int ApmUsed, int? ApmLimit, int Selbstchecks, IReadOnlyList<string> Warnings);

public sealed record HotelChecksDto(int HotelId, int Pm, int Apm, int Selbst);

public sealed record ChecksOverviewDto(string Date, IReadOnlyList<RoomCheckDto> Entries, IReadOnlyList<HotelChecksDto> Today, CheckUsageDto Month);

public sealed record CloseSupervisorDto(string Role, string? StaffId, string Name, string? Start, string? End, decimal PauseHours);

public sealed record CloseCleanerDto(string? StaffId, string Name, int Gereinigt, int Ns, int Rk, int Ks, string? Start, string? End, decimal PauseHours);

/// <summary>Keys stay upper case on the wire (camelCase would give "ns").</summary>
public sealed record CloseRoomsDto(
    [property: JsonPropertyName("NS")] IReadOnlyList<string>? NS,
    [property: JsonPropertyName("RK")] IReadOnlyList<string>? RK,
    [property: JsonPropertyName("KS")] IReadOnlyList<string>? KS,
    [property: JsonPropertyName("LATE_CHECKOUT")] IReadOnlyList<string>? LATE_CHECKOUT,
    [property: JsonPropertyName("LIEGEZIMMER")] IReadOnlyList<string>? LIEGEZIMMER);

public sealed record CloseHotelInputDto(
    int HotelId, string? RichterStaffName, bool FlatRateDay, int PauschaleChecks, int Sauber, int Abzuege, string? Informationen,
    IReadOnlyList<CloseCleanerDto>? Cleaners, CloseRoomsDto? Rooms);

public sealed record DailyCloseInputDto(
    string? Comment, decimal ExtraReinigungHours, IReadOnlyList<CloseSupervisorDto>? Supervisors, IReadOnlyList<CloseHotelInputDto>? Hotels, bool Confirmed = false);

public sealed record CloseSheetDto(int TotalTaskSheets, int TotalRooms, int DepartureRooms, int DirtyRooms, int CleanRooms, decimal TotalCredits, string? PublishedAt);

public sealed record CloseChecksDto(int Pm, int Apm, int Selbst);

public sealed record CloseTotalsDto(int Gereinigt, int Ns, int Rk, int Ks, int Sauber, int Liegezimmer, int Summe, int ChecksActual, int ZuBerechnen);

public sealed record CloseHotelDto(
    int HotelId, string RichterStaffName, bool FlatRateDay, int PauschaleChecks, int Sauber, int Abzuege, string Informationen,
    IReadOnlyList<CloseCleanerDto> Cleaners, CloseRoomsDto Rooms, CloseSheetDto? Sheet, CloseChecksDto Checks, int RoomMoves, CloseTotalsDto Totals,
    int OtherFlatRateDays);

public sealed record CloseIssueDto(string Level, int? HotelId, string Message);

public sealed record EmailLogEntryDto(string Id, string Kind, string Period, IReadOnlyList<string> Recipients, string Status, string? Detail, string At);

public sealed record DailyCloseDto(
    string Date, string Status, string Comment, decimal ExtraReinigungHours, IReadOnlyList<CloseSupervisorDto> Supervisors,
    IReadOnlyList<CloseHotelDto> Hotels, IReadOnlyList<CloseIssueDto> Issues, string? UpdatedBy, string? UpdatedAt,
    string? ConfirmedBy, string? ConfirmedAt, IReadOnlyList<EmailLogEntryDto> Emails);

public sealed record DailyCloseListItemDto(string Date, string Status, string? ConfirmedBy, string? ConfirmedAt, IReadOnlyDictionary<string, int> Rooms, bool Emailed);

public sealed record ReopenRequest(string? Reason, bool Confirmed);

public sealed record ConfirmOnlyRequest(bool Confirmed);

public sealed record MonthlyHotelCellsDto(int Zimmer, int Gecheckt, int ZuBerechnen, int Abzuege);

public sealed record MonthlyRowDto(
    int Day, string? Date, string Status, MonthlyHotelCellsDto? Novotel, MonthlyHotelCellsDto? Ibis,
    decimal? Hausmann, decimal? FloorSupervisor, decimal? HausdameAssistenz, decimal? Hausdame, decimal? ExtraReinigung, string? Kommentar);

public sealed record MonthlyTotalsDto(
    MonthlyHotelCellsDto Novotel, MonthlyHotelCellsDto Ibis, decimal Hausmann, decimal FloorSupervisor, decimal HausdameAssistenz, decimal Hausdame, decimal ExtraReinigung);

public sealed record MonthlyStatementDto(
    string Month, string Objekt, string Kostenstelle, string Zeitraum, IReadOnlyList<MonthlyRowDto> Rows, MonthlyTotalsDto Totals,
    int ConfirmedDays, int DaysInMonth, IReadOnlyList<EmailLogEntryDto> Emails);

public sealed record PricesDto(
    decimal? RoomNovotel, decimal? RoomIbis, decimal? Check, decimal? Hausdame, decimal? HausdameAssistenz, decimal? FloorSupervisor,
    decimal? Hausmann, decimal? ExtraReinigung, decimal? InternalRoom);

public sealed record AppSettingsDto(
    string Objekt, string Kostenstelle, int PauschaleChecks, string PauschaleLabel, bool EmailEnabled, PricesDto Prices,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Recipients, string EmailTransport);

public sealed record SaveSettingsRequest(
    string? Objekt, string? Kostenstelle, int PauschaleChecks, string? PauschaleLabel, bool EmailEnabled, PricesDto? Prices,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? Recipients, bool Confirmed);

public sealed record DashboardDayDto(string Date, decimal? ExternalBilling, int Rooms, string Basis);

public sealed record DashboardFinancialsDto(
    decimal? ExternalBilling, int ExternalRoomsCleaned, decimal? InternalLaborCost, int InternalRoomsCleaned, int ChecksBilled, decimal SupervisorHours, bool PricesSet);

public sealed record DashboardWorkloadDto(
    int TotalRoomsServiced, int RoomMoves, int RoomMovesOpen, int Red, int Blue, int Purple, int Green, int Gold, int GreenNoService, int Waiting, int Liegezimmer);

public sealed record DashboardQaDto(
    string Month, int PmUsed, int? PmLimit, int ApmUsed, int? ApmLimit, int Selbstchecks, IReadOnlyList<string> Warnings, int RangePm, int RangeApm, int RangeSelbst);

public sealed record LogicComparisonDto(string Logic, int Services, decimal? Cost, bool Active);

public sealed record DashboardClosesDto(int Confirmed, int Days, string TodayStatus);

public sealed record DashboardMetricsDto(
    string Timeframe, string From, string To, object Hotel, string Basis, DashboardFinancialsDto Financials, DashboardWorkloadDto Workload,
    DashboardQaDto Qa, IReadOnlyList<DashboardDayDto> Days, IReadOnlyList<LogicComparisonDto> LogicComparison, DashboardClosesDto Closes);
