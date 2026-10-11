using System.Text.Json;
using System.Text.RegularExpressions;

namespace HousekeepingParser.Parsing;

/// <summary>
/// Turns OpenDataLoader's element tree into the pages -> lines -> words contract (C15).
///
/// The reader groups text into blocks (paragraphs, list items) with one bounding box each; with
/// --keep-line-breaks a block's lines are separated by '\n'. Opera prints the task sheet column by
/// column, so one block is usually one column over several rows. Each block is split back into its
/// lines, spread evenly over the block's height (rows have a constant pitch), and every word gets an
/// x position in proportion to its place in the line. Words from all blocks are then grouped into
/// page lines by their vertical centre. The row parser places words into columns by x position.
/// </summary>
public static partial class OdlLayoutMapper
{
    /// <summary>Words whose vertical centres differ by less than this belong to one line (row pitch is ~12.4 pt).</summary>
    private const double SameLineTolerance = 4.0;

    public static IReadOnlyList<PdfPage> Map(JsonElement root, IReadOnlyDictionary<int, (double Width, double Height)>? pageSizes = null)
    {
        var wordsByPage = new SortedDictionary<int, List<PdfWord>>();
        foreach (var node in TextNodes(root))
        {
            var page = node.GetProperty("page number").GetInt32();
            if (!wordsByPage.TryGetValue(page, out var list)) wordsByPage[page] = list = [];
            list.AddRange(SplitBlock(node));
        }

        var pageCount = root.TryGetProperty("number of pages", out var n) && n.ValueKind == JsonValueKind.Number ? n.GetInt32() : 0;
        var pages = new List<PdfPage>();
        for (var p = 1; p <= Math.Max(pageCount, wordsByPage.Keys.DefaultIfEmpty(0).Max()); p++)
        {
            var (w, h) = pageSizes is not null && pageSizes.TryGetValue(p, out var size) ? size : (0d, 0d);
            pages.Add(new PdfPage(p, w, h, wordsByPage.TryGetValue(p, out var words) ? GroupIntoLines(words) : []));
        }
        return pages;
    }

    private static IEnumerable<JsonElement> TextNodes(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object) yield break;
        if (node.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String && c.GetString()!.Trim().Length > 0
            && node.TryGetProperty("bounding box", out var bb) && bb.ValueKind == JsonValueKind.Array
            && node.TryGetProperty("page number", out _))
        {
            yield return node;
        }
        // Children live under "kids", "list items", "rows", "cells" … depending on the element type.
        foreach (var prop in node.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Array) continue;
            foreach (var child in prop.Value.EnumerateArray())
            {
                foreach (var t in TextNodes(child)) yield return t;
            }
        }
    }

    private static IEnumerable<PdfWord> SplitBlock(JsonElement node)
    {
        var box = node.GetProperty("bounding box").EnumerateArray().Select(v => v.GetDouble()).ToArray();
        if (box.Length != 4) yield break;
        double x0 = box[0], y0 = box[1], x1 = box[2], y1 = box[3];
        var fontSize = node.TryGetProperty("font size", out var fs) && fs.ValueKind == JsonValueKind.Number ? fs.GetDouble() : 8.0;

        var lines = node.GetProperty("content").GetString()!.Replace("\r", "").Split('\n');
        var lineHeight = Math.Min(y1 - y0, fontSize * 1.375);
        var pitch = lines.Length > 1 ? (y1 - y0 - lineHeight) / (lines.Length - 1) : 0;
        var longest = Math.Max(1, lines.Max(l => l.Length));

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Trim().Length == 0) continue;
            var top = y1 - i * pitch;
            var bottom = top - lineHeight;
            var width = (x1 - x0) * line.Length / longest;
            foreach (Match m in WordPattern().Matches(line))
            {
                var left = x0 + width * m.Index / line.Length;
                var right = x0 + width * (m.Index + m.Length) / line.Length;
                yield return new PdfWord(m.Value, left, right, top, bottom);
            }
        }
    }

    private static List<PdfLine> GroupIntoLines(List<PdfWord> words)
    {
        var lines = new List<List<PdfWord>>();
        foreach (var w in words.OrderByDescending(w => (w.Top + w.Bottom) / 2).ThenBy(w => w.Left))
        {
            var centre = (w.Top + w.Bottom) / 2;
            var current = lines.Count > 0 ? lines[^1] : null;
            if (current is not null && Math.Abs(current.Average(x => (x.Top + x.Bottom) / 2) - centre) < SameLineTolerance)
                current.Add(w);
            else
                lines.Add([w]);
        }

        return lines.Select(ws =>
        {
            var sorted = ws.OrderBy(w => w.Left).ToList();
            return new PdfLine(
                string.Join(' ', sorted.Select(w => w.Text)),
                sorted.Max(w => w.Top),
                sorted.Min(w => w.Bottom),
                sorted.Min(w => w.Left),
                sorted.Max(w => w.Right),
                sorted);
        }).ToList();
    }

    [GeneratedRegex(@"\S+")]
    private static partial Regex WordPattern();
}
