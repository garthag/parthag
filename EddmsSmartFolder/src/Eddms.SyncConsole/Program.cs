using System.Text.Json;
using Eddms.Core;

// EddmsSync - command-line sync, handy for Windows Task Scheduler.
//
//   EddmsSync --login "YOUR LOGIN" [--sql "<connection string>"] [--download] [--download-root "D:\EDMS"]
//   EddmsSync --login "YOUR LOGIN" --json out.json        (no SQL: just dump what the site returns)
//
// The password is read from the EDMS_PASSWORD environment variable, or asked for.

var a = ParseArgs(args);
if (a.ContainsKey("selftest"))
    return await SelfTest.RunAsync(a.GetValueOrDefault("sql") ?? EddmsDatabase.DefaultConnectionString);
if (!a.TryGetValue("login", out var login))
{
    Console.WriteLine("Usage: EddmsSync --login <name> [--sql <conn>] [--download] [--download-root <dir>] [--json <file>]");
    return 2;
}
var password = Environment.GetEnvironmentVariable("EDMS_PASSWORD") ?? ReadPassword();
var progress = new Progress<SyncProgress>(p =>
    Console.WriteLine(p.Total > 0 ? $"[{p.Done + 1}/{p.Total}] {p.Message}" : p.Message));

try
{
    if (a.TryGetValue("json", out var jsonFile))
    {
        using var client = new WrenchClient();
        await client.LoginAsync(login, password);
        var roots = await client.GetFolderTreeAsync(progress);
        var output = new List<object>();
        foreach (var f in roots.SelectMany(r => r.SelfAndDescendants()))
        {
            if (f.HasChild) { output.Add(new { f.FullPath, f.FolderId, Documents = 0 }); continue; }
            var c = await client.GetFolderDocumentsAsync(f);
            Console.WriteLine($"{f.FullPath}: {c.Documents.Count} documents ({c.Columns.Count} columns)");
            output.Add(new { f.FullPath, f.FolderId, Documents = c.Documents.Count, Columns = c.Columns, Rows = c.Documents });
        }
        if (a.TryGetValue("download-test", out var dir))
        {
            var first = roots.SelectMany(r => r.SelfAndDescendants()).First(f => !f.HasChild && f.FullPath.Contains("Civil"));
            var c = await client.GetFolderDocumentsAsync(first);
            var path = await client.DownloadDocumentAsync(c.Documents[0].DocId, first.FolderType, dir);
            Console.WriteLine($"Downloaded {path} ({new FileInfo(path).Length} bytes)");
        }
        await File.WriteAllTextAsync(jsonFile, JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Wrote " + jsonFile);
        return 0;
    }

    var db = new EddmsDatabase(a.GetValueOrDefault("sql") ?? EddmsDatabase.DefaultConnectionString);
    var options = new SyncOptions
    {
        LoginName = login,
        Password = password,
        DownloadFiles = a.ContainsKey("download"),
        DownloadRoot = a.GetValueOrDefault("download-root") ?? new SyncOptions { LoginName = "", Password = "" }.DownloadRoot,
    };
    var result = await new SyncService(db).RunAsync(options, progress);
    foreach (var w in result.Warnings) Console.WriteLine("WARNING: " + w);
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine("ERROR: " + ex.Message);
    return 1;
}

static Dictionary<string, string> ParseArgs(string[] args)
{
    var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (int i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--")) continue;
        var key = args[i][2..];
        d[key] = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : "true";
    }
    return d;
}

static string ReadPassword()
{
    Console.Write("EDMS password: ");
    var pw = new System.Text.StringBuilder();
    while (true)
    {
        var k = Console.ReadKey(true);
        if (k.Key == ConsoleKey.Enter) break;
        if (k.Key == ConsoleKey.Backspace) { if (pw.Length > 0) pw.Length--; continue; }
        pw.Append(k.KeyChar);
    }
    Console.WriteLine();
    return pw.ToString();
}
