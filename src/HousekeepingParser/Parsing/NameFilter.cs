using System.Text.RegularExpressions;

namespace HousekeepingParser.Parsing;

/// <summary>
/// Removes staff names and staff numbers from the free-text instruction before anything is saved or
/// logged (rule 3, plan 9.3). Runs in memory.
/// </summary>
public static partial class NameFilter
{
    public const string NoInstructions = "Keine TA.";

    public static string CleanInstructions(string raw, IReadOnlySet<string> knownNames)
    {
        var text = raw.Trim();
        if (text.Length == 0) return NoInstructions;

        // July sheets: "<name> // <instruction> //" — keep only the text between the slashes.
        var slashed = Slashed().Match(text);
        if (slashed.Success) text = slashed.Groups["text"].Value.Trim();

        // Staff numbers such as MA10, and any word that appeared on an attendant line of this file.
        text = StaffNumber().Replace(text, "");
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !knownNames.Contains(w.Trim('.', ',', ';', ':', '/', '-')));
        text = string.Join(' ', words).Trim(' ', '/', '-');

        return text.Length == 0 || text.Equals(NoInstructions, StringComparison.OrdinalIgnoreCase) ? NoInstructions : text;
    }

    [GeneratedRegex(@"//\s*(?<text>.*?)\s*//")]
    private static partial Regex Slashed();

    [GeneratedRegex(@"\bMA\s?\d+\b")]
    private static partial Regex StaffNumber();
}
