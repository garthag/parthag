using Microsoft.Maui.Controls.Shapes;
using SiteNotes.App.Services;
using SiteNotes.App.UI;
using SiteNotes.Core.Imaging;
using SiteNotes.Core.Models;
using SiteNotes.Core.Reports;

namespace SiteNotes.App.Pages;

/// <summary>
/// Multi-page document scanner. Capture pages one after another (each photo becomes its own page),
/// clean them up (Enhanced / Grayscale / Black &amp; white), rotate, reorder, add remarks per page,
/// then save: every page is stored as a separate image and combined into one PDF, filed automatically
/// in Scans › yyyy-MM-dd unless you choose a folder.
/// </summary>
public class ScannerPage : ContentPage
{
    private sealed class PageItem
    {
        public string RawPath = "";
        public int Rotation;
        public string Remarks = "";
        public string Preview = "";
        public string? MediaId;
    }

    private readonly string? _docId;
    private string _folderId;
    private readonly Action<ScanDocument>? _onSaved;
    private ScanDocument _doc = new();
    private bool _notified;
    private readonly List<PageItem> _pages = [];
    private readonly VerticalStackLayout _list = new() { Spacing = 8 };
    private readonly Entry _title = new() { Placeholder = "Document title, e.g. Delivery challan – RMC", FontSize = 17 };
    private readonly Editor _remarks = new() { Placeholder = "Remarks", AutoSize = EditorAutoSizeOption.TextChanges };
    private readonly Picker _filter = new() { ItemsSource = new[] { "Original", "Enhanced", "Grayscale", "BlackWhite" }, SelectedIndex = 1 };
    private readonly Label _folderLabel = Ui.Caption("");
    private readonly ActivityIndicator _busy = new() { IsRunning = false, IsVisible = false, Color = Ui.Primary };
    private static string RawFolder { get { var d = System.IO.Path.Combine(FileSystem.AppDataDirectory, "scanraw"); Directory.CreateDirectory(d); return d; } }

    public ScannerPage(string? docId, string folderId, Action<ScanDocument>? onSaved = null)
    {
        _docId = docId;
        _folderId = folderId;
        _onSaved = onSaved;
        Title = docId == null ? "Scan document" : "Scanned document";
        this.Page();
        ToolbarItems.Add(new ToolbarItem("💾 Save PDF", null, () => _ = Ui.Guard(() => SaveAsync())));
        ToolbarItems.Add(new ToolbarItem("Share PDF", null, () => _ = Ui.Guard(async () => { if (await SaveAsync(silent: true)) await FileExport.ShareAsync(AppServices.Db.MediaPath(_doc.PdfFileName)); })) { Order = ToolbarItemOrder.Secondary });
        ToolbarItems.Add(new ToolbarItem("Open PDF", null, () => _ = Ui.Guard(async () => { if (await SaveAsync(silent: true)) await FileExport.OpenAsync(AppServices.Db.MediaPath(_doc.PdfFileName)); })) { Order = ToolbarItemOrder.Secondary });
        _filter.SelectedIndexChanged += async (_, _) => await Ui.Guard(RefreshPreviewsAsync);

        Content = Ui.Scroll(new VerticalStackLayout
        {
            Spacing = 10,
            Children =
            {
                _title,
                Ui.Columns(3,
                    Ui.Button("📷 Capture pages", CaptureLoopAsync),
                    Ui.Button("🖼 Import photos", ImportAsync, false),
                    Ui.Field("Clean-up filter", _filter)),
                Ui.Columns(2, _folderLabel, Ui.Button("📁 Change folder", ChooseFolderAsync, false)),
                _busy,
                _list,
                Ui.Field("Remarks", _remarks),
                Ui.Button("💾 Save as PDF", async () => { await SaveAsync(); }),
            }
        });
    }

    private bool _loaded;

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_loaded) return;
        _loaded = true;
        if (_docId != null && await AppServices.Db.GetAsync<ScanDocument>(_docId) is { } d)
        {
            _doc = d;
            _folderId = d.FolderId;
            _title.Text = d.Title;
            _remarks.Text = d.Remarks;
            _filter.SelectedItem = d.Filter.ToString();
            foreach (var m in (await AppServices.Db.ListAsync<MediaItem>(x => x.OwnerId == d.Id)).OrderBy(x => x.PageNo))
            {
                var raw = System.IO.Path.Combine(RawFolder, m.Id + ".jpg");
                _pages.Add(new PageItem { MediaId = m.Id, Remarks = m.Remarks, RawPath = File.Exists(raw) ? raw : AppServices.Db.MediaPath(m.FileName), Preview = AppServices.Db.MediaPath(m.FileName) });
            }
        }
        else
        {
            _doc = new ScanDocument { ProjectId = AppServices.ProjectId, Title = $"Scan {DateTime.Now:dd-MMM-yyyy HH:mm}" };
            _title.Text = "";
        }
        await UpdateFolderLabelAsync();
        Render();
        if (_docId == null) await CaptureLoopAsync();
    }

    private ScanFilter Filter => Enum.TryParse<ScanFilter>(_filter.SelectedItem as string, out var f) ? f : ScanFilter.Enhanced;

    private async Task CaptureLoopAsync()
    {
        while (true)
        {
            var raw = await MediaService.CaptureRawAsync();
            if (raw == null) break;
            await AddPageAsync(raw);
            var next = await DisplayActionSheetAsync($"Page {_pages.Count} added", "Done", null, "📷 Next page");
            if (next != "📷 Next page") break;
        }
    }

    private async Task ImportAsync()
    {
        foreach (var raw in await MediaService.PickRawPhotosAsync()) await AddPageAsync(raw);
    }

    private async Task AddPageAsync(string raw)
    {
        var p = new PageItem { RawPath = raw };
        _pages.Add(p);
        await MakePreviewAsync(p);
        Render();
    }

    private async Task MakePreviewAsync(PageItem p)
    {
        _busy.IsVisible = _busy.IsRunning = true;
        var dest = System.IO.Path.Combine(FileSystem.CacheDirectory, $"scanprev_{Guid.NewGuid():N}.jpg");
        var filter = Filter;
        var ok = await Task.Run(() => ScanProcessor.Process(p.RawPath, dest, filter, p.Rotation, 1000));
        if (ok) p.Preview = dest;
        _busy.IsVisible = _busy.IsRunning = false;
    }

    private async Task RefreshPreviewsAsync()
    {
        // Pages are independent, so they are processed in parallel on all CPU cores.
        _busy.IsVisible = _busy.IsRunning = true;
        var filter = Filter;
        await Parallel.ForEachAsync(_pages, async (p, _) =>
        {
            var dest = System.IO.Path.Combine(FileSystem.CacheDirectory, $"scanprev_{Guid.NewGuid():N}.jpg");
            if (await Task.Run(() => ScanProcessor.Process(p.RawPath, dest, filter, p.Rotation, 1000))) p.Preview = dest;
        });
        _busy.IsVisible = _busy.IsRunning = false;
        Render();
    }

    private void Render()
    {
        _list.Children.Clear();
        if (_pages.Count == 0) _list.Children.Add(Ui.Caption("No pages yet. Tap 📷 Capture pages – every photo becomes a separate page."));
        for (var i = 0; i < _pages.Count; i++)
        {
            var p = _pages[i];
            var index = i;
            var remarks = new Entry { Text = p.Remarks, Placeholder = $"Remarks for page {i + 1}", FontSize = 13 };
            remarks.TextChanged += (_, a) => p.Remarks = a.NewTextValue ?? "";
            var tools = new HorizontalStackLayout
            {
                Spacing = 4,
                Children =
                {
                    Ui.Tool("⟳", async () => { p.Rotation = (p.Rotation + 90) % 360; await MakePreviewAsync(p); Render(); }, "Rotate"),
                    Ui.Tool("▲", () => { if (index > 0) { (_pages[index - 1], _pages[index]) = (_pages[index], _pages[index - 1]); Render(); } }, "Move up"),
                    Ui.Tool("▼", () => { if (index < _pages.Count - 1) { (_pages[index + 1], _pages[index]) = (_pages[index], _pages[index + 1]); Render(); } }, "Move down"),
                    Ui.Tool("🗑", () => { _pages.RemoveAt(index); Render(); }, "Delete page"),
                }
            };
            var g = new Grid { ColumnDefinitions = [new(new GridLength(110)), new(GridLength.Star)], ColumnSpacing = 10 };
            g.Add(new Border
            {
                StrokeThickness = 1, Stroke = Ui.Muted, StrokeShape = new RoundRectangle { CornerRadius = 4 }, HeightRequest = 150,
                Content = new Image { Source = p.Preview == "" ? null : ImageSource.FromFile(p.Preview), Aspect = Aspect.AspectFit },
            }, 0);
            g.Add(new VerticalStackLayout { Spacing = 6, Children = { Ui.Label($"Page {i + 1}", 15, true), tools, remarks } }, 1);
            _list.Children.Add(Ui.Card(g, 8));
        }
    }

    private async Task UpdateFolderLabelAsync()
    {
        var f = _folderId == "" ? null : await AppServices.Db.GetAsync<Folder>(_folderId);
        _folderLabel.Text = f == null ? $"Folder: Scans › {DateTime.Today:yyyy-MM-dd} (automatic)" : $"Folder: {f.Name}";
    }

    private async Task ChooseFolderAsync()
    {
        var folders = (await AppServices.Db.ListForProjectAsync<Folder>(AppServices.ProjectId)).OrderBy(f => f.Name).ToList();
        var labels = new[] { "Automatic (Scans › date)" }.Concat(folders.Select(f => f.Name)).ToArray();
        var c = await DisplayActionSheetAsync("Save in folder", "Cancel", null, labels);
        if (c == null || c == "Cancel") return;
        _folderId = folders.FirstOrDefault(f => f.Name == c)?.Id ?? "";
        await UpdateFolderLabelAsync();
    }

    /// <summary>Finds or creates Scans › yyyy-MM-dd.</summary>
    private static async Task<string> AutoFolderAsync()
    {
        var pid = AppServices.ProjectId;
        var all = await AppServices.Db.ListForProjectAsync<Folder>(pid);
        var root = all.FirstOrDefault(f => f.ParentId == "" && f.Kind == FolderKind.Scans && f.Name == "Scans");
        if (root == null)
        {
            root = new Folder { ProjectId = pid, Name = "Scans", Kind = FolderKind.Scans, Color = "#1F4E79" };
            await AppServices.Db.SaveAsync(root);
        }
        var name = DateTime.Today.ToString("yyyy-MM-dd");
        var day = all.FirstOrDefault(f => f.ParentId == root.Id && f.Name == name);
        if (day == null)
        {
            day = new Folder { ProjectId = pid, ParentId = root.Id, Name = name, Kind = FolderKind.Scans };
            await AppServices.Db.SaveAsync(day);
        }
        return day.Id;
    }

    private async Task<bool> SaveAsync(bool silent = false)
    {
        if (_pages.Count == 0) { await DisplayAlertAsync("No pages", "Capture or import at least one page.", "OK"); return false; }
        if (!await AppShell.RequireProjectAsync()) return false;
        _busy.IsVisible = _busy.IsRunning = true;
        try
        {
            if (_folderId == "") _folderId = await AutoFolderAsync();
            _doc.FolderId = _folderId;
            _doc.ProjectId = AppServices.ProjectId;
            _doc.Title = string.IsNullOrWhiteSpace(_title.Text) ? _doc.Title : _title.Text.Trim();
            _doc.Remarks = _remarks.Text ?? "";
            _doc.Filter = Filter;
            _doc.PageCount = _pages.Count;

            var oldMedia = await AppServices.Db.ListAsync<MediaItem>(x => x.OwnerId == _doc.Id);
            var filter = Filter;
            var media = new MediaItem[_pages.Count];
            // Process every page in parallel (multi-core); each page is its own JPEG file.
            await Parallel.ForAsync(0, _pages.Count, async (i, _) =>
            {
                var p = _pages[i];
                var existing = oldMedia.FirstOrDefault(m => m.Id == p.MediaId);
                var item = existing ?? new MediaItem();
                var name = $"scan_{_doc.Id}_{Guid.NewGuid():N}.jpg";
                var path = AppServices.Db.MediaPath(name);
                await Task.Run(() => ScanProcessor.Process(p.RawPath, path, filter, p.Rotation, 2200));
                var raw = System.IO.Path.Combine(RawFolder, item.Id + ".jpg");
                if (p.RawPath != raw) File.Copy(p.RawPath, raw, true); // keep the original so the filter can be changed later
                item.ProjectId = _doc.ProjectId;
                item.OwnerType = MediaOwner.Scan;
                item.OwnerId = _doc.Id;
                item.Kind = MediaKind.ScanPage;
                item.FileName = name;
                item.PageNo = i + 1;
                item.Remarks = p.Remarks;
                item.Caption = $"{_doc.Title} – page {i + 1}";
                item.FileSize = new FileInfo(path).Length;
                item.IsUploaded = false;
                p.MediaId = item.Id;
                p.RawPath = raw;
                media[i] = item;
            });
            await AppServices.Db.SaveAllAsync(media);
            foreach (var gone in oldMedia.Where(o => media.All(m => m.Id != o.Id))) await AppServices.Db.DeleteAsync(gone);

            _doc.PdfFileName = $"scan_{_doc.Id}.pdf";
            var pdfPath = AppServices.Db.MediaPath(_doc.PdfFileName);
            var pagePaths = media.Select(m => AppServices.Db.MediaPath(m.FileName)).ToList();
            await Task.Run(() =>
            {
                using var fs = File.Create(pdfPath);
                PdfReports.ScanPages(fs, _doc.Title, pagePaths);
            });
            await AppServices.Db.SaveAsync(_doc);
            if (!_notified) { _notified = true; _onSaved?.Invoke(_doc); }
            if (!silent)
            {
                var c = await DisplayActionSheetAsync($"Saved {_pages.Count} page(s) as PDF", "Done", null, "Open PDF", "Share PDF");
                if (c == "Open PDF") await FileExport.OpenAsync(pdfPath);
                else if (c == "Share PDF") await FileExport.ShareAsync(pdfPath);
                if (_onSaved != null) await Navigation.PopAsync();
            }
            return true;
        }
        finally { _busy.IsVisible = _busy.IsRunning = false; }
    }
}
