using DrawingRegister.Core;
using Xunit;

namespace DrawingRegister.Tests;

public class ParserTests
{
    [Fact]
    public void PlantNumberingWithAreaCodeDisciplineAndRevision()
    {
        var d = DrawingParser.Parse("RF-100-ST-GA-0001_R2.pdf", "Refinery");
        Assert.Equal("RF-100-ST-GA-0001", d.DrawingNo);
        Assert.Equal("2", d.Revision);
        Assert.Equal("Unit 100", d.Unit);
        Assert.Equal(DrawingType.Structural, d.Type);
        Assert.Equal("General Arrangement", d.Kind);
        Assert.Equal("Refinery", d.Plant);
        Assert.Equal(DrawingStatus.ForConstruction, d.Status);
    }

    [Fact]
    public void PlantAndUnitFromFoldersLetteredRevision()
    {
        var d = DrawingParser.Parse("Plant-2/Unit 300/Piping/ISO-300-1023 Rev B.pdf");
        Assert.Equal("Plant 2", d.Plant);
        Assert.Equal("Unit 300", d.Unit);
        Assert.Equal(DrawingType.Piping, d.Type);
        Assert.Equal("B", d.Revision);
        Assert.Equal("ISO-300-1023", d.DrawingNo);
        Assert.Equal(DrawingStatus.ForApproval, d.Status);
    }

    [Fact]
    public void UPrefixedUnitStatusWordAndTitle()
    {
        var d = DrawingParser.Parse("U200-EL-SLD-001-R0 Main SLD (IFC).pdf");
        Assert.Equal("Unit 200", d.Unit);
        Assert.Equal(DrawingType.Electrical, d.Type);
        Assert.Equal("0", d.Revision);
        Assert.Equal(DrawingStatus.ForConstruction, d.Status);
        Assert.Equal("U200-EL-SLD-001", d.DrawingNo);
        Assert.Equal("Main SLD", d.Title);
        Assert.Equal("Single Line Diagram", d.Kind);
    }

    [Fact]
    public void TypeFromNumberedFolder()
    {
        var d = DrawingParser.Parse(@"02_Structural\Foundation Plan.dwg");
        Assert.Equal(DrawingType.Structural, d.Type);
        Assert.Equal("Foundation Plan", d.DrawingNo);
        Assert.Equal("0", d.Revision);
    }

    [Fact]
    public void TypeFromKeywords()
    {
        var d = DrawingParser.Parse("CDU-PID-0004 Rev 3 Crude Charge P&ID.pdf");
        Assert.Equal(DrawingType.Process, d.Type);
        Assert.Equal("3", d.Revision);
    }

    [Fact]
    public void PlainFolderNamesBecomePlantAndUnit()
    {
        var d = DrawingParser.Parse("Refinery/CDU/Piping/ISO-1023_R1.pdf");
        Assert.Equal("Refinery", d.Plant);
        Assert.Equal("CDU", d.Unit);
        Assert.Equal(DrawingType.Piping, d.Type);
        var e = DrawingParser.Parse("External Works/SR-EX-CV-DRN-001_R0.pdf", "Site");
        Assert.Equal("Site", e.Plant);
        Assert.Equal("External Works", e.Unit);
        Assert.Equal(DrawingType.Civil, e.Type);
    }

    [Fact]
    public void UnclassifiedFallsIntoOther() => Assert.Equal(DrawingType.Other, DrawingParser.Parse("scan001.pdf").Type);

    [Theory]
    [InlineData("__MACOSX/._a.pdf", false)]
    [InlineData("pkg/Thumbs.db", false)]
    [InlineData("pkg/~$sheet.xlsx", false)]
    [InlineData("pkg/A-100-ST-001.dwl", false)]
    [InlineData("pkg/A-100-ST-001.pdf", true)]
    public void JunkFilesAreIgnored(string path, bool keep) => Assert.Equal(keep, DrawingParser.IsDrawingFile(path));

    [Fact]
    public void RevisionOrderLettersBeforeNumbers()
    {
        var revs = new List<string> { "1", "A", "0", "C", "10", "2" };
        revs.Sort(Revisions.Compare);
        Assert.Equal(["A", "C", "0", "1", "2", "10"], revs);
    }

    [Fact]
    public void NaturalOrder()
    {
        var list = new List<string> { "X-10", "X-2", "X-1", "U-100", "U-20" };
        list.Sort(NaturalComparer.Instance);
        Assert.Equal(["U-20", "U-100", "X-1", "X-2", "X-10"], list);
    }

    [Fact]
    public void OrganiseGroupsByPlantUnitTypeOrderAndNumber()
    {
        Drawing D(string no, string rev, string unit, string type) =>
            new() { DrawingNo = no, Revision = rev, Plant = "P1", Unit = unit, TypeCode = type, FileName = no + ".pdf" };
        var groups = Register.Organise([
            D("X-10", "0", "Unit 200", "EL"), D("X-2", "0", "Unit 100", "PP"), D("X-1", "1", "Unit 100", "ST"),
            D("X-1", "0", "Unit 100", "ST"), D("X-10", "0", "Unit 100", "ST"), D("X-3", "A", "Unit 100", "CV"),
        ]);
        var p = Assert.Single(groups);
        Assert.Equal(["Unit 100", "Unit 200"], p.Units.Select(u => u.Unit));
        var u100 = p.Units[0];
        Assert.Equal(["CV", "ST", "PP"], u100.Types.Select(t => t.Type.Code));
        var st = u100.Types[1].Drawings;
        Assert.Equal(["X-1", "X-10"], st.Select(e => e.Current.DrawingNo));
        Assert.Equal("1", st[0].Current.Revision);
        Assert.Equal("0", Assert.Single(st[0].Superseded).Revision);
    }
}
