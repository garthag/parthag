using BSL.Core.Data;
using Microsoft.Data.SqlClient;

namespace BSL.Desktop.Forms;

/// <summary>Database connection (SQL Server / Express / LocalDB) and report header settings.</summary>
public sealed class SettingsForm : StyledForm
{
    readonly AppSettings _s;
    readonly TextBox _server = new() { Width = 340, Tag = UiStyle.NoCaps };
    readonly TextBox _db = new() { Width = 340, Tag = UiStyle.NoCaps };
    readonly RadioButton _win = new() { Text = "Windows authentication", AutoSize = true };
    readonly RadioButton _sql = new() { Text = "SQL Server login", AutoSize = true };
    readonly TextBox _user = new() { Width = 200, Tag = UiStyle.NoCaps };
    readonly TextBox _pwd = new() { Width = 200, UseSystemPasswordChar = true };
    readonly CheckBox _trust = new() { Text = "Trust server certificate", AutoSize = true, Checked = true };
    readonly TextBox _company = new() { Width = 420 };
    readonly TextBox _address = new() { Width = 420 };
    readonly TextBox _by = new() { Width = 200 };
    readonly PictureBox _logo = new() { Width = 180, Height = 70, SizeMode = PictureBoxSizeMode.Zoom, BorderStyle = BorderStyle.FixedSingle, BackColor = Color.White };
    string? _logoPath;

    public SettingsForm(AppSettings s)
    {
        _s = s;
        Text = "Settings – database, company & logo";
        Width = 680; Height = 640; StartPosition = FormStartPosition.CenterScreen; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false;

        var b = new SqlConnectionStringBuilder(s.ConnectionString);
        _server.Text = b.DataSource; _db.Text = b.InitialCatalog;
        _win.Checked = b.IntegratedSecurity; _sql.Checked = !b.IntegratedSecurity;
        _user.Text = b.UserID; _pwd.Text = b.Password; _trust.Checked = b.TrustServerCertificate;
        _company.Text = s.CompanyName; _address.Text = s.CompanyAddress; _by.Text = s.PreparedBy;
        _logoPath = s.LogoPath;
        ShowLogo();

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
        Row("Company / report header", _company);
        Row("Address / project line", _address);
        Row("Prepared by", _by);
        var logoBox = new FlowLayoutPanel { AutoSize = true };
        logoBox.Controls.AddRange([_logo, Ui.Button("Browse logo…", (_, _) => PickLogo(), 120), Ui.Button("Remove", (_, _) => { _logoPath = null; ShowLogo(); }, 80)]);
        Row("Company logo", logoBox);
        t.Controls.Add(new Label()); t.Controls.Add(new Label { Text = "PNG or JPG. Printed at the top-left of every PDF report, on Excel exports and on the dashboard.", AutoSize = true, MaximumSize = new Size(440, 0), ForeColor = Color.DimGray });

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
        _s.CompanyAddress = _address.Text.Trim();
        _s.LogoPath = _logoPath;
        _s.PreparedBy = _by.Text.Trim();
        _s.Save();
        DialogResult = DialogResult.OK;
    }

    void PickLogo()
    {
        using var d = new OpenFileDialog { Title = "Company logo", Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp" };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            var dest = Path.Combine(AppSettings.Folder, "logo.png");
            _logo.Image?.Dispose(); _logo.Image = null;
            // re-save as a standard 8-bit PNG: works in PDF (PDFsharp), Excel and on screen whatever the source format
            using (var src = Image.FromFile(d.FileName))
            using (var bmp = new Bitmap(src.Width, src.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp)) { g.Clear(Color.White); g.DrawImage(src, 0, 0, src.Width, src.Height); }
                bmp.Save(dest, System.Drawing.Imaging.ImageFormat.Png);
            }
            _logoPath = dest;
            ShowLogo();
        }
        catch (Exception ex) { Ui.Error("Not a usable image: " + ex.Message); }
    }

    void ShowLogo()
    {
        _logo.Image?.Dispose();
        _logo.Image = null;
        if (_logoPath != null && File.Exists(_logoPath))
            using (var fs = File.OpenRead(_logoPath)) _logo.Image = Image.FromStream(fs).Clone() as Image;   // no file lock
    }
}
