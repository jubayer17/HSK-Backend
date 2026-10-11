using HousekeepingParser.Accounts;
using HousekeepingParser.Audit;
using HousekeepingParser.Coloring;
using HousekeepingParser.Core;
using HousekeepingParser.Data;
using HousekeepingParser.Functions;
using HousekeepingParser.Parsing;
using HousekeepingParser.PdfMarking;
using HousekeepingParser.Rhythm;
using HousekeepingParser.Runs;
using HousekeepingParser.Storage;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = FunctionsApplication.CreateBuilder(args);
builder.ConfigureFunctionsWebApplication();

builder.Services
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights();

var config = builder.Configuration;
builder.Services.Configure<DatabaseOptions>(config.GetSection(DatabaseOptions.SectionName));
builder.Services.Configure<StorageOptions>(config.GetSection(StorageOptions.SectionName));
builder.Services.Configure<OpenDataLoaderOptions>(config.GetSection(OpenDataLoaderOptions.SectionName));
builder.Services.Configure<AuthOptions>(config.GetSection(AuthOptions.SectionName));
builder.Services.Configure<ValidationOptions>(config.GetSection(ValidationOptions.SectionName));

builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<IDbConnectionFactory, SqlConnectionFactory>();
builder.Services.AddSingleton<DbMigrator>();
builder.Services.AddSingleton<IBlobStore, AzureBlobStore>();
builder.Services.AddSingleton<IAuditLog, SqlAuditLog>();
builder.Services.AddSingleton<IPdfProcessingAuditSink, SqlPdfProcessingAuditSink>();
builder.Services.AddSingleton<ICompliantPdfExtractor, CompliantOpenDataLoaderExtractor>();
builder.Services.AddSingleton<IColorRuleService, ColorRuleService>();
builder.Services.AddSingleton<IPdfMarker, PdfSharpMarker>();
builder.Services.AddSingleton<IRunReportRenderer, PdfSharpReportRenderer>();
builder.Services.AddSingleton<IAuthService, AuthService>();
builder.Services.AddSingleton<IRhythmService, RhythmService>();
builder.Services.AddSingleton<IRunService, RunService>();
builder.Services.AddSingleton<RunStore>();
builder.Services.AddSingleton<MorningRunPipeline>();
builder.Services.AddSingleton<Api>();
builder.Services.AddHostedService<StartupTasks>();

builder.Build().Run();

/// <summary>Schema migrations, blob containers and the Stage 1 sign-in accounts, before the first request.</summary>
internal sealed class StartupTasks(
    DbMigrator migrator,
    IBlobStore blobs,
    IAuthService auth,
    Microsoft.Extensions.Options.IOptions<DatabaseOptions> db,
    ILogger<StartupTasks> log) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        if (db.Value.MigrateOnStartup) await migrator.MigrateAsync(ct);
        await blobs.EnsureContainersAsync(ct);
        await auth.SeedUsersAsync(ct);
        log.LogInformation("Start-up tasks finished");
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
