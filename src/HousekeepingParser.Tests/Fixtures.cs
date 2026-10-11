using System.Text.Json;
using HousekeepingParser.Parsing;

namespace HousekeepingParser.Tests;

/// <summary>
/// The reader's JSON output for the four sample task sheets, with staff names and staff numbers replaced
/// by fake values (plan 5.3), so the tests need neither Java nor the real PDFs.
/// </summary>
public static class Fixtures
{
    public const string Novotel0710 = "novotel-2026-07-10.json";
    public const string Novotel0828 = "novotel-2026-08-28.json";
    public const string Novotel0830 = "novotel-2026-08-30.json";
    public const string Ibis0830 = "ibis-2026-08-30.json";

    public static readonly string[] All = [Novotel0710, Novotel0828, Novotel0830, Ibis0830];

    /// <summary>The fake names put into the fixtures in place of the real ones.</summary>
    public static readonly string[] FakeNames =
        ["Alex T.", "Sam R.", "Kim W.", "Robin d L.", "Jo E.", "Mika H.", "Toni Q.", "Chris V.", "Pat Y.", "Lee Z.", "Dana X.", "Noa dos U.", "Sasha O."];

    public static IReadOnlyList<PdfPage> Pages(string name)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)));
        return OdlLayoutMapper.Map(doc.RootElement);
    }

    public static TaskSheetReport Report(string name) => TaskSheetParser.Parse(Pages(name));
}
