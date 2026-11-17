using System.IO.Compression;
using ClosedXML.Excel;
using DrawingRegister.Core;
using Xunit;

namespace DrawingRegister.Tests;

public sealed class ExcelListTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "dr-xl-" + Guid.NewGuid().ToString("N"));

    public ExcelListTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    [Theory]
    [InlineData("ERC/MCK/BSL/STH/F-001_R3 (SH. 1OF 1)", null, null, "ERC/MCK/BSL/STH/F-001", "1/1", "3")]
    [InlineData("ERC MCK BSL  STH F-004 (SHT.1 OF 1)R1", null, null, "ERC/MCK/BSL/STH/F-004", "1/1", "1")]
    [InlineData("16282205R0", "1/1", 0.0, "16282205", "1/1", "0")]
    [InlineData("DRW 16153298 001 -", "1/1", 1.0, "16153298 001", "1/1", "1")]
    [InlineData("BSL-BF3-REV-SMS-D-R-M-4026 R1, Sht-1/5", "1/5", 1.0, "BSL-BF3-REV-SMS-D-R-M-4026", "1/5", "1")]
    [InlineData("BSL-BF3-REV-SMS-D-R-M-4028-R1, Sht-2/3-B", null, "R1", "BSL-BF3-REV-SMS-D-R-M-4028", "2/3", "1")]
    [InlineData("BSL-BF3-REVV-SMS-D-R-M-4033, R1, Sht-1/1", "1/1", "R1", "BSL-BF3-REVV-SMS-D-R-M-4033", "1/1", "1")]
    [InlineData("BSL-BF3-REV-SMS-D-R-M-4024_B", "1/1", "B", "BSL-BF3-REV-SMS-D-R-M-4024", "1/1", "B")]
    [InlineData("ERC/SMS/SSCGM/F-021 1 OF 1", null, null, "ERC/SMS/SSCGM/F-021", "1/1", "0")]
    [InlineData("ERC-SMS-BSL-HBS-F-002 (SHT.2 OF 2)", "2/2", 0.0, "ERC-SMS-BSL-HBS-F-002", "2/2", "0")]
    [InlineData("30052580", " SHEET 9/32", "R2", "30052580", "9/32", "2")]
    [InlineData("16193442", "2/6", "A", "16193442", "2/6", "A")]
    public void SplitsNumberSheetAndRevision(string raw, object? sheet, object? rev, string no, string sh, string r)
    {
        var (n, s, v) = ExcelDrawingList.SplitNumber(raw, sheet, rev);
        Assert.Equal(no, n);
        Assert.Equal(sh, s);
        Assert.Equal(r, v);
    }

    [Fact]
    public void SheetThatExcelTurnedIntoADateIsRestored()
    {
        var (_, s, _) = ExcelDrawingList.SplitNumber("X-100", new DateTime(2026, 3, 2), null);
        Assert.Equal("2/3", s);
    }

    [Theory]
    [InlineData("GA & DETAIL OF STOVE SHELL VESSEL", "ME")]
    [InlineData("SCRUBBER & DEMISTER STRUCTURE", "ST")]
    [InlineData("Template for Anchor Bolt Positioning", "ST")]
    [InlineData("Crude Gas Main Duct", "PP")]
    [InlineData("Stove Connecting Platform", "ST")]
    [InlineData("GA AND DETAIL OF BACK DRAFT STACK", "ME")]
    [InlineData("HBS Condensation Trap", "PP")]
    public void ClassifiesByDescription(string text, string code) => Assert.Equal(code, ExcelDrawingList.Classify(text)!.Code);

    string BomWorkbook()
    {
        var path = Path.Combine(_dir, "BOM.xlsx");
        using var wb = new XLWorkbook();
        var a = wb.AddWorksheet("STOVE");
        a.Cell(1, 1).Value = "BSL_BF#3_STOVE_SHELL_BOQ";
        string[] h = ["S.no", "DRG NUMBER", "Sheet", "Revision", "DRG Description", "Item Description"];
        for (var i = 0; i < h.Length; i++) a.Cell(3, i + 1).Value = h[i];
        object[][] rows =
        [
            [1, 16193442, "2/6", "A", "GA & DETAIL OF STOVE SHELL VESSEL", "Dome"],
            [2, 16193442, "2/6", "A", "GA & DETAIL OF STOVE SHELL VESSEL", "Dome"],
            [3, 16193442, "3/6", "A", "GA & DETAIL OF STOVE SHELL VESSEL", "Ring"],
            [4, "BOLT&Nut with Washer 20X60", "", "", "", ""],
            [5, "BSL-BF3-REV-SMS-D-R-M-4026 R1, Sht-1/5", "1/5", 1, "GA AND DETAIL OF BACK DRAFT STACK", "Pipe"],
        ];
        for (var r = 0; r < rows.Length; r++)
            for (var c = 0; c < rows[r].Length; c++)
                a.Cell(5 + r, c + 1).Value = XLCellValue.FromObject(rows[r][c]);
        var b = wb.AddWorksheet("Back Draft Line");
        b.Cell(1, 1).Value = "BSL_BF#3_BACK DRAFT LINE_BOQ";
        for (var i = 0; i < h.Length; i++) b.Cell(2, i + 1).Value = h[i];
        b.Cell(3, 2).Value = "BSL-BF3-REV-SMS-D-R-M-4026 R1, Sht-1/5";
        b.Cell(3, 3).Value = "1/5";
        b.Cell(3, 4).Value = "R1";
        b.Cell(3, 5).Value = "Back Draft Line";
        b.Cell(4, 2).Value = "BSL-BF3-REV-SMS-D-R-M-4028-R1, Sht-2/3-B";
        b.Cell(4, 3).Value = new DateTime(2026, 3, 2);
        b.Cell(4, 4).Value = "R1";
        b.Cell(4, 5).Value = "COLD BLAST MIXING PIPE";
        var s = wb.AddWorksheet("DRAWING ");
        string[] sh = ["Si.No", "Area", "Drawing_No", "Sub Area", "Description", "Qty", "Agency"];
        for (var i = 0; i < sh.Length; i++) s.Cell(1, i + 1).Value = sh[i];
        s.Cell(2, 4).Value = "BELLOW_HOT_BLAST"; s.Cell(2, 5).Value = "HBJ01"; s.Cell(2, 7).Value = "MDE";
        s.Cell(3, 4).Value = "SCRUBBER 16271312 DRG"; s.Cell(3, 7).Value = "MDE";
        s.Cell(4, 3).Value = "BSL-BF3-SMS-D-R-M-900-R0"; s.Cell(4, 4).Value = "BOLT M56 X 1550 + OTHER";
        wb.SaveAs(path);
        return path;
    }

    [Fact]
    public void ReadsBomAndScopeIntoDrawingRows()
    {
        var r = ExcelDrawingList.Read([BomWorkbook()], "");
        Assert.Equal("BSL BF#3", r.Plant);
        Assert.Equal(1, r.NotDrawingRows);
        Assert.Equal(1, r.ScopeItemsWithoutNumber);
        Assert.Equal(1, r.MergedDuplicates);

        var stove = r.Drawings.Where(d => d.DrawingNo == "16193442").OrderBy(d => d.Sheet).ToList();
        Assert.Equal(["2/6", "3/6"], stove.Select(d => d.Sheet));
        Assert.All(stove, d => Assert.Equal(("BSL BF#3", "STOVE", "A", "ME"), (d.Plant, d.Unit, d.Revision, d.TypeCode)));
        Assert.Contains("2 item line", stove[0].Remarks);

        var stack = Assert.Single(r.Drawings, d => d.DrawingNo == "BSL-BF3-REV-SMS-D-R-M-4026");
        Assert.Contains("Also listed in BOM.xlsx › Back Draft Line", stack.Remarks);

        var mix = Assert.Single(r.Drawings, d => d.DrawingNo == "BSL-BF3-REV-SMS-D-R-M-4028");
        Assert.Equal(("2/3", "1", "BACK DRAFT LINE", "PP"), (mix.Sheet, mix.Revision, mix.Unit, mix.TypeCode));

        var scrubber = Assert.Single(r.Drawings, d => d.DrawingNo == "16271312");
        Assert.Equal(("Contractor scope", "MDE"), (scrubber.Unit, scrubber.Agency));
        Assert.Single(r.Drawings, d => d.DrawingNo == "BSL-BF3-SMS-D-R-M-900");
        Assert.All(r.Drawings, d => Assert.True(d.FileMissing));
    }

    [Fact]
    public void ZipFilesAttachToExcelRowsAndInheritTheirPlace()
    {
        var store = new InMemoryDrawingStore();
        var lib = new DrawingLibrary(Path.Combine(_dir, "Library"));
        var importer = new DrawingImporter(store, lib);
        var list = ExcelDrawingList.Read([BomWorkbook()], "");
        var (added, already) = importer.ImportList(list, "BOM.xlsx");
        Assert.Equal(list.Drawings.Count, added);
        Assert.Equal(0, already);
        Assert.Equal((0, list.Drawings.Count), importer.ImportList(list, "BOM.xlsx")); // second time: nothing new

        var zip = Path.Combine(_dir, "drawings.zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            foreach (var name in new[]
                     {
                         "Drawings/16193442 SH 2 OF 6 Rev A.pdf",               // fills the 2/6 row
                         "Drawings/BSL-BF3-REV-SMS-D-R-M-4026_R1 (Sht 1 of 5).pdf", // fills the 4026 row
                         "Drawings/BSL-BF3-REV-SMS-D-R-M-4026_R2 (Sht 1 of 5).pdf", // newer revision: new row, same place
                         "Drawings/16193442 Rev A.pdf",                          // several sheets, none named: not attached
                     })
            {
                using var s = z.CreateEntry(name).Open();
                s.Write([1]);
            }
        }
        var r = importer.Import([zip], "Elsewhere");
        Assert.Empty(r.Errors);
        Assert.Equal(2, r.AttachedToList);

        var all = store.All();
        var sheet2 = Assert.Single(all, d => d.DrawingNo == "16193442" && d.Sheet == "2/6");
        Assert.False(sheet2.FileMissing);
        Assert.Equal(("BSL BF#3", "STOVE", "ME"), (sheet2.Plant, sheet2.Unit, sheet2.TypeCode));
        Assert.Contains(Path.Combine("BSL BF#3", "STOVE", "06 ME - Mechanical - Equipment"), sheet2.StoredPath);
        Assert.True(Assert.Single(all, d => d.DrawingNo == "16193442" && d.Sheet == "3/6").FileMissing);

        var revs = all.Where(d => d.DrawingNo == "BSL-BF3-REV-SMS-D-R-M-4026").OrderBy(d => d.RevRank).ToList();
        Assert.Equal(["1", "2"], revs.Select(d => d.Revision));
        Assert.All(revs, d => Assert.Equal(("STOVE", "1/5", false), (d.Unit, d.Sheet, d.FileMissing)));
        var entry = Register.Organise(all).SelectMany(p => p.Units).SelectMany(u => u.Types).SelectMany(t => t.Drawings)
            .Single(e => e.Current.DrawingNo == "BSL-BF3-REV-SMS-D-R-M-4026");
        Assert.Equal("2", entry.Current.Revision);

        // The unnamed-sheet file could not be placed, so it is filed on its own (from the ZIP's plant).
        Assert.Contains(all, d => d.DrawingNo == "16193442" && d.Sheet == "" && d.Plant == "Elsewhere");

        var csv = RegisterExport.Csv(all);
        Assert.Contains("\"16193442\",\"3/6\"", csv);
        Assert.Contains(",No,", csv);
        Assert.True(RegisterExport.Pdf(all, "BSL").Length > 1000);
    }
}
