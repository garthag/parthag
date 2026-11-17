using DrawingRegister.Core;

namespace DrawingRegister.App;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Ui.Error("Unexpected error", e.Exception);

        var settings = AppSettings.Load();
        // Connect (creating the database on first run); let the user fix the settings if it fails.
        while (true)
        {
            try
            {
                Cursor.Current = Cursors.WaitCursor;
                new SqlDrawingStore(settings.ConnectionString).EnsureDatabase();
                Directory.CreateDirectory(settings.LibraryFolder);
                break;
            }
            catch (Exception e)
            {
                Cursor.Current = Cursors.Default;
                Ui.Error("Could not open the drawing register database or library folder.\n\n" +
                         "Check the SQL Server connection (SQL Server Express or LocalDB must be installed) and the library folder.", e);
                using var f = new SettingsForm(settings);
                if (f.ShowDialog() != DialogResult.OK) return;
                settings.Save();
            }
        }
        Cursor.Current = Cursors.Default;
        Application.Run(new MainForm(settings));
    }
}

static class Ui
{
    public static void Error(string message, Exception? e = null)
    {
        Log(message, e);
        MessageBox.Show(e == null ? message : $"{message}\n\n{e.Message}", "Drawing Register",
            MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    public static void Info(string message) =>
        MessageBox.Show(message, "Drawing Register", MessageBoxButtons.OK, MessageBoxIcon.Information);

    public static bool Confirm(string message) =>
        MessageBox.Show(message, "Drawing Register", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) == DialogResult.OK;

    static void Log(string message, Exception? e)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            File.AppendAllText(Path.Combine(AppSettings.Folder, "error.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}\n{e}\n\n");
        }
        catch (IOException) { }
    }

    public static Color ParseColour(string hex) => ColorTranslator.FromHtml(hex);

    public static string Size(long bytes) => bytes switch
    {
        <= 0 => "",
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.0} MB",
    };
}
