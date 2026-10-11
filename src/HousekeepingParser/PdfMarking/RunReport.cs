using System.Globalization;
using HousekeepingParser.Core;
using HousekeepingParser.Parsing;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace HousekeepingParser.PdfMarking;

/// <summary>One room line of the report. Contains no names (plan 5.3).</summary>
public sealed record ReportRoom(
    string RoomNo,
    string SheetNo,
    string RoomStatus,
    string ReservationStatus,
    DateOnly? Arrival,
    DateOnly? Departure,
    int? Adults,
    int? Children,
    ColourName? Colour,
    string Reason,
    bool Cleaned);

public sealed record ReportSheet(string SheetNo, string TaskCodes, string Instructions, int? TotalRooms);

public sealed record RunReport(
    HotelId Hotel,
    DateOnly ReportDate,
    DateTime? PublishedAtLocal,
    DateTime GeneratedAtLocal,
    RhythmType? Rhythm,
    ReportSummary? Summary,
    IReadOnlyList<ReportSheet> Sheets,
    IReadOnlyList<ReportRoom> Rooms,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Builds the downloadable PDF: designed report pages first (summary, colour legend, room list per task sheet),
/// then the original Opera pages with the coloured dots, unchanged, so the team still has the sheet they know.
/// </summary>
public interface IRunReportRenderer
{
    byte[] Compose(RunReport report, byte[] markedOriginal);
}

public sealed class PdfSharpReportRenderer : IRunReportRenderer
{
    static PdfSharpReportRenderer() => InterFontResolver.Register();

    public byte[] Compose(RunReport report, byte[] markedOriginal)
    {
        using var original = PdfReader.Open(new MemoryStream(markedOriginal, writable: false), PdfDocumentOpenMode.Import);
        using var doc = new PdfDocument();
        doc.Info.Title = $"Housekeeping plan · {Hotels.PrintedNames[report.Hotel]} · {report.ReportDate:dd.MM.yyyy}";
        doc.Info.Creator = "HSK Automation";

        var reportPages = new ReportWriter(doc, report).Write();

        var firstOriginal = doc.PageCount + 1;
        foreach (var page in original.Pages) doc.AddPage(page);
        if (original.PageCount > 0)
            doc.Outlines.Add("Original task sheet with coloured dots", doc.Pages[firstOriginal - 1], true);

        ReportWriter.DrawFooters(doc, report, reportPages, original.PageCount);

        using var output = new MemoryStream();
        doc.Save(output, closeStream: false);
        return output.ToArray();
    }
}

/// <summary>Colour meanings as the housekeeping team reads them (core workflow, step 5).</summary>
public static class ColourMeaning
{
    public static string Service(ColourName? colour, bool cleaned) => colour switch
    {
        ColourName.Red => "Full clean",
        ColourName.Blue => "Standard + towels & linen",
        ColourName.Purple => "Standard + new towels",
        ColourName.Green => cleaned ? "Light service" : "Only if a card is shown",
        ColourName.Gold => "Special request",
        _ => "No service",
    };

    public static string Legend(ColourName? colour) => colour switch
    {
        ColourName.Red => "Full clean – arrival, departure or due out",
        ColourName.Blue => "Standard clean + fresh towels and bed linen",
        ColourName.Purple => "Standard clean + new towels",
        ColourName.Green => "Light service – only if the guest shows a card",
        ColourName.Gold => "Special request from the guest",
        _ => "No dot – empty clean room or nothing to do",
    };
}

internal sealed class ReportWriter(PdfDocument doc, RunReport report)
{
    private const double PageW = 595.28, PageH = 841.89, Margin = 40, ContentW = PageW - 2 * Margin;
    private const double Bottom = PageH - 52; // room kept free for the footer

    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-GB");

    private static readonly XColor Ink = Hex("#1B2738"), Muted = Hex("#5D6A7D"), Line = Hex("#D5DCE6"),
        Zebra = Hex("#F6F8FB"), Accent = Hex("#0F6E6A"), AccentSoft = Hex("#E3F2F0"), Warn = Hex("#A5281B"), WarnSoft = Hex("#FBEAE7");

    private static readonly XFont H1 = Font(20, true), H2 = Font(13, true), H3 = Font(10.5, true), Body = Font(9),
        BodyBold = Font(9, true), Small = Font(7.5), SmallBold = Font(7.5, true), Kicker = Font(7.5, true), Big = Font(19, true);

    private static readonly ColourName?[] LegendOrder = [ColourName.Red, ColourName.Blue, ColourName.Purple, ColourName.Green, ColourName.Gold, null];

    private PdfSharp.Pdf.PdfPage page = null!;
    private XGraphics gfx = null!;
    private double y;

    private string HotelName => Hotels.PrintedNames[report.Hotel];
    private XColor HotelColour => report.Hotel == HotelId.Novotel ? Hex("#C0392B") : Hex("#2471A3");

    /// <summary>Writes the report pages and returns how many there are.</summary>
    public int Write()
    {
        StartPage(first: true);
        doc.Outlines.Add("Summary", page, true);
        StatusBanner();
        StatTiles();
        Legend();
        SheetOverview();

        // The room list starts on its own page, so page 1 is a one-page overview.
        StartPage();
        foreach (var sheet in OrderedSheets())
            RoomSection(sheet.Sheet, sheet.Rooms);

        gfx.Dispose();
        return doc.PageCount;
    }

    // ---------- page frame ----------

    private void StartPage(bool first = false, string? continuing = null)
    {
        gfx?.Dispose();
        page = doc.AddPage();
        page.Width = XUnit.FromPoint(PageW);
        page.Height = XUnit.FromPoint(PageH);
        gfx = XGraphics.FromPdfPage(page);

        if (first)
        {
            gfx.DrawRectangle(new XSolidBrush(HotelColour), 0, 0, PageW, 104);
            gfx.DrawString("HOUSEKEEPING PLAN", Kicker, new XSolidBrush(XColor.FromArgb(220, 255, 255, 255)), Margin, 30, XStringFormats.TopLeft);
            gfx.DrawString(HotelName, H1, XBrushes.White, Margin, 44, XStringFormats.TopLeft);

            var weekday = report.ReportDate.ToString("dddd", English);
            var date = report.ReportDate.ToString("dd.MM.yyyy", German);
            gfx.DrawString(weekday.ToUpperInvariant(), Kicker, new XSolidBrush(XColor.FromArgb(220, 255, 255, 255)), PageW - Margin, 30, XStringFormats.TopRight);
            gfx.DrawString(date, H1, XBrushes.White, PageW - Margin, 44, XStringFormats.TopRight);

            var meta = new List<string>();
            if (report.PublishedAtLocal is { } p) meta.Add($"Printed by Opera {p.ToString("dd.MM.yyyy HH:mm", German)}");
            meta.Add($"Created {report.GeneratedAtLocal.ToString("dd.MM.yyyy HH:mm", German)}");
            if (report.Rhythm is { } r) meta.Add($"Cleaning rhythm: {(r == RhythmType.TwoTages ? "2-Tages" : "3-Tages")}");
            gfx.DrawString(string.Join("   ·   ", meta), Small, new XSolidBrush(XColor.FromArgb(235, 255, 255, 255)), Margin, 80, XStringFormats.TopLeft);
            y = 124;
        }
        else
        {
            gfx.DrawRectangle(new XSolidBrush(HotelColour), 0, 0, PageW, 6);
            gfx.DrawString(HotelName, BodyBold, new XSolidBrush(Ink), Margin, 22, XStringFormats.TopLeft);
            gfx.DrawString(report.ReportDate.ToString("dd.MM.yyyy", German), Body, new XSolidBrush(Muted), PageW - Margin, 22, XStringFormats.TopRight);
            gfx.DrawLine(new XPen(Line, 0.6), Margin, 40, PageW - Margin, 40);
            y = 54;
            if (continuing is not null)
            {
                gfx.DrawString(continuing, H3, new XSolidBrush(Muted), Margin, y, XStringFormats.TopLeft);
                y += 20;
            }
        }
    }

    private void Ensure(double height, string? continuing = null)
    {
        if (y + height > Bottom) StartPage(continuing: continuing);
    }

    public static void DrawFooters(PdfDocument doc, RunReport report, int reportPages, int originalPages)
    {
        var total = doc.PageCount;
        var name = Hotels.PrintedNames[report.Hotel];
        for (var i = 0; i < reportPages; i++)
        {
            using var g = XGraphics.FromPdfPage(doc.Pages[i], XGraphicsPdfPageOptions.Append);
            g.DrawLine(new XPen(Line, 0.6), Margin, PageH - 38, PageW - Margin, PageH - 38);
            var left = originalPages > 0
                ? $"{name} · {report.ReportDate.ToString("dd.MM.yyyy", German)} · Original task sheet with coloured dots: pages {reportPages + 1}–{total}"
                : $"{name} · {report.ReportDate.ToString("dd.MM.yyyy", German)}";
            g.DrawString(left, Small, new XSolidBrush(Muted), Margin, PageH - 30, XStringFormats.TopLeft);
            g.DrawString($"Page {i + 1} of {total}", SmallBold, new XSolidBrush(Muted), PageW - Margin, PageH - 30, XStringFormats.TopRight);
        }
    }

    // ---------- summary page ----------

    private void StatusBanner()
    {
        var read = report.Rooms.Count;
        var expected = report.Summary?.TotalRooms;
        var ok = report.Warnings.Count == 0;
        var title = ok ? "All checks passed – ready to print" : $"Please check {report.Warnings.Count} {(report.Warnings.Count == 1 ? "point" : "points")} before printing";
        var sub = expected is { } e
            ? $"{read} of {e} rooms read · {report.Sheets.Count} task {(report.Sheets.Count == 1 ? "sheet" : "sheets")}"
            : $"{read} rooms read · {report.Sheets.Count} task sheets";

        var lines = report.Warnings.SelectMany(w => Wrap(w, Body, ContentW - 48)).ToList();
        var height = 46 + (lines.Count > 0 ? lines.Count * 12 + 6 : 0);
        var fg = ok ? Accent : Warn;
        gfx.DrawRoundedRectangle(new XSolidBrush(ok ? AccentSoft : WarnSoft), new XRect(Margin, y, ContentW, height), new XSize(10, 10));
        gfx.DrawRectangle(new XSolidBrush(fg), Margin, y + 10, 3, height - 20);

        // Check mark or exclamation inside a circle.
        gfx.DrawEllipse(new XSolidBrush(fg), Margin + 14, y + 13, 20, 20);
        gfx.DrawString(ok ? "✓" : "!", BodyBold, XBrushes.White, new XRect(Margin + 14, y + 13, 20, 20), XStringFormats.Center);

        gfx.DrawString(title, H3, new XSolidBrush(fg), Margin + 44, y + 11, XStringFormats.TopLeft);
        gfx.DrawString(sub, Body, new XSolidBrush(Ink), Margin + 44, y + 26, XStringFormats.TopLeft);
        var ly = y + 46;
        foreach (var l in lines)
        {
            gfx.DrawString(l, Body, new XSolidBrush(Ink), Margin + 44, ly, XStringFormats.TopLeft);
            ly += 12;
        }
        y += height + 16;
    }

    private void StatTiles()
    {
        var toClean = report.Rooms.Count(r => r.Colour is not null && r.Cleaned);
        var tiles = new (string Label, string Value)[]
        {
            ("Rooms on the sheet", report.Rooms.Count.ToString(English)),
            ("Rooms to clean", toClean.ToString(English)),
            ("Departures", (report.Summary?.DepartureRooms ?? report.Rooms.Count(r => r.ReservationStatus.Contains("Due Out", StringComparison.OrdinalIgnoreCase))).ToString(English)),
            ("Dirty rooms", (report.Summary?.DirtyRooms ?? report.Rooms.Count(r => r.RoomStatus == "DI")).ToString(English)),
            ("Clean rooms", (report.Summary?.CleanRooms ?? report.Rooms.Count(r => r.RoomStatus == "CL")).ToString(English)),
        };
        const double gap = 8;
        var w = (ContentW - gap * (tiles.Length - 1)) / tiles.Length;
        for (var i = 0; i < tiles.Length; i++)
        {
            var x = Margin + i * (w + gap);
            var rect = new XRect(x, y, w, 52);
            gfx.DrawRoundedRectangle(new XPen(Line, 0.8), XBrushes.White, rect, new XSize(8, 8));
            gfx.DrawString(tiles[i].Value, Big, new XSolidBrush(i == 1 ? Accent : Ink), x + 10, y + 9, XStringFormats.TopLeft);
            gfx.DrawString(tiles[i].Label, Small, new XSolidBrush(Muted), x + 10, y + 35, XStringFormats.TopLeft);
        }
        y += 52 + 22;
    }

    private void Legend()
    {
        SectionTitle("Cleaning today", "What each colour on the sheet means");
        var total = Math.Max(1, report.Rooms.Count);
        const double rowH = 24;
        var top = y;
        gfx.DrawRoundedRectangle(new XPen(Line, 0.8), XBrushes.White, new XRect(Margin, top, ContentW, rowH * LegendOrder.Length + 8), new XSize(8, 8));
        y += 4;
        for (var i = 0; i < LegendOrder.Length; i++)
        {
            var colour = LegendOrder[i];
            var rooms = report.Rooms.Where(r => r.Colour == colour).ToList();
            if (i > 0) gfx.DrawLine(new XPen(Line, 0.5), Margin + 10, y, Margin + ContentW - 10, y);

            Swatch(colour, Margin + 12, y + 7, 10);
            gfx.DrawString(ColourWord(colour), BodyBold, new XSolidBrush(Ink), Margin + 30, y + 7, XStringFormats.TopLeft);
            var meaning = ColourMeaning.Legend(colour);
            if (colour == ColourName.Green && rooms.Count > 0)
                meaning += $" · {rooms.Count(r => r.Cleaned)} cleaned today";
            gfx.DrawString(meaning, Body, new XSolidBrush(Muted), Margin + 96, y + 7, XStringFormats.TopLeft);

            // Count and a small proportion bar.
            const double barW = 70;
            var barX = Margin + ContentW - 12 - barW;
            gfx.DrawRoundedRectangle(new XSolidBrush(Line), new XRect(barX, y + 10, barW, 5), new XSize(3, 3));
            if (rooms.Count > 0)
                gfx.DrawRoundedRectangle(new XSolidBrush(colour is { } c ? Hex(Colours.Hex[c]) : Muted), new XRect(barX, y + 10, Math.Max(5, barW * rooms.Count / total), 5), new XSize(3, 3));
            gfx.DrawString(rooms.Count.ToString(English), BodyBold, new XSolidBrush(Ink), barX - 10, y + 7, XStringFormats.TopRight);
            y += rowH;
        }
        y = top + rowH * LegendOrder.Length + 8 + 22;
    }

    private void SheetOverview()
    {
        var sheets = OrderedSheets().ToList();
        if (sheets.Count == 0) return;
        Ensure(60 + 18 * sheets.Count);
        SectionTitle("Task sheets", "Rooms per sheet and colour");

        string[] heads = ["Sheet", "Rooms", "Red", "Blue", "Purple", "Green", "Gold", "No dot", "Totals check"];
        double[] widths = [70, 50, 44, 44, 50, 50, 44, 50, 113];
        TableHeader(heads, widths, SheetAlign);
        var row = 0;
        foreach (var (sheet, rooms) in sheets)
        {
            const double h = 18;
            if (row++ % 2 == 1) gfx.DrawRectangle(new XSolidBrush(Zebra), Margin, y, ContentW, h);
            var x = Margin;
            var cells = new List<string> { SheetLabel(sheet.SheetNo), rooms.Count.ToString(English) };
            cells.AddRange(LegendOrder.Select(c => Count(rooms.Count(r => r.Colour == c))));
            for (var i = 0; i < cells.Count; i++)
            {
                var right = i >= 1;
                gfx.DrawString(cells[i], i == 0 ? BodyBold : Body, new XSolidBrush(cells[i] == "–" ? Line : Ink),
                    right ? x + widths[i] - 8 : x + 8, y + 4, right ? XStringFormats.TopRight : XStringFormats.TopLeft);
                x += widths[i];
            }
            var matches = sheet.TotalRooms is null || sheet.TotalRooms == rooms.Count;
            var check = sheet.TotalRooms is null ? "No total printed" : matches ? $"✓ matches ({sheet.TotalRooms})" : $"Sheet says {sheet.TotalRooms}";
            gfx.DrawString(check, matches ? Body : BodyBold, new XSolidBrush(matches ? Accent : Warn), x + 12, y + 4, XStringFormats.TopLeft);
            y += h;
        }
        gfx.DrawLine(new XPen(Line, 0.6), Margin, y, Margin + ContentW, y);
        y += 22;
    }

    // ---------- room list ----------

    private static readonly string[] RoomHeads = ["Room", "Colour", "Service", "Why", "Status", "Stay", "Guests"];
    private static readonly double[] RoomWidths = [44, 70, 122, 118, 52, 74, 35.28];
    private static readonly (bool, double)[] RoomAlign = [(false, 10), (false, 2), (false, 6), (false, 6), (false, 6), (false, 6), (true, 2)];
    private static readonly (bool, double)[] SheetAlign = [(false, 8), (true, 8), (true, 8), (true, 8), (true, 8), (true, 8), (true, 8), (true, 8), (false, 12)];

    private void RoomSection(ReportSheet sheet, IReadOnlyList<ReportRoom> rooms)
    {
        var hasNote = !string.IsNullOrWhiteSpace(sheet.Instructions) && !sheet.Instructions.Trim().Equals("Keine TA.", StringComparison.OrdinalIgnoreCase);
        var noteLines = hasNote ? Wrap(sheet.Instructions.Trim(), Body, ContentW - 24) : [];
        var headerHeight = 34 + (hasNote ? noteLines.Count * 12 + 26 : 0) + 22 + 22 * Math.Min(3, rooms.Count);
        if (y + headerHeight > Bottom) StartPage();
        else if (y > 80) y += 6;

        var title = $"Task sheet {SheetLabel(sheet.SheetNo)}";
        doc.Outlines.Add(title, page, true);
        var toClean = rooms.Count(r => r.Colour is not null && r.Cleaned);
        gfx.DrawRectangle(new XSolidBrush(HotelColour), Margin, y + 2, 4, 16);
        gfx.DrawString(title, H2, new XSolidBrush(Ink), Margin + 12, y, XStringFormats.TopLeft);
        gfx.DrawString($"{rooms.Count} rooms · {toClean} to clean{(string.IsNullOrWhiteSpace(sheet.TaskCodes) ? "" : $" · Tasks {sheet.TaskCodes}")}",
            Body, new XSolidBrush(Muted), PageW - Margin, y + 4, XStringFormats.TopRight);
        y += 28;

        if (hasNote)
        {
            var h = noteLines.Count * 12 + 20;
            gfx.DrawRoundedRectangle(new XSolidBrush(Zebra), new XRect(Margin, y, ContentW, h), new XSize(6, 6));
            gfx.DrawString("INSTRUCTIONS", SmallBold, new XSolidBrush(Muted), Margin + 12, y + 6, XStringFormats.TopLeft);
            var ny = y + 16;
            foreach (var l in noteLines)
            {
                gfx.DrawString(l, Body, new XSolidBrush(Ink), Margin + 12, ny, XStringFormats.TopLeft);
                ny += 12;
            }
            y += h + 10;
        }

        TableHeader(RoomHeads, RoomWidths, RoomAlign);
        var row = 0;
        foreach (var room in rooms)
        {
            const double h = 22;
            if (y + h > Bottom)
            {
                StartPage(continuing: $"{title} (continued)");
                TableHeader(RoomHeads, RoomWidths, RoomAlign);
            }
            RoomRow(room, row++ % 2 == 1, h);
            y += h;
        }
        gfx.DrawLine(new XPen(Line, 0.6), Margin, y, Margin + ContentW, y);
        y += 22;
    }

    private void RoomRow(ReportRoom room, bool zebra, double h)
    {
        if (zebra) gfx.DrawRectangle(new XSolidBrush(Zebra), Margin, y, ContentW, h);
        if (room.Colour is { } c) gfx.DrawRectangle(new XSolidBrush(Hex(Colours.Hex[c])), Margin, y + 3, 3, h - 6);

        var x = Margin;
        var ty = y + (h - 11) / 2;
        var muted = room.Colour is null || !room.Cleaned;

        gfx.DrawString(room.RoomNo, H3, new XSolidBrush(Ink), x + 10, ty - 1, XStringFormats.TopLeft);
        x += RoomWidths[0];

        ColourPill(room.Colour, room.Cleaned, x + 2, y + 4, RoomWidths[1] - 10, h - 8);
        x += RoomWidths[1];

        gfx.DrawString(Fit(ColourMeaning.Service(room.Colour, room.Cleaned), muted ? Body : BodyBold, RoomWidths[2] - 10), muted ? Body : BodyBold,
            new XSolidBrush(muted ? Muted : Ink), x + 6, ty, XStringFormats.TopLeft);
        x += RoomWidths[2];

        // The service column already says "only if a card is shown".
        var why = room.Reason.Replace(", only if a card is shown", " of stay");
        gfx.DrawString(Fit(why, Body, RoomWidths[3] - 10), Body, new XSolidBrush(Ink), x + 6, ty, XStringFormats.TopLeft);
        x += RoomWidths[3];

        var (status, dirty) = RoomStatusText(room.RoomStatus);
        if (dirty) gfx.DrawEllipse(new XSolidBrush(Warn), x + 6, ty + 3, 5, 5);
        gfx.DrawString(status, Body, new XSolidBrush(dirty ? Warn : Ink), x + (dirty ? 14 : 6), ty, XStringFormats.TopLeft);
        x += RoomWidths[4];

        gfx.DrawString(Fit(StayText(room), Body, RoomWidths[5] - 8), Body, new XSolidBrush(Ink), x + 6, ty, XStringFormats.TopLeft);
        x += RoomWidths[5];

        var guests = room.Adults is null && room.Children is null ? "–" : room.Children is > 0 ? $"{room.Adults ?? 0}+{room.Children}" : $"{room.Adults ?? 0}";
        gfx.DrawString(guests, Body, new XSolidBrush(guests == "–" ? Muted : Ink), x + RoomWidths[6] - 2, ty, XStringFormats.TopRight);
    }

    // ---------- building blocks ----------

    private void SectionTitle(string title, string subtitle)
    {
        gfx.DrawString(title, H2, new XSolidBrush(Ink), Margin, y, XStringFormats.TopLeft);
        gfx.DrawString(subtitle, Body, new XSolidBrush(Muted), Margin + gfx.MeasureString(title, H2).Width + 10, y + 4, XStringFormats.TopLeft);
        y += 24;
    }

    /// <summary>Header row; each column's text is placed like its cells: (right-aligned?, inset from the column edge).</summary>
    private void TableHeader(string[] heads, double[] widths, (bool Right, double Inset)[] align)
    {
        const double h = 20;
        gfx.DrawRoundedRectangle(new XSolidBrush(Hex("#EEF2F6")), new XRect(Margin, y, ContentW, h), new XSize(5, 5));
        var x = Margin;
        for (var i = 0; i < heads.Length; i++)
        {
            var (right, inset) = align[i];
            gfx.DrawString(heads[i].ToUpperInvariant(), SmallBold, new XSolidBrush(Muted),
                right ? x + widths[i] - inset : x + inset, y + 6, right ? XStringFormats.TopRight : XStringFormats.TopLeft);
            x += widths[i];
        }
        y += h + 2;
    }

    private void ColourPill(ColourName? colour, bool cleaned, double x, double top, double w, double h)
    {
        if (colour is not { } c)
        {
            gfx.DrawString("No dot", Body, new XSolidBrush(Muted), x + 4, top + (h - 11) / 2, XStringFormats.TopLeft);
            return;
        }
        var solid = Hex(Colours.Hex[c]);
        var rect = new XRect(x, top, w, h);
        if (cleaned)
            gfx.DrawRoundedRectangle(new XSolidBrush(XColor.FromArgb(46, solid.R, solid.G, solid.B)), rect, new XSize(h, h));
        else
            gfx.DrawRoundedRectangle(new XPen(solid, 0.8) { DashStyle = XDashStyle.Dash }, XBrushes.White, rect, new XSize(h, h));
        gfx.DrawEllipse(new XSolidBrush(solid), x + 6, top + (h - 7) / 2, 7, 7);
        gfx.DrawString(ColourWord(c), SmallBold, new XSolidBrush(Ink), x + 17, top + (h - 9) / 2, XStringFormats.TopLeft);
    }

    private void Swatch(ColourName? colour, double x, double top, double size)
    {
        if (colour is { } c) gfx.DrawEllipse(new XSolidBrush(Hex(Colours.Hex[c])), x, top, size, size);
        else gfx.DrawEllipse(new XPen(Muted, 0.8), x + 0.5, top + 0.5, size - 1, size - 1);
    }

    private IEnumerable<(ReportSheet Sheet, IReadOnlyList<ReportRoom> Rooms)> OrderedSheets()
    {
        var known = report.Sheets.Select(s => s.SheetNo).ToHashSet();
        var sheets = report.Sheets.ToList();
        // Rooms on a sheet the reader could not match to a header still get listed.
        sheets.AddRange(report.Rooms.Select(r => r.SheetNo).Distinct().Where(s => !known.Contains(s)).Select(s => new ReportSheet(s, "", "", null)));
        return sheets.Select(s => (s, (IReadOnlyList<ReportRoom>)report.Rooms.Where(r => r.SheetNo == s.SheetNo).ToList()));
    }

    private static string SheetLabel(string sheetNo) => sheetNo.Equals("FLOAT", StringComparison.OrdinalIgnoreCase) ? "Float" : sheetNo;

    private static string Count(int n) => n == 0 ? "–" : n.ToString(English);

    private static string ColourWord(ColourName? c) => c?.ToString() ?? "No dot";

    private static (string Text, bool Dirty) RoomStatusText(string status) => status.Trim().ToUpperInvariant() switch
    {
        "DI" => ("Dirty", true),
        "CL" => ("Clean", false),
        "IN" or "IP" => ("Inspected", false),
        "OO" => ("Out of order", false),
        "OS" => ("Out of service", false),
        "" => ("–", false),
        var s => (s, false),
    };

    private static string StayText(ReportRoom r)
    {
        static string D(DateOnly d) => d.ToString("dd.MM.", German);
        return (r.Arrival, r.Departure) switch
        {
            ({ } a, { } d) => $"{D(a)}–{D(d)}",
            ({ } a, null) => $"from {D(a)}",
            (null, { } d) => $"until {D(d)}",
            _ => "–",
        };
    }

    private string Fit(string text, XFont font, double width)
    {
        if (gfx.MeasureString(text, font).Width <= width) return text;
        while (text.Length > 1 && gfx.MeasureString(text + "…", font).Width > width) text = text[..^1];
        return text.TrimEnd() + "…";
    }

    private List<string> Wrap(string text, XFont font, double width)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.Replace("\r", "").Split('\n'))
        {
            var line = "";
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = line.Length == 0 ? word : $"{line} {word}";
                if (gfx.MeasureString(candidate, font).Width <= width) { line = candidate; continue; }
                if (line.Length > 0) lines.Add(line);
                line = gfx.MeasureString(word, font).Width <= width ? word : Fit(word, font, width);
            }
            if (line.Length > 0) lines.Add(line);
        }
        return lines;
    }

    private static XFont Font(double size, bool bold = false) =>
        new(bold ? InterFontResolver.SemiBold : InterFontResolver.Regular, size, XFontStyleEx.Regular);

    private static XColor Hex(string hex) =>
        XColor.FromArgb(int.Parse(hex.TrimStart('#'), NumberStyles.HexNumber) | unchecked((int)0xFF000000));
}

/// <summary>
/// Serves the bundled Inter font (SIL Open Font License, see Fonts/OFL-LICENSE.txt) to PDFsharp, so the report
/// looks the same on every machine; the Function App image has no system fonts.
/// </summary>
public sealed class InterFontResolver : IFontResolver
{
    public const string Regular = "Inter", SemiBold = "Inter SemiBold";
    private static readonly object Gate = new();
    private static bool registered;

    public static void Register()
    {
        lock (Gate)
        {
            if (registered) return;
            if (GlobalFontSettings.FontResolver is null) GlobalFontSettings.FontResolver = new InterFontResolver();
            registered = true;
        }
    }

    public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic) =>
        familyName.Equals(SemiBold, StringComparison.OrdinalIgnoreCase) || bold
            ? new FontResolverInfo("Inter-SemiBold")
            : new FontResolverInfo("Inter-Regular");

    public byte[]? GetFont(string faceName)
    {
        using var stream = typeof(InterFontResolver).Assembly.GetManifestResourceStream($"HousekeepingParser.Fonts.{faceName}.ttf");
        if (stream is null) return null;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }
}
