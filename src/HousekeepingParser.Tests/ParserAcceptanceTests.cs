using HousekeepingParser.Core;
using HousekeepingParser.Parsing;
using Xunit;

namespace HousekeepingParser.Tests;

/// <summary>Must-pass numbers from plan sections 2 and 16: the reader must reproduce the sheets' own totals exactly.</summary>
public class ParserAcceptanceTests
{
    public static TheoryData<string, HotelId, string, int, int, int, int, int, decimal> Summaries => new()
    {
        { Fixtures.Novotel0710, HotelId.Novotel, "2026-07-10 07:23", 6, 103, 64, 103, 0, 238.5m },
        { Fixtures.Novotel0828, HotelId.Novotel, "2026-08-28 13:36", 4, 74, 2, 62, 12, 170.5m },
        { Fixtures.Novotel0830, HotelId.Novotel, "2026-08-30 10:19", 5, 112, 52, 112, 0, 255.5m },
        { Fixtures.Ibis0830, HotelId.IbisBudget, "2026-08-30 17:34", 4, 116, 0, 67, 49, 218.5m },
    };

    [Theory]
    [MemberData(nameof(Summaries))]
    public void Reads_header_and_summary_exactly(string file, HotelId hotel, string published, int sheets, int rooms, int departures, int dirty, int clean, decimal credits)
    {
        var r = Fixtures.Report(file);

        Assert.True(r.IsTaskSheetReport);
        Assert.Equal(hotel, r.Header!.HotelId);
        Assert.Equal(DateTime.Parse(published), r.Header.PublishedAtLocal);
        Assert.Equal(new ReportSummary(sheets, rooms, departures, dirty, clean, credits), r.Summary);
    }

    [Theory]
    [MemberData(nameof(Summaries))]
    public void Parsed_rows_match_the_totals_page(string file, HotelId hotel, string published, int sheets, int rooms, int departures, int dirty, int clean, decimal credits)
    {
        var r = Fixtures.Report(file);

        Assert.Equal(hotel, r.Header!.HotelId);
        Assert.Equal(published[..10], r.Header.ReportDate.ToString("yyyy-MM-dd"));
        Assert.Equal(rooms, r.Rooms.Count);
        Assert.Equal(sheets, r.Sheets.Count);
        Assert.Equal(dirty, r.Rooms.Count(x => x.RoomStatus == "DI"));
        Assert.Equal(clean, r.Rooms.Count(x => x.RoomStatus == "CL"));
        Assert.Equal(departures, r.Rooms.Count(x => x.ReservationStatus == "Due Out"));
        Assert.Equal(credits, r.Rooms.Sum(x => x.Credits ?? 0));
        Assert.Empty(r.Warnings);
    }

    public static TheoryData<string, string[], int[], int[], int[]> Sheets => new()
    {
        { Fixtures.Novotel0710, ["1", "2", "3", "4", "5", "6"], [22, 13, 27, 14, 9, 18], [29, 14, 36, 16, 9, 19], [2, 0, 2, 0, 0, 0] },
        { Fixtures.Novotel0828, ["1", "2", "3", "4"], [17, 15, 22, 20], [20, 22, 25, 21], [3, 2, 2, 0] },
        { Fixtures.Novotel0830, ["1", "2", "3", "4", "FLOAT"], [25, 21, 24, 22, 20], [34, 31, 31, 26, 26], [0, 3, 3, 1, 3] },
        { Fixtures.Ibis0830, ["1", "2", "3", "FLOAT"], [27, 17, 30, 42], [33, 18, 36, 56], [0, 1, 0, 0] },
    };

    [Theory]
    [MemberData(nameof(Sheets))]
    public void Rooms_adults_and_children_per_sheet(string file, string[] sheetNos, int[] rooms, int[] adults, int[] children)
    {
        var r = Fixtures.Report(file);

        Assert.Equal(sheetNos, r.Sheets.Select(s => s.SheetNo));
        for (var i = 0; i < sheetNos.Length; i++)
        {
            var onSheet = r.Rooms.Where(x => x.SheetNo == sheetNos[i]).ToList();
            Assert.Equal(rooms[i], onSheet.Count);
            Assert.Equal(adults[i], onSheet.Sum(x => x.Adults ?? 0));
            Assert.Equal(children[i], onSheet.Sum(x => x.Children ?? 0));
            // The sheet's own printed totals agree as well (second cross-check per sheet).
            Assert.Equal(rooms[i], r.Sheets[i].Totals!.Rooms);
            Assert.Equal(adults[i], r.Sheets[i].Totals!.Adults);
        }
    }

    [Fact]
    public void Ibis_sheet_that_spans_two_pages_is_one_sheet()
    {
        var r = Fixtures.Report(Fixtures.Ibis0830);
        var floatSheet = r.Sheets.Single(s => s.SheetNo == "FLOAT");

        Assert.Equal(4, floatSheet.FirstPage);
        Assert.Equal(5, floatSheet.LastPage);
        Assert.Equal(42, floatSheet.Totals!.Rooms);
    }

    [Fact]
    public void Room_row_cells_are_read_into_the_right_columns()
    {
        var r = Fixtures.Report(Fixtures.Ibis0830);
        var room303 = r.Rooms.Single(x => x.RoomNo == "303");

        Assert.Equal("2", room303.SheetNo);
        Assert.Equal("CL", room303.RoomStatus);
        Assert.Equal(2m, room303.Credits);
        Assert.Equal("Arrived", room303.ReservationStatus);
        Assert.Equal(new DateOnly(2026, 8, 30), room303.Arrival);
        Assert.Equal(new DateOnly(2026, 8, 31), room303.Departure);
        Assert.Equal(1, room303.Adults);
        Assert.Equal(1, room303.Children);
        Assert.Equal("Tasks:CLN(Linen Change)", room303.Tasks);

        var room117 = r.Rooms.Single(x => x.RoomNo == "117");
        Assert.Equal("", room117.ReservationStatus);
        Assert.Null(room117.Arrival);

        var room210 = r.Rooms.Single(x => x.RoomNo == "210");
        Assert.Equal("!", room210.Marker);
    }

    [Fact]
    public void Reservation_words_found_in_the_real_files_are_kept_as_text()
    {
        var words = Fixtures.All.SelectMany(f => Fixtures.Report(f).Rooms).Select(r => r.ReservationStatus).ToHashSet();

        Assert.Subset(new HashSet<string> { "Stayover", "Due Out", "Departed", "Arrived", "Arrival", "Day Use", "" }, words);
    }

    [Fact]
    public void Room_positions_are_on_the_room_number()
    {
        var r = Fixtures.Report(Fixtures.Ibis0830);
        var room111 = r.Rooms.Single(x => x.RoomNo == "111");

        Assert.Equal(1, room111.Position.Page);
        Assert.InRange(room111.Position.Left, 14, 16);
        Assert.InRange((room111.Position.Top + room111.Position.Bottom) / 2, 527, 530);
    }

    [Fact]
    public void A_file_without_the_title_is_not_a_task_sheet()
    {
        var page = new PdfPage(1, 595, 842, [new PdfLine("Invoice 2026", 800, 790, 10, 100, [new PdfWord("Invoice", 10, 50, 800, 790), new PdfWord("2026", 55, 100, 800, 790)])]);

        var r = TaskSheetParser.Parse([page]);

        Assert.False(r.IsTaskSheetReport);
        Assert.Null(r.Summary);
        Assert.Empty(r.Rooms);
    }

    [Fact]
    public void Unknown_hotel_name_is_not_mapped()
    {
        Assert.Null(Hotels.FromPrintedName("Mercure Münster City"));
        Assert.Equal(HotelId.Novotel, Hotels.FromPrintedName("Novotel  Münster City"));
        Assert.Equal(HotelId.IbisBudget, Hotels.FromPrintedName("IBIS BUDGET Münster City"));
    }
}
