using Camelot;
using UglyToad.PdfPig.Content;

namespace HousekeepingParser.Parsing;

public static class PdfPigLayoutMapper
{
    public static PdfPage Map(Page page, IReadOnlyList<Camelot.Core.Table>? tables = null)
    {
        var words = page.GetWords().Where(w => !string.IsNullOrWhiteSpace(w.Text))
            .Select(w => new PdfWord(w.Text.Trim(), w.BoundingBox.Left, w.BoundingBox.Right,
                w.BoundingBox.Top, w.BoundingBox.Bottom)).ToList();
        var remaining = new HashSet<PdfWord>(words);
        var lines = new List<PdfLine>();

        // Stream supplies the row bands; original word positions preserve exact room-marker coordinates.
        // Headers/footers outside those bands remain available for hotel, date and total validation.
        if (tables is not null)
        {
            foreach (var table in tables)
            foreach (var row in table.Cells)
            {
                if (row.Count == 0) continue;
                var left = row.Min(c => c.X1);
                var right = row.Max(c => c.X2);
                var bottom = row.Min(c => c.Y1);
                var top = row.Max(c => c.Y2);
                var selected = remaining.Where(w => w.CentreX >= left && w.CentreX <= right
                    && w.CentreY >= bottom && w.CentreY <= top).ToList();
                // Some Stream bands contain wrapped text; split these by baseline to avoid merging rooms.
                foreach (var group in GroupByBaseline(selected)) lines.Add(Line(group));
                remaining.ExceptWith(selected);
            }
        }
        foreach (var group in GroupByBaseline(remaining)) lines.Add(Line(group));
        return new PdfPage(page.Number, page.Width, page.Height, lines.OrderByDescending(l => l.CentreY).ThenBy(l => l.Left).ToList());
    }

    internal static IReadOnlyList<IReadOnlyList<PdfWord>> GroupByBaseline(IEnumerable<PdfWord> words)
    {
        var groups = new List<List<PdfWord>>();
        foreach (var word in words.OrderByDescending(w => w.CentreY).ThenBy(w => w.Left))
        {
            var group = groups.LastOrDefault();
            if (group is null || Math.Abs(group.Average(w => w.CentreY) - word.CentreY) > 2)
                groups.Add([word]);
            else group.Add(word);
        }
        return groups.Select(g => (IReadOnlyList<PdfWord>)g.OrderBy(w => w.Left).ToList()).ToList();
    }

    private static PdfLine Line(IReadOnlyList<PdfWord> words) => new(string.Join(' ', words.Select(w => w.Text)),
        words.Max(w => w.Top), words.Min(w => w.Bottom), words.Min(w => w.Left), words.Max(w => w.Right), words);
}
