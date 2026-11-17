using System.Text.Json;
using Bf3Billing.Core;

namespace Bf3Billing.App;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Length > 0 && args[0] == "--selftest")
        {
            Environment.Exit(SelfTest.Run(args.Length > 1 ? args[1] : null));
            return;
        }
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
            MessageBox.Show(e.Exception.Message, "BF#3 Billing – unexpected error", MessageBoxButtons.OK, MessageBoxIcon.Error);

        var settings = AppSettings.Load();
        var path = args.Length > 0 ? args[0] : settings.DataFile;
        if (string.IsNullOrWhiteSpace(path)) path = BillingDb.DefaultPath;

        BillingRepository repo;
        try
        {
            repo = new BillingRepository(new BillingDb(path));
        }
        catch (Exception ex)
        {
            MessageBox.Show($"The data file could not be opened:{Environment.NewLine}{path}{Environment.NewLine}{Environment.NewLine}{ex.Message}{Environment.NewLine}{Environment.NewLine}The default data file is used instead.",
                "BF#3 Billing", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            repo = new BillingRepository(new BillingDb(BillingDb.DefaultPath));
        }
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
