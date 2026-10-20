using System.Globalization;
using HousekeepingParser.Core;
using HousekeepingParser.PdfMarking;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace HousekeepingParser.Reports;

/// <summary>Renders the three business PDFs as typed replicas of the paper forms.</summary>
public sealed class PdfSharpBusinessReportRenderer : IBusinessReportRenderer
{
    static PdfSharpBusinessReportRenderer() => InterFontResolver.Register();

    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    public byte[] TaskSheetReport(TaskSheetReportModel model) =>
        Save($"Task Sheet Report · {Hotels.PrintedNames[model.Hotel]} · {model.ReportDate.ToString("dd.MM.yyyy", German)}",
            doc => new TaskSheetFormWriter(doc, model).Write());

    public byte[] Handover(HandoverModel model) =>
        Save($"Übergabe Etage – Empfang · {Hotels.PrintedNames[model.Hotel]} · {model.ReportDate.ToString("dd.MM.yyyy", German)}",
            doc => new HandoverFormWriter(doc, model).Write());

    public byte[] MonthlyStatement(MonthlyStatementModel model) =>
        Save($"Leistungsnachweis · {model.Objekt} · {model.Zeitraum}", doc => new MonthlyStatementWriter(doc, model).Write());

    private static byte[] Save(string title, Action<PdfDocument> write)
    {
        using var doc = new PdfDocument();
        doc.Info.Title = title;
        doc.Info.Creator = "HSK Automation";
        write(doc);
        using var output = new MemoryStream();
        doc.Save(output, closeStream: false);
        return output.ToArray();
    }
}

/// <summary>Page frame, footer and drawing helpers shared by the forms.</summary>
internal abstract class FormWriter
{
    protected static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    // Black on white; grey only where the paper has it.
    protected static readonly XColor Ink = Hex("#111111"), Muted = Hex("#666666"), Faint = Hex("#8C8C8C"),
        HeaderGrey = Hex("#BFBFBF"), Shade = Hex("#E7E7E7");

    protected static readonly XFont Small = Font(7), SmallBold = Font(7, true), Body = Font(9), BodyBold = Font(9, true);

    protected readonly PdfDocument doc;
    protected readonly double PageW, PageH, Margin;
    protected PdfPage page = null!;
    protected XGraphics gfx = null!;
    protected double y;

    protected FormWriter(PdfDocument doc, bool landscape, double margin)
    {
        this.doc = doc;
        (PageW, PageH) = landscape ? (841.89, 595.28) : (595.28, 841.89);
        Margin = margin;
    }

    protected double ContentW => PageW - 2 * Margin;
    protected double Bottom => PageH - 50; // room kept free for the footer

    /// <summary>Left footer text, e.g. "HSK Automation · erstellt 01.09.2026 07:20".</summary>
    protected abstract string FooterLeft { get; }

    /// <summary>Draws a new page top and moves <see cref="y"/> below it.</summary>
    protected abstract void PageHeader(int pageNo);

    protected void StartPage()
    {
        gfx?.Dispose();
        page = doc.AddPage();
        page.Width = XUnit.FromPoint(PageW);
        page.Height = XUnit.FromPoint(PageH);
        gfx = XGraphics.FromPdfPage(page);
        y = Margin;
        PageHeader(doc.PageCount);
    }

    /// <summary>Closes the last page and adds "Seite x von y" to every page.</summary>
    protected void Finish()
    {
        gfx.Dispose();
        var total = doc.PageCount;
        for (var i = 0; i < total; i++)
        {
            using var g = XGraphics.FromPdfPage(doc.Pages[i], XGraphicsPdfPageOptions.Append);
            g.DrawLine(new XPen(Shade, 0.6), Margin, PageH - 36, PageW - Margin, PageH - 36);
            g.DrawString(FooterLeft, Small, new XSolidBrush(Faint), Margin, PageH - 30, XStringFormats.TopLeft);
            g.DrawString($"Seite {i + 1} von {total}", Small, new XSolidBrush(Faint), PageW - Margin, PageH - 30, XStringFormats.TopRight);
        }
    }

    // ---------- building blocks ----------

    protected void Text(string s, XFont font, double x, double top, XColor? colour = null) =>
        gfx.DrawString(s, font, new XSolidBrush(colour ?? Ink), x, top, XStringFormats.TopLeft);

    protected void TextRight(string s, XFont font, double right, double top, XColor? colour = null) =>
        gfx.DrawString(s, font, new XSolidBrush(colour ?? Ink), right, top, XStringFormats.TopRight);

    protected void TextCentre(string s, XFont font, double centre, double top, XColor? colour = null) =>
        gfx.DrawString(s, font, new XSolidBrush(colour ?? Ink), centre, top, XStringFormats.TopCenter);

    /// <summary>Text centred vertically in a cell; align -1 left, 0 centre, 1 right.</summary>
    protected void CellText(string s, XFont font, XRect cell, int align = 0, double inset = 5, XColor? colour = null)
    {
        if (s.Length == 0) return;
        s = Fit(s, font, cell.Width - 2 * Math.Min(inset, 3));
        var top = cell.Y + cell.Height / 2 - 0.6 * font.Size; // centres the cap height of Inter
        switch (align)
        {
            case < 0: Text(s, font, cell.X + inset, top, colour); break;
            case > 0: TextRight(s, font, cell.Right - inset, top, colour); break;
            default: TextCentre(s, font, cell.X + cell.Width / 2, top, colour); break;
        }
    }

    protected void Line(double x1, double y1, double x2, double y2, double width, XColor? colour = null) =>
        gfx.DrawLine(new XPen(colour ?? Ink, width), x1, y1, x2, y2);

    protected void Box(XRect r, double width) => gfx.DrawRectangle(new XPen(Ink, width), r);

    protected void Fill(XRect r, XColor colour) => gfx.DrawRectangle(new XSolidBrush(colour), r);

    /// <summary>A value on an underline from x1 to x2, top at <paramref name="top"/>.</summary>
    protected void Underlined(string value, XFont font, double x1, double x2, double top, bool centre = false)
    {
        Line(x1, top + font.Size * 1.3, x2, top + font.Size * 1.3, 0.6);
        value = Fit(value, font, x2 - x1 - 4);
        if (centre) TextCentre(value, font, (x1 + x2) / 2, top);
        else Text(value, font, x1 + 3, top);
    }

    /// <summary>Signature line with the approver (or "draft") under it.</summary>
    protected void Signature(string label, double x, double width, double top, string? approvedBy, DateTime? approvedAt)
    {
        Text(label, BodyBold, x, top);
        Line(x, top + 46, x + width, top + 46, 0.8);
        var note = approvedBy is null
            ? "Entwurf – nicht freigegeben"
            : approvedAt is { } at ? $"Freigegeben von {approvedBy} am {at.ToString("dd.MM.yyyy HH:mm", German)}" : $"Freigegeben von {approvedBy}";
        var ly = top + 50;
        foreach (var l in Wrap(note, Small, width))
        {
            Text(l, Small, x, ly, Muted);
            ly += 9;
        }
    }

    protected string Fit(string text, XFont font, double width)
    {
        if (gfx.MeasureString(text, font).Width <= width) return text;
        while (text.Length > 1 && gfx.MeasureString(text + "…", font).Width > width) text = text[..^1];
        return text.TrimEnd() + "…";
    }

    protected List<string> Wrap(string text, XFont font, double width)
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

    // ---------- German formats ----------

    protected static string N(int v) => v.ToString(German);
    protected static string N(decimal v) => v.ToString("0.##", German);          // 196, 0,5
    protected static string Hours(decimal v) => v.ToString("0.00", German);      // 8,75
    protected static string Signed(int v) => v.ToString("+0;-0;0", German);      // -7, +3
    protected static string Time(TimeOnly t) => t.ToString("HH:mm", German);

    protected static string Span(TimeOnly? start, TimeOnly? end) => (start, end) switch
    {
        ({ } s, { } e) => $"{Time(s)} – {Time(e)}",
        ({ } s, null) => $"{Time(s)} –",
        (null, { } e) => $"– {Time(e)}",
        _ => "",
    };

    protected static XFont Font(double size, bool bold = false) =>
        new(bold ? InterFontResolver.SemiBold : InterFontResolver.Regular, size, XFontStyleEx.Regular);

    protected static XColor Hex(string hex) =>
        XColor.FromArgb(int.Parse(hex.TrimStart('#'), NumberStyles.HexNumber) | unchecked((int)0xFF000000));
}

/// <summary>Opera "Task Sheet Report" summary page, handwritten fields typed.</summary>
internal sealed class TaskSheetFormWriter(PdfDocument doc, TaskSheetReportModel m) : FormWriter(doc, landscape: false, margin: 40)
{
    private static readonly XFont Title = Font(15, true), HotelFont = Font(11.5), DateFont = Font(11, true), Cell = Font(9), CellBold = Font(9, true);

    private static readonly string[] Heads = ["Cleaner", "Gereinigt", "NS", "RK", "KS", "Start", "Ende", "Pause"];
    private static readonly double[] Widths = [150, 62, 46, 46, 46, 55.28, 55, 55];
    private const double HeadH = 20, RowH = 19;

    protected override string FooterLeft => $"HSK Automation · erstellt {m.GeneratedAtLocal.ToString("dd.MM.yyyy HH:mm", German)}";

    public void Write()
    {
        StartPage();
        SummaryAndStaff();
        CleanerTable();
        TotalsBlock();
        Finish();
    }

    protected override void PageHeader(int pageNo)
    {
        var hotel = Hotels.PrintedNames[m.Hotel];
        TextCentre(hotel, HotelFont, PageW / 2, 34);
        TextCentre(pageNo == 1 ? "Task Sheet Report" : "Task Sheet Report (Fortsetzung)", Title, PageW / 2, 51);
        TextRight(m.ReportDate.ToString("dd.MM.yy", German), DateFont, PageW - Margin, 34);
        TextRight(m.GeneratedAtLocal.ToString("HH:mm", German), Body, PageW - Margin, 50, Muted);
        Line(Margin, 78, PageW - Margin, 78, 0.8);
        y = 92;
    }

    private void SummaryAndStaff()
    {
        var top = y;

        // Opera "Report Summary": label left, value right-aligned.
        Text("Report Summary", Font(10, true), Margin, top);
        var s = m.OperaSummary;
        (string Label, string Value)[] rows =
        [
            ("Total Task Sheets", s is null ? "–" : N(s.TotalTaskSheets)),
            ("Total Rooms", s is null ? "–" : N(s.TotalRooms)),
            ("Departure Rooms", s is null ? "–" : N(s.DepartureRooms)),
            ("Dirty Rooms", s is null ? "–" : N(s.DirtyRooms)),
            ("Clean Rooms", s is null ? "–" : N(s.CleanRooms)),
            ("Total Credits", s is null ? "–" : N(s.TotalCredits)),
        ];
        for (var i = 0; i < rows.Length; i++)
        {
            var ry = top + 20 + i * 15;
            Text(rows[i].Label, Body, Margin, ry);
            TextRight(rows[i].Value, BodyBold, Margin + 190, ry);
        }

        // Staff of the day: label, name, "Zeit:" and times on underlines.
        const double labelRight = 345, nameX = 351, nameEnd = 452, zeitX = 460, timeX = 486;
        for (var i = 0; i < m.Supervisors.Count; i++)
        {
            var sup = m.Supervisors[i];
            var ry = top + 2 + i * 18;
            TextRight($"{sup.Label}:", BodyBold, labelRight, ry);
            Underlined(sup.Name ?? "/", Body, nameX, nameEnd, ry);
            Text("Zeit:", Body, zeitX, ry);
            Underlined(sup.Name is null ? "/" : Span(sup.Start, sup.End), Body, timeX, PageW - Margin, ry);
        }

        y = top + Math.Max(20 + rows.Length * 15, 2 + m.Supervisors.Count * 18) + 18;
    }

    private void CleanerTable()
    {
        var frameTop = y;
        TableHeader();
        foreach (var c in m.Cleaners)
        {
            if (y + RowH > Bottom)
            {
                Frame(frameTop);
                StartPage();
                frameTop = y;
                TableHeader();
            }
            string[] cells =
            [
                c.Name,
                c.Gereinigt is { } g ? N(g) : "",
                c.Ns is { } ns ? N(ns) : "",
                c.Rk is { } rk ? N(rk) : "",
                c.Ks is { } ks ? N(ks) : "",
                c.Start is { } st ? Time(st) : "",
                c.End is { } en ? Time(en) : "",
                c.PauseHours is { } p ? N(p) : "",
            ];
            var x = Margin;
            for (var i = 0; i < cells.Length; i++)
            {
                CellText(cells[i], Cell, new XRect(x, y, Widths[i], RowH), i == 0 ? -1 : 0, 6);
                x += Widths[i];
            }
            y += RowH;
            Line(Margin, y, Margin + ContentW, y, 0.5);
        }
        Frame(frameTop);
    }

    private void TableHeader()
    {
        Fill(new XRect(Margin, y, ContentW, HeadH), HeaderGrey);
        var x = Margin;
        for (var i = 0; i < Heads.Length; i++)
        {
            CellText(Heads[i], CellBold, new XRect(x, y, Widths[i], HeadH), i == 0 ? -1 : 0, 6);
            x += Widths[i];
        }
        y += HeadH;
        Line(Margin, y, Margin + ContentW, y, 1);
    }

    /// <summary>Column lines and outer border of this page's table part.</summary>
    private void Frame(double top)
    {
        var x = Margin;
        for (var i = 0; i < Widths.Length - 1; i++)
        {
            x += Widths[i];
            Line(x, top, x, y, 0.5);
        }
        Box(new XRect(Margin, top, ContentW, y - top), 1.6);
    }

    private void TotalsBlock()
    {
        y += 20;
        if (y + 125 > Bottom) StartPage();
        var top = y;
        var t = m.Totals;

        (string Label, string Value)[] left =
        [
            ("Gereinigt:", N(t.Gereinigt)), ("No Service:", N(t.NoService)), ("Rote Karten:", N(t.RoteKarten)),
            ("Kein Service:", N(t.KeinService)), ("Sauber:", N(t.Sauber)), ("Liegezimmer:", N(t.Liegezimmer)), ("Summe:", N(t.Summe)),
        ];
        for (var i = 0; i < left.Length; i++)
        {
            var ry = top + i * 17;
            var last = i == left.Length - 1;
            Text(left[i].Label, last ? BodyBold : Body, Margin, ry);
            Underlined(left[i].Value, BodyBold, Margin + 80, Margin + 135, ry, centre: true);
        }

        const double midX = 222, midValueX = 326, midEnd = 392;
        (string Label, string Value)[] mid =
        [
            ("Pauschale Checks:", m.FlatRateDay ? $"{N(m.PauschaleChecks)} ({m.PauschaleLabel})" : "–"),
            ("Tatsächlich gecheckt:", N(m.ChecksActual)),
            ("zu berechnen:", Signed(m.ZuBerechnen)),
        ];
        for (var i = 0; i < mid.Length; i++)
        {
            var ry = top + i * 17;
            Text(mid[i].Label, i == 2 ? BodyBold : Body, midX, ry);
            Underlined(mid[i].Value, BodyBold, midValueX, midEnd, ry, centre: true);
        }

        const double sigX = 415;
        Signature("Unterschrift Richter", sigX, PageW - Margin - sigX, top, m.ApprovedBy, m.ApprovedAtLocal);
        y = top + left.Length * 17;
    }
}

/// <summary>"Übergabe Etage - Empfang": room lists handed to reception.</summary>
internal sealed class HandoverFormWriter(PdfDocument doc, HandoverModel m) : FormWriter(doc, landscape: false, margin: 40)
{
    private const int Cols = 5, FormCells = 30;
    private const double MinCellH = 18, TitleH = 20;
    private static readonly XFont Title = Font(12.5, true), LabelBold = Font(8.5, true), Note = Font(7.2), Room = Font(9.5, true),
        RowTitle = Font(10, true);

    private enum Part { Form, Annex }
    private Part part = Part.Form;
    private int annexFirstPage;
    private double frameTop;

    protected override string FooterLeft => $"HSK Automation · erstellt {m.GeneratedAtLocal.ToString("dd.MM.yyyy HH:mm", German)}";

    private string Date => m.ReportDate.ToString("dd.MM.yyyy", German);
    private string InHouse => m.Hotel == HotelId.Novotel ? "im Hause NOVOTEL City Münster" : "im Hause ibis budget City Münster";

    public void Write()
    {
        StartPage();
        frameTop = y;
        TitleRow("Übergabe Housekeeping");
        Section("* Zimmer - NS = No Service",
            ["(Gast hat keine \"Reinigung\"-Karte bis 7:00 Uhr an der Zimmertür gehängt, und teilt somit das er für den Tag auf einen Reinigungsservice verzichtet.)"],
            m.NoService, 9);
        Section("* Zimmer - RK = Rote Karte", ["(Gast im Zimmer, möchte nicht gestört werden)"], m.RoteKarte, 3);
        Section("* Zimmer - KS  = Kein Service", ["(Gast im Zimmer, möchte keinen Reinigungsservice haben)"], m.KeinService, 3);
        TitleRow("Liegezimmer");
        Section("* Zimmer - Late Check Out Liegezimmer", ["(Liegezimmer falls Late Check-out ab 15:30 und nicht benötigt wird.)"], m.LateCheckOut, 2);

        // The form holds 30; the rest goes on a separate list.
        var extra = m.Liegezimmer.Count - FormCells;
        Section("* Zimmer - Nicht gereinigt = Liegezimmer für Folgetag",
            ["Eintragungen hier möglich bis zu 30 Zimmer", "Bei mehr als 30 Zimmer bitte seperate Aufstellung anhängen"],
            m.Liegezimmer.Take(FormCells).ToList(), FormCells / Cols,
            extra > 0 ? $"weitere siehe Anlage ({extra} Zimmer)" : null);
        TitleRow("Informationen");
        InformationBox();
        Frame();

        y += 18;
        if (y + 70 > Bottom) StartPage();
        const double sigW = 200;
        Signature("Unterschrift Richter", PageW - Margin - sigW, sigW, y, m.ApprovedBy, m.ApprovedAtLocal);

        if (extra > 0) Annex(m.Liegezimmer.Skip(FormCells).ToList());
        Finish();
    }

    protected override void PageHeader(int pageNo)
    {
        if (part == Part.Annex)
        {
            Text(pageNo == annexFirstPage ? "Separate Aufstellung – Liegezimmer für Folgetag" : "Separate Aufstellung – Liegezimmer für Folgetag (Fortsetzung)",
                Title, Margin, 34);
            Text($"Anlage zur Übergabe Etage – Empfang · {Hotels.PrintedNames[m.Hotel]} · {Date}", Body, Margin, 54, Muted);
            y = 80;
            return;
        }
        if (pageNo == 1)
        {
            TextCentre("Richter Premium Hotelservice - Übergabe Etage - Empfang", Title, PageW / 2, 34);
            TextCentre(InHouse, Body, PageW / 2, 53, Muted);

            const double rowTop = 80;
            Text("Datum:", BodyBold, Margin, rowTop);
            Underlined(Date, Body, Margin + 40, Margin + 160, rowTop);
            var nameX = Margin + 210;
            Text("Name MA Richter:", BodyBold, nameX, rowTop);
            Underlined(m.RichterStaffName ?? "", Body, nameX + gfx.MeasureString("Name MA Richter:", BodyBold).Width + 6, PageW - Margin, rowTop);
            y = 110;
            return;
        }
        Text($"Übergabe Etage – Empfang · {InHouse} · {Date} (Fortsetzung)", BodyBold, Margin, 34, Muted);
        y = 56;
    }

    private void NewTablePage()
    {
        Frame();
        StartPage();
        frameTop = y;
    }

    /// <summary>Outer border of this page's table part.</summary>
    private void Frame()
    {
        if (y > frameTop) Box(new XRect(Margin, frameTop, ContentW, y - frameTop), 1.4);
    }

    private void TitleRow(string title)
    {
        if (y + TitleH + 2 * MinCellH > Bottom) NewTablePage(); // never leave a title alone at the bottom
        var r = new XRect(Margin, y, ContentW, TitleH);
        Fill(r, Shade);
        Box(r, 0.6);
        CellText(title, RowTitle, r);
        y += TitleH;
    }

    /// <summary>Label cell (40 %) left, 5-column room-number grid right.</summary>
    private void Section(string label, string[] notes, IReadOnlyList<string> rooms, int minRows, string? flag = null)
    {
        var labelW = Math.Round(ContentW * 0.4);
        var colW = (ContentW - labelW) / Cols;

        var lines = new List<(string Text, XFont Font, XColor Colour, double H)>();
        foreach (var l in Wrap(label, LabelBold, labelW - 12)) lines.Add((l, LabelBold, Ink, 11));
        foreach (var n in notes)
            foreach (var l in Wrap(n, Note, labelW - 12)) lines.Add((l, Note, Muted, 9));
        if (flag is not null) lines.Add((flag, LabelBold, Ink, 12));
        var labelH = 12 + lines.Sum(l => l.H);

        var rows = Math.Max(minRows, (rooms.Count + Cols - 1) / Cols);
        var cellH = Math.Max(MinCellH, labelH / rows);

        var row = 0;
        while (row < rows)
        {
            var fit = (int)Math.Floor((Bottom - y) / cellH);
            // The first part must hold the whole label; later parts at least two rows.
            var needed = row == 0 ? Math.Min(rows, Math.Max(2, (int)Math.Ceiling(labelH / cellH))) : Math.Min(rows - row, 2);
            if (fit < needed) { NewTablePage(); continue; }

            var take = Math.Min(fit, rows - row);
            var top = y;
            var labelCell = new XRect(Margin, top, labelW, take * cellH);
            Box(labelCell, 0.6);
            if (row == 0)
            {
                var ly = top + 6;
                foreach (var l in lines)
                {
                    if (ReferenceEquals(l.Text, flag)) ly += 2;
                    Text(l.Text, l.Font, Margin + 6, ly, l.Colour);
                    ly += l.H;
                }
            }
            else Text("(Fortsetzung)", Note, Margin + 6, top + 6, Muted);

            for (var r = 0; r < take; r++)
            {
                for (var c = 0; c < Cols; c++)
                {
                    var cell = new XRect(Margin + labelW + c * colW, top + r * cellH, colW, cellH);
                    gfx.DrawRectangle(new XPen(Ink, 0.4), cell);
                    var i = (row + r) * Cols + c;
                    if (i < rooms.Count) CellText(rooms[i], Room, cell);
                }
            }
            y = top + take * cellH;
            row += take;
        }
    }

    private void InformationBox()
    {
        const double lineH = 12.5;
        var lines = Wrap(m.Informationen ?? "", Body, ContentW - 16);
        var total = Math.Max(3, lines.Count);
        var done = 0;
        while (done < total)
        {
            var fit = (int)Math.Floor((Bottom - y - 10) / lineH);
            if (fit < Math.Min(total - done, 2)) { NewTablePage(); continue; }
            var take = Math.Min(fit, total - done);
            var h = take * lineH + 10;
            Box(new XRect(Margin, y, ContentW, h), 0.6);
            for (var i = 0; i < take && done + i < lines.Count; i++)
                Text(lines[done + i], Body, Margin + 8, y + 6 + i * lineH);
            y += h;
            done += take;
        }
    }

    /// <summary>"Separate Aufstellung": Liegezimmer beyond the form's 30.</summary>
    private void Annex(IReadOnlyList<string> rooms)
    {
        part = Part.Annex;
        annexFirstPage = doc.PageCount + 1;
        StartPage();
        Text($"{rooms.Count} weitere Liegezimmer für Folgetag (die ersten {FormCells} stehen auf der Übergabe, Seite 1)", Body, Margin, y);
        y += 20;

        const double cellH = 20;
        var colW = ContentW / Cols;
        var rows = (rooms.Count + Cols - 1) / Cols;
        var row = 0;
        while (row < rows)
        {
            var fit = (int)Math.Floor((Bottom - y) / cellH);
            if (fit < 1) { StartPage(); continue; }
            var take = Math.Min(fit, rows - row);
            var top = y;
            for (var r = 0; r < take; r++)
            {
                for (var c = 0; c < Cols; c++)
                {
                    var cell = new XRect(Margin + c * colW, top + r * cellH, colW, cellH);
                    gfx.DrawRectangle(new XPen(Ink, 0.4), cell);
                    var i = (row + r) * Cols + c;
                    if (i < rooms.Count) CellText(rooms[i], Room, cell);
                }
            }
            y = top + take * cellH;
            Box(new XRect(Margin, top, ContentW, y - top), 1.4);
            row += take;
        }
    }
}

/// <summary>"Leistungsnachweis": one landscape page per month, both hotels.</summary>
internal sealed class MonthlyStatementWriter(PdfDocument doc, MonthlyStatementModel m) : FormWriter(doc, landscape: true, margin: 28)
{
    private static readonly XFont Title = Font(20, true), Head = Font(6.8, true), Cell = Font(7.6), CellBold = Font(7.6, true);

    private static readonly string[] Heads =
    [
        "Tag", "Novotel Zimmer", "Gechecke Zimmer", "zu Berechnen", "Abzüge Novotel", "Ibis Zimmer", "Gechecke Zimmer", "zu Berechnen",
        "Abzüge Ibis", "Hausmann", "Floor Supervisor", "Hausdame Assistenz", "Hausdame", "Extra Reinigung", "Kommentare",
    ];
    private static readonly XColor NovotelFill = Hex("#E06666"), NovotelText = Hex("#2B0A0A"), IbisFill = Hex("#3D85C6");
    private const double HeadH = 26, RowH = 12.6;

    private double[] widths = [];

    protected override string FooterLeft =>
        $"HSK Automation · erstellt {m.GeneratedAtLocal.ToString("dd.MM.yyyy HH:mm", German)} · " +
        $"{m.ConfirmedDays} von {DateTime.DaysInMonth(m.Month.Year, m.Month.Month)} Tagen freigegeben";

    public void Write()
    {
        // Tag narrow, Kommentare ~22 %, 13 figure columns share the rest.
        const double tagW = 38;
        var commentW = Math.Round(ContentW * 0.22);
        var figureW = (ContentW - tagW - commentW) / 13;
        widths = [tagW, .. Enumerable.Repeat(figureW, 13), commentW];

        StartPage();
        Table();
        Finish();
    }

    protected override void PageHeader(int pageNo)
    {
        Text("Leistungsnachweis", Title, Margin, 24);
        (string Label, string Value)[] fields = [("Objekt", m.Objekt), ("Kostenstelle", m.Kostenstelle), ("Zeitraum", m.Zeitraum)];
        for (var i = 0; i < fields.Length; i++)
        {
            var ry = 56 + i * 13;
            Text(fields[i].Label, Body, Margin, ry, Muted);
            Text(fields[i].Value, BodyBold, Margin + 72, ry);
        }

        Wordmark(430, 26);

        const double sigW = 190;
        Signature("Unterschrift RPH", PageW - Margin - sigW, sigW, 24, m.ApprovedBy, m.ApprovedAtLocal);
        y = 104;
    }

    /// <summary>The HSK logo, set as text.</summary>
    private void Wordmark(double x, double top)
    {
        XFont hsk = Font(7.5, true), thin = Font(19), bold = Font(19, true), spaced = Font(7.5);
        Text("HSK", hsk, x, top, Ink);
        var richterW = gfx.MeasureString("Richter ", thin).Width;
        Text("Richter", thin, x, top + 9, Muted);
        Text("Premium", bold, x + richterW, top + 9, Ink);
        var lineW = richterW + gfx.MeasureString("Premium", bold).Width;

        // Letters spread to the width of the line above.
        const string word = "HOTELSERVICE";
        var letters = word.Select(ch => gfx.MeasureString(ch.ToString(), spaced).Width).ToArray();
        var gap = (lineW - letters.Sum()) / (word.Length - 1);
        var lx = x;
        for (var i = 0; i < word.Length; i++)
        {
            Text(word[i].ToString(), spaced, lx, top + 35, Muted);
            lx += letters[i] + gap;
        }
    }

    private void Table()
    {
        var top = y;

        // Header: Novotel red, ibis blue, rest white; up to two lines.
        var x = Margin;
        for (var i = 0; i < Heads.Length; i++)
        {
            var cell = new XRect(x, y, widths[i], HeadH);
            var (fill, text) = i switch
            {
                >= 1 and <= 4 => ((XColor?)NovotelFill, NovotelText),
                >= 5 and <= 8 => (IbisFill, XColors.White),
                _ => ((XColor?)null, Ink),
            };
            if (fill is { } f) Fill(cell, f);
            var lines = Wrap(Heads[i], Head, widths[i] - 4).Take(2).ToList();
            var ly = y + HeadH / 2 - lines.Count * 8.2 / 2 - 0.6;
            foreach (var l in lines)
            {
                TextCentre(l, Head, x + widths[i] / 2, ly, text);
                ly += 8.2;
            }
            x += widths[i];
        }
        y += HeadH;
        Line(Margin, y, Margin + ContentW, y, 0.9);

        foreach (var r in m.Rows)
        {
            if (!r.InMonth) Fill(new XRect(Margin, y, ContentW, RowH), Shade);
            DrawRow(Cells(r), Cell, comment: r.Kommentar ?? "");
            y += RowH;
            Line(Margin, y, Margin + ContentW, y, 0.35);
        }

        // "Gesamt" row with a heavier rule above it.
        Line(Margin, y, Margin + ContentW, y, 1.1);
        var t = m.Totals;
        string[] totals =
        [
            "Gesamt", N(t.Novotel.Zimmer), N(t.Novotel.Gecheckt), N(t.Novotel.ZuBerechnen), N(t.Novotel.Abzuege),
            N(t.Ibis.Zimmer), N(t.Ibis.Gecheckt), N(t.Ibis.ZuBerechnen), N(t.Ibis.Abzuege),
            Hours(t.Hausmann), Hours(t.FloorSupervisor), Hours(t.HausdameAssistenz), Hours(t.Hausdame), Hours(t.ExtraReinigung),
        ];
        DrawRow(totals, CellBold, comment: "", totalRowH: RowH + 2);
        y += RowH + 2;

        // Column lines and the outer border.
        x = Margin;
        for (var i = 0; i < widths.Length - 1; i++)
        {
            x += widths[i];
            // Heavier separators between the Novotel, ibis and staff groups.
            var group = i is 0 or 4 or 8;
            Line(x, top, x, y, group ? 0.9 : 0.35);
        }
        Box(new XRect(Margin, top, ContentW, y - top), 1.2);
    }

    private void DrawRow(string[] figures, XFont font, string comment, double totalRowH = RowH)
    {
        var x = Margin;
        for (var i = 0; i < figures.Length; i++)
        {
            CellText(figures[i], font, new XRect(x, y, widths[i], totalRowH), 0, 3);
            x += widths[i];
        }
        CellText(comment, font, new XRect(x, y, widths[^1], totalRowH), -1, 4);
    }

    private static string[] Cells(MonthlyRowModel r)
    {
        static string[] Hotel(MonthlyHotelCells? c) =>
            c is null ? ["", "", "", ""] : [N(c.Zimmer), N(c.Gecheckt), N(c.ZuBerechnen), N(c.Abzuege)];
        static string H(decimal? v) => v switch { null => "", 0m => "0", { } d => Hours(d) };
        return [N(r.Day), .. Hotel(r.Novotel), .. Hotel(r.Ibis), H(r.Hausmann), H(r.FloorSupervisor), H(r.HausdameAssistenz), H(r.Hausdame), H(r.ExtraReinigung)];
    }
}
