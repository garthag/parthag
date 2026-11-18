using System.Text.RegularExpressions;

namespace DrawingRegister.Core;

/// <summary>What could be inferred about a drawing from its file name and folder.</summary>
public sealed record ParsedName(
    string DrawingNumber,
    string? Revision,
    string? Discipline,
    string? Area,
    string? DrawingType,
    string? FolderPath);

/// <summary>
/// Infers drawing number, revision, discipline, area and type from names such as
/// <c>NRP-ST-A01-GA-0001_RevB.pdf</c> and folder paths such as <c>02_Piping/Area 03/Isometric</c>.
/// </summary>
public static partial class FileNameParser
{
    private static readonly Dictionary<string, string> DisciplineAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CIV"] = "Civil", ["CV"] = "Civil", ["CIVIL"] = "Civil", ["C"] = "Civil",
        ["STR"] = "Structural", ["ST"] = "Structural", ["STRUCTURAL"] = "Structural", ["STRUCT"] = "Structural", ["S"] = "Structural",
        ["MEC"] = "Mechanical", ["ME"] = "Mechanical", ["MECH"] = "Mechanical", ["MECHANICAL"] = "Mechanical", ["M"] = "Mechanical",
        ["PIP"] = "Piping", ["PI"] = "Piping", ["PIPING"] = "Piping", ["PIPE"] = "Piping", ["P"] = "Piping",
        ["ELE"] = "Electrical", ["EL"] = "Electrical", ["ELEC"] = "Electrical", ["ELECTRICAL"] = "Electrical", ["E"] = "Electrical",
        ["INS"] = "Instrumentation", ["IN"] = "Instrumentation", ["INST"] = "Instrumentation",
        ["INSTRUMENTATION"] = "Instrumentation", ["I"] = "Instrumentation",
        ["ARC"] = "Architectural", ["AR"] = "Architectural", ["ARCH"] = "Architectural", ["ARCHITECTURAL"] = "Architectural",
        ["A"] = "Architectural",
        ["PRO"] = "Process", ["PR"] = "Process", ["PROC"] = "Process", ["PROCESS"] = "Process",
        ["HVAC"] = "HVAC", ["HV"] = "HVAC",
    };

    private static readonly Dictionary<string, string> DrawingTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["GA"] = "General Arrangement", ["PID"] = "P&ID", ["ISO"] = "Isometric", ["LAY"] = "Layout",
        ["SLD"] = "Single Line Diagram", ["DET"] = "Detail", ["FDN"] = "Foundation", ["PLN"] = "Plan",
        ["SEC"] = "Section", ["LOOP"] = "Loop Diagram", ["DS"] = "Datasheet", ["SCH"] = "Schematic",
        ["CBL"] = "Cable Schedule", ["STD"] = "Standard", ["FAB"] = "Fabrication", ["ERE"] = "Erection",
    };

    public static IReadOnlyCollection<string> Disciplines { get; } =
        DisciplineAliases.Values.Distinct().Order().ToArray();

    [GeneratedRegex(@"^(.*?)[\s_\-.]*[(\[]?(?:REV|R)[\s_.\-]?([A-Z0-9]{1,3})[)\]]?$", RegexOptions.IgnoreCase)]
    private static partial Regex RevisionSuffix();

    [GeneratedRegex(@"^(?:A|AREA|U|UNIT)[-_ ]?(\d{1,3}[A-Z]?)$", RegexOptions.IgnoreCase)]
    private static partial Regex AreaToken();

    [GeneratedRegex(@"^\d+[\s_.-]*")]
    private static partial Regex LeadingNumber();

    [GeneratedRegex(@"[-_ ]+")]
    private static partial Regex Separators();

    /// <summary>"ABC-ST-0001_RevB.pdf" -> ("ABC-ST-0001", "B").</summary>
    public static (string Number, string? Revision) SplitFileName(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName).Trim();
        var match = RevisionSuffix().Match(stem);
        if (match.Success && match.Groups[1].Value.Trim(' ', '_', '-', '.').Length >= 3)
        {
            return (match.Groups[1].Value.Trim(' ', '_', '-', '.').ToUpperInvariant(), match.Groups[2].Value.ToUpperInvariant());
        }

        return (stem.ToUpperInvariant(), null);
    }

    public static ParsedName Parse(string fileName, string? relativePath = null)
    {
        var (number, revision) = SplitFileName(fileName);
        string? discipline = null, area = null, type = null;
        var parts = Separators().Split(number).Where(p => p.Length > 0).ToArray();
        for (var i = 0; i < parts.Length; i++)
        {
            var token = parts[i];
            // a single letter is too ambiguous as the first token (often a company / plant prefix)
            if (discipline is null && i > 0 && DisciplineAliases.TryGetValue(token, out var d))
            {
                discipline = d;
            }
            else if (area is null && AreaToken().IsMatch(token))
            {
                area = token.ToUpperInvariant();
            }
            else if (type is null && DrawingTypes.ContainsKey(token))
            {
                type = token.ToUpperInvariant();
            }
        }

        string? folder = null;
        if (!string.IsNullOrWhiteSpace(relativePath))
        {
            var folders = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).SkipLast(1).ToArray();
            folder = folders.Length > 0 ? string.Join('/', folders) : null;
            foreach (var raw in folders)
            {
                var clean = LeadingNumber().Replace(raw, "").Trim();
                if (discipline is null && clean.Length > 1 && DisciplineAliases.TryGetValue(clean, out var fd))
                {
                    discipline = fd;
                }

                var compact = clean.Replace(" ", "");
                if (area is null && AreaToken().IsMatch(compact))
                {
                    area = compact.ToUpperInvariant();
                }

                if (type is null)
                {
                    foreach (var (key, name) in DrawingTypes)
                    {
                        if (clean.Equals(key, StringComparison.OrdinalIgnoreCase) || clean.Equals(name, StringComparison.OrdinalIgnoreCase))
                        {
                            type = key;
                            break;
                        }
                    }
                }
            }
        }

        return new ParsedName(number, revision, discipline, area, type, folder);
    }
}
