using System.Diagnostics;
using DrawingRegister.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DrawingRegister.App;

/// <summary>
/// Drawing viewer. PDFs and images open in the Edge (WebView2) viewer, which
/// gives zoom, pan, rotate, page thumbnails, search and print. CAD files open
/// in the associated program (AutoCAD, DWG TrueView …).
/// </summary>
sealed class ViewerPanel : UserControl
{
    readonly Label _number = new() { Dock = DockStyle.Top, Height = 26, Font = new Font("Segoe UI", 12f, FontStyle.Bold), AutoEllipsis = true };
    readonly Label _info = new() { Dock = DockStyle.Top, Height = 40, AutoEllipsis = true, ForeColor = SystemColors.GrayText };
    readonly Panel _banner = new() { Dock = DockStyle.Top, Height = 36, BackColor = Color.FromArgb(255, 235, 238), Visible = false, Padding = new Padding(8, 4, 8, 4) };
    readonly Label _bannerText = new() { Dock = DockStyle.Fill, ForeColor = Color.FromArgb(183, 28, 28), TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) };
    readonly LinkLabel _openCurrent = new() { Dock = DockStyle.Right, Width = 130, TextAlign = ContentAlignment.MiddleRight };
    readonly ComboBox _revisions = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    readonly Panel _body = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(224, 224, 224) };
    readonly Label _message = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI", 10f) };
    readonly Button _openExternal = new() { Text = "Open in default program", AutoSize = true };
    readonly PictureBox _picture = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, Visible = false };
    WebView2? _web;
    Task<bool>? _webInit;
    Drawing? _shown;
    List<Drawing> _revs = [];
    bool _updatingRevs;

    public DrawingLibrary Library { get; set; } = null!;

    /// <summary>Raised when the user picks another revision (or "open current").</summary>
    public event Action<Drawing>? RevisionSelected;

    public ViewerPanel()
    {
        Padding = new Padding(8);
        var tools = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34, WrapContents = false };
        var folder = new Button { Text = "Show in folder", AutoSize = true };
        tools.Controls.AddRange([new Label { Text = "Revision:", AutoSize = true, Padding = new Padding(0, 7, 0, 0) }, _revisions, _openExternal, folder]);
        _banner.Controls.AddRange([_bannerText, _openCurrent]);
        _body.Controls.AddRange([_message, _picture]);
        Controls.AddRange([_body, _banner, tools, _info, _number]);

        _revisions.SelectedIndexChanged += (_, _) =>
        {
            if (!_updatingRevs && _revisions.SelectedIndex >= 0) RevisionSelected?.Invoke(_revs[_revisions.SelectedIndex]);
        };
        _openCurrent.LinkClicked += (_, _) => { if (_revs.Count > 0) RevisionSelected?.Invoke(_revs[0]); };
        _openExternal.Click += (_, _) => OpenExternal(_shown);
        folder.Click += (_, _) => ShowInFolder(_shown);
        Display(null, []);
    }

    public void OpenExternal(Drawing? d)
    {
        if (d == null) return;
        if (!Library.Exists(d)) { Ui.Info("The file is missing from the library folder."); return; }
        try { Process.Start(new ProcessStartInfo(Library.FullPath(d)) { UseShellExecute = true }); }
        catch (Exception e) { Ui.Error($"No program is set up to open {Path.GetExtension(d.FileName)} files.", e); }
    }

    public void ShowInFolder(Drawing? d)
    {
        if (d == null || !Library.Exists(d)) return;
        Process.Start("explorer.exe", $"/select,\"{Library.FullPath(d)}\"");
    }

    /// <param name="revisions">All revisions of the drawing, newest first.</param>
    public async void Display(Drawing? d, List<Drawing> revisions)
    {
        _shown = d;
        _revs = revisions;
        _updatingRevs = true;
        _revisions.Items.Clear();
        foreach (var (r, i) in revisions.Select((r, i) => (r, i)))
            _revisions.Items.Add($"{Revisions.Label(r.Revision)}  {r.Status.Code()}  {(i == 0 ? "(current)" : "(superseded)")}  {r.ImportedOn:dd-MM-yyyy}");
        _revisions.SelectedIndex = d == null ? -1 : revisions.FindIndex(r => r.Id == d.Id);
        _updatingRevs = false;
        _revisions.Enabled = _openExternal.Enabled = d != null;

        if (d == null)
        {
            _number.Text = "";
            _info.Text = "";
            _banner.Visible = false;
            ShowMessage("Select a drawing to view it.\n\nImport a drawing ZIP with the toolbar, or drag a ZIP onto this window.");
            return;
        }

        _number.Text = $"{d.DrawingNo}   {Revisions.Label(d.Revision)}";
        _info.Text = $"{d.DisplayTitle}\n{Register.PlantLabel(d.Plant)} › {Register.UnitLabel(d.Unit)} › {d.Type.Label}   ·   " +
                     $"{d.Status.Label()}   ·   {d.FileName} {Ui.Size(d.SizeBytes)}";
        var current = revisions.Count > 0 ? revisions[0] : d;
        _banner.Visible = current.Id != d.Id;
        _bannerText.Text = $"SUPERSEDED – Rev {d.Revision} is not the latest. Rev {current.Revision} ({current.Status.Code()}) is current. Do not use for construction.";
        _openCurrent.Text = $"Open Rev {current.Revision}";

        if (!Library.Exists(d))
        {
            ShowMessage($"The file is missing from the library folder:\n{Library.FullPath(d)}");
            return;
        }
        var path = Library.FullPath(d);
        switch (d.Format)
        {
            case DrawingFormat.Pdf:
            case DrawingFormat.Image:
                var webReady = await EnsureWeb();
                if (!ReferenceEquals(_shown, d)) return; // another drawing was selected meanwhile
                if (webReady)
                {
                    _message.Visible = _picture.Visible = false;
                    _web!.Visible = true;
                    _web.CoreWebView2.Navigate(new Uri(path).AbsoluteUri);
                }
                else if (d.Format == DrawingFormat.Image) ShowPicture(path);
                else ShowMessage("The PDF viewer needs the Microsoft Edge WebView2 Runtime (included in Windows 10/11 updates).\n" +
                                 "Click \"Open in default program\" to view this drawing.");
                break;
            default:
                ShowMessage($"{Path.GetExtension(d.FileName).TrimStart('.').ToUpperInvariant()} files open in their own program " +
                            "(AutoCAD, DWG TrueView, …).\nClick \"Open in default program\" or double-click the drawing.");
                break;
        }
    }

    void ShowMessage(string text)
    {
        if (_web != null) _web.Visible = false;
        _picture.Visible = false;
        _picture.Image?.Dispose();
        _picture.Image = null;
        _message.Text = text;
        _message.Visible = true;
    }

    void ShowPicture(string path)
    {
        ShowMessage("");
        _message.Visible = false;
        try
        {
            using var fs = File.OpenRead(path);
            _picture.Image = Image.FromStream(fs);
            _picture.Visible = true;
        }
        catch (Exception e) when (e is ArgumentException or IOException or OutOfMemoryException)
        {
            ShowMessage($"This image cannot be shown here ({e.Message}).");
        }
    }

    /// <summary>Initialises the Edge viewer once; later calls share the same task.</summary>
    Task<bool> EnsureWeb() => _webInit ??= InitWeb();

    async Task<bool> InitWeb()
    {
        try
        {
            _web ??= new WebView2 { Dock = DockStyle.Fill, Visible = false };
            if (!_body.Controls.Contains(_web)) _body.Controls.Add(_web);
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(AppSettings.Folder, "WebView2"));
            await _web.EnsureCoreWebView2Async(env);
            _web.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _web.CoreWebView2.Settings.IsStatusBarEnabled = false;
            return true;
        }
        catch (Exception e) when (e is WebView2RuntimeNotFoundException or InvalidOperationException or ArgumentException
                                       or System.Runtime.InteropServices.COMException)
        {
            return false;
        }
    }
}
