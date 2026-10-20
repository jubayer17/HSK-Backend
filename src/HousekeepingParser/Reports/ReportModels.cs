using HousekeepingParser.Core;
using HousekeepingParser.Parsing;

namespace HousekeepingParser.Reports;

// Report PDF inputs, built from the confirmed daily close.

/// <summary>A staff line with times worked. Name null = nobody ("/").</summary>
public sealed record SupervisorLine(string Label, string? Name, TimeOnly? Start, TimeOnly? End);

/// <summary>A cleaner row. Numbers null = did not work (empty row).</summary>
public sealed record CleanerLine(string Name, int? Gereinigt, int? Ns, int? Rk, int? Ks, TimeOnly? Start, TimeOnly? End, decimal? PauseHours);

/// <summary>Totals block; the parts add up to Summe.</summary>
public sealed record DailyTotals(int Gereinigt, int NoService, int RoteKarten, int KeinService, int Sauber, int Liegezimmer, int Summe);

/// <summary>"Task Sheet Report" page, one per hotel and day.</summary>
public sealed record TaskSheetReportModel(
    HotelId Hotel,
    DateOnly ReportDate,
    DateTime GeneratedAtLocal,
    /// <summary>Opera "Report Summary"; null when no sheet was processed.</summary>
    ReportSummary? OperaSummary,
    /// <summary>Always six lines: HD, HDA, FS, RPH FS, HM, RPH HM.</summary>
    IReadOnlyList<SupervisorLine> Supervisors,
    IReadOnlyList<CleanerLine> Cleaners,
    DailyTotals Totals,
    /// <summary>Flat-rate checks of the day; 0 if not a flat-rate day.</summary>
    int PauschaleChecks,
    /// <summary>Printed after the number, e.g. "5T/W".</summary>
    string PauschaleLabel,
    bool FlatRateDay,
    int ChecksActual,
    int ZuBerechnen,
    /// <summary>Who confirmed the close ("Freigegeben von … am …").</summary>
    string? ApprovedBy,
    DateTime? ApprovedAtLocal);

/// <summary>"Übergabe Etage – Empfang", one per hotel and day.</summary>
public sealed record HandoverModel(
    HotelId Hotel,
    DateOnly ReportDate,
    DateTime GeneratedAtLocal,
    /// <summary>"Name MA Richter".</summary>
    string? RichterStaffName,
    IReadOnlyList<string> NoService,
    IReadOnlyList<string> RoteKarte,
    IReadOnlyList<string> KeinService,
    IReadOnlyList<string> LateCheckOut,
    /// <summary>Liegezimmer: up to 30 on the form, the rest on a separate page.</summary>
    IReadOnlyList<string> Liegezimmer,
    string? Informationen,
    string? ApprovedBy,
    DateTime? ApprovedAtLocal);

/// <summary>Novotel or ibis cells of one day on the Leistungsnachweis.</summary>
public sealed record MonthlyHotelCells(int Zimmer, int Gecheckt, int ZuBerechnen, int Abzuege);

/// <summary>One day row. Null cells stay empty (unconfirmed or no such day).</summary>
public sealed record MonthlyRowModel(
    int Day,
    bool InMonth,
    MonthlyHotelCells? Novotel,
    MonthlyHotelCells? Ibis,
    decimal? Hausmann,
    decimal? FloorSupervisor,
    decimal? HausdameAssistenz,
    decimal? Hausdame,
    decimal? ExtraReinigung,
    string? Kommentar);

public sealed record MonthlyTotals(
    MonthlyHotelCells Novotel,
    MonthlyHotelCells Ibis,
    decimal Hausmann,
    decimal FloorSupervisor,
    decimal HausdameAssistenz,
    decimal Hausdame,
    decimal ExtraReinigung);

/// <summary>"Leistungsnachweis": monthly statement for both hotels.</summary>
public sealed record MonthlyStatementModel(
    /// <summary>"Novotel / Ibis Budget Münster".</summary>
    string Objekt,
    /// <summary>"4900".</summary>
    string Kostenstelle,
    DateOnly Month,
    /// <summary>"Sep. 26".</summary>
    string Zeitraum,
    /// <summary>Always 31 rows, as on the paper form.</summary>
    IReadOnlyList<MonthlyRowModel> Rows,
    MonthlyTotals Totals,
    int ConfirmedDays,
    DateTime GeneratedAtLocal,
    string? ApprovedBy,
    DateTime? ApprovedAtLocal);

public interface IBusinessReportRenderer
{
    byte[] TaskSheetReport(TaskSheetReportModel model);
    byte[] Handover(HandoverModel model);
    byte[] MonthlyStatement(MonthlyStatementModel model);
}
