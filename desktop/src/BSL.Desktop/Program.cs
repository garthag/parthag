using BSL.Core.Data;
using BSL.Core.Excel;
using BSL.Core.Reports;
using BSL.Desktop.Forms;

namespace BSL.Desktop;

internal static class Program
{
    public static AppSettings Settings { get; private set; } = new();
    public static Database Db { get; private set; } = null!;
    public static EntityRepository Repo { get; private set; } = null!;

    [STAThread]
    static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.ThreadException += (_, e) => Ui.Error(e.Exception);
        Settings = AppSettings.Load();

        while (true)
        {
            try
            {
                Connect(Settings.ConnectionString);
                break;
            }
            catch (Exception ex)
            {
                Ui.Error("Cannot open the database:\n\n" + ex.Message + "\n\nPlease check the connection settings.");
                using var f = new SettingsForm(Settings);
                if (f.ShowDialog() != DialogResult.OK) return;
            }
        }
        Application.Run(new MainForm());
    }

    public static void Connect(string connectionString)
    {
        var db = new Database(connectionString);
        Cursor.Current = Cursors.WaitCursor;
        db.EnsureCreated();          // creates DB, tables, views and seed data if missing
        Db = db;
        Repo = new EntityRepository(db);
        PdfReport.CompanyName = Settings.CompanyName;
        PdfReport.PreparedBy = Settings.PreparedBy;
        TableExcel.LookupText = lk => Repo.LookupData(lk).Rows.Cast<System.Data.DataRow>()
                                          .ToDictionary(r => Convert.ToInt32(r[0]), r => r[1]?.ToString() ?? "");
        Cursor.Current = Cursors.Default;
    }
}
