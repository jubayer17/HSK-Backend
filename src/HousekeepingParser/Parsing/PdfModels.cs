namespace HousekeepingParser.Parsing;

// Shared contract for the .NET Stream parser and PdfPig fallback.
// Coordinates are PDF points measured from the bottom-left corner of the page, so Top > Bottom.

public sealed record PdfExtractionResult(
    string CorrelationId,
    IReadOnlyList<PdfPage> Pages,
    int TotalLines,
    TimeSpan Duration,
    long PeakHeapHintBytes = 0,
    IReadOnlyList<PdfPageQuality>? Quality = null);

// These are extraction diagnostics, not proof that business fields are correct; acceptance tests do that.
public sealed record PdfPageQuality(int Page, string Parser, int Tables, double? Accuracy, double? Whitespace, string? FallbackReason);

public sealed record PdfPage(
    int PageNumber,
    double Width,
    double Height,
    IReadOnlyList<PdfLine> Lines);

public sealed record PdfLine(
    string Text,
    double Top,
    double Bottom,
    double Left,
    double Right,
    IReadOnlyList<PdfWord> Words)
{
    public double CentreY => (Top + Bottom) / 2;
}

public sealed record PdfWord(
    string Text,
    double Left,
    double Right,
    double Top,
    double Bottom)
{
    public double CentreX => (Left + Right) / 2;
    public double CentreY => (Top + Bottom) / 2;
}
