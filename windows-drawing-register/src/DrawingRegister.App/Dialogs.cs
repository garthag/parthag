using DrawingRegister.Core;
using Microsoft.Data.SqlClient;

namespace DrawingRegister.App;

/// <summary>Small helper for label + control rows in a dialog.</summary>
abstract class FormDialog : Form
{
    protected readonly TableLayoutPanel Grid = new()
    {
        Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(12), AutoScroll = true,
    };
    protected readonly Button Ok = new() { Text = "OK", DialogResult = DialogResult.OK, Width = 96, Height = 30 };
    protected readonly Button Cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 96, Height = 30 };

    protected FormDialog(string title, int width, int height)
    {
        Text = title;
        Font = new Font("Segoe UI", 9.5f);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(width, height);
        AcceptButton = Ok;
        CancelButton = Cancel;
        Grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        Grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 48, Padding = new Padding(8) };
        buttons.Controls.AddRange([Cancel, Ok]);
        Controls.Add(Grid);
        Controls.Add(buttons);
    }

    protected T Row<T>(string label, T control) where T : Control
    {
        control.Dock = DockStyle.Fill;
        Grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 6, 0, 0) });
        Grid.Controls.Add(control);
        return control;
    }

    protected void Note(string text)
    {
        var l = new Label { Text = text, AutoSize = true, MaximumSize = new Size(ClientSize.Width - 40, 0), ForeColor = SystemColors.GrayText, Padding = new Padding(0, 4, 0, 8) };
        Grid.Controls.Add(l);
        Grid.SetColumnSpan(l, 2);
    }
}

sealed class SettingsForm : FormDialog
{
    readonly AppSettings _s;
    readonly TextBox _project, _conn, _library;

    public SettingsForm(AppSettings s) : base("Settings", 640, 330)
    {
        _s = s;
        _project = Row("Project name", new TextBox { Text = s.ProjectName });
        _conn = Row("SQL Server connection", new TextBox { Text = s.ConnectionString });
        Note(@"LocalDB (single PC): Server=(localdb)\MSSQLLocalDB;Database=DrawingRegister;Integrated Security=true;TrustServerCertificate=true" + "\n" +
             @"SQL Server Express / shared server: Server=PCNAME\SQLEXPRESS;Database=DrawingRegister;Integrated Security=true;TrustServerCertificate=true");
        var lib = new FlowLayoutPanel { Height = 32, WrapContents = false };
        _library = new TextBox { Text = s.LibraryFolder, Width = 340 };
        var browse = new Button { Text = "Browse…", Height = 27 };
        browse.Click += (_, _) =>
        {
            using var d = new FolderBrowserDialog { SelectedPath = _library.Text, Description = "Folder that holds the drawing files" };
            if (d.ShowDialog(this) == DialogResult.OK) _library.Text = d.SelectedPath;
        };
        lib.Controls.AddRange([_library, browse]);
        Row("Library folder", lib);
        Note("Drawing files are copied here in Plant \\ Unit \\ Type folders. Use a shared folder (\\\\server\\drawings) " +
             "together with a shared SQL Server so the whole team works on one register.");
        Ok.Click += (_, _) => Save();
    }

    void Save()
    {
        try
        {
            _ = new SqlConnectionStringBuilder(_conn.Text);
        }
        catch (ArgumentException e)
        {
            Ui.Error("The connection string is not valid.", e);
            DialogResult = DialogResult.None;
            return;
        }
        _s.ProjectName = _project.Text.Trim();
        _s.ConnectionString = _conn.Text.Trim();
        _s.LibraryFolder = _library.Text.Trim();
    }
}

sealed class ImportOptionsForm : FormDialog
{
    readonly ComboBox _plant;
    public string DefaultPlant => _plant.Text.Trim();

    public ImportOptionsForm(IReadOnlyList<string> files, IEnumerable<string> knownPlants) : base($"Import {files.Count} file(s)", 560, 230)
    {
        Note("Drawings are filed Plant → Unit → Drawing type → Drawing no. Plant and unit are read from folder names " +
             "(Plant-1, Unit-100, CDU …) and from drawing numbers (…-100-ST-…, U200-…).");
        var zip = files.FirstOrDefault(f => f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        _plant = Row("Plant name (when not in folder / number)", new ComboBox { DropDownStyle = ComboBoxStyle.DropDown });
        _plant.Items.AddRange(knownPlants.Where(p => p.Length > 0).Distinct().Order(NaturalComparer.Instance).Cast<object>().ToArray());
        _plant.Text = zip == null ? "" : System.Text.RegularExpressions.Regex.Replace(Path.GetFileNameWithoutExtension(zip).Replace('_', ' '), @"\s+", " ").Trim();
        Ok.Text = "Unzip && file";
    }
}

sealed class ProgressForm : Form
{
    readonly ProgressBar _bar = new() { Dock = DockStyle.Top, Height = 22 };
    readonly Label _label = new() { Dock = DockStyle.Top, Height = 44, AutoEllipsis = true };
    public readonly CancellationTokenSource Cancellation = new();

    public ProgressForm(string title)
    {
        Text = title;
        Font = new Font("Segoe UI", 9.5f);
        ClientSize = new Size(520, 120);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        ControlBox = false;
        ShowInTaskbar = false;
        Padding = new Padding(12);
        var cancel = new Button { Text = "Cancel", Dock = DockStyle.Bottom, Height = 30 };
        cancel.Click += (_, _) => { Cancellation.Cancel(); cancel.Enabled = false; _label.Text = "Cancelling…"; };
        Controls.AddRange([cancel, _label, _bar]);
    }

    public void Report(ImportProgress p)
    {
        if (IsDisposed) return;
        _bar.Maximum = Math.Max(1, p.Total);
        _bar.Value = Math.Min(p.Done, _bar.Maximum);
        _label.Text = p.Total == 0 ? "Reading archive…" : $"{p.Done} / {p.Total}\n{p.Current}";
    }
}

sealed class EditDrawingForm : FormDialog
{
    readonly TextBox _no, _title, _rev, _kind, _remarks;
    readonly ComboBox _plant, _unit, _type, _status;
    readonly CheckBox _all;
    public bool ApplyToAllRevisions => _all.Checked;

    public EditDrawingForm(Drawing d, int revisionCount, IEnumerable<Drawing> all) : base($"Drawing details – {d.DrawingNo}", 620, 520)
    {
        _no = Row("Drawing no.", new TextBox { Text = d.DrawingNo });
        _title = Row("Title", new TextBox { Text = d.Title });
        _rev = Row("Revision", new TextBox { Text = d.Revision, CharacterCasing = CharacterCasing.Upper });
        _status = Row("Status", new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList });
        foreach (var s in Enum.GetValues<DrawingStatus>()) _status.Items.Add(new Item<DrawingStatus>(s, $"{s.Code()} – {s.Label()}"));
        _status.SelectedIndex = (int)d.Status;
        _type = Row("Drawing type", new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList });
        _type.Items.AddRange(DrawingType.All.Cast<object>().ToArray());
        _type.SelectedItem = d.Type;
        _plant = Row("Plant", new ComboBox { Text = d.Plant });
        _plant.Items.AddRange(all.Select(x => x.Plant).Where(x => x.Length > 0).Distinct().Order(NaturalComparer.Instance).Cast<object>().ToArray());
        _plant.Text = d.Plant;
        _unit = Row("Unit / Area", new ComboBox { Text = d.Unit });
        _unit.Items.AddRange(all.Select(x => x.Unit).Where(x => x.Length > 0).Distinct().Order(NaturalComparer.Instance).Cast<object>().ToArray());
        _unit.Text = d.Unit;
        _kind = Row("Sheet kind", new TextBox { Text = d.Kind });
        _remarks = Row("Remarks", new TextBox { Text = d.Remarks, Multiline = true, Height = 60 });
        _all = new CheckBox
        {
            Text = $"Apply drawing no., plant, unit and type to all {revisionCount} revisions",
            Checked = true, Visible = revisionCount > 1, AutoSize = true,
        };
        Grid.Controls.Add(_all);
        Grid.SetColumnSpan(_all, 2);
        Note($"File: {d.FileName}\nStored at: {d.StoredPath}" + (d.SourcePath.Length > 0 ? $"\nZIP folder: {d.SourcePath}" : ""));
    }

    /// <summary>Fields shared by every revision of the drawing.</summary>
    public void ApplyShared(Drawing x)
    {
        if (_no.Text.Trim().Length > 0) x.DrawingNo = _no.Text.Trim();
        x.Plant = _plant.Text.Trim();
        x.Unit = _unit.Text.Trim();
        x.TypeCode = ((DrawingType)_type.SelectedItem!).Code;
    }

    /// <summary>Fields of this revision only.</summary>
    public void ApplyOwn(Drawing x)
    {
        x.Title = _title.Text.Trim();
        if (_rev.Text.Trim().Length > 0) x.Revision = _rev.Text.Trim();
        x.Status = ((Item<DrawingStatus>)_status.SelectedItem!).Value;
        x.Kind = _kind.Text.Trim();
        x.Remarks = _remarks.Text.Trim();
    }

    sealed record Item<T>(T Value, string Text)
    {
        public override string ToString() => Text;
    }
}
