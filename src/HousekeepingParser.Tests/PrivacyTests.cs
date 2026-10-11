using System.Text.Json;
using System.Text.RegularExpressions;
using HousekeepingParser.Parsing;
using Xunit;

namespace HousekeepingParser.Tests;

/// <summary>Rule 3 / plan 5.4 leak test: no staff name and no staff number survives parsing.</summary>
public partial class PrivacyTests
{
    [Theory]
    [InlineData(Fixtures.Novotel0710)]
    [InlineData(Fixtures.Novotel0828)]
    [InlineData(Fixtures.Novotel0830)]
    [InlineData(Fixtures.Ibis0830)]
    public void Parsed_result_contains_no_staff_names_or_numbers(string file)
    {
        var raw = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", file));
        var namesInFile = Fixtures.FakeNames.Where(raw.Contains).ToList();
        Assert.NotEmpty(namesInFile); // the fixture really contains names to filter

        var everything = JsonSerializer.Serialize(Fixtures.Report(file));

        foreach (var name in namesInFile) Assert.DoesNotContain(name, everything);
        Assert.DoesNotMatch(StaffNumber(), everything);
        Assert.DoesNotContain("Mitarbeitender", everything);
    }

    [Fact]
    public void July_instruction_keeps_only_the_text_between_the_slashes()
    {
        var r = Fixtures.Report(Fixtures.Novotel0710);

        Assert.Equal("Por favor, limpie todas las puertas por todos sus lados y limpie los desagües.", r.Sheets[0].Instructions);
        Assert.Equal("Please clean all the doors from all the sides and clean the drains", r.Sheets[1].Instructions);
    }

    [Fact]
    public void Empty_instruction_is_stored_as_keine_ta()
    {
        var r = Fixtures.Report(Fixtures.Ibis0830);

        Assert.Equal("Keine TA.", r.Sheets[0].Instructions);
        Assert.Equal("Cleaning Cleaning and Linen Change Inhouse (Opt In Cleaning) Clean Departures First", r.Sheets[3].Instructions);
    }

    [Fact]
    public void Name_filter_removes_known_names_and_staff_numbers()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Kim", "W" };

        Assert.Equal("please check the minibar", NameFilter.CleanInstructions("Kim W. please check the minibar MA12", names));
        Assert.Equal("Keine TA.", NameFilter.CleanInstructions("  ", names));
        Assert.Equal("Bitte Fenster putzen", NameFilter.CleanInstructions("Alex T. // Bitte Fenster putzen //", names));
    }

    [GeneratedRegex(@"\bMA\s?\d+\b")]
    private static partial Regex StaffNumber();
}
