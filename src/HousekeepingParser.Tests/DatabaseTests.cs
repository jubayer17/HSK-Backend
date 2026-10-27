using System.Text.RegularExpressions;
using Dapper;
using HousekeepingParser.Accounts;
using HousekeepingParser.Audit;
using HousekeepingParser.Coloring;
using HousekeepingParser.Core;
using HousekeepingParser.Data;
using HousekeepingParser.PdfMarking;
using HousekeepingParser.Rhythm;
using HousekeepingParser.Runs;
using HousekeepingParser.Storage;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Testcontainers.MsSql;
using Xunit;

namespace HousekeepingParser.Tests;

/// <summary>One throw-away SQL Server for all database tests (plan 16: integration tests with Testcontainers).</summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private MsSqlContainer? _container;
    private int _databases;

    public async Task InitializeAsync()
    {
        if (!DockerCheck.Available) return;
        _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04").Build();
        await _container.StartAsync();
    }

    /// <summary>A fresh, migrated database per test.</summary>
    public async Task<string> NewDatabaseAsync()
    {
        var builder = new SqlConnectionStringBuilder(_container!.GetConnectionString())
        {
            InitialCatalog = $"hsk_test_{Interlocked.Increment(ref _databases)}",
        };
        var cs = builder.ConnectionString;
        await new DbMigrator(Options.Create(new DatabaseOptions { ConnectionString = cs, CreateIfMissing = true }), NullLogger<DbMigrator>.Instance).MigrateAsync();
        return cs;
    }

    public async Task DisposeAsync()
    {
        if (_container is not null) await _container.DisposeAsync();
    }
}

[CollectionDefinition("sql")]
public sealed class SqlCollection : ICollectionFixture<SqlServerFixture>;

[Collection("sql")]
public sealed partial class DatabaseTests(SqlServerFixture sql)
{
    // 10.10.2026, 08:00 Berlin
    private static DateTime Oct10 => new(2026, 10, 10, 6, 0, 0, DateTimeKind.Utc);

    private sealed class Env
    {
        public required string ConnectionString { get; init; }
        public required FakeClock Clock { get; init; }
        public required IDbConnectionFactory Db { get; init; }
        public required RhythmService Rhythms { get; init; }
        public required AuthService Auth { get; init; }
        public required RunService Runs { get; init; }
        public required MorningRunPipeline Pipeline { get; init; }
        public required MemoryBlobStore Blobs { get; init; }
        public required CurrentUser Director { get; init; }
        public required CurrentUser Operations { get; init; }
    }

    private async Task<Env> CreateAsync()
    {
        var cs = await sql.NewDatabaseAsync();
        var clock = new FakeClock(Oct10);
        var db = new SqlConnectionFactory(Options.Create(new DatabaseOptions { ConnectionString = cs }));
        var audit = new SqlAuditLog();
        var auth = new AuthService(db, Options.Create(new AuthOptions
        {
            SeedUsers =
            [
                new SeedUser { Username = "director", DisplayName = "Hotel Director", Role = "Director", Password = "test-pass" },
                new SeedUser { Username = "operations", DisplayName = "Operations", Role = "Operations", Password = "test-pass" },
            ],
        }), clock, NullLogger<AuthService>.Instance);
        await auth.SeedUsersAsync(default);

        async Task<CurrentUser> SignIn(string name)
        {
            var (_, token, _) = await auth.LoginAsync(name, "test-pass", default);
            return (await auth.ResolveAsync(token, default))!;
        }

        var rhythms = new RhythmService(db, audit, clock, NullLogger<RhythmService>.Instance);
        var blobs = new MemoryBlobStore();
        var store = new RunStore(db);
        return new Env
        {
            ConnectionString = cs,
            Clock = clock,
            Db = db,
            Rhythms = rhythms,
            Auth = auth,
            Blobs = blobs,
            Runs = new RunService(db, blobs, rhythms, audit, clock, new HousekeepingParser.Operations.RoomMoveService(db, audit, clock)),
            Pipeline = new MorningRunPipeline(db, store, blobs, new FixtureExtractor(), new ColorRuleService(), rhythms, new PdfSharpMarker(), new PdfSharpReportRenderer(), clock,
                Options.Create(new ValidationOptions { EnforceSheetAge = false }), NullLogger<MorningRunPipeline>.Instance),
            Director = await SignIn("director"),
            Operations = await SignIn("operations"),
        };
    }

    private static async Task SetBothRhythms(Env e)
    {
        await e.Rhythms.SetAsync(e.Director, HotelId.Novotel, RhythmType.TwoTages, new DateOnly(2026, 10, 1), "first set-up", default);
        await e.Rhythms.SetAsync(e.Director, HotelId.IbisBudget, RhythmType.ThreeTages, new DateOnly(2026, 10, 1), null, default);
    }

    /// <summary>Runs the orchestration's steps in order, as Durable Functions does.</summary>
    private static async Task<Guid> RunAsync(Env e, CurrentUser user, (string Name, string Fixture, int Pages)[] files, params NewRequest[] requests)
    {
        var uploads = files.Select(f => new UploadedFile(f.Name, SyntheticPdf.Create(f.Pages, f.Fixture))).ToList();
        var runId = await e.Runs.CreateAsync(user, uploads, requests, default);
        var fileIds = await e.Pipeline.ListFilesAsync(runId, default);
        var read = new List<ReadOutcome>();
        foreach (var f in fileIds) read.Add(await e.Pipeline.ReadFileAsync(f, default));
        var accepted = await e.Pipeline.ResolveAsync(runId, read, default);
        foreach (var r in read.Where(r => accepted.Contains(r.FileId))) await e.Pipeline.SaveAsync(r, default);
        foreach (var f in accepted) await e.Pipeline.ColourAsync(f, default);
        foreach (var f in accepted) await e.Pipeline.MarkAsync(f, default);
        await e.Pipeline.FinishAsync(runId, default);
        return runId;
    }

    // ---------- schema ----------

    [DockerFact]
    public async Task Migrations_are_applied_once_and_seed_the_room_inventory()
    {
        var e = await CreateAsync();
        await new DbMigrator(Options.Create(new DatabaseOptions { ConnectionString = e.ConnectionString }), NullLogger<DbMigrator>.Instance).MigrateAsync();

        await using var c = await e.Db.OpenAsync();
        Assert.Equal(4, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.SchemaVersions"));
        Assert.Equal(117, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.room_inventory WHERE hotel_id = 2"));
        Assert.Equal(123, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.room_inventory WHERE hotel_id = 1"));
    }

    /// <summary>Plan 5.4 schema guard: no column for a person's name outside the Stage 2 staff list and report tables.</summary>
    [DockerFact]
    public async Task No_table_has_a_name_attendant_employee_or_guest_column()
    {
        var e = await CreateAsync();
        await using var c = await e.Db.OpenAsync();
        var columns = (await c.QueryAsync<string>(
            "SELECT TABLE_NAME + '.' + COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME <> 'AttendantMaster'")).ToList();

        var allowed = new HashSet<string>
        {
            "phase1_run_file.blob_name", "phase1_run_file.marked_blob_name", "SchemaVersions.script_name", "PdfProcessingLog.SourceBlobName", "app_user.username",
            // Stage 2: names entered by staff for the reports (README "Privacy"), never read from the Opera PDF.
            "staff_member.staff_id", "staff_member.display_name",
            "room_move.employee_name",
            "room_check_log.staff_id", "room_check_log.checked_by_name",
            "daily_close_hotel.richter_staff_name",
            "daily_close_cleaner.staff_id", "daily_close_cleaner.cleaner_name",
            "daily_close_supervisor.staff_id", "daily_close_supervisor.person_name",
        };
        var bad = columns.Where(col => ForbiddenColumn().IsMatch(col.Split('.')[1]) && !allowed.Contains(col)).ToList();
        Assert.Empty(bad);
    }

    // ---------- accounts ----------

    [DockerFact]
    public async Task Wrong_password_is_refused_and_sessions_can_be_revoked()
    {
        var e = await CreateAsync();
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => e.Auth.LoginAsync("director", "nope", default));
        Assert.Equal(401, ex.Status);

        var (_, token, _) = await e.Auth.LoginAsync("Director", "test-pass", default);
        Assert.NotNull(await e.Auth.ResolveAsync(token, default));
        await e.Auth.LogoutAsync(token, default);
        Assert.Null(await e.Auth.ResolveAsync(token, default));
    }

    // ---------- rhythm rules ----------

    [DockerFact]
    public async Task Rhythm_rules_are_enforced()
    {
        var e = await CreateAsync();

        // Only the Director.
        var forbidden = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            e.Rhythms.SetAsync(e.Operations, HotelId.Novotel, RhythmType.TwoTages, new DateOnly(2026, 10, 1), null, default));
        Assert.Equal(403, forbidden.Status);

        // Only on the 1st of a month.
        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            e.Rhythms.SetAsync(e.Director, HotelId.Novotel, RhythmType.TwoTages, new DateOnly(2026, 10, 15), null, default));

        // First set-up for the current month is active at once.
        var first = await e.Rhythms.SetAsync(e.Director, HotelId.Novotel, RhythmType.TwoTages, new DateOnly(2026, 10, 1), null, default);
        Assert.Equal(RhythmType.TwoTages, first.Active!.Rhythm);

        // A change must be for a future month, and only one may be planned.
        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            e.Rhythms.SetAsync(e.Director, HotelId.Novotel, RhythmType.ThreeTages, new DateOnly(2026, 10, 1), null, default));
        var planned = await e.Rhythms.SetAsync(e.Director, HotelId.Novotel, RhythmType.ThreeTages, new DateOnly(2026, 11, 1), "test", default);
        Assert.Equal(new DateOnly(2026, 11, 1), planned.Scheduled!.EffectiveFrom);
        var conflict = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            e.Rhythms.SetAsync(e.Director, HotelId.Novotel, RhythmType.TwoTages, new DateOnly(2026, 12, 1), null, default));
        Assert.Equal(409, conflict.Status);

        // Cancel until the day before; then it is gone.
        var cancelled = await e.Rhythms.CancelScheduledAsync(e.Director, HotelId.Novotel, "changed my mind", default);
        Assert.Null(cancelled.Scheduled);

        var history = await e.Rhythms.HistoryAsync(default);
        Assert.Equal(["cancelled", "scheduled", "set"], history.Select(h => h.Action));
        Assert.All(history, h => Assert.Equal("Hotel Director", h.By));
    }

    [DockerFact]
    public async Task Database_refuses_a_rhythm_that_does_not_start_on_the_1st_or_a_second_active_rhythm()
    {
        var e = await CreateAsync();
        await SetBothRhythms(e);
        await using var c = await e.Db.OpenAsync();
        const string insert = "INSERT INTO dbo.rhythm_configuration (hotel_id, rhythm_type, effective_from, status, created_by) VALUES (2, 'TwoTages', @d, @s, @u)";

        await Assert.ThrowsAsync<SqlException>(() => c.ExecuteAsync(insert, new { d = new DateOnly(2027, 1, 2), s = "SCHEDULED", u = e.Director.User.UserId }));
        await Assert.ThrowsAsync<SqlException>(() => c.ExecuteAsync(insert, new { d = new DateOnly(2027, 1, 1), s = "ACTIVE", u = e.Director.User.UserId }));
    }

    [DockerFact]
    public async Task Planned_change_starts_on_the_1st_and_guests_keep_the_rhythm_they_arrived_with()
    {
        var e = await CreateAsync();
        await SetBothRhythms(e);
        await e.Rhythms.SetAsync(e.Director, HotelId.Novotel, RhythmType.ThreeTages, new DateOnly(2026, 11, 1), null, default);

        await using var c = await e.Db.OpenAsync();
        // Guest arrived in October under 2-Tages.
        Assert.Equal(RhythmType.TwoTages, await e.Rhythms.GuestRhythmAsync(c, HotelId.Novotel, "214", new DateOnly(2026, 10, 28), new DateOnly(2026, 11, 5)));

        e.Clock.UtcNow = new DateTime(2026, 11, 1, 0, 10, 0, DateTimeKind.Utc);
        Assert.Equal(1, await e.Rhythms.ActivateDueAsync(default));
        var now = (await e.Rhythms.GetAllAsync(default)).Single(r => r.HotelId == HotelId.Novotel);
        Assert.Equal(RhythmType.ThreeTages, now.Active!.Rhythm);
        Assert.Null(now.Scheduled);

        // Same guest, stay extended: still 2-Tages. A new guest in November gets 3-Tages.
        Assert.Equal(RhythmType.TwoTages, await e.Rhythms.GuestRhythmAsync(c, HotelId.Novotel, "214", new DateOnly(2026, 10, 28), new DateOnly(2026, 11, 8)));
        Assert.Equal(RhythmType.ThreeTages, await e.Rhythms.GuestRhythmAsync(c, HotelId.Novotel, "215", new DateOnly(2026, 11, 1), null));
        Assert.Equal(new DateOnly(2026, 11, 8), await c.ExecuteScalarAsync<DateOnly>(
            "SELECT departure_date FROM dbo.guest_rhythm_assignment WHERE room_no = '214'"));
    }

    // ---------- morning run ----------

    [DockerFact]
    public async Task Run_cannot_start_without_an_active_rhythm()
    {
        var e = await CreateAsync();
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => RunAsync(e, e.Operations,
            [("a.pdf", Fixtures.Novotel0830, 6), ("b.pdf", Fixtures.Ibis0830, 6)]));
        Assert.Equal(409, ex.Status);
    }

    [DockerFact]
    public async Task Full_run_reads_checks_colours_and_marks_both_hotels()
    {
        var e = await CreateAsync();
        await SetBothRhythms(e);

        var runId = await RunAsync(e, e.Operations,
            [("sheet-a.pdf", Fixtures.Ibis0830, 6), ("sheet-b.pdf", Fixtures.Novotel0830, 6)],
            new NewRequest(HotelId.Novotel, "104", RequestType.SPECIAL, ColourName.Gold, "Allergy"),
            new NewRequest(HotelId.IbisBudget, "106", RequestType.DAILY_FULL, null, null));

        var run = (await e.Runs.GetAsync(runId, default))!;
        Assert.Equal("completed", run.Status);
        Assert.Equal("2026-08-30", run.ReportDate);

        var ibis = run.Hotels.Single(h => h.HotelId == 1);
        var novotel = run.Hotels.Single(h => h.HotelId == 2);
        Assert.Equal("completed", ibis.Status);
        Assert.Equal(116, ibis.Rooms.Count);
        Assert.Equal(116, ibis.ParsedRooms);
        Assert.Equal(116, ibis.Summary!.TotalRooms);
        Assert.Empty(ibis.Warnings);
        Assert.All(ibis.Steps, s => Assert.Equal("done", s.Status));
        Assert.True(ibis.MarkedPdfAvailable);
        Assert.Equal(112, novotel.Rooms.Count);

        // Rules applied with the real rows: guest wish, departure, arrival, empty room, rhythm.
        Assert.Equal(("Gold", "SPECIAL"), Colour(novotel, "104"));
        Assert.Equal(("Blue", "BLUE_DAILY_FULL"), Colour(ibis, "106"));
        Assert.Equal(("Red", "DUEOUT_RED"), Colour(novotel, "101"));
        Assert.Equal(("Red", "ARRIVAL_0"), Colour(ibis, "112"));
        Assert.Equal((null, "CLEAN_ROOM_012"), Colour(ibis, "117"));
        // ibis 103: Stayover since 28.08 → night 2 → 3-Tages [G, G, P, B] → Green, not cleaned without card.
        Assert.Equal(("Green", "SERVICE_1"), Colour(ibis, "103"));
        Assert.False(ibis.Rooms.Single(r => r.RoomNo == "103").Cleaned);
        // ibis 113: Stayover since 26.08 → night 4 → Blue.
        Assert.Equal(("Blue", "SERVICE_3"), Colour(ibis, "113"));

        // The marked PDF exists; the original went to the archive.
        var pdf = await e.Runs.OpenMarkedPdfAsync(runId, HotelId.IbisBudget, default);
        Assert.NotNull(pdf);
        Assert.Equal("housekeeping-plan-ibis-budget-2026-08-30.pdf", pdf.Value.FileName);
        Assert.DoesNotContain(e.Blobs.Blobs.Keys, k => k.Container == Containers.Incoming);
        Assert.Equal(2, e.Blobs.Blobs.Keys.Count(k => k.Container == Containers.Archive));

        // Every write was confirmed and logged.
        await using var c = await e.Db.OpenAsync();
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.UserConfirmationLog WHERE action = 'RUN_START'"));
    }

    [DockerFact]
    public async Task Same_sheet_twice_and_two_sheets_of_one_hotel_are_refused()
    {
        var e = await CreateAsync();
        await SetBothRhythms(e);
        await RunAsync(e, e.Operations, [("a.pdf", Fixtures.Ibis0830, 6), ("b.pdf", Fixtures.Novotel0830, 6)]);

        var again = (await e.Runs.GetAsync(await RunAsync(e, e.Operations, [("a.pdf", Fixtures.Ibis0830, 6), ("c.pdf", Fixtures.Novotel0828, 5)]), default))!;
        var dup = again.Hotels.Single(h => h.FileName == "a.pdf");
        Assert.Equal("rejected", dup.Status);
        Assert.Contains("already been processed", dup.RejectReason);
        Assert.Equal("completed", again.Hotels.Single(h => h.FileName == "c.pdf").Status);

        var twoNovotel = (await e.Runs.GetAsync(await RunAsync(e, e.Operations, [("x.pdf", Fixtures.Novotel0710, 7), ("y.pdf", Fixtures.Novotel0710, 7)]), default))!;
        Assert.Equal("completed", twoNovotel.Hotels[0].Status);
        Assert.Contains("Both files are Novotel", twoNovotel.Hotels[1].RejectReason);
    }

    [DockerFact]
    public async Task A_file_that_is_not_a_pdf_is_refused_with_a_reason()
    {
        var e = await CreateAsync();
        await SetBothRhythms(e);
        var runId = await e.Runs.CreateAsync(e.Operations,
            [new UploadedFile("notes.pdf", "just some text"u8.ToArray()), new UploadedFile("b.pdf", SyntheticPdf.Create(6, Fixtures.Ibis0830))], [], default);
        var files = await e.Pipeline.ListFilesAsync(runId, default);
        var read = await e.Pipeline.ReadFileAsync(files[0], default);
        Assert.False(read.Ok);

        var run = (await e.Runs.GetAsync(runId, default))!;
        Assert.Equal("rejected", run.Hotels[0].Status);
        Assert.Contains("not a PDF", run.Hotels[0].RejectReason);
        Assert.Contains(e.Blobs.Blobs.Keys, k => k.Container == Containers.Failed);
    }

    [DockerFact]
    public async Task Guest_request_for_an_unknown_room_is_refused()
    {
        var e = await CreateAsync();
        await SetBothRhythms(e);
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => e.Runs.CreateAsync(e.Operations,
            [new UploadedFile("a.pdf", SyntheticPdf.Create(1)), new UploadedFile("b.pdf", SyntheticPdf.Create(1))],
            [new NewRequest(HotelId.Novotel, "127", RequestType.CARD, null, null)], default));
        Assert.Contains("does not exist", ex.Message);
    }

    /// <summary>Plan 5.4 leak test: after processing all samples, no text column anywhere holds a staff name or number.</summary>
    [DockerFact]
    public async Task No_staff_name_or_number_anywhere_in_the_database()
    {
        var e = await CreateAsync();
        await SetBothRhythms(e);
        await RunAsync(e, e.Operations, [("a.pdf", Fixtures.Ibis0830, 6), ("b.pdf", Fixtures.Novotel0710, 7)]);
        await RunAsync(e, e.Operations, [("c.pdf", Fixtures.Novotel0828, 5), ("d.pdf", Fixtures.Novotel0830, 6)]);

        await using var c = await e.Db.OpenAsync();
        var columns = await c.QueryAsync<(string Table, string Column)>(
            "SELECT TABLE_NAME, COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'dbo' AND DATA_TYPE IN ('varchar', 'nvarchar', 'char', 'nchar') AND TABLE_NAME NOT LIKE '%history'");
        foreach (var (table, column) in columns)
        {
            var values = await c.QueryAsync<string>($"SELECT CAST([{column}] AS NVARCHAR(MAX)) FROM dbo.[{table}] WHERE [{column}] IS NOT NULL");
            foreach (var v in values)
            {
                Assert.DoesNotMatch(StaffNumber(), v);
                foreach (var name in Fixtures.FakeNames) Assert.DoesNotContain(name, v);
            }
        }
    }

    private static (string? Colour, string Code) Colour(Functions.HotelRunResultDto hotel, string room)
    {
        var r = hotel.Rooms.Single(x => x.RoomNo == room);
        return (r.Colour, r.ServiceCode);
    }

    [GeneratedRegex(@"^(name|.*_name|.*Name|attendant.*|employee.*|guest_.*|staff.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex ForbiddenColumn();

    [GeneratedRegex(@"\bMA\s?\d+\b")]
    private static partial Regex StaffNumber();
}
