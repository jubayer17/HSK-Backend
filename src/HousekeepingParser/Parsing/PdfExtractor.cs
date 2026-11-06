using Microsoft.Extensions.Options;

namespace HousekeepingParser.Parsing;

// Phase 1 decision: keep the contract independent of Java so the pipeline can use either .NET reader.
public sealed class PdfParserOptions
{
    public const string SectionName = "PdfParser";
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
    public int MaxPages { get; set; } = 100;
    public bool ForcePdfPigFallback { get; set; }
}

public interface ICompliantPdfExtractor
{
    Task<PdfExtractionResult> ExtractAsync(byte[] pdfBytes, string sourceBlobName, CancellationToken ct);
}

public class PdfExtractionException(string message, Exception? inner = null) : Exception(message, inner);
public sealed class PdfExtractionTimeoutException(TimeSpan timeout)
    : PdfExtractionException($"PDF extraction exceeded its {timeout.TotalSeconds:F0}s cooperative processing budget.");

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
