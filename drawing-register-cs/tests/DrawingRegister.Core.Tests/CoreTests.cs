namespace DrawingRegister.Core.Tests;

public class FileNameParserTests
{
    [Theory]
    [InlineData("NRP-ST-A01-GA-0001_RevB.pdf", "NRP-ST-A01-GA-0001", "B")]
    [InlineData("X-ME-0001 (Rev 2).pdf", "X-ME-0001", "2")]
    [InlineData("abc_R3.pdf", "ABC", "3")]
    [InlineData("drawing.pdf", "DRAWING", null)]
    [InlineData("PLANT-EL-SLD-0002 REV C01.pdf", "PLANT-EL-SLD-0002", "C01")]
    public void SplitsNumberAndRevision(string file, string number, string? revision)
    {
        Assert.Equal((number, revision), FileNameParser.SplitFileName(file));
    }

    [Fact]
    public void InfersDisciplineAreaAndTypeFromNumber()
    {
        var p = FileNameParser.Parse("NRP-ST-A01-GA-0001_RevB.pdf");
        Assert.Equal(("Structural", "A01", "GA", "B"), (p.Discipline, p.Area, p.DrawingType, p.Revision));
        Assert.Null(FileNameParser.Parse("E-001.pdf").Discipline); // a leading single letter is ambiguous
    }

    [Fact]
    public void InfersFromFolders()
    {
        var p = FileNameParser.Parse("12345.pdf", "Project X/03_Piping/Area 07/Isometric/12345.pdf");
        Assert.Equal(("Piping", "AREA07", "ISO"), (p.Discipline, p.Area, p.DrawingType));
        Assert.Equal("Project X/03_Piping/Area 07/Isometric", p.FolderPath);
        Assert.Null(FileNameParser.Parse("1.pdf", "1.pdf").FolderPath);
        Assert.Contains("Structural", FileNameParser.Disciplines);
    }
}

public class RevisionOrderTests
{
    [Fact]
    public void OrdersEpcRevisionCodes()
    {
        string[] codes = ["10", "R2", "1", "0", "AA", "Z", "C01", "B", "A", "P2", "P1"];
        Assert.Equal(["P1", "P2", "A", "B", "C01", "Z", "AA", "0", "1", "R2", "10"], codes.OrderBy(RevisionOrder.Sequence));
        Assert.Equal(0, RevisionOrder.Sequence(null));
        Assert.Equal(1, RevisionOrder.Sequence("???"));
    }
}

public sealed class ProjectRepositoryTests : IDisposable
{
    private readonly TestEnv _env = new();

    public void Dispose() => _env.Dispose();

    [Fact]
    public void CreatesUpdatesListsAndDeletes()
    {
        var p = _env.Projects.Save(new Project
        {
            Code = " nrp-01 ", Name = " North Refinery ", Client = "Acme", Location = " ", Status = ProjectStatus.Active,
            StartDate = new DateTime(2026, 1, 1), EndDate = new DateTime(2026, 12, 31),
        });
        Assert.True(p.Id > 0);
        Assert.Equal(("NRP-01", "North Refinery"), (p.Code, p.Name));
        Assert.Null(p.Location);
        Assert.Equal(new DateTime(2026, 12, 31), p.EndDate);

        p.Name = "North Refinery Expansion";
        p.Status = ProjectStatus.OnHold;
        var updated = _env.Projects.Save(p);
        Assert.Equal(("North Refinery Expansion", ProjectStatus.OnHold), (updated.Name, updated.Status));

        _env.NewProject("LNG");
        Assert.Equal(["LNG", "NRP-01"], _env.Projects.List().Select(x => x.Code));
        Assert.Single(_env.Projects.List("refinery"));

        _env.Projects.Delete(p.Id);
        Assert.Null(_env.Projects.Get(p.Id));
    }

    [Theory]
    [InlineData("", "Name", "at least 2")]
    [InlineData("A B", "Name", "only contain")]
    [InlineData("OK", " ", "name is required")]
    public void ValidatesInput(string code, string name, string message)
    {
        var ex = Assert.Throws<ValidationException>(() => _env.Projects.Save(new Project { Code = code, Name = name }));
        Assert.Contains(message, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsDuplicateCodesAndBadDates()
    {
        _env.NewProject("DUP");
        var ex = Assert.Throws<ValidationException>(() => _env.Projects.Save(new Project { Code = "dup", Name = "Again" }));
        Assert.Contains("already exists", ex.Message);
        Assert.Throws<ValidationException>(() => _env.Projects.Save(new Project
        {
            Code = "DT", Name = "Dates", StartDate = new DateTime(2026, 2, 1), EndDate = new DateTime(2026, 1, 1),
        }));
        Assert.Throws<ValidationException>(() => _env.Projects.Save(new Project { Code = "ST", Name = "S", Status = "Bogus" }));
    }
}

public sealed class ImportServiceTests : IDisposable
{
    private readonly TestEnv _env = new();

    public void Dispose() => _env.Dispose();

    [Fact]
    public void RegistersDrawingsRevisionsAndDuplicates()
    {
        var project = _env.NewProject();
        var a = _env.Pdf("NRP-ST-A01-GA-0001_RevA.pdf", "a");
        var b = _env.Pdf("NRP-ST-A01-GA-0001_RevB.pdf", "b");
        var copy = _env.Pdf("copy-of-rev-a.pdf", "a");
        File.Copy(a, copy, overwrite: true);

        var first = _env.Import.Import(project.Id, [new ImportSource(a)]);
        Assert.Equal(ImportStatus.Created, first.Items[0].Status);
        Assert.Equal(("NRP-ST-A01-GA-0001", "A", "Structural"), (first.Items[0].DrawingNumber, first.Items[0].Revision, first.Items[0].Discipline));

        var progress = new List<(int, int)>();
        var second = _env.Import.Import(project.Id, [new ImportSource(b), new ImportSource(copy)], new SyncProgress(progress));
        Assert.Equal([ImportStatus.NewRevision, ImportStatus.Duplicate], second.Items.Select(i => i.Status));
        Assert.Equal([(1, 2), (2, 2)], progress);
        Assert.Equal(100, second.SuccessRate);

        var drawing = Assert.Single(_env.Drawings.List(new DrawingQuery(project.Id)));
        Assert.Equal(("B", 2, "Structural", "A01", "GA"),
            (drawing.CurrentRevision, drawing.RevisionCount, drawing.Discipline, drawing.Area, drawing.DrawingType));
        var revisions = _env.Drawings.Revisions(drawing.Id);
        Assert.Equal(["B", "A"], revisions.Select(r => r.Code));
        Assert.True(revisions[0].IsCurrent);
        Assert.NotNull(revisions[0].File);
        Assert.True(File.Exists(_env.Paths.AbsoluteStoredPath(revisions[0].File!.StoredPath)));
        Assert.Equal(1, _env.Projects.Get(project.Id)!.DrawingCount);
    }

    [Fact]
    public void RejectsBadFiles()
    {
        var project = _env.NewProject();
        var txt = Path.Combine(_env.Inbox, "notes.txt");
        File.WriteAllText(txt, "hello");
        var fake = Path.Combine(_env.Inbox, "fake.pdf");
        File.WriteAllText(fake, "not a pdf");
        var summary = _env.Import.Import(project.Id,
            [new ImportSource(txt), new ImportSource(fake), new ImportSource(Path.Combine(_env.Inbox, "missing.pdf"))]);
        Assert.Equal(3, summary.Failed);
        Assert.Equal(["Only PDF files are supported.", "Not a valid PDF file.", "File not found."], summary.Items.Select(i => i.Error));
        Assert.Equal(0, summary.SuccessRate);
        Assert.Throws<ValidationException>(() => _env.Import.Import(999, [new ImportSource(fake)]));
    }

    [Fact]
    public void ImportsNestedFolderWithCategorisation()
    {
        var project = _env.NewProject();
        _env.Pdf("Package/02_Piping/Area 03/1001.pdf");
        _env.Pdf("Package/05_Electrical/Area 01/2002_RevC.pdf");
        _env.Pdf("Package/Misc/deep/nested/3003.pdf");
        _env.Pdf("Package/readme.txt");
        var sources = ImportService.CollectFolder(Path.Combine(_env.Inbox, "Package"));
        Assert.Equal(3, sources.Count);
        Assert.All(sources, s => Assert.StartsWith("Package/", s.RelativePath, StringComparison.Ordinal));

        var summary = _env.Import.Import(project.Id, sources);
        Assert.Equal(3, summary.Created);
        var drawings = _env.Drawings.List(new DrawingQuery(project.Id)).ToDictionary(d => d.DrawingNumber);
        Assert.Equal(("Piping", "AREA03", "0"), (drawings["1001"].Discipline, drawings["1001"].Area, drawings["1001"].CurrentRevision));
        Assert.Equal(("Electrical", "C"), (drawings["2002"].Discipline, drawings["2002"].CurrentRevision));
        Assert.Equal("Package/Misc/deep/nested", drawings["3003"].FolderPath);
    }

    [Fact]
    public void ReuploadWithoutRevisionCreatesPlaceholderRevision()
    {
        var project = _env.NewProject();
        _env.Import.Import(project.Id, [new ImportSource(_env.Pdf("A-100.pdf", "v1"))]);
        _env.Import.Import(project.Id, [new ImportSource(_env.Pdf("A-100.pdf", "v2"))]);
        var drawing = Assert.Single(_env.Drawings.List(new DrawingQuery(project.Id)));
        Assert.Equal(["U2", "0"], _env.Drawings.Revisions(drawing.Id).Select(r => r.Code));
        Assert.Equal("U2", drawing.CurrentRevision);
        _env.Import.Import(project.Id, [new ImportSource(_env.Pdf("A-100.pdf", "v3"))]);
        Assert.Equal("U3", _env.Drawings.Get(drawing.Id)!.CurrentRevision);
    }

    private sealed class SyncProgress(List<(int, int)> sink) : IProgress<(int Done, int Total)>
    {
        public void Report((int Done, int Total) value) => sink.Add(value);
    }
}

public sealed class DrawingRepositoryTests : IDisposable
{
    private readonly TestEnv _env = new();

    public void Dispose() => _env.Dispose();

    private (Project Project, Drawing Rack, Drawing Pipe) Seed()
    {
        var project = _env.NewProject();
        _env.Import.Import(project.Id,
        [
            new ImportSource(_env.Pdf("NRP-ST-A01-GA-0001.pdf", "1")),
            new ImportSource(_env.Pdf("NRP-PI-A02-ISO-0002.pdf", "2")),
        ]);
        var all = _env.Drawings.List(new DrawingQuery(project.Id)).ToDictionary(d => d.DrawingNumber);
        var rack = all["NRP-ST-A01-GA-0001"];
        rack.Title = "Pipe rack general arrangement";
        rack = _env.Drawings.Update(rack);
        return (project, rack, all["NRP-PI-A02-ISO-0002"]);
    }

    [Fact]
    public void SearchesAndFilters()
    {
        var (project, rack, pipe) = Seed();
        Assert.Equal([rack.Id], _env.Drawings.List(new DrawingQuery(project.Id, "rack")).Select(d => d.Id));
        Assert.Equal([pipe.Id], _env.Drawings.List(new DrawingQuery(project.Id, "PI-A02")).Select(d => d.Id));
        Assert.Equal([pipe.Id], _env.Drawings.List(new DrawingQuery(project.Id, Discipline: "Piping")).Select(d => d.Id));
        Assert.Empty(_env.Drawings.List(new DrawingQuery(project.Id, Status: "IFC")));
        Assert.Equal(2, _env.Drawings.List(new DrawingQuery(project.Id, " ")).Count);
    }

    [Fact]
    public void UpdatesWithValidation()
    {
        var (_, rack, pipe) = Seed();
        rack.Status = "IFC";
        rack.Remarks = "Checked";
        var saved = _env.Drawings.Update(rack);
        Assert.Equal(("IFC", "Checked", "Pipe rack general arrangement"), (saved.Status, saved.Remarks, saved.Title));
        rack.Status = "WRONG";
        Assert.Throws<ValidationException>(() => _env.Drawings.Update(rack));
        rack.Status = "IFC";
        rack.DrawingNumber = pipe.DrawingNumber;
        Assert.Contains("already exists", Assert.Throws<ValidationException>(() => _env.Drawings.Update(rack)).Message);
        rack.DrawingNumber = " ";
        Assert.Throws<ValidationException>(() => _env.Drawings.Update(rack));
    }

    [Fact]
    public void DeletingRemovesRecordsAndReturnsFilesToClean()
    {
        var (project, rack, pipe) = Seed();
        var other = _env.NewProject("OTH");
        var rackFile = _env.Drawings.Revisions(rack.Id)[0].File!;
        // the same PDF in another project is stored as that project's own copy
        var otherImport = _env.Import.Import(other.Id, [new ImportSource(_env.Paths.AbsoluteStoredPath(rackFile.StoredPath))]);
        Assert.Equal(ImportStatus.Created, otherImport.Items[0].Status);

        var removed = _env.Drawings.Delete(rack.Id);
        Assert.Equal([rackFile.StoredPath], removed);
        Assert.Null(_env.Drawings.Get(rack.Id));
        _env.Import.DeleteStoredFiles(removed);
        Assert.False(File.Exists(_env.Paths.AbsoluteStoredPath(rackFile.StoredPath)));
        var otherDrawing = Assert.Single(_env.Drawings.List(new DrawingQuery(other.Id)));
        Assert.True(File.Exists(_env.Paths.AbsoluteStoredPath(_env.Drawings.Revisions(otherDrawing.Id)[0].File!.StoredPath)));

        var pipeFile = _env.Drawings.Revisions(pipe.Id)[0].File!;
        Assert.Equal([pipeFile.StoredPath], _env.Projects.Delete(project.Id));
        Assert.Null(_env.Drawings.Get(pipe.Id)); // cascade
        Assert.Throws<InvalidOperationException>(() => _env.Paths.AbsoluteStoredPath("../../escape.pdf"));
    }

    [Fact]
    public void ExportsCsv()
    {
        var (project, _, _) = Seed();
        var path = Path.Combine(_env.Paths.Root, "register.csv");
        CsvExporter.Export(project, _env.Drawings.List(new DrawingQuery(project.Id)), path);
        var lines = File.ReadAllLines(path);
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("Project,Drawing No,Title", lines[0].TrimStart('﻿'), StringComparison.Ordinal);
        Assert.Contains("Pipe rack general arrangement", File.ReadAllText(path));
        Assert.Equal("\"a,b\"", CsvExporter.Escape("a,b"));
        Assert.Equal("'=SUM(A1)", CsvExporter.Escape("=SUM(A1)"));
        Assert.Equal("-5", CsvExporter.Escape("-5"));
        Assert.Equal("", CsvExporter.Escape(null));
    }

    [Fact]
    public void MigrationIsIdempotentAndPathsResolve()
    {
        _env.Db.Migrate();
        Assert.Equal(1, Database.SchemaVersion);
        Environment.SetEnvironmentVariable("DRAWING_REGISTER_HOME", _env.Paths.Root);
        try
        {
            Assert.Equal(_env.Paths.Root, AppPaths.Default().Root);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DRAWING_REGISTER_HOME", null);
        }
    }
}
