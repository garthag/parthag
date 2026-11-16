using System.Data;
using BSL.Core.Data;
using BSL.Desktop.Forms;

namespace BSL.Desktop.Controls;

/// <summary>NOTES tab on every record: dated, categorised notes with an "important" flag.</summary>
public sealed class NotesPanel : UserControl
{
    /// <summary>Raised with the new number of notes (the tab title shows it).</summary>
    public event Action<int>? CountChanged;
    readonly DocumentService _docs = new(Program.Db);
    readonly DataGridView _grid = Grid();
    readonly Label _info = new() { AutoSize = true, Padding = new Padding(6, 8, 0, 0), ForeColor = Color.DimGray };
    string _entity = "";
    int _id;

    public NotesPanel()
    {
        Dock = DockStyle.Fill;
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36 };
        bar.Controls.AddRange([Ui.Button("+ Add note", (_, _) => Edit(null), 110), Ui.Button("Edit", (_, _) => Edit(CurrentId), 80),
                               Ui.Button("Delete", (_, _) => Delete(), 80), _info]);
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) Edit(CurrentId); };
        _grid.CellFormatting += (_, e) =>
        {
            if (e.RowIndex >= 0 && _grid.Rows[e.RowIndex].DataBoundItem is DataRowView v && v["IsImportant"] is true)
                e.CellStyle!.ForeColor = Color.DarkRed;
        };
        Controls.Add(_grid);
        Controls.Add(bar);
    }

    int? CurrentId => _grid.CurrentRow?.DataBoundItem is DataRowView v ? (int)v["Id"] : null;

    public int SetRecord(string entity, int id)
    {
        _entity = entity; _id = id;
        var t = _docs.Notes(entity, id);
        _grid.DataSource = t;
        foreach (DataGridViewColumn c in _grid.Columns)
        {
            c.HeaderText = c.DataPropertyName switch
            {
                "NoteDate" => "DATE", "IsImportant" => "IMPORTANT", "NoteText" => "NOTE", "CreatedBy" => "BY",
                "CreatedOn" => "ENTERED ON", _ => c.DataPropertyName.ToUpperInvariant()
            };
            if (c.DataPropertyName == "Id") c.Visible = false;
            if (c.DataPropertyName == "NoteText") { c.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill; c.DefaultCellStyle.WrapMode = DataGridViewTriState.True; }
            if (c.DataPropertyName is "NoteDate") c.DefaultCellStyle.Format = "dd-MM-yyyy";
            if (c.DataPropertyName is "CreatedOn") c.DefaultCellStyle.Format = "dd-MM-yyyy HH:mm";
        }
        _grid.AutoResizeRows(DataGridViewAutoSizeRowsMode.AllCells);
        _info.Text = t.Rows.Count == 0 ? "No notes yet." : "";
        CountChanged?.Invoke(t.Rows.Count);
        return t.Rows.Count;
    }

    void Edit(int? noteId)
    {
        if (_id <= 0) return;
        DataRowView? v = noteId == null ? null : _grid.CurrentRow?.DataBoundItem as DataRowView;
        using var f = new NoteDialog(v?["NoteText"] as string, v?["Category"] as string, v?["IsImportant"] is true);
        if (f.ShowDialog(this) != DialogResult.OK) return;
        Ui.Busy(this, () =>
        {
            if (noteId == null) _docs.AddNote(_entity, _id, f.NoteText, f.Category, f.Important);
            else _docs.UpdateNote(noteId.Value, f.NoteText, f.Category, f.Important);
            SetRecord(_entity, _id);
        });
    }

    void Delete()
    {
        if (CurrentId is not { } id || !Ui.Confirm("Delete this note?")) return;
        Ui.Busy(this, () => { _docs.DeleteNote(id); SetRecord(_entity, _id); });
    }

    internal static DataGridView Grid() => new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, BackgroundColor = SystemColors.Window,
        BorderStyle = BorderStyle.None, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells
    };
}

/// <summary>Add / edit a note.</summary>
public sealed class NoteDialog : StyledForm
{
    readonly TextBox _text = new() { Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical };
    readonly ComboBox _cat = new() { Width = 200, DropDownStyle = ComboBoxStyle.DropDownList };
    readonly CheckBox _imp = new() { Text = "Important", AutoSize = true, Padding = new Padding(12, 4, 0, 0) };
    public string NoteText => _text.Text;
    public string Category => _cat.SelectedItem as string ?? "GENERAL";
    public bool Important => _imp.Checked;

    public NoteDialog(string? text, string? category, bool important)
    {
        Text = text == null ? "Add note" : "Edit note";
        Width = 640; Height = 360;
        _text.Text = text ?? "";
        _cat.Items.AddRange(Program.Repo.Choices("NoteCategory").Cast<object>().ToArray());
        if (_cat.Items.Count == 0) _cat.Items.Add("GENERAL");
        _cat.SelectedItem = category != null && _cat.Items.Contains(category) ? category : _cat.Items[0];
        _imp.Checked = important;
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 38, Padding = new Padding(6) };
        top.Controls.AddRange([new Label { Text = "Category:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, _cat, _imp]);
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(4) };
        var cancel = Ui.Button("Cancel", (_, _) => DialogResult = DialogResult.Cancel);
        bar.Controls.AddRange([cancel, Ui.Button("Save", (_, _) =>
        {
            if (_text.Text.Trim().Length == 0) { Ui.Error("Type the note first."); return; }
            DialogResult = DialogResult.OK;
        })]);
        CancelButton = cancel;
        Controls.Add(_text); Controls.Add(top); Controls.Add(bar);
    }
}

/// <summary>ATTACHMENTS tab on every record: upload, open, save a copy, delete (files are stored in the database).</summary>
public sealed class AttachmentsPanel : UserControl
{
    /// <summary>Raised with the new number of files (the tab title shows it).</summary>
    public event Action<int>? CountChanged;
    readonly DocumentService _docs = new(Program.Db);
    readonly DataGridView _grid = NotesPanel.Grid();
    readonly Label _info = new() { AutoSize = true, Padding = new Padding(6, 8, 0, 0), ForeColor = Color.DimGray };
    string _entity = "";
    int _id;
    Func<string?>? _defaultRevision;

    public AttachmentsPanel()
    {
        Dock = DockStyle.Fill;
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36 };
        bar.Controls.AddRange([Ui.Button("Upload file(s)…", (_, _) => Upload(), 130), Ui.Button("Open", (_, _) => OpenFile(), 80),
                               Ui.Button("Save a copy…", (_, _) => SaveCopy(), 120), Ui.Button("Delete", (_, _) => Delete(), 80), _info]);
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) OpenFile(); };
        AllowDrop = true;
        _grid.AllowDrop = true;
        DragEventHandler enter = (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        DragEventHandler drop = (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] files) UploadFiles(files); };
        DragEnter += enter; DragDrop += drop; _grid.DragEnter += enter; _grid.DragDrop += drop;
        Controls.Add(_grid);
        Controls.Add(bar);
    }

    int? CurrentId => _grid.CurrentRow?.DataBoundItem is DataRowView v ? (int)v["Id"] : null;

    /// <param name="defaultRevision">revision proposed for new uploads (e.g. the drawing's current revision)</param>
    public int SetRecord(string entity, int id, Func<string?>? defaultRevision = null)
    {
        _entity = entity; _id = id; _defaultRevision = defaultRevision;
        var t = _docs.Attachments(entity, id);
        _grid.DataSource = t;
        foreach (DataGridViewColumn c in _grid.Columns)
        {
            c.HeaderText = c.DataPropertyName switch
            {
                "DocType" => "TYPE", "FileName" => "FILE", "FileSizeKb" => "SIZE (KB)", "UploadedBy" => "UPLOADED BY",
                "UploadedOn" => "UPLOADED ON", "Revision" => "REV", _ => c.DataPropertyName.ToUpperInvariant()
            };
            if (c.DataPropertyName == "Id") c.Visible = false;
            if (c.DataPropertyName == "UploadedOn") c.DefaultCellStyle.Format = "dd-MM-yyyy HH:mm";
            if (c.DataPropertyName == "FileSizeKb") { c.DefaultCellStyle.Format = "#,##0"; c.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight; }
        }
        _info.Text = t.Rows.Count == 0 ? "No files yet – upload or drag files here." : "";
        CountChanged?.Invoke(t.Rows.Count);
        return t.Rows.Count;
    }

    void Upload()
    {
        if (_id <= 0) { Ui.Error("Save the record first."); return; }
        using var d = new OpenFileDialog
        {
            Title = "Upload file(s)", Multiselect = true,
            Filter = "Drawings and documents|*.pdf;*.dwg;*.dxf;*.tif;*.tiff;*.jpg;*.jpeg;*.png;*.xlsx;*.xls;*.docx;*.doc;*.zip|All files|*.*"
        };
        if (d.ShowDialog(this) == DialogResult.OK) UploadFiles(d.FileNames);
    }

    void UploadFiles(string[] files)
    {
        if (_id <= 0) { Ui.Error("Save the record first."); return; }
        var type = _entity is "DrawingMaster" or "DrawingRegister" ? "DRAWING" : "DOCUMENT";
        var pick = PickDialog.Show("Upload", $"Document type for {files.Length} file(s):", Program.Repo.Choices("DocType"), type);
        if (pick == null) return;
        Ui.Busy(this, () =>
        {
            var rev = _defaultRevision?.Invoke();
            foreach (var f in files) _docs.Upload(_entity, _id, f, pick, rev);
            SetRecord(_entity, _id, _defaultRevision);
        });
    }

    void OpenFile()
    {
        if (CurrentId is not { } id) return;
        Ui.Busy(this, () => Ui.OpenFile(_docs.Extract(id)));
    }

    void SaveCopy()
    {
        if (CurrentId is not { } id || _grid.CurrentRow?.DataBoundItem is not DataRowView v) return;
        var path = Ui.Save((string)v["FileName"], "All files|*.*");
        if (path == null) return;
        Ui.Busy(this, () =>
        {
            var tmp = _docs.Extract(id);
            File.Copy(tmp, path, true);
        });
    }

    void Delete()
    {
        if (CurrentId is not { } id || !Ui.Confirm("Delete this file from the database?")) return;
        Ui.Busy(this, () => { _docs.DeleteAttachment(id); SetRecord(_entity, _id, _defaultRevision); });
    }
}
