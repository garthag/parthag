using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SiteNotes.Core;
using SiteNotes.Core.Imaging;
using SiteNotes.Core.Models;
using SiteNotes.Desktop.Services;
using SiteNotes.Desktop.UI;

namespace SiteNotes.Desktop.Views;

/// <summary>
/// Scan to PDF on the PC: add page photos (from a phone, camera, flatbed scanner folder or drag &amp; drop),
/// clean them up (Enhanced / Grayscale / Black &amp; white), rotate, reorder, remarks per page, save as one PDF.
/// Every page is also kept as its own image. Filed in Scans › date automatically.
/// </summary>
public class ScannerView : ViewBase, IPrintable
{
    private sealed class PageVm
    {
        public ScanPageInput Input { get; init; } = new();
        public string Preview { get; set; } = "";
    }

    private readonly string? _docId;
    private string _folderId;
    private ScanDocument _doc = new();
    private readonly ObservableCollection<PageVm> _pages = [];
    private readonly WrapPanel _strip = new();
    private readonly TextBox _title = new() { FontSize = 16, Width = 420 };
    private readonly TextBox _remarks = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 50 };
    private ScanFilter _filter = ScanFilter.Enhanced;
    private readonly string _key;
    private static string RawFolder => Path.Combine(Desk.DataFolder, "scanraw");
    private static string PreviewFolder { get { var d = Path.Combine(Path.GetTempPath(), "SiteNotesScan"); Directory.CreateDirectory(d); return d; } }

    public ScannerView(string? docId, string folderId = "")
    {
        _docId = docId;
        _folderId = folderId;
        _key = "scan:" + (docId ?? Guid.NewGuid().ToString("N"));
        AllowDrop = true;
        Drop += async (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files) await Dialogs.Guard(() => AddAsync(files));
        };
    }

    public override string Title => _docId == null ? "Scan to PDF" : $"Scan: {_doc.Title}";
    public override string Key => _key;

    public override async Task RefreshAsync()
    {
        if (_docId != null && await Desk.Db.GetAsync<ScanDocument>(_docId) is { } d)
        {
            _doc = d;
            _folderId = d.FolderId;
            _filter = d.Filter;
            _pages.Clear();
            foreach (var p in await new ScanService(Desk.Db, RawFolder).LoadPagesAsync(d)) _pages.Add(new PageVm { Input = p, Preview = p.RawPath });
            await PreviewAllAsync();
        }
        else _doc = new ScanDocument { ProjectId = Desk.ProjectId, FolderId = _folderId };
        _title.Text = _doc.Title;
        _remarks.Text = _doc.Remarks;
        Build();
        Render();
        MainWindow.Current.Retitle(this);
    }

    private void Build()
    {
        var tb = W.ToolBar(
            (Glyph.Add, "Add pages…", () => AddAsync(Dialogs.OpenImages(true, "Choose page photos (in order)"))),
            null,
            (Glyph.Save, "Save PDF", SaveAsync),
            (Glyph.Print, "Open PDF", PdfAsync));
        var filter = W.Choice([(ScanFilter.Original, "Original"), (ScanFilter.Enhanced, "Enhanced"), (ScanFilter.Grayscale, "Grayscale"), (ScanFilter.BlackWhite, "Black & white (removes shadows)")],
            _filter, async f => { _filter = f; await Dialogs.Guard(PreviewAllAsync); Render(); });
        var head = W.Card(W.Stack(
            W.Form(2, W.Field("Title", _title), W.Field("Clean-up", filter)),
            W.Field("Remarks", _remarks),
            W.Muted("Add page photos in order (or drag & drop files here). Each photo becomes one page. Saved as one PDF plus each page as an image, filed in Scans › date.")), 10);
        head.Margin = new Thickness(10, 8, 10, 6);
        var dock = new DockPanel();
        DockPanel.SetDock(tb, Dock.Top);
        dock.Children.Add(tb);
        DockPanel.SetDock(head, Dock.Top);
        dock.Children.Add(head);
        dock.Children.Add(W.Scroll(_strip, 10));
        Content = dock;
    }

    private async Task AddAsync(IEnumerable<string> files)
    {
        foreach (var f in files.Where(File.Exists))
        {
            var vm = new PageVm { Input = new ScanPageInput { RawPath = f } };
            _pages.Add(vm);
            await PreviewAsync(vm);
        }
        Render();
    }

    private Task PreviewAsync(PageVm p)
    {
        var dest = Path.Combine(PreviewFolder, Guid.NewGuid().ToString("N") + ".jpg");
        var filter = _filter;
        return Task.Run(() => { if (ScanProcessor.Process(p.Input.RawPath, dest, filter, p.Input.Rotation, 900)) p.Preview = dest; });
    }

    private Task PreviewAllAsync() => Parallel.ForEachAsync(_pages, async (p, _) => await PreviewAsync(p));

    private void Render()
    {
        _strip.Children.Clear();
        if (_pages.Count == 0) _strip.Children.Add(W.Muted("No pages yet. Click “Add pages…” or drop photos here."));
        for (var i = 0; i < _pages.Count; i++)
        {
            var p = _pages[i];
            var idx = i;
            var remarks = new TextBox { Text = p.Input.Remarks, Width = 200, ToolTip = "Remarks for this page" };
            remarks.TextChanged += (_, _) => p.Input.Remarks = remarks.Text;
            var tools = W.Wrap(
                W.Button("⟳", async () => { p.Input.Rotation = (p.Input.Rotation + 90) % 360; await PreviewAsync(p); Render(); }),
                W.Button("◀", () => { if (idx > 0) { _pages.Move(idx, idx - 1); Render(); } }),
                W.Button("▶", () => { if (idx < _pages.Count - 1) { _pages.Move(idx, idx + 1); Render(); } }),
                W.Button("✕", () => { _pages.RemoveAt(idx); Render(); }));
            var card = W.Card(W.Stack(
                W.Text($"Page {i + 1}", 13, true),
                new Border { Width = 200, Height = 260, Background = Theme.Page, Child = new Image { Source = File.Exists(p.Preview) ? DprEditorView.Thumb(p.Preview, 400) : null, Stretch = Stretch.Uniform } },
                tools, remarks), 8);
            _strip.Children.Add(card);
        }
    }

    private async Task SaveAsync()
    {
        if (_pages.Count == 0) { Dialogs.Info("Add at least one page."); return; }
        _doc.ProjectId = Desk.ProjectId;
        _doc.Title = _title.Text.Trim();
        _doc.Remarks = _remarks.Text;
        _doc.FolderId = _folderId;
        var inputs = _pages.Select(p => p.Input).ToList();
        _doc = await new ScanService(Desk.Db, RawFolder).SaveAsync(_doc, inputs, _filter);
        _folderId = _doc.FolderId;
        _title.Text = _doc.Title;
        MainWindow.Current.Retitle(this);
        Desk.Say($"Saved “{_doc.Title}” – {_doc.PageCount} page(s)");
    }

    public async Task PdfAsync()
    {
        if (_doc.PdfFileName == "" || !File.Exists(Desk.Db.MediaPath(_doc.PdfFileName))) await SaveAsync();
        if (_doc.PdfFileName != "") Dialogs.Open(Desk.Db.MediaPath(_doc.PdfFileName));
    }
}
