namespace HousekeepingParser.Parsing;

// Result contract of the client's compliant wrapper (OpenDataLoader v2 document).
// Coordinates are PDF points measured from the bottom-left corner of the page, so Top > Bottom.

public sealed record PdfExtractionResult(
    string CorrelationId,
    IReadOnlyList<PdfPage> Pages,
    int TotalLines,
    TimeSpan Duration,
    long PeakHeapHintBytes = 0);

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
}
