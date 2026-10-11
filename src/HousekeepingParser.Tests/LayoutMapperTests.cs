using System.Text.Json;
using HousekeepingParser.Parsing;
using Xunit;

namespace HousekeepingParser.Tests;

public class LayoutMapperTests
{
    [Fact]
    public void Column_blocks_are_split_into_rows_and_joined_by_height()
    {
        // Two column blocks of three rows each, as the reader returns them for a task sheet.
        const string json = """
            { "number of pages": 1, "kids": [
              { "type": "paragraph", "page number": 1, "bounding box": [15, 536, 28, 571], "font size": 8.0, "content": "103\n106\n108" },
              { "type": "list", "kids": [
                { "type": "list item", "page number": 1, "bounding box": [40, 536, 50, 571], "font size": 8.0, "content": "DI\nDI\nCL" } ] }
            ] }
            """;
        using var doc = JsonDocument.Parse(json);

        var page = OdlLayoutMapper.Map(doc.RootElement).Single();

        Assert.Equal(["103 DI", "106 DI", "108 CL"], page.Lines.Select(l => l.Text));
        Assert.All(page.Lines, l => Assert.Equal(2, l.Words.Count));
        Assert.True(page.Lines[0].CentreY > page.Lines[1].CentreY);
    }

    [Fact]
    public void Words_inside_a_line_get_increasing_x_positions()
    {
        const string json = """
            { "number of pages": 1, "kids": [
              { "type": "paragraph", "page number": 1, "bounding box": [8, 207, 125, 218], "font size": 8.0, "content": "Departure Rooms 14" } ] }
            """;
        using var doc = JsonDocument.Parse(json);

        var words = OdlLayoutMapper.Map(doc.RootElement).Single().Lines.Single().Words;

        Assert.Equal(["Departure", "Rooms", "14"], words.Select(w => w.Text));
        Assert.True(words[0].Left < words[1].Left && words[1].Left < words[2].Left);
        Assert.Equal(8, words[0].Left, 3);
    }
}
