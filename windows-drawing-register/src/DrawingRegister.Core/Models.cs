using System.Text.RegularExpressions;

namespace DrawingRegister.Core;

/// <summary>
/// Drawing type (engineering discipline). <see cref="All"/> is the order the
/// register is displayed and printed in.
/// </summary>
public sealed record DrawingType(string Code, string Name, string[] Aliases, string[] KindHints, string Colour)
{
    public string Label => $"{Code} - {Name}";
    public int Order => Array.IndexOf(All, this);

    public static readonly DrawingType General = new("GN", "General / Plot Plan", ["G", "GN", "GEN", "GENERAL"],
        ["PLOT PLAN", "KEY PLAN", "OVERALL", "LEGEND", "COVER", "INDEX", "SITE PLAN", "MASTER PLAN"], "#607D8B");
    public static readonly DrawingType Process = new("PR", "Process", ["PR", "PRO", "PROC", "PROCESS", "PRS"],
        ["P&ID", "PID", "PFD", "UFD", "HMB", "PROCESS FLOW", "FLOW DIAGRAM"], "#00897B");
    public static readonly DrawingType Civil = new("CV", "Civil", ["C", "CV", "CIV", "CIVIL"],
        ["GRADING", "ROAD", "DRAINAGE", "PAVING", "BOUNDARY", "CULVERT", "SEWER", "EARTHWORK", "TRENCH"], "#8D6E63");
    public static readonly DrawingType Structural = new("ST", "Structural", ["S", "ST", "STR", "STRUC", "STRUCT", "STRUCTURAL"],
        ["FOUNDATION", "FDN", "PILE", "RCC", "BBS", "REINFORCEMENT", "STEEL STRUCTURE", "PIPE RACK", "PIPERACK", "BEAM", "COLUMN", "SLAB"], "#5E35B1");
    public static readonly DrawingType Architectural = new("AR", "Architectural", ["A", "AR", "ARC", "ARCH", "ARCHITECTURAL"],
        ["FLOOR PLAN", "ELEVATION", "DOOR", "WINDOW", "FINISH", "FACADE", "TOILET"], "#D81B60");
    public static readonly DrawingType Mechanical = new("ME", "Mechanical / Equipment", ["M", "ME", "MEC", "MECH", "MECHANICAL", "EQ", "EQP", "EQUIP"],
        ["EQUIPMENT", "VESSEL", "TANK", "PUMP", "COMPRESSOR", "HEAT EXCHANGER", "NOZZLE", "DATASHEET"], "#3949AB");
    public static readonly DrawingType Piping = new("PP", "Piping", ["P", "PP", "PI", "PIP", "PIPING", "PIPE"],
        ["ISO", "ISOMETRIC", "GAD", "PIPING LAYOUT", "PIPE SUPPORT", "ROUTING", "LINE LIST"], "#1E88E5");
    public static readonly DrawingType Electrical = new("EL", "Electrical", ["E", "EL", "ELE", "ELEC", "ELECTRICAL"],
        ["SLD", "SINGLE LINE", "CABLE", "EARTHING", "LIGHTING", "LIGHTNING", "SUBSTATION", "TRANSFORMER", "MCC", "PANEL"], "#F9A825");
    public static readonly DrawingType Instrumentation = new("IN", "Instrumentation & Control", ["I", "IN", "INS", "INST", "IC", "C&I", "CI", "INSTRUMENTATION"],
        ["LOOP", "HOOK UP", "HOOKUP", "HOOK-UP", "JB", "JUNCTION BOX", "DCS", "PLC", "CONTROL", "INSTRUMENT"], "#43A047");
    public static readonly DrawingType Hvac = new("HV", "HVAC", ["H", "HV", "HVAC"],
        ["DUCT", "AHU", "VENTILATION", "AIR CONDITIONING", "CHILLER"], "#00ACC1");
    public static readonly DrawingType Fire = new("FP", "Fire Protection", ["F", "FF", "FP", "FPS", "FIRE"],
        ["HYDRANT", "SPRINKLER", "DELUGE", "FIRE ALARM", "FIRE FIGHTING", "FOAM"], "#E53935");
    public static readonly DrawingType Plumbing = new("PL", "Plumbing / Utilities", ["PL", "PH", "PLB", "PLUMB", "PLUMBING", "UT", "UTIL", "UTILITY"],
        ["PLUMBING", "WATER SUPPLY", "SANITARY", "UTILITY"], "#6D4C41");
    public static readonly DrawingType Other = new("OT", "Other / Unclassified", [], [], "#9E9E9E");

    public static readonly DrawingType[] All =
        [General, Process, Civil, Structural, Architectural, Mechanical, Piping, Electrical, Instrumentation, Hvac, Fire, Plumbing, Other];

    public static DrawingType ByCode(string? code) => All.FirstOrDefault(t => t.Code == code) ?? Other;

    public override string ToString() => Label;
}

public enum DrawingStatus { ForReview, ForApproval, ForConstruction, AsBuilt, ForInformation }

public static class DrawingStatusInfo
{
    public static string Code(this DrawingStatus s) => s switch
    {
        DrawingStatus.ForReview => "IFR",
        DrawingStatus.ForApproval => "IFA",
        DrawingStatus.ForConstruction => "IFC",
        DrawingStatus.AsBuilt => "AB",
        _ => "FI",
    };

    public static string Label(this DrawingStatus s) => s switch
    {
        DrawingStatus.ForReview => "Issued for Review",
        DrawingStatus.ForApproval => "Issued for Approval",
        DrawingStatus.ForConstruction => "Issued for Construction",
        DrawingStatus.AsBuilt => "As-Built",
        _ => "For Information",
    };

    public static DrawingStatus FromCode(string? code) =>
        Enum.GetValues<DrawingStatus>().FirstOrDefault(s => s.Code() == code, DrawingStatus.ForConstruction);
}

public enum DrawingFormat { Pdf, Image, Cad, Other }

/// <summary>One revision of one drawing file.</summary>
public sealed class Drawing
{
    public int Id { get; set; }
    public string DrawingNo { get; set; } = "";
    public string Title { get; set; } = "";
    public string Revision { get; set; } = "0";

    /// <summary>Sheet of a multi-sheet drawing, e.g. "2/6". Empty for single-sheet drawings.</summary>
    public string Sheet { get; set; } = "";
    public string Plant { get; set; } = "";
    public string Unit { get; set; } = "";
    public string TypeCode { get; set; } = DrawingType.Other.Code;
    public string Kind { get; set; } = "";
    public DrawingStatus Status { get; set; } = DrawingStatus.ForConstruction;
    public string FileName { get; set; } = "";

    /// <summary>Path of the stored file relative to the library folder.</summary>
    public string StoredPath { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string Remarks { get; set; } = "";

    /// <summary>Contractor / agency responsible, when known (e.g. from the contractor workbook).</summary>
    public string Agency { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTime ImportedOn { get; set; } = DateTime.Now;
    public int? BatchId { get; set; }

    public DrawingType Type => DrawingType.ByCode(TypeCode);
    public DrawingFormat Format => FormatOf(FileName.Length > 0 ? FileName : StoredPath);
    public int RevRank => Revisions.Rank(Revision);

    /// <summary>Revisions of the same drawing sheet share this key.</summary>
    public string RegisterKey =>
        $"{Plant.Trim().ToUpperInvariant()}|{Unit.Trim().ToUpperInvariant()}|{DrawingNo.Trim().ToUpperInvariant()}|{Sheet.Trim()}";

    /// <summary>True when the register row is known (e.g. from a BOM) but its file has not been imported yet.</summary>
    public bool FileMissing => StoredPath.Length == 0;

    /// <summary>Drawing number with only letters and digits, used to match files to register rows
    /// ("ERC-MCK-BSL-STH-F-001" matches "ERC/MCK/BSL/STH/F-001").</summary>
    public static string NormaliseNumber(string number) =>
        new(number.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());

    public string NumberKey => NormaliseNumber(DrawingNo);

    /// <summary>"16193442  Sh 2/6".</summary>
    public string NumberWithSheet => Sheet.Length == 0 ? DrawingNo : $"{DrawingNo}  Sh {Sheet}";

    /// <summary>Same drawing, same revision, same file type: treated as a duplicate on import.</summary>
    public string DuplicateKey => $"{RegisterKey}|{Revision.ToUpperInvariant()}|{Path.GetExtension(FileName).ToLowerInvariant()}";

    public string DisplayTitle => Title.Length > 0 ? Title : Kind.Length > 0 ? $"{Kind} - {Type.Name}" : Type.Name;

    public static DrawingFormat FormatOf(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".pdf" => DrawingFormat.Pdf,
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".tif" or ".tiff" => DrawingFormat.Image,
        ".dwg" or ".dxf" or ".dgn" or ".dwf" or ".dwfx" => DrawingFormat.Cad,
        _ => DrawingFormat.Other,
    };

    public Drawing Clone() => (Drawing)MemberwiseClone();
}

public static class Revisions
{
    /// <summary>
    /// Sort rank of a revision. Lettered revisions (A, B ... for review /
    /// approval) come before numbered ones (0, 1, 2 ... for construction).
    /// </summary>
    public static int Rank(string rev)
    {
        var r = rev.Trim().ToUpperInvariant();
        if (r.Length == 0) return -1;
        if (int.TryParse(r, out var n)) return 1000 + n;
        var v = 0;
        foreach (var c in r)
        {
            if (c < 'A' || c > 'Z') return 0;
            v = v * 26 + (c - 'A' + 1);
        }
        return Math.Min(v, 999);
    }

    public static int Compare(string a, string b)
    {
        var c = Rank(a).CompareTo(Rank(b));
        return c != 0 ? c : string.CompareOrdinal(a, b);
    }

    /// <summary>"R2" for numbered revisions, "Rev A" for lettered ones.</summary>
    public static string Label(string rev) => int.TryParse(rev, out _) ? $"R{rev}" : $"Rev {rev}";
}

/// <summary>Natural string order: "U-20" before "U-100", "0002" before "0010".</summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();
    static readonly Regex Parts = new(@"\d+|\D+", RegexOptions.Compiled);

    public int Compare(string? a, string? b)
    {
        var ma = Parts.Matches((a ?? "").ToUpperInvariant());
        var mb = Parts.Matches((b ?? "").ToUpperInvariant());
        for (var i = 0; i < ma.Count && i < mb.Count; i++)
        {
            var x = ma[i].Value;
            var y = mb[i].Value;
            int c;
            if (char.IsDigit(x[0]) && char.IsDigit(y[0]))
            {
                var tx = x.TrimStart('0');
                var ty = y.TrimStart('0');
                c = tx.Length != ty.Length ? tx.Length.CompareTo(ty.Length) : string.CompareOrdinal(tx, ty);
            }
            else c = string.CompareOrdinal(x, y);
            if (c != 0) return c;
        }
        return ma.Count.CompareTo(mb.Count);
    }

    /// <summary>Blank values last (e.g. drawings with no unit).</summary>
    public static int BlankLast(string a, string b) =>
        a.Length == 0 != (b.Length == 0) ? (a.Length == 0 ? 1 : -1) : Instance.Compare(a, b);
}

/// <summary>One drawing with all its revisions, newest first.</summary>
public sealed class DrawingEntry(List<Drawing> revisions)
{
    public List<Drawing> Revisions { get; } = revisions;
    public Drawing Current => Revisions[0];
    public IEnumerable<Drawing> Superseded => Revisions.Skip(1);
}

public sealed record TypeGroup(DrawingType Type, List<DrawingEntry> Drawings);
public sealed record UnitGroup(string Unit, List<TypeGroup> Types)
{
    public int Count => Types.Sum(t => t.Drawings.Count);
}
public sealed record PlantGroup(string Plant, List<UnitGroup> Units)
{
    public int Count => Units.Sum(u => u.Count);
}

public static class Register
{
    public static string PlantLabel(string plant) => plant.Length == 0 ? "Plant not specified" : plant;
    public static string UnitLabel(string unit) => unit.Length == 0 ? "General / All units" : unit;

    /// <summary>Groups drawings Plant → Unit → Type → Drawing no., newest revision first.</summary>
    public static List<PlantGroup> Organise(IEnumerable<Drawing> drawings)
    {
        var entries = drawings
            .GroupBy(d => d.RegisterKey)
            .Select(g => new DrawingEntry(g
                .OrderByDescending(d => d.Revision, Comparer<string>.Create(Revisions.Compare))
                .ThenByDescending(d => d.ImportedOn)
                .ToList()))
            .ToList();

        return entries
            .GroupBy(e => e.Current.Plant)
            .OrderBy(g => g.Key, Comparer<string>.Create(NaturalComparer.BlankLast))
            .Select(pg => new PlantGroup(pg.Key, pg
                .GroupBy(e => e.Current.Unit)
                .OrderBy(g => g.Key, Comparer<string>.Create(NaturalComparer.BlankLast))
                .Select(ug => new UnitGroup(ug.Key, ug
                    .GroupBy(e => e.Current.Type)
                    .OrderBy(g => g.Key.Order)
                    .Select(tg => new TypeGroup(tg.Key, tg
                        .OrderBy(e => e.Current.DrawingNo, NaturalComparer.Instance)
                        .ToList()))
                    .ToList()))
                .ToList()))
            .ToList();
    }

    /// <summary>Current revisions in register order, flattened.</summary>
    public static IEnumerable<(PlantGroup Plant, UnitGroup Unit, TypeGroup Type, DrawingEntry Entry)> Flatten(List<PlantGroup> groups) =>
        from p in groups from u in p.Units from t in u.Types from e in t.Drawings select (p, u, t, e);
}
