using System.Text;
using PdfSharp.Pdf.IO;

namespace HousekeepingParser.Parsing;

public static class PdfBytes
{
    public static bool LooksLikePdf(byte[] bytes) =>
        bytes.Length > 8 && Encoding.ASCII.GetString(bytes, 0, 5) == "%PDF-";

    /// <summary>
    /// Repairs a PDF whose line feeds were all turned into CR LF on the way (e.g. a text-mode transfer):
    /// the cross-reference offsets then point to the wrong bytes and the reader refuses the file.
    /// Only applied when the file has no bare LF at all and its xref offset is wrong; Opera's PDFs use
    /// ASCII85 streams, so turning CR LF back into LF restores them. Returns the original bytes otherwise.
    /// </summary>
    public static (byte[] Bytes, bool Repaired) RepairLineEndings(byte[] bytes)
    {
        if (XrefOffsetIsValid(bytes)) return (bytes, false);

        var crlf = 0;
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] != (byte)'\n') continue;
            if (i == 0 || bytes[i - 1] != (byte)'\r') return (bytes, false); // a bare LF: not this kind of damage
            crlf++;
        }
        if (crlf == 0) return (bytes, false);

        var output = new byte[bytes.Length - crlf];
        var o = 0;
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'\r' && i + 1 < bytes.Length && bytes[i + 1] == (byte)'\n') continue;
            output[o++] = bytes[i];
        }
        return (output, true);
    }

    private static bool XrefOffsetIsValid(byte[] bytes)
    {
        var tailLength = Math.Min(bytes.Length, 1024);
        var tail = Encoding.ASCII.GetString(bytes, bytes.Length - tailLength, tailLength);
        var at = tail.LastIndexOf("startxref", StringComparison.Ordinal);
        if (at < 0) return false;
        var digits = new string(tail[(at + 9)..].SkipWhile(char.IsWhiteSpace).TakeWhile(char.IsDigit).ToArray());
        if (!long.TryParse(digits, out var offset) || offset < 0 || offset >= bytes.Length - 4) return false;
        var probe = Encoding.ASCII.GetString(bytes, (int)offset, (int)Math.Min(20, bytes.Length - offset));
        return probe.StartsWith("xref", StringComparison.Ordinal)
            || System.Text.RegularExpressions.Regex.IsMatch(probe, @"^\d+\s+\d+\s+obj");
    }
}

public static class PdfPageSizes
{
    /// <summary>Page sizes in points (the reader's JSON does not carry them). Empty if the file cannot be opened.</summary>
    public static IReadOnlyDictionary<int, (double Width, double Height)> Read(byte[] pdfBytes)
    {
        try
        {
            using var stream = new MemoryStream(pdfBytes, writable: false);
            using var doc = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
            var sizes = new Dictionary<int, (double, double)>();
            for (var i = 0; i < doc.PageCount; i++)
            {
                var page = doc.Pages[i];
                sizes[i + 1] = (page.Width.Point, page.Height.Point);
            }
            return sizes;
        }
        catch (Exception)
        {
            return new Dictionary<int, (double, double)>();
        }
    }
}
