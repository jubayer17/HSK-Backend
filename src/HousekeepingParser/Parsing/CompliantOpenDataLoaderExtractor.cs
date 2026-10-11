using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HousekeepingParser.Parsing;

// The client's GDPR/BDSG-compliant wrapper ("OpenDataLoader v2" document), used as written except
// where the real CLI differs from the document (conflict C15, checked against opendataloader-pdf 2.5.11):
//  - the input file is a positional argument and the output folder is "-o" (not --input / --output);
//  - the JSON is a tree of elements with bounding boxes, not pages -> lines -> words.
//    OdlLayoutMapper turns it into the PdfPage / PdfLine / PdfWord contract below;
//  - "--include-header-footer" is needed (the hotel name and date are in the page header) and
//    "--image-output off" stops the reader writing the logo images to disk. "--hybrid off" keeps
//    the AI backend switched off explicitly (rule 2). No other data is passed.

public sealed class OpenDataLoaderOptions
{
    public const string SectionName = "OpenDataLoader";

    public string JavaExecutable { get; set; } = "/usr/bin/java";
    public string JarPath { get; set; } = "/opt/opendataloader/opendataloader-pdf.jar";

    /// <summary>Hard heap cap. Default 1g — prevents OOM affecting the Function host.</summary>
    public string MaxHeap { get; set; } = "1g";

    /// <summary>Wall-clock budget. Default 30 s per Phase 1 acceptance criterion #8.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Extra JVM flags. Keep GC serial for cold-start latency.</summary>
    public string[] ExtraJvmArgs { get; set; } = ["-XX:+UseSerialGC", "-XX:TieredStopAtLevel=1"];
}

public interface ICompliantPdfExtractor
{
    /// <summary>
    /// Extracts PDF structure via the OpenDataLoader JVM subprocess. All temporary artefacts are written
    /// to a per-invocation directory and deleted in a finally block.
    /// </summary>
    Task<PdfExtractionResult> ExtractAsync(byte[] pdfBytes, string sourceBlobName, CancellationToken ct);
}

public sealed class CompliantOpenDataLoaderExtractor(
    IOptions<OpenDataLoaderOptions> opts,
    ILogger<CompliantOpenDataLoaderExtractor> log,
    TelemetryClient telemetry,
    IPdfProcessingAuditSink audit) : ICompliantPdfExtractor
{
    private readonly OpenDataLoaderOptions _opts = opts.Value;

    public async Task<PdfExtractionResult> ExtractAsync(byte[] pdfBytes, string sourceBlobName, CancellationToken ct)
    {
        // 1. Correlation ID — the only identifier that crosses the boundary
        var correlationId = Guid.NewGuid().ToString("N");
        var sw = Stopwatch.StartNew();

        // 2. Isolated temp directory (per invocation)
        var workDir = Path.Combine(Path.GetTempPath(), "odl-" + correlationId);
        Directory.CreateDirectory(workDir);

        try
        {
            // 3. Write PDF to isolated dir (data minimisation: only bytes, never the upload name)
            var pdfPath = Path.Combine(workDir, "input.pdf");
            var outPath = Path.Combine(workDir, "output");
            await File.WriteAllBytesAsync(pdfPath, pdfBytes, ct).ConfigureAwait(false);

            // 4. ProcessStartInfo — argument list, no shell
            var psi = new ProcessStartInfo(_opts.JavaExecutable)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = false,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workDir,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add($"-Xmx{_opts.MaxHeap}");
            foreach (var flag in _opts.ExtraJvmArgs) psi.ArgumentList.Add(flag);
            psi.ArgumentList.Add("-jar");
            psi.ArgumentList.Add(_opts.JarPath);
            psi.ArgumentList.Add("--quiet");
            psi.ArgumentList.Add("--output-dir");
            psi.ArgumentList.Add(outPath);
            psi.ArgumentList.Add("--format");
            psi.ArgumentList.Add("json");
            psi.ArgumentList.Add("--keep-line-breaks");
            psi.ArgumentList.Add("--include-header-footer");
            psi.ArgumentList.Add("--image-output");
            psi.ArgumentList.Add("off");
            psi.ArgumentList.Add("--hybrid");
            psi.ArgumentList.Add("off");
            psi.ArgumentList.Add(pdfPath);

            // 5. Launch with timeout-linked cancellation
            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            if (!process.Start())
                throw new OpenDataLoaderException("Failed to start OpenDataLoader JVM.");

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_opts.Timeout);

            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                KillTree(process);
                await LogAuditAsync(correlationId, sourceBlobName, "TIMEOUT", sw.Elapsed, pdfBytes.Length, null, CancellationToken.None)
                    .ConfigureAwait(false);
                throw new OpenDataLoaderTimeoutException(_opts.Timeout);
            }
            catch (OperationCanceledException)
            {
                KillTree(process);
                await LogAuditAsync(correlationId, sourceBlobName, "CANCELLED", sw.Elapsed, pdfBytes.Length, null, CancellationToken.None)
                    .ConfigureAwait(false);
                throw;
            }

            await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                await LogAuditAsync(correlationId, sourceBlobName, "JVM_FAILURE", sw.Elapsed, pdfBytes.Length, stderr, ct)
                    .ConfigureAwait(false);
                throw new OpenDataLoaderException(
                    $"OpenDataLoader exited with code {process.ExitCode}. stderr: {Truncate(stderr, 2000)}", process.ExitCode);
            }

            // 6. Parse JSON output (only structured data, no raw text logged)
            var jsonPath = Path.Combine(outPath, "input.json");
            if (!File.Exists(jsonPath))
                throw new OpenDataLoaderException("OpenDataLoader produced no JSON output.");

            await using var json = File.OpenRead(jsonPath);
            using var doc = await JsonDocument.ParseAsync(json, cancellationToken: ct).ConfigureAwait(false);
            var pages = OdlLayoutMapper.Map(doc.RootElement, PdfPageSizes.Read(pdfBytes));

            // 7. Audit success
            await LogAuditAsync(correlationId, sourceBlobName, "SUCCESS", sw.Elapsed, pdfBytes.Length, null, ct).ConfigureAwait(false);
            log.LogInformation(
                "OpenDataLoader completed. CorrelationId={CorrelationId} Blob={Blob} Bytes={Bytes} DurationMs={DurationMs}",
                correlationId, sourceBlobName, pdfBytes.Length, sw.ElapsedMilliseconds);

            return new PdfExtractionResult(correlationId, pages, pages.Sum(p => p.Lines.Count), sw.Elapsed);
        }
        finally
        {
            // 8. SHRED: delete every temp artefact, even on failure
            SafeDeleteDirectory(workDir);
        }
    }

    private static void KillTree(Process p)
    {
        try
        {
            if (!p.HasExited) p.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { /* already gone */ }
        catch (NotSupportedException) { /* platform */ }
    }

    private static void SafeDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException ex)
        {
            // Never throw from finally — the pipeline must continue.
            Debug.WriteLine($"Temp cleanup failed: {ex.Message}");
        }
    }

    private async Task LogAuditAsync(
        string correlationId, string blobName, string outcome, TimeSpan duration, int byteCount, string? errorDetail, CancellationToken ct)
    {
        var evt = new EventTelemetry("OpenDataLoader.Invocation");
        evt.Properties["CorrelationId"] = correlationId;
        evt.Properties["BlobName"] = blobName;
        evt.Properties["Outcome"] = outcome;
        evt.Metrics["DurationMs"] = duration.TotalMilliseconds;
        evt.Metrics["ByteCount"] = byteCount;
        telemetry.TrackEvent(evt);

        if (errorDetail is not null)
        {
            telemetry.TrackTrace(new TraceTelemetry($"OpenDataLoader error ({outcome}): {Truncate(errorDetail, 1000)}", SeverityLevel.Error)
            {
                Properties = { ["CorrelationId"] = correlationId, ["BlobName"] = blobName },
            });
        }

        await audit.RecordAsync(new PdfProcessingAuditRecord
        {
            CorrelationId = correlationId,
            SourceBlobName = blobName,
            Outcome = outcome,
            DurationMs = (int)duration.TotalMilliseconds,
            ByteCount = byteCount,
            ErrorDetail = errorDetail is null ? null : Truncate(errorDetail, 2000),
            OccurredAtUtc = DateTime.UtcNow,
        }, ct).ConfigureAwait(false);
    }

    private static string Truncate(string? s, int max) =>
        s is null ? string.Empty : (s.Length <= max ? s : s[..max] + "…");
}

public class OpenDataLoaderException(string msg, int? exitCode = null, Exception? inner = null) : Exception(msg, inner)
{
    public int? ExitCode { get; } = exitCode;
}

public sealed class OpenDataLoaderTimeoutException(TimeSpan t)
    : OpenDataLoaderException($"OpenDataLoader exceeded the {t.TotalSeconds:F0}s JVM budget.");

public interface IPdfProcessingAuditSink
{
    Task RecordAsync(PdfProcessingAuditRecord record, CancellationToken ct);
}

public sealed record PdfProcessingAuditRecord
{
    public string CorrelationId { get; init; } = "";
    public string SourceBlobName { get; init; } = "";
    public string Outcome { get; init; } = "";
    public int DurationMs { get; init; }
    public int ByteCount { get; init; }
    public string? ErrorDetail { get; init; }
    public DateTime OccurredAtUtc { get; init; }
}
