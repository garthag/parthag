using System.Text.Json;
using Bf3Billing.Core;

namespace Bf3Billing.App;

static class Program
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern void ExitProcess(uint exitCode);

    /// <summary>
    /// Opens the data file; never writes to a file made by another program. Falls back to the default file,
    /// and if that one belongs to another program, to <see cref="BillingDb.AlternatePath"/> next to it.
    /// </summary>
    static BillingRepository OpenDataFile(string path, AppSettings settings)
    {
        var tried = new List<string>();
        foreach (var candidate in new[] { path, BillingDb.DefaultPath, BillingDb.AlternatePath }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var repo = new BillingRepository(new BillingDb(candidate));
                if (tried.Count > 0)
                    MessageBox.Show(string.Join(Environment.NewLine + Environment.NewLine, tried) + Environment.NewLine + Environment.NewLine +
                        "BF#3 Billing now keeps its data in:" + Environment.NewLine + repo.Db.FilePath + Environment.NewLine + Environment.NewLine +
                        "(Data › Open data file switches files at any time.)",
                        "BF#3 Billing", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return repo;
            }
            catch (Exception ex) when (ex is BillingException or Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
            {
                tried.Add(ex is IncompatibleDataFileException ? ex.Message : $"The data file could not be opened:{Environment.NewLine}{candidate}{Environment.NewLine}{ex.Message}");
            }
        }
        // Last resort: a fresh file with a unique name, so the app always starts.
        var fresh = Path.Combine(Path.GetDirectoryName(BillingDb.DefaultPath)!, $"bf3-billing-{DateTime.Now:yyyyMMdd-HHmmss}.db");
        MessageBox.Show(string.Join(Environment.NewLine + Environment.NewLine, tried) + Environment.NewLine + Environment.NewLine + "A new data file is used: " + fresh,
            "BF#3 Billing", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return new BillingRepository(new BillingDb(fresh));
    }

    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Length > 1 && args[0] == "--screenshots")
        {
            ExitProcess((uint)Screenshots.Run(args[1]));
            return;
        }
        if (args.Length > 0 && args[0] == "--selftest")
        {
            var code = SelfTest.Run(args.Length > 1 ? args[1] : null);
            // Leave at once: a normal exit can wait on Windows Forms' own threads after the windows are gone.
            ExitProcess((uint)code);
            return;
        }
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
            MessageBox.Show(e.Exception.Message, "BF#3 Billing – unexpected error", MessageBoxButtons.OK, MessageBoxIcon.Error);

        var settings = AppSettings.Load();
        var path = args.Length > 0 ? args[0] : settings.DataFile;
        if (string.IsNullOrWhiteSpace(path)) path = BillingDb.DefaultPath;

        var repo = OpenDataFile(path, settings);
        settings.DataFile = repo.Db.FilePath;
        settings.Save();
        Application.Run(new MainForm(repo, settings));
    }
}

/// <summary>Per-user settings in %LOCALAPPDATA%\Bf3Billing\settings.json.</summary>
sealed class AppSettings
{
    public string DataFile { get; set; } = "";
    public string LastBackupFolder { get; set; } = "";

    static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bf3Billing", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
