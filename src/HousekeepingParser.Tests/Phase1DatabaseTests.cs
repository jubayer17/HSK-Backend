using Dapper;
using HousekeepingParser.Audit;
using HousekeepingParser.Coloring;
using HousekeepingParser.Core;
using HousekeepingParser.Operations;
using HousekeepingParser.Parsing;
using HousekeepingParser.Runs;
using HousekeepingParser.PdfMarking;
using HousekeepingParser.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace HousekeepingParser.Tests;

public sealed partial class DatabaseTests
{
    private static DailyCloseService Closes(Env e) => new(e.Db, new RoomCheckService(e.Db, new SqlAuditLog(), e.Clock), new SqlAuditLog(), e.Clock);

    [DockerFact]
    public async Task Invalid_data_layout_is_rejected_and_moved_to_failed_pdf()
    {
        var e = await CreateAsync();
        await SetBothRhythms(e);
        var id = await e.Runs.CreateAsync(e.Operations,
            [new UploadedFile("one.pdf", SyntheticPdf.Create(6, Fixtures.Ibis0830)),
             new UploadedFile("two.pdf", SyntheticPdf.Create(6, Fixtures.Novotel0830))], [], default);
        var pipeline = new MorningRunPipeline(e.Db, new RunStore(e.Db), e.Blobs, new MissingHeadingExtractor(),
            new ColorRuleService(), e.Rhythms, new PdfSharpMarker(), new PdfSharpReportRenderer(), e.Clock,
            Options.Create(new ValidationOptions { EnforceSheetAge = false }), NullLogger<MorningRunPipeline>.Instance);
        var file = (await pipeline.ListFilesAsync(id, default))[0];
        Assert.False((await pipeline.ReadFileAsync(file, default)).Ok);
        await using var c = await e.Db.OpenAsync();
        Assert.Equal("LAYOUT", await c.ExecuteScalarAsync<string>("SELECT reject_code FROM dbo.phase1_run_file WHERE file_id = @file", new { file }));
        Assert.Contains(e.Blobs.Blobs.Keys, k => k.Container == Containers.Failed);
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.room_rows WHERE file_id = @file", new { file }));
    }

    private sealed class MissingHeadingExtractor : ICompliantPdfExtractor
    {
        public async Task<PdfExtractionResult> ExtractAsync(byte[] bytes, string name, CancellationToken ct)
        {
            var result = await new FixtureExtractor().ExtractAsync(bytes, name, ct);
            var pages = result.Pages.ToList();
            pages[0] = pages[0] with { Lines = pages[0].Lines.Select(l =>
            {
                var words = l.Words.Where(w => w.Text != "Resv").ToList();
                return l with { Words = words, Text = string.Join(' ', words.Select(w => w.Text)) };
            }).ToList() };
            return result with { Pages = pages };
        }
    }

    [DockerFact]
    public async Task Cleaner_break_minutes_are_saved_and_read_without_changing_the_hours_API()
    {
        var e = await CreateAsync();
        var closes = Closes(e);
        var date = new DateOnly(2026, 10, 9);
        var input = (await closes.GetAsync(date, default)).Input;
        input = input with { Hotels = input.Hotels.Select(h => h with
        {
            Cleaners = [new CloseCleaner(null, "Test Cleaner", 1, 0, 0, 0, new TimeOnly(8, 0), new TimeOnly(12, 0), 0.5m)],
        }).ToList() };
        var saved = await closes.SaveAsync(e.Operations, date, input, default);
        Assert.All(saved.Input.Hotels, h => Assert.Equal(0.5m, Assert.Single(h.Cleaners).PauseHours));
        await using var c = await e.Db.OpenAsync();
        Assert.All(await c.QueryAsync<decimal>("SELECT break_minutes FROM dbo.daily_close_cleaner"), minutes => Assert.Equal(30m, minutes));
        // Minutes are authoritative even when an older compatibility value differs.
        await c.ExecuteAsync("UPDATE dbo.daily_close_cleaner SET break_minutes = 15");
        Assert.All((await closes.GetAsync(date, default)).Input.Hotels, h => Assert.Equal(0.25m, Assert.Single(h.Cleaners).PauseHours));
    }

    [DockerFact]
    public async Task Uncleaned_room_move_is_added_once_to_Liegezimmer_and_saved()
    {
        var e = await CreateAsync();
        var moves = new RoomMoveService(e.Db, new SqlAuditLog(), e.Clock);
        await moves.AddAsync(e.Operations, new NewRoomMove(HotelId.Novotel, "214", null, false, "External", null, null), default);
        await moves.AddAsync(e.Operations, new NewRoomMove(HotelId.Novotel, "214", null, false, "External", null, null), default);
        var closes = Closes(e);
        var view = await closes.GetAsync(e.Clock.Today(), default);
        Assert.Equal(["214"], view.Hotel(HotelId.Novotel).Input.RoomsOf(RoomCategory.LIEGEZIMMER));
        await closes.SaveAsync(e.Operations, view.Date, view.Input, default);
        await using var c = await e.Db.OpenAsync();
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.daily_close_room WHERE room_no = '214' AND category = 'LIEGEZIMMER'"));
    }

    [DockerFact]
    public async Task Yesterday_Liegezimmer_does_not_create_waiting_for_a_new_stayover()
    {
        var e = await CreateAsync();
        e.Clock.UtcNow = new DateTime(2026, 8, 1, 1, 0, 0, DateTimeKind.Utc);
        await e.Rhythms.SetAsync(e.Director, HotelId.Novotel, RhythmType.OneNight, new DateOnly(2026, 8, 1), null, default);
        await e.Rhythms.SetAsync(e.Director, HotelId.IbisBudget, RhythmType.OneNight, new DateOnly(2026, 8, 1), null, default);
        e.Clock.UtcNow = new DateTime(2026, 8, 30, 10, 0, 0, DateTimeKind.Utc);
        var target = Fixtures.Report(Fixtures.Novotel0830).Rooms.First(r => r.ReservationStatus == "Stayover"
            && r.Arrival < new DateOnly(2026, 8, 30)).RoomNo;
        var date = new DateOnly(2026, 8, 29);
        var closes = Closes(e);
        var input = (await closes.GetAsync(date, default)).Input;
        input = input with { Hotels = input.Hotels.Select(h => h with
        {
            Rooms = Enum.GetValues<RoomCategory>().ToDictionary(cat => cat, cat => (IReadOnlyList<string>)
                (h.Hotel == HotelId.Novotel && cat == RoomCategory.LIEGEZIMMER ? new[] { target } : [])),
        }).ToList() };
        await closes.ConfirmAsync(e.Operations, date, input, default);
        var id = await RunAsync(e, e.Operations,
            [("novotel.pdf", Fixtures.Novotel0830, 6), ("ibis.pdf", Fixtures.Ibis0830, 6)]);
        var hotel = (await e.Runs.GetAsync(id, default))!.Hotels.Single(h => h.HotelId == (int)HotelId.Novotel);
        var room = Assert.Single(hotel.Rooms, r => r.RoomNo == target);
        Assert.Equal("STAYOVER_GREEN", room.ServiceCode);
    }
}
