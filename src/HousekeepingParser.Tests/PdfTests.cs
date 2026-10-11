using System.Text;
using HousekeepingParser.Core;
using HousekeepingParser.Parsing;
using HousekeepingParser.PdfMarking;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Xunit;

namespace HousekeepingParser.Tests;

public class PdfTests
{
    /// <summary>A synthetic two-page PDF without compressed streams (so it contains no CR bytes).</summary>
    private static byte[] SyntheticPdf()
    {
        using var doc = new PdfDocument();
        doc.Options.CompressContentStreams = false;
        doc.Options.NoCompression = true;
        for (var i = 0; i < 2; i++)
        {
            var page = doc.AddPage();
            page.Width = XUnit.FromPoint(612);
            page.Height = XUnit.FromPoint(792);
            using var g = XGraphics.FromPdfPage(page);
            g.DrawRectangle(XBrushes.LightGray, 15, 250, 13, 11);
        }
        using var ms = new MemoryStream();
        doc.Save(ms, closeStream: false);
        return ms.ToArray();
    }

    [Fact]
    public void Marking_keeps_the_page_count_and_adds_content()
    {
        var original = SyntheticPdf();
        var dots = new List<DotPlacement>
        {
            new(1, 15, 534, 523, ColourName.Red),
            new(2, 15, 400, 389, ColourName.Blue),
        };

        var marked = new PdfSharpMarker().AddDots(original, dots);

        using var doc = PdfReader.Open(new MemoryStream(marked), PdfDocumentOpenMode.Import);
        Assert.Equal(2, doc.PageCount);
        Assert.True(marked.Length > original.Length);
    }

    [Fact]
    public void Dot_sits_left_of_the_room_number_and_flips_the_y_axis()
    {
        // Reader: origin bottom-left. Drawing: origin top-left.
        var (x, y, d) = DotGeometry.ToTopLeft(new DotPlacement(1, 15, 534, 523, ColourName.Green), 792);

        Assert.Equal(792 - 528.5, y, 3);
        Assert.True(x + d / 2 < 15, "the dot must end before the room number starts");
        Assert.True(x - d / 2 > 6, "the dot must not cover the '!' arrival marker at x 4–6");
        Assert.InRange(d, 3.5, 6);
    }

    /// <summary>A minimal hand-written PDF with LF line endings and exact xref offsets, like Opera's files.</summary>
    private static byte[] LfOnlyPdf()
    {
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
        };
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = sb.Length;
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    [Fact]
    public void Line_endings_damaged_in_transfer_are_repaired()
    {
        var original = LfOnlyPdf();
        Assert.Single(PdfPageSizes.Read(original));
        var damaged = Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(original).Replace("\n", "\r\n"));

        var (repaired, changed) = PdfBytes.RepairLineEndings(damaged);

        Assert.True(changed);
        Assert.Equal(original, repaired);
    }

    [Fact]
    public void A_healthy_pdf_is_left_alone()
    {
        var original = LfOnlyPdf();
        var (bytes, changed) = PdfBytes.RepairLineEndings(original);

        Assert.False(changed);
        Assert.Same(original, bytes);
        Assert.True(PdfBytes.LooksLikePdf(original));
        Assert.False(PdfBytes.LooksLikePdf(Encoding.ASCII.GetBytes("hello world, not a pdf")));
    }

    [Fact]
    public void Report_pages_come_first_and_the_marked_sheet_follows_unchanged()
    {
        var marked = new PdfSharpMarker().AddDots(SyntheticPdf(), [new(1, 15, 534, 523, ColourName.Red)]);
        var colours = new ColourName?[] { ColourName.Red, ColourName.Green, ColourName.Purple, ColourName.Blue, ColourName.Gold, null };
        var rooms = Enumerable.Range(0, 90).Select(i => new ReportRoom(
            (101 + i).ToString(), i < 50 ? "1" : "FLOAT", i % 3 == 0 ? "CL" : "DI", i % 4 == 0 ? "Due Out" : "Stayover",
            new DateOnly(2026, 8, 27), new DateOnly(2026, 9, 1), 2, i % 5 == 0 ? 1 : 0,
            colours[i % colours.Length], i % 6 == 5 ? "No booking on the sheet" : $"Night {i % 5} of stay", i % 7 != 1)).ToList();
        var report = new RunReport(HotelId.Novotel, new DateOnly(2026, 8, 30), new DateTime(2026, 8, 30, 17, 34, 0), new DateTime(2026, 8, 31, 6, 50, 0),
            RhythmType.TwoTages, new ReportSummary(2, 90, 23, 60, 30, 120m),
            [new("1", "DEP, STAY", "Please check the minibar in every room on the fourth floor before noon.", 50), new("FLOAT", "", "Keine TA.", 41)],
            rooms, ["Task sheet FLOAT: 40 rooms read, the sheet's own total says 41."]);

        var pdf = new PdfSharpReportRenderer().Compose(report, marked);

        using var doc = PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.Import);
        Assert.True(doc.PageCount >= 2 + 3, "summary and at least two room pages before the original");
        var originalSizes = PdfPageSizes.Read(marked).OrderBy(p => p.Key).Select(p => p.Value).ToList();
        var tail = PdfPageSizes.Read(pdf).OrderBy(p => p.Key).Select(p => p.Value).TakeLast(originalSizes.Count).ToList();
        Assert.Equal(originalSizes, tail);

        // Optional visual check on a developer machine: HSK_MARK_OUT=/some/folder dotnet test …
        var outDir = Environment.GetEnvironmentVariable("HSK_MARK_OUT");
        if (!string.IsNullOrEmpty(outDir)) File.WriteAllBytes(Path.Combine(outDir, "report-synthetic.pdf"), pdf);
    }
}
