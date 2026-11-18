using System.Text.RegularExpressions;

namespace DrawingRegister.Core;

/// <summary>
/// Sortable rank for revision codes, following the usual EPC sequence:
/// P1 &lt; P2 &lt; A &lt; B &lt; … &lt; Z &lt; AA &lt; 0 &lt; 1 &lt; 2 (R2 is treated as 2).
/// </summary>
public static partial class RevisionOrder
{
    [GeneratedRegex(@"^P(\d{1,3})$")]
    private static partial Regex Preliminary();

    [GeneratedRegex(@"^(?:R|REV)?(\d{1,3})$")]
    private static partial Regex Numeric();

    [GeneratedRegex(@"^([A-Z]{1,2})(\d{0,2})$")]
    private static partial Regex Letters();

    public static int Sequence(string? revision)
    {
        if (string.IsNullOrWhiteSpace(revision))
        {
            return 0;
        }

        var rev = revision.Trim().ToUpperInvariant();
        var m = Preliminary().Match(rev);
        if (m.Success)
        {
            return 10_000 + int.Parse(m.Groups[1].Value);
        }

        m = Numeric().Match(rev);
        if (m.Success)
        {
            return 30_000 + int.Parse(m.Groups[1].Value);
        }

        m = Letters().Match(rev);
        if (m.Success)
        {
            var index = 0;
            foreach (var ch in m.Groups[1].Value)
            {
                index = (index * 26) + (ch - 'A' + 1);
            }

            var suffix = m.Groups[2].Value.Length > 0 ? int.Parse(m.Groups[2].Value) : 0;
            return 20_000 + (index * 100) + suffix;
        }

        return 1;
    }
}
