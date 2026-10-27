using HousekeepingParser.Parsing;
using HousekeepingParser.Runs;
using Xunit;

namespace HousekeepingParser.Tests;

/// <summary>Edge cases and validation rules from the Phase 1 GO decision (sections 4 and 7).</summary>
public class ParserEdgeCaseTests
{
    // ---------- synthetic pages in the Opera layout ----------

    private static PdfLine Line(double top, params (string Text, double Left)[] words)
    {
        var ws = words.Select(w => new PdfWord(w.Text, w.Left, w.Left + w.Text.Length * 4, top, top - 8)).ToList();
        return new PdfLine(string.Join(' ', ws.Select(w => w.Text)), top, top - 8, ws[0].Left, ws[^1].Right, ws);
    }

    private static PdfLine Row(double top, string room, string status = "", string resv = "", string arrival = "", string departure = "",
        string adults = "", string children = "", string tasks = "")
    {
        var words = new List<(string, double)> { (room, 15) };
        if (status.Length > 0) words.Add((status, 40));
        if (resv.Length > 0) words.AddRange(resv.Split(' ').Select((w, i) => (w, 96 + i * 18.0)));
        if (arrival.Length > 0) words.Add((arrival, 138));
        if (departure.Length > 0) words.Add((departure, 177));
        if (adults.Length > 0) words.Add((adults, 225));
        if (children.Length > 0) words.Add((children, 242));
        if (tasks.Length > 0) words.Add((tasks, 265));
        return Line(top, words.ToArray());
    }

    private static PdfPage Page(int no, bool signature, params PdfLine[] rows)
    {
        var lines = new List<PdfLine>
        {
            Line(780, ("Novotel", 240), ("Münster", 270), ("City", 305), ("30.08.26", 330)),
            Line(768, ("Task", 240), ("Sheet", 260), ("Report", 285), ("10:19", 315)),
            Line(725, ("30.08.26/CLN,GLC,GPS,INS/1", 99)),
        };
        if (signature) lines.Add(Line(700, ("Room", 10), ("Details", 32)));
        lines.Add(signature
            ? Line(680, ("Room", 15), ("Status", 40), ("Credits", 65), ("Resv", 96), ("Arrival", 138), ("Departure", 177), ("Tasks", 265))
            : Line(680, ("Room", 15), ("Status", 40), ("Credits", 65), ("Arrival", 138), ("Departure", 177), ("Tasks", 265)));
        lines.AddRange(rows);
        return new PdfPage(no, 595, 842, lines);
    }

    // ---------- edge cases ----------

    [Fact]
    public void Due_Out_in_two_words_is_one_reservation_status()
    {
        var r = TaskSheetParser.Parse([Page(1, true, Row(660, "101", "DI", "Due Out", "29.08.26", "30.08.26", "2", "", "Tasks:CLN"))]);

        Assert.Equal("Due Out", r.Rooms.Single().ReservationStatus);
    }

    [Fact]
    public void One_number_after_the_dates_is_adults_and_no_children()
    {
        var r = TaskSheetParser.Parse([Page(1, true, Row(660, "101", "DI", "Stayover", "23.10.26", "24.10.26", "1", "", "Tasks:CLN"))]);

        var room = r.Rooms.Single();
        Assert.Equal(1, room.Adults);
        Assert.Equal(0, room.Children);
    }

    [Fact]
    public void Incomplete_row_keeps_missing_fields_empty()
    {
        var r = TaskSheetParser.Parse([Page(1, true, Row(660, "214", "DI", adults: "2", tasks: "Tasks:CLN"))]);

        var room = r.Rooms.Single();
        Assert.Equal("214", room.RoomNo);
        Assert.Equal("", room.ReservationStatus);
        Assert.Null(room.Arrival);
        Assert.Null(room.Departure);
        Assert.Null(room.Credits);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void Day_Use_is_read_as_a_reservation_status()
    {
        var r = TaskSheetParser.Parse([Page(1, true, Row(660, "402", "DI", "Day Use", "30.08.26", "30.08.26", "2", "", "Tasks:CLN"))]);

        Assert.Equal("Day Use", r.Rooms.Single().ReservationStatus);
    }

    [Theory]
    [InlineData("Tasks:GLCL", "Tasks:GLC")]
    [InlineData("Tasks:CLM", "Tasks:CLN")]
    [InlineData("Tasks:CLN(Linen Change)", "Tasks:CLN(Linen Change)")]
    [InlineData("Tasks:CLN,INSS", "Tasks:CLN,INS")]
    public void Misprinted_task_codes_are_mapped_to_the_known_code(string printed, string expected)
    {
        var warnings = new List<ParseWarning>();

        Assert.Equal(expected, TaskSheetParser.CanonicalTasks(printed, "CLN,GLC,GPS,INS", "101", warnings));
        Assert.Equal(printed == expected ? 0 : 1, warnings.Count(w => w.Code == "TASK_CODE"));
    }

    [Fact]
    public void Unknown_task_code_is_kept_as_printed_with_a_warning()
    {
        var warnings = new List<ParseWarning>();

        Assert.Equal("Tasks:XYZQ", TaskSheetParser.CanonicalTasks("Tasks:XYZQ", "CLN,GLC,GPS,INS", "101", warnings));
        Assert.Contains(warnings, w => w.Code == "TASK_CODE");
    }

    [Fact]
    public void Cover_sheet_with_bare_room_numbers_is_skipped()
    {
        var cover = Page(1, false, Row(660, "103"), Row(648, "104"), Row(636, "105"));
        var data = Page(2, true, Row(660, "201", "DI", "Stayover", "29.08.26", "31.08.26", "2", "", "Tasks:CLN"));

        var r = TaskSheetParser.Parse([cover, data]);

        Assert.Equal(["201"], r.Rooms.Select(x => x.RoomNo));
        Assert.Contains(r.Warnings, w => w.Code == "COVER_SHEET");
        Assert.Empty(r.PagesWithoutLayout);
    }

    // ---------- validation ----------

    [Fact]
    public void Data_page_without_the_layout_signature_is_reported()
    {
        var r = TaskSheetParser.Parse([Page(1, false, Row(660, "101", "DI", "Stayover", "29.08.26", "31.08.26", "2", "", "Tasks:CLN"))]);

        Assert.Equal([1], r.PagesWithoutLayout);
    }

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void Real_samples_have_the_layout_signature_and_balanced_totals(string file)
    {
        var r = Fixtures.Report(file);

        Assert.Empty(r.PagesWithoutLayout);
        Assert.Empty(MorningRunPipeline.TotalsWarnings(r));
    }

    public static TheoryData<string> AllFixtures => new(Fixtures.All);

    [Fact]
    public void Departure_and_credit_mismatches_are_warnings()
    {
        var r = Fixtures.Report(Fixtures.Novotel0830);
        var changed = r with { Summary = r.Summary! with { DepartureRooms = r.Summary.DepartureRooms + 1, TotalCredits = r.Summary.TotalCredits + 0.5m } };

        var codes = MorningRunPipeline.TotalsWarnings(changed).Select(w => w.Code).ToList();

        Assert.Contains("DEPARTURES", codes);
        Assert.Contains("CREDITS", codes);
    }
}
