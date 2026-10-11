using HousekeepingParser.Core;
using PdfSharp.Drawing;
using PdfSharp.Pdf.IO;

namespace HousekeepingParser.PdfMarking;

/// <summary>Where to draw one dot: the room number's position as the reader returned it (PDF points, origin bottom-left).</summary>
public sealed record DotPlacement(int Page, double RoomLeft, double Top, double Bottom, ColourName Colour);

/// <summary>
/// Draws one filled circle just left of each room number (plan 9.7). Pages, text and fonts stay as
/// Opera printed them: the dots are appended as a new content layer.
/// The handoff names Syncfusion PDF; its licence is open decision D1, so drawing sits behind this
/// interface and the PDFsharp (MIT) implementation is used until the licence is settled.
/// </summary>
public interface IPdfMarker
{
    byte[] AddDots(byte[] pdf, IReadOnlyList<DotPlacement> dots);
}

public sealed class PdfSharpMarker : IPdfMarker
{
    public byte[] AddDots(byte[] pdf, IReadOnlyList<DotPlacement> dots)
    {
        using var input = new MemoryStream(pdf, writable: false);
        using var doc = PdfReader.Open(input, PdfDocumentOpenMode.Modify);

        foreach (var group in dots.GroupBy(d => d.Page))
        {
            if (group.Key < 1 || group.Key > doc.PageCount) continue;
            var page = doc.Pages[group.Key - 1];
            using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
            var pageHeight = page.Height.Point;

            foreach (var dot in group)
            {
                var (x, y, d) = DotGeometry.ToTopLeft(dot, pageHeight);
                var colour = XColor.FromArgb(int.Parse(Colours.Hex[dot.Colour][1..], System.Globalization.NumberStyles.HexNumber) | unchecked((int)0xFF000000));
                gfx.DrawEllipse(new XPen(XColor.FromArgb(90, 0, 0, 0), 0.4), new XSolidBrush(colour), x - d / 2, y - d / 2, d, d);
            }
        }

        using var output = new MemoryStream();
        doc.Save(output, closeStream: false);
        return output.ToArray();
    }
}

public static class DotGeometry
{
    /// <summary>
    /// Converts a dot from the reader's coordinates (origin bottom-left, y up) to drawing coordinates
    /// (origin top-left, y down). Returns the circle centre and diameter (~60 % of the text height).
    /// </summary>
    public static (double X, double Y, double Diameter) ToTopLeft(DotPlacement dot, double pageHeight)
    {
        var lineHeight = Math.Max(1, dot.Top - dot.Bottom);
        var diameter = Math.Clamp(lineHeight * 0.45, 3.5, 6.0);
        var centreX = dot.RoomLeft - 1.2 - diameter / 2;
        var centreY = pageHeight - (dot.Top + dot.Bottom) / 2;
        return (centreX, centreY, diameter);
    }
}
