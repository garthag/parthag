using BSL.Core.Data;
using Microsoft.Data.SqlClient;

namespace BSL.Desktop.Forms;

/// <summary>Database connection (SQL Server / Express / LocalDB) and report header settings.</summary>
public sealed class SettingsForm : Form
{
    readonly AppSettings _s;
    readonly TextBox _server = new() { Width = 340 };
    readonly TextBox _db = new() { Width = 340 };
    readonly RadioButton _win = new() { Text = "Windows authentication", AutoSize = true };
    readonly RadioButton _sql = new() { Text = "SQL Server login", AutoSize = true };
    readonly TextBox _user = new() { Width = 200 };
    readonly TextBox _pwd = new() { Width = 200, UseSystemPasswordChar = true };
    readonly CheckBox _trust = new() { Text = "Trust server certificate", AutoSize = true, Checked = true };
    readonly TextBox _company = new() { Width = 340 };
    readonly TextBox _by = new() { Width = 200 };

    public SettingsForm(AppSettings s)
    {
        _s = s;
        Text = "Settings – database & reports";
        Width = 600; Height = 470; StartPosition = FormStartPosition.CenterScreen; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false;

        var b = new SqlConnectionStringBuilder(s.ConnectionString);
        _server.Text = b.DataSource; _db.Text = b.InitialCatalog;
        _win.Checked = b.IntegratedSecurity; _sql.Checked = !b.IntegratedSecurity;
        _user.Text = b.UserID; _pwd.Text = b.Password; _trust.Checked = b.TrustServerCertificate;
        _company.Text = s.CompanyName; _by.Text = s.PreparedBy;

        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(12), AutoScroll = true };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        void Row(string l, Control c) { t.Controls.Add(new Label { Text = l, AutoSize = true, Padding = new Padding(0, 6, 0, 0) }); t.Controls.Add(c); }
        Row("SQL Server", _server);
        t.Controls.Add(new Label()); t.Controls.Add(new Label { Text = @"e.g. (localdb)\MSSQLLocalDB   or   .\SQLEXPRESS   or   SERVER01", AutoSize = true, ForeColor = Color.DimGray });
        Row("Database name", _db);
        var auth = new FlowLayoutPanel { AutoSize = true }; auth.Controls.AddRange([_win, _sql]);
        Row("Authentication", auth);
        Row("User", _user); Row("Password", _pwd);
        Row("", _trust);
        Row("Report header", _company);
        Row("Prepared by", _by);

        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
        var cancel = Ui.Button("Cancel", (_, _) => DialogResult = DialogResult.Cancel);
        bar.Controls.AddRange([cancel, Ui.Button("Save", (_, _) => SaveSettings()), Ui.Button("Test / create DB", (_, _) => Test(), 130)]);
        CancelButton = cancel;
        _win.CheckedChanged += (_, _) => { _user.Enabled = _pwd.Enabled = !_win.Checked; };
        _user.Enabled = _pwd.Enabled = !_win.Checked;
        Controls.Add(t); Controls.Add(bar);
    }

    string Build()
    {
        var b = new SqlConnectionStringBuilder
        {
            DataSource = _server.Text.Trim(), InitialCatalog = _db.Text.Trim(), TrustServerCertificate = _trust.Checked,
            IntegratedSecurity = _win.Checked, ConnectTimeout = 15, ApplicationName = "BSL Project Control"
        };
        if (!_win.Checked) { b.UserID = _user.Text.Trim(); b.Password = _pwd.Text; }
        return b.ConnectionString;
    }

    void Test()
    {
        Ui.Busy(this, () =>
        {
            var db = new Database(Build());
            db.EnsureCreated();
            var n = db.Scalar("SELECT COUNT(*) FROM dbo.Contractor");
            Ui.Info($"Connected. Database is ready ({n} contractors).");
        });
    }

    void SaveSettings()
    {
        if (_server.Text.Trim().Length == 0 || _db.Text.Trim().Length == 0) { Ui.Error("Server and database are required."); return; }
        _s.ConnectionString = Build();
        _s.CompanyName = _company.Text.Trim();
        _s.PreparedBy = _by.Text.Trim();
        _s.Save();
        DialogResult = DialogResult.OK;
    }
}
