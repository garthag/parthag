using System.IO.Compression;
using DrawingRegister.Core;
using Xunit;

namespace DrawingRegister.Tests;

public sealed class ImportTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "dr-tests-" + Guid.NewGuid().ToString("N"));

    public ImportTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    static byte[] Zip(Dictionary<string, byte[]> files)
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var (name, data) in files)
            {
                using var s = z.CreateEntry(name).Open();
                s.Write(data);
            }
        return ms.ToArray();
    }

    string WriteZip(string name, Dictionary<string, byte[]> files)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, Zip(files));
        return path;
    }

    [Fact]
    public void UnzipsNestedZipsSkipsJunkAndDuplicatesAndFilesIntoLibrary()
    {
        var inner = Zip(new() { ["Unit-300/ISO-300-1023 Rev B.pdf"] = [1, 2, 3] });
        var zip = WriteZip("Package.zip", new()
        {
            ["Package/RF-100-ST-GA-0001_R0.pdf"] = [1],
            ["Package/RF-100-ST-GA-0001_R1.pdf"] = [2],
            ["Package/RF-100-PP-ISO-0007_R0.pdf"] = [3],
            ["Package/Plant-2/U200-EL-SLD-001-R0.png"] = [4],
            ["Package/Piping/isos.zip"] = inner,
            ["Package/Thumbs.db"] = [0],
            ["__MACOSX/Package/._x.pdf"] = [0],
        });
        var store = new InMemoryDrawingStore();
        var lib = new DrawingLibrary(Path.Combine(_dir, "Library"));
        var importer = new DrawingImporter(store, lib);
        var progress = new List<ImportProgress>();

        var r = importer.Import([zip], "Refinery", new SyncProgress<ImportProgress>(progress.Add));

        Assert.Empty(r.Errors);
        Assert.Equal(5, r.Added.Count);
        Assert.Equal(2, r.JunkSkipped);
        Assert.Equal(5, store.All().Count);
        Assert.Equal(5, progress.Last().Done);

        var iso = r.Added.Single(d => d.DrawingNo == "ISO-300-1023");
        Assert.Equal("Unit 300", iso.Unit);
        Assert.Equal(DrawingType.Piping, iso.Type);
        Assert.Equal("Refinery", iso.Plant);
        Assert.Equal("Plant 2", r.Added.Single(d => d.DrawingNo == "U200-EL-SLD-001").Plant);

        // Files are laid out Plant\Unit\Type in the library.
        var ga = r.Added.Single(d => d.DrawingNo == "RF-100-ST-GA-0001" && d.Revision == "1");
        Assert.Equal(Path.Combine("Refinery", "Unit 100", "04 ST - Structural", "RF-100-ST-GA-0001_R1.pdf"), ga.StoredPath);
        Assert.Equal([2], File.ReadAllBytes(lib.FullPath(ga)));
        Assert.Equal(1, ga.SizeBytes);

        var again = importer.Import([zip], "Refinery");
        Assert.Empty(again.Added);
        Assert.Equal(5, again.Duplicates.Count);
    }

    [Fact]
    public void SingleFilesCanBeAddedAndRelocatedAfterEdit()
    {
        var file = Path.Combine(_dir, "CDU-PID-0004 Rev 3.pdf");
        File.WriteAllBytes(file, [9]);
        var store = new InMemoryDrawingStore();
        var lib = new DrawingLibrary(Path.Combine(_dir, "Library"));
        var r = new DrawingImporter(store, lib).Import([file], "Refinery");
        var d = Assert.Single(r.Added);
        Assert.Equal(DrawingType.Process, d.Type);

        d.Unit = "CDU";
        lib.Relocate(d);
        Assert.Equal(Path.Combine("Refinery", "CDU", "02 PR - Process", "CDU-PID-0004 Rev 3.pdf"), d.StoredPath);
        Assert.True(File.Exists(lib.FullPath(d)));
        Assert.False(Directory.Exists(Path.Combine(lib.Root, "Refinery", "General - All units")));

        lib.Delete(d);
        Assert.False(Directory.Exists(Path.Combine(lib.Root, "Refinery")));
    }

    [Fact]
    public void BrokenZipIsReportedNotThrown()
    {
        var bad = Path.Combine(_dir, "bad.zip");
        File.WriteAllText(bad, "not a zip");
        var r = new DrawingImporter(new InMemoryDrawingStore(), new DrawingLibrary(_dir)).Import([bad], "");
        Assert.Single(r.Errors);
        Assert.Empty(r.Added);
    }

    [Fact]
    public void ExportsCsvAndPdf()
    {
        var zip = WriteZip("p.zip", new()
        {
            ["A-100-ST-GA-001_R0.pdf"] = [1],
            ["A-100-ST-GA-001_R1.pdf"] = [2],
            ["A-100-EL-SLD-001_RevA.pdf"] = [3],
        });
        var store = new InMemoryDrawingStore();
        new DrawingImporter(store, new DrawingLibrary(Path.Combine(_dir, "L"))).Import([zip], "Plant A");

        var csv = RegisterExport.Csv(store.All());
        Assert.Contains("A-100-ST-GA-001", csv);
        Assert.Contains("Superseded", csv);
        Assert.Equal(4, csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);

        var pdf = RegisterExport.Pdf(store.All(), "Test Project");
        Assert.True(pdf.Length > 1000);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));
    }

    [Fact]
    public void SchemaScriptIsEmbeddedAndSplitIntoBatches()
    {
        var batches = SqlDrawingStore.SchemaBatches().ToList();
        Assert.True(batches.Count >= 7);
        Assert.Contains(batches, b => b.Contains("CREATE TABLE dbo.Drawing"));
        Assert.Contains(batches, b => b.Contains("vw_DrawingRegister"));
    }

    sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
