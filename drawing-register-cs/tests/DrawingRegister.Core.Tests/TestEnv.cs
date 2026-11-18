using System.Text;

namespace DrawingRegister.Core.Tests;

/// <summary>A throw-away data folder with a migrated database.</summary>
public sealed class TestEnv : IDisposable
{
    public TestEnv()
    {
        Paths = new AppPaths(Path.Combine(Path.GetTempPath(), "drawing-register-tests", Guid.NewGuid().ToString("N")));
        Db = new Database(Paths.DatabaseFile);
        Db.Migrate();
        Projects = new ProjectRepository(Db);
        Drawings = new DrawingRepository(Db);
        Import = new ImportService(Db, Paths);
        Inbox = Directory.CreateDirectory(Path.Combine(Paths.Root, "inbox")).FullName;
    }

    public AppPaths Paths { get; }
    public Database Db { get; }
    public ProjectRepository Projects { get; }
    public DrawingRepository Drawings { get; }
    public ImportService Import { get; }
    public string Inbox { get; }

    public Project NewProject(string code = "NRP") =>
        Projects.Save(new Project { Code = code, Name = $"{code} Project", Client = "Acme" });

    /// <summary>Write a small valid PDF; <paramref name="marker"/> makes the content (and hash) unique.</summary>
    public string Pdf(string relativePath, string marker = "")
    {
        var path = Path.Combine(Inbox, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var body = $"%PDF-1.4\n1 0 obj << /Type /Catalog >> endobj\n% {relativePath} {marker}\n%%EOF\n";
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(body));
        return path;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Paths.Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
