using SiteNotes.App.Services;
using SiteNotes.App.UI;
using SiteNotes.Core.Models;
using SiteNotes.Core.Reports;

namespace SiteNotes.App.Pages;

/// <summary>Folders, notes and scanned documents with breadcrumb navigation, search and sort.</summary>
public class NotesPage : ContentPage
{
    private string _folderId;
    private readonly VerticalStackLayout _body = new() { Spacing = 6, Padding = 12 };
    private readonly Entry _search = new() { Placeholder = "🔍 Search all notes and scans", ClearButtonVisibility = ClearButtonVisibility.WhileEditing };
    private readonly Picker _sort = new() { ItemsSource = new[] { "Last edited", "Title", "Created" }, SelectedIndex = 0 };
    private readonly Label _crumbs = Ui.Label("", 13, true, Ui.Primary);

    public NotesPage() : this("") { }

    public NotesPage(string folderId)
    {
        _folderId = folderId;
        Title = "Notes & scans";
        this.Page();
        ToolbarItems.Add(new ToolbarItem("＋ Note", null, () => _ = Ui.Guard(async () => { if (await AppShell.RequireProjectAsync()) await AppShell.Push(new NoteEditorPage(null, _folderId)); })));
        ToolbarItems.Add(new ToolbarItem("📄 Scan", null, () => _ = Ui.Guard(async () => { if (await AppShell.RequireProjectAsync()) await AppShell.Push(new ScannerPage(null, _folderId)); })));
        ToolbarItems.Add(new ToolbarItem("＋ Folder", null, () => _ = Ui.Guard(NewFolderAsync)));
        _search.TextChanged += async (_, _) => await Ui.Guard(RenderAsync);
        _sort.SelectedIndexChanged += async (_, _) => await Ui.Guard(RenderAsync);
        var bar = new Grid { ColumnDefinitions = [new(GridLength.Star), new(new GridLength(130))], ColumnSpacing = 8, Padding = new Thickness(12, 8, 12, 0) };
        bar.Add(_search, 0);
        bar.Add(_sort, 1);
        var g = new Grid { RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star)] };
        g.Add(bar, 0, 0);
        g.Add(new ContentView { Padding = new Thickness(16, 6, 16, 0), Content = _crumbs }, 0, 1);
        g.Add(new ScrollView { Content = _body }, 0, 2);
        Content = g;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        AppServices.DataChanged += OnData;
        await Ui.Guard(RenderAsync);
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        AppServices.DataChanged -= OnData;
    }

    private async void OnData() => await Ui.Guard(RenderAsync);

    private async Task RenderAsync()
    {
        var pid = AppServices.ProjectId;
        var folders = await AppServices.Db.ListForProjectAsync<Folder>(pid);
        var notes = await AppServices.Db.ListForProjectAsync<Note>(pid);
        var scans = await AppServices.Db.ListForProjectAsync<ScanDocument>(pid);
        var map = folders.ToDictionary(f => f.Id);
        var s = _search.Text?.Trim() ?? "";
        _body.Children.Clear();

        // Breadcrumb
        var path = new List<Folder>();
        for (var id = _folderId; id != "" && map.TryGetValue(id, out var f) && path.Count < 20; id = f.ParentId) path.Insert(0, f);
        _crumbs.Text = "📁 All notes" + string.Concat(path.Select(p => " › " + p.Name));
        if (_folderId != "")
        {
            var up = Ui.Button("⬆ Up", () => { _folderId = path.Count > 1 ? path[^2].Id : ""; return RenderAsync(); }, false);
            up.HorizontalOptions = LayoutOptions.Start;
            _body.Children.Add(up);
        }

        IEnumerable<Note> noteQ;
        IEnumerable<ScanDocument> scanQ;
        if (s.Length > 0)
        {
            noteQ = notes.Where(n => n.Title.Contains(s, StringComparison.OrdinalIgnoreCase) || n.PlainText.Contains(s, StringComparison.OrdinalIgnoreCase) || n.Tags.Contains(s, StringComparison.OrdinalIgnoreCase));
            scanQ = scans.Where(d => d.Title.Contains(s, StringComparison.OrdinalIgnoreCase) || d.Remarks.Contains(s, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            foreach (var f in folders.Where(f => f.ParentId == _folderId).OrderBy(f => f.Name))
            {
                var count = notes.Count(n => n.FolderId == f.Id) + scans.Count(d => d.FolderId == f.Id) + folders.Count(x => x.ParentId == f.Id);
                _body.Children.Add(Row($"{(f.Kind == FolderKind.Scans ? "🗂" : "📁")} {f.Name}", $"{count} item(s)", "", () => { _folderId = f.Id; return RenderAsync(); }, () => FolderMenuAsync(f, folders)));
            }
            noteQ = notes.Where(n => n.FolderId == _folderId);
            scanQ = scans.Where(d => d.FolderId == _folderId);
        }

        noteQ = (_sort.SelectedItem as string) switch
        {
            "Title" => noteQ.OrderBy(n => n.Title),
            "Created" => noteQ.OrderByDescending(n => n.CreatedAtUtc),
            _ => noteQ.OrderByDescending(n => n.UpdatedAtUtc),
        };
        foreach (var n in noteQ.OrderByDescending(n => n.IsPinned))
        {
            var (done, total) = NoteDocument.ChecklistProgress(NoteDocument.Parse(n.ContentJson));
            var preview = n.PlainText.Length > 140 ? n.PlainText[..140] + "…" : n.PlainText;
            var meta = $"{n.UpdatedAtUtc.ToLocalTime():dd-MMM HH:mm}" + (total > 0 ? $" · ☑ {done}/{total}" : "") + (n.Tags != "" ? $" · #{n.Tags.Replace(",", " #")}" : "");
            var note = n;
            _body.Children.Add(Row($"{(n.IsPinned ? "📌" : "📝")} {(n.Title == "" ? "Untitled" : n.Title)}", preview.Replace("\n", " · "), meta,
                () => AppShell.Push(new NoteEditorPage(note.Id, note.FolderId)), () => ItemMenuAsync(note, folders)));
        }
        foreach (var d in scanQ.OrderByDescending(d => d.CreatedAtUtc))
        {
            var doc = d;
            _body.Children.Add(Row($"📄 {d.Title}", d.Remarks, $"{d.PageCount} page(s) · {d.CreatedAtUtc.ToLocalTime():dd-MMM HH:mm}",
                () => AppShell.Push(new ScannerPage(doc.Id, doc.FolderId)), () => ItemMenuAsync(doc, folders)));
        }
        if (_body.Children.Count == 0 || (_folderId != "" && _body.Children.Count == 1))
            _body.Children.Add(Ui.Caption(s.Length > 0 ? "Nothing found." : "Empty. Add a note, a scan or a folder from the toolbar."));
    }

    private static View Row(string title, string subtitle, string meta, Func<Task> open, Func<Task> menu)
    {
        var more = Ui.Tool("⋯", menu, "More", 36);
        var g = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)], ColumnSpacing = 6 };
        var text = new VerticalStackLayout { Spacing = 2, Children = { Ui.Label(title, 15, true) } };
        if (!string.IsNullOrWhiteSpace(subtitle)) text.Children.Add(new Label { Text = subtitle, FontSize = 12, TextColor = Ui.Muted, MaxLines = 2, LineBreakMode = LineBreakMode.TailTruncation });
        if (!string.IsNullOrWhiteSpace(meta)) text.Children.Add(Ui.Label(meta, 11, false, Ui.Primary));
        g.Add(text, 0);
        g.Add(more, 1);
        var card = Ui.Card(g, 10);
        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) => await Ui.Guard(open);
        text.GestureRecognizers.Add(tap);
        return card;
    }

    private async Task NewFolderAsync()
    {
        if (!await AppShell.RequireProjectAsync()) return;
        var name = await DisplayPromptAsync("New folder", "Folder name", "Create", "Cancel", "e.g. Inspections, MOM, Drawings");
        if (string.IsNullOrWhiteSpace(name)) return;
        await AppServices.Db.SaveAsync(new Folder { ProjectId = AppServices.ProjectId, ParentId = _folderId, Name = name.Trim() });
        await RenderAsync();
    }

    private async Task FolderMenuAsync(Folder f, List<Folder> all)
    {
        var c = await DisplayActionSheetAsync(f.Name, "Cancel", "Delete", "Rename", "Move", "Export notes as PDF");
        switch (c)
        {
            case "Rename":
                var n = await DisplayPromptAsync("Rename folder", "", "Save", "Cancel", initialValue: f.Name);
                if (!string.IsNullOrWhiteSpace(n)) { f.Name = n.Trim(); await AppServices.Db.SaveAsync(f); }
                break;
            case "Move":
                if (await PickFolderAsync(all, f.Id) is { } target) { f.ParentId = target; await AppServices.Db.SaveAsync(f); }
                break;
            case "Export notes as PDF":
                foreach (var note in await AppServices.Db.ListForProjectAsync<Note>(AppServices.ProjectId, x => x.FolderId == f.Id))
                    await NoteEditorPage.ExportAsync(note, f.Name, share: false);
                await DisplayAlertAsync("Exported", $"PDFs saved in {FileExport.ReportsFolder}", "OK");
                break;
            case "Delete":
                var hasItems = (await AppServices.Db.ListForProjectAsync<Note>(AppServices.ProjectId, x => x.FolderId == f.Id)).Count > 0 || all.Any(x => x.ParentId == f.Id);
                if (hasItems) { await DisplayAlertAsync("Folder not empty", "Move or delete its notes and sub-folders first.", "OK"); break; }
                if (await DisplayAlertAsync("Delete folder?", f.Name, "Delete", "Cancel")) await AppServices.Db.DeleteAsync(f);
                break;
        }
        await RenderAsync();
    }

    private async Task ItemMenuAsync(SyncEntity item, List<Folder> all)
    {
        var pinText = item is Note { IsPinned: true } ? "Unpin" : "Pin to top";
        var options = item is Note ? new[] { pinText, "Move to folder", "Duplicate", "Export PDF" } : ["Move to folder", "Open PDF", "Share PDF"];
        var c = await DisplayActionSheetAsync(item is Note n0 ? n0.Title : ((ScanDocument)item).Title, "Cancel", "Delete", options);
        switch (c)
        {
            case "Pin to top" or "Unpin" when item is Note n:
                n.IsPinned = !n.IsPinned; await AppServices.Db.SaveAsync(n); break;
            case "Duplicate" when item is Note n:
                await AppServices.Db.SaveAsync(new Note { ProjectId = n.ProjectId, FolderId = n.FolderId, Title = n.Title + " (copy)", ContentJson = n.ContentJson, PlainText = n.PlainText, Tags = n.Tags });
                break;
            case "Export PDF" when item is Note n:
                await NoteEditorPage.ExportAsync(n, all.FirstOrDefault(f => f.Id == n.FolderId)?.Name ?? "Notes", share: true);
                break;
            case "Open PDF" when item is ScanDocument d:
                await FileExport.OpenAsync(AppServices.Db.MediaPath(d.PdfFileName)); break;
            case "Share PDF" when item is ScanDocument d:
                await FileExport.ShareAsync(AppServices.Db.MediaPath(d.PdfFileName)); break;
            case "Move to folder":
                if (await PickFolderAsync(all, null) is { } target)
                {
                    if (item is Note mn) mn.FolderId = target; else if (item is ScanDocument md) md.FolderId = target;
                    await AppServices.Db.SaveAsync(item);
                }
                break;
            case "Delete":
                if (await DisplayAlertAsync("Delete?", "This removes it on every synced device.", "Delete", "Cancel")) await AppServices.Db.DeleteAsync(item);
                break;
        }
        await RenderAsync();
    }

    private async Task<string?> PickFolderAsync(List<Folder> all, string? excludeId)
    {
        var map = all.ToDictionary(f => f.Id);
        string PathOf(Folder f)
        {
            var parts = new List<string>();
            for (var x = f; x != null && parts.Count < 20; x = map.GetValueOrDefault(x.ParentId)) parts.Insert(0, x.Name);
            return string.Join(" › ", parts);
        }
        var options = all.Where(f => f.Id != excludeId).Select(f => (Id: f.Id, Name: PathOf(f))).OrderBy(x => x.Name).ToList();
        options.Insert(0, ("", "(top level)"));
        var labels = options.Select(o => o.Name).ToArray();
        var c = await DisplayActionSheetAsync("Move to", "Cancel", null, labels);
        var i = Array.IndexOf(labels, c);
        return i < 0 ? null : options[i].Id;
    }
}
