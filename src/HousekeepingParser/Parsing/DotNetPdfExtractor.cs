using System.Diagnostics;
using System.Globalization;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UglyToad.PdfPig;

namespace HousekeepingParser.Parsing;

public sealed class DotNetPdfExtractor(IOptions<PdfParserOptions> options, ILogger<DotNetPdfExtractor> log,
    TelemetryClient telemetry, IPdfProcessingAuditSink audit) : ICompliantPdfExtractor
{
    // In-process parsing must not create concurrent copies of memory-heavy PDFs in the Function worker.
    private static readonly SemaphoreSlim Slots = new(1, 1);

    public async Task<PdfExtractionResult> ExtractAsync(byte[] pdfBytes, string sourceBlobName, CancellationToken ct)
    {
        if (options.Value.Timeout <= TimeSpan.Zero || options.Value.MaxPages < 1)
            throw new InvalidOperationException("PDF parser timeout and page limit must be positive.");
        var id = Guid.NewGuid().ToString("N");
        var elapsed = Stopwatch.StartNew();
        await Slots.WaitAsync(ct);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(options.Value.Timeout);
        var outcome = "SUCCESS";
        string? error = null;
        try
        {
            // All parsing is local and in memory: no Java, shell, temporary PDF or external AI service.
            return await Task.Run(() => Read(pdfBytes, id, elapsed, budget.Token), budget.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            outcome = "TIMEOUT";
            throw new PdfExtractionTimeoutException(options.Value.Timeout);
        }
        catch (OperationCanceledException)
        {
            outcome = "CANCELLED";
            throw;
        }
        catch (Exception ex)
        {
            outcome = "READ_FAILED";
            // Exception messages can contain PDF text. Record only a type, never source document content.
            error = ex.GetType().Name;
            throw new PdfExtractionException("The local .NET PDF reader could not read this file.", ex);
        }
        finally
        {
            Slots.Release();
            var evt = new EventTelemetry("PdfParser.Invocation");
            evt.Properties["CorrelationId"] = id;
            evt.Properties["Outcome"] = outcome;
            evt.Metrics["DurationMs"] = elapsed.Elapsed.TotalMilliseconds;
            evt.Metrics["ByteCount"] = pdfBytes.Length;
            telemetry.TrackEvent(evt);
            await audit.RecordAsync(new PdfProcessingAuditRecord
            {
                CorrelationId = id, SourceBlobName = sourceBlobName, Outcome = outcome,
                DurationMs = (int)elapsed.ElapsedMilliseconds, ByteCount = pdfBytes.Length,
                ErrorDetail = error, OccurredAtUtc = DateTime.UtcNow,
            }, CancellationToken.None);
        }
    }

    private PdfExtractionResult Read(byte[] bytes, string id, Stopwatch elapsed, CancellationToken ct)
    {
        using var document = PdfDocument.Open(bytes, new ParsingOptions { ClipPaths = true });
        if (document.NumberOfPages > options.Value.MaxPages) throw new PdfExtractionException("The PDF exceeds the configured page limit.");
        var pages = new List<PdfPage>();
        var quality = new List<PdfPageQuality>();
        foreach (var page in document.GetPages())
        {
            ct.ThrowIfCancellationRequested();
            PdfPage? mapped = null;
            var tables = 0;
            double? accuracy = null, whitespace = null;
            string? fallback = options.Value.ForcePdfPigFallback ? "CONFIGURED_FALLBACK" : null;
            if (fallback is null)
            {
                try
                {
                    var stream = new Camelot.Parsers.Stream();
                    var extracted = stream.ExtractTables(page, suppress_stdout: true);
                    tables = extracted.Count;
                    if (tables == 0) fallback = "NO_TABLES";
                    else
                    {
                        mapped = PdfPigLayoutMapper.Map(page, extracted);
                        var reports = extracted.Select(t => t.ParsingReport()).ToList();
                        accuracy = reports.Average(r => Convert.ToDouble(r["accuracy"], CultureInfo.InvariantCulture));
                        whitespace = reports.Average(r => Convert.ToDouble(r["whitespace"], CultureInfo.InvariantCulture));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                {
                    // The selected package is an archived prerelease: page-level fallback isolates its failures.
                    fallback = ex.GetType().Name;
                    mapped = null;
                }
            }
            ct.ThrowIfCancellationRequested();
            mapped ??= PdfPigLayoutMapper.Map(page);
            var parser = fallback is null ? "Camelot.Stream" : "PdfPig.GetWords";
            pages.Add(mapped);
            quality.Add(new PdfPageQuality(page.Number, parser, tables, accuracy, whitespace, fallback));
            log.LogInformation("PDF quality CorrelationId={CorrelationId} Page={Page} Parser={Parser} Tables={Tables} Accuracy={Accuracy} Whitespace={Whitespace} Fallback={Fallback}",
                id, page.Number, parser, tables, accuracy, whitespace, fallback);
        }
        ct.ThrowIfCancellationRequested();
        return new PdfExtractionResult(id, pages, pages.Sum(p => p.Lines.Count), elapsed.Elapsed, Quality: quality);
    }
}
