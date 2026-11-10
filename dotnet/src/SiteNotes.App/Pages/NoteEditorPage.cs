using Microsoft.Maui.Controls.Shapes;
using SiteNotes.App.Services;
using SiteNotes.App.UI;
using SiteNotes.Core.Models;
using SiteNotes.Core.Reports;

namespace SiteNotes.App.Pages;

/// <summary>
/// Block-based rich note editor: headings, sub-headings, bullets, numbering, arrows, checklists
/// (checked items are struck through), quotes, dividers, photos, videos and scans, with per-block
/// font family, size, bold / italic / underline / strike, colour, highlight, alignment and indent.
/// Press Enter to start the next line of the same kind (Enter on an empty list item ends the list).
/// </summary>
public class NoteEditorPage : ContentPage
{
    private Note _note = new();
    private List<NoteBlock> _blocks = [];
    private readonly string? _noteId;
    private readonly string _folderId;
    private readonly VerticalStackLayout _host = new() { Spacing = 2, Padding = new Thickness(14, 8, 14, 120) };
    private readonly Entry _title = new() { Placeholder = "Title", FontSize = 22, FontAttributes = FontAttributes.Bold };
    private readonly Entry _tags = new() { Placeholder = "Tags (comma separated)", FontSize = 13 };
    private readonly Label _status = Ui.Caption("");
    private BlockView? _selected;
    private bool _dirty;
    private bool _loaded;

    public NoteEditorPage(string? noteId, string folderId)
    {
        _noteId = noteId;
        _folderId = folderId;
        Title = "Note";
        this.Page();
        ToolbarItems.Add(new ToolbarItem("💾 Save", null, () => _ = Ui.Guard(() => SaveAsync(true))));
        ToolbarItems.Add(new ToolbarItem("PDF", null, () => _ = Ui.Guard(async () => { await SaveAsync(false); await ExportAsync(_note, "Notes", share: true); })));
        ToolbarItems.Add(new ToolbarItem("Share text", null, () => _ = Ui.Guard(() => Share.Default.RequestAsync(new ShareTextRequest(_title.Text + "\n\n" + NoteDocument.ToPlainText(_blocks), _title.Text)))) { Order = ToolbarItemOrder.Secondary });
        ToolbarItems.Add(new ToolbarItem("Delete note", null, () => _ = Ui.Guard(DeleteAsync)) { Order = ToolbarItemOrder.Secondary });
        _title.TextChanged += (_, _) => _dirty = true;
        _tags.TextChanged += (_, _) => _dirty = true;

        var g = new Grid { RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star)] };
        var head = new VerticalStackLayout { Padding = new Thickness(14, 8, 14, 0), Spacing = 0, Children = { _title, _tags, _status } };
        g.Add(head, 0, 0);
        g.Add(BuildToolbox(), 0, 1);
        var scroll = new ScrollView { Content = _host };
        var tapEmpty = new TapGestureRecognizer();
        tapEmpty.Tapped += (_, _) => { if (_blocks.Count == 0 || _blocks[^1].IsMedia || _blocks[^1].Type == BlockType.Divider) InsertAfter(_selected, new NoteBlock()); };
        _host.GestureRecognizers.Add(tapEmpty);
        g.Add(scroll, 0, 2);
        Content = g;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_loaded) return;
        _loaded = true;
        if (_noteId != null && await AppServices.Db.GetAsync<Note>(_noteId) is { } n) _note = n;
        else _note = new Note { ProjectId = AppServices.ProjectId, FolderId = _folderId };
        _blocks = NoteDocument.Parse(_note.ContentJson);
        if (_blocks.Count == 0) _blocks.Add(new NoteBlock());
        _title.Text = _note.Title;
        _tags.Text = _note.Tags;
        _status.Text = _noteId == null ? "New note" : $"Last edited {_note.UpdatedAtUtc.ToLocalTime():dd-MMM-yyyy HH:mm}" + (_note.UpdatedBy != "" ? $" by {_note.UpdatedBy}" : "");
        Render(_blocks[0]); // first line selected so the toolbox works straight away
        _dirty = false;
        if (_noteId == null) _title.Focus();
    }

    protected override async void OnDisappearing()
    {
        base.OnDisappearing();
        if (_dirty) await SaveAsync(false);
    }

    // ------------------------------------------------------------------ toolbox

    private View BuildToolbox()
    {
        View Group(string name, params View[] tools)
        {
            var row = new HorizontalStackLayout { Spacing = 3 };
            foreach (var t in tools) row.Children.Add(t);
            return new VerticalStackLayout
            {
                Spacing = 1, Margin = new Thickness(0, 0, 10, 0),
                Children = { row, new Label { Text = name, FontSize = 9, TextColor = Ui.Muted, HorizontalOptions = LayoutOptions.Center } }
            };
        }

        var strip = new HorizontalStackLayout
        {
            Padding = new Thickness(10, 6), Spacing = 0,
            Children =
            {
                Group("Paragraph",
                    Ui.Tool("¶", () => SetType(BlockType.Paragraph), "Normal text"),
                    Ui.Tool("H1", () => SetType(BlockType.Heading1), "Heading"),
                    Ui.Tool("H2", () => SetType(BlockType.Heading2), "Sub-heading"),
                    Ui.Tool("H3", () => SetType(BlockType.Heading3), "Small heading"),
                    Ui.Tool("❝", () => SetType(BlockType.Quote), "Quote / instruction")),
                Group("Lists",
                    Ui.Tool("•", () => SetType(BlockType.Bullet), "Bullet list"),
                    Ui.Tool("1.", () => SetType(BlockType.Numbered), "Numbered list"),
                    Ui.Tool("➜", () => SetType(BlockType.Arrow), "Arrow list"),
                    Ui.Tool("☑", () => SetType(BlockType.Checklist), "Checklist"),
                    Ui.Tool("⇤", () => Indent(-1), "Decrease indent"),
                    Ui.Tool("⇥", () => Indent(1), "Increase indent")),
                Group("Font",
                    Ui.Tool("B", () => ApplyStyle(s => s.Bold = !s.Bold), "Bold"),
                    Ui.Tool("I", () => ApplyStyle(s => s.Italic = !s.Italic), "Italic"),
                    Ui.Tool("U", () => ApplyStyle(s => s.Underline = !s.Underline), "Underline"),
                    Ui.Tool("S̶", () => ApplyStyle(s => s.Strikethrough = !s.Strikethrough), "Strikethrough"),
                    Ui.Tool("Aa", PickFontAsync, "Font style", 44),
                    Ui.Tool("A−", () => ApplyStyle(s => s.FontSize = Step(s, -1)), "Smaller", 40),
                    Ui.Tool("A+", () => ApplyStyle(s => s.FontSize = Step(s, +1)), "Bigger", 40),
                    Ui.Tool("🎨", PickColorAsync, "Text colour"),
                    Ui.Tool("🖍", PickHighlightAsync, "Highlight")),
                Group("Align",
                    Ui.Tool("⯇", () => ApplyStyle(s => s.Align = TextAlign.Left), "Left"),
                    Ui.Tool("≡", () => ApplyStyle(s => s.Align = TextAlign.Center), "Centre"),
                    Ui.Tool("⯈", () => ApplyStyle(s => s.Align = TextAlign.Right), "Right")),
                Group("Insert",
                    Ui.Tool("📷", () => InsertMediaAsync("camera"), "Take photo"),
                    Ui.Tool("🖼", () => InsertMediaAsync("gallery"), "Photos from gallery"),
                    Ui.Tool("🎥", () => InsertMediaAsync("video"), "Record video"),
                    Ui.Tool("📄", InsertScanAsync, "Scan document (multi-page PDF)"),
                    Ui.Tool("―", () => InsertAfter(_selected, new NoteBlock { Type = BlockType.Divider }), "Divider line"),
                    Ui.Tool("＋", () => InsertAfter(_selected, new NoteBlock()), "New line")),
                Group("Block",
                    Ui.Tool("▲", () => Move(-1), "Move up"),
                    Ui.Tool("▼", () => Move(1), "Move down"),
                    Ui.Tool("⧉", Duplicate, "Duplicate"),
                    Ui.Tool("🗑", Remove, "Delete block")),
            }
        };
        var bg = new ScrollView { Orientation = ScrollOrientation.Horizontal, Content = strip, HorizontalScrollBarVisibility = ScrollBarVisibility.Never };
        bg.SetAppThemeColor(BackgroundColorProperty, Color.FromArgb("#F8FAFC"), Color.FromArgb("#181C21"));
        return bg;
    }

    private static double Step(TextStyle s, int dir)
    {
        var sizes = FontCatalog.Sizes;
        var current = s.FontSize > 0 ? s.FontSize : 15;
        var i = Array.FindIndex(sizes, x => x >= current);
        if (i < 0) i = sizes.Length - 1;
        return sizes[Math.Clamp(i + dir, 0, sizes.Length - 1)];
    }

    private NoteBlock? Current => _selected?.Block;

    private void SetType(BlockType t)
    {
        if (Current is not { IsMedia: false } b || b.Type == BlockType.Divider) { InsertAfter(_selected, new NoteBlock { Type = t }); return; }
        b.Type = b.Type == t && t != BlockType.Paragraph ? BlockType.Paragraph : t; // tapping again toggles off
        if (b.Type is BlockType.Heading1 or BlockType.Heading2 or BlockType.Heading3) b.Style.FontSize = 0;
        Changed(refreshAll: true);
    }

    private void ApplyStyle(Action<TextStyle> change)
    {
        if (Current is not { } b) return;
        change(b.Style);
        _selected!.Apply();
        Changed();
    }

    private void Indent(int d)
    {
        if (Current is not { } b) return;
        b.Indent = Math.Clamp(b.Indent + d, 0, 6);
        Changed(refreshAll: true);
    }

    private async Task PickFontAsync()
    {
        if (Current == null) return;
        var c = await DisplayActionSheetAsync("Font style", "Cancel", null, FontCatalog.Names);
        if (c != null && FontCatalog.Names.Contains(c)) ApplyStyle(s => s.FontFamily = c);
        var size = await DisplayActionSheetAsync("Font size", "Keep", null, ["Auto", .. FontCatalog.Sizes.Select(x => x.ToString())]);
        if (size == "Auto") ApplyStyle(s => s.FontSize = 0);
        else if (double.TryParse(size, out var v)) ApplyStyle(s => s.FontSize = v);
    }

    private async Task PickColorAsync()
    {
        if (Current == null) return;
        var c = await DisplayActionSheetAsync("Text colour", "Cancel", null, FontCatalog.Colors.Select(x => x.Name).ToArray());
        var hit = FontCatalog.Colors.FirstOrDefault(x => x.Name == c);
        if (hit.Name != null) ApplyStyle(s => s.Color = hit.Hex);
    }

    private async Task PickHighlightAsync()
    {
        if (Current == null) return;
        var c = await DisplayActionSheetAsync("Highlight", "Cancel", null, FontCatalog.Highlights.Select(x => x.Name).ToArray());
        var hit = FontCatalog.Highlights.FirstOrDefault(x => x.Name == c);
        if (hit.Name != null) ApplyStyle(s => s.Highlight = hit.Hex);
    }

    private void Move(int dir)
    {
        if (Current is not { } b) return;
        var i = _blocks.IndexOf(b);
        var j = i + dir;
        if (j < 0 || j >= _blocks.Count) return;
        (_blocks[i], _blocks[j]) = (_blocks[j], _blocks[i]);
        Render(b);
        Changed(refreshAll: true);
    }

    private void Duplicate()
    {
        if (Current is not { } b) return;
        InsertAfter(_selected, new NoteBlock { Type = b.Type, Text = b.Text, Indent = b.Indent, Style = b.Style.Clone(), MediaId = b.MediaId });
    }

    private void Remove()
    {
        if (Current is not { } b) return;
        var i = _blocks.IndexOf(b);
        _blocks.Remove(b);
        if (_blocks.Count == 0) _blocks.Add(new NoteBlock());
        Render(_blocks[Math.Clamp(i - 1, 0, _blocks.Count - 1)]);
        Changed();
    }

    private async Task InsertMediaAsync(string source)
    {
        if (!await AppShell.RequireProjectAsync()) return;
        var items = source switch
        {
            "camera" => await MediaService.CapturePhotoAsync(MediaOwner.Note, _note.Id) is { } m ? [m] : [],
            "video" => await MediaService.CaptureVideoAsync(MediaOwner.Note, _note.Id) is { } v ? [v] : [],
            _ => await MediaService.PickPhotosAsync(MediaOwner.Note, _note.Id),
        };
        var after = _selected;
        foreach (var m in items)
        {
            var nb = new NoteBlock { Type = m.Kind == MediaKind.Video ? BlockType.Video : BlockType.Image, MediaId = m.Id };
            after = InsertAfter(after, nb, focus: false);
        }
        if (items.Count > 0) InsertAfter(after, new NoteBlock());
    }

    private async Task InsertScanAsync()
    {
        if (!await AppShell.RequireProjectAsync()) return;
        var anchor = _selected;
        await AppShell.Push(new ScannerPage(null, _note.FolderId, doc =>
        {
            var v = InsertAfter(anchor, new NoteBlock { Type = BlockType.Scan, MediaId = doc.Id, Text = doc.Title }, focus: false);
            InsertAfter(v, new NoteBlock());
        }));
    }

    // ------------------------------------------------------------------ blocks

    private void Render(NoteBlock? select = null)
    {
        _host.Children.Clear();
        foreach (var b in _blocks) _host.Children.Add(new BlockView(this, b));
        RefreshMarkers();
        var target = _host.Children.OfType<BlockView>().FirstOrDefault(v => v.Block == select);
        if (target != null) Select(target);
    }

    internal void RefreshMarkers()
    {
        foreach (var v in _host.Children.OfType<BlockView>()) v.RefreshMarker(_blocks);
    }

    internal void Select(BlockView v)
    {
        if (_selected == v) return;
        _selected?.SetSelected(false);
        _selected = v;
        v.SetSelected(true);
    }

    internal BlockView InsertAfter(BlockView? after, NoteBlock block, bool focus = true)
    {
        var index = after == null ? _blocks.Count : _blocks.IndexOf(after.Block) + 1;
        _blocks.Insert(index, block);
        var view = new BlockView(this, block);
        _host.Children.Insert(index, view);
        RefreshMarkers();
        Select(view);
        if (focus && !block.IsMedia && block.Type != BlockType.Divider) view.BeginEdit();
        Changed();
        return view;
    }

    /// <summary>Enter pressed inside a block: split it and continue with the same kind of line.</summary>
    internal void Split(BlockView v, string before, string after)
    {
        var b = v.Block;
        if (b.IsListItem && before.Length == 0 && after.Length == 0)
        {
            b.Type = BlockType.Paragraph; // Enter on an empty list item ends the list
            b.Indent = 0;
            v.Apply();
            RefreshMarkers();
            Changed();
            return;
        }
        b.Text = before;
        v.Apply();
        var nextType = b.IsListItem || b.Type == BlockType.Quote ? b.Type : BlockType.Paragraph;
        var style = b.Type is BlockType.Heading1 or BlockType.Heading2 or BlockType.Heading3 ? new TextStyle() : b.Style.Clone();
        InsertAfter(v, new NoteBlock { Type = nextType, Text = after, Indent = b.Indent, Style = style });
    }

    internal void Changed(bool refreshAll = false)
    {
        _dirty = true;
        if (refreshAll)
        {
            foreach (var v in _host.Children.OfType<BlockView>()) v.Apply();
            RefreshMarkers();
        }
    }

    internal MediaItem? Media(string id) => _media.GetValueOrDefault(id);
    private readonly Dictionary<string, MediaItem> _media = [];

    internal async Task<MediaItem?> LoadMediaAsync(string id)
    {
        if (_media.TryGetValue(id, out var m)) return m;
        m = await AppServices.Db.GetAsync<MediaItem>(id);
        if (m == null && await AppServices.Db.GetAsync<ScanDocument>(id) is { } doc)
            m = (await AppServices.Db.ListAsync<MediaItem>(x => x.OwnerId == doc.Id)).OrderBy(x => x.PageNo).FirstOrDefault();
        if (m != null) _media[id] = m;
        return m;
    }

    // ------------------------------------------------------------------ save / export

    private async Task SaveAsync(bool notify)
    {
        foreach (var v in _host.Children.OfType<BlockView>()) v.CommitEdit();
        _note.Title = _title.Text?.Trim() ?? "";
        if (_note.Title == "") _note.Title = _blocks.FirstOrDefault(b => !b.IsMedia && b.Text.Trim() != "")?.Text.Trim() is { } t ? (t.Length > 60 ? t[..60] : t) : $"Note {DateTime.Now:dd-MMM HH:mm}";
        _note.Tags = string.Join(", ", (_tags.Text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        _note.ContentJson = NoteDocument.Serialize(_blocks);
        _note.PlainText = NoteDocument.ToPlainText(_blocks);
        if (_note.ProjectId == "") _note.ProjectId = AppServices.ProjectId;
        await AppServices.Db.SaveAsync(_note);
        _dirty = false;
        _status.Text = $"Saved {DateTime.Now:HH:mm:ss}";
        if (notify) await DisplayAlertAsync("Saved", "Note saved" + (AppServices.SyncSettings.Enabled ? " and syncing." : "."), "OK");
    }

    private async Task DeleteAsync()
    {
        if (!await DisplayAlertAsync("Delete note?", _note.Title, "Delete", "Cancel")) return;
        _dirty = false;
        if (_noteId != null) await AppServices.Db.DeleteAsync(_note);
        await Navigation.PopAsync();
    }

    public static async Task ExportAsync(Note note, string folderName, bool share)
    {
        var blocks = NoteDocument.Parse(note.ContentJson);
        var media = new Dictionary<string, MediaItem>();
        foreach (var id in blocks.Where(b => b.IsMedia && b.MediaId != "").Select(b => b.MediaId).Distinct())
        {
            var m = await AppServices.Db.GetAsync<MediaItem>(id)
                    ?? (await AppServices.Db.ListAsync<MediaItem>(x => x.OwnerId == id)).OrderBy(x => x.PageNo).FirstOrDefault();
            if (m != null) media[id] = m;
        }
        var opt = await ReportProfiles.CurrentAsync();
        var path = await FileExport.CreateAsync($"Note_{FileExport.SafeName(note.Title)}_{DateTime.Now:yyyyMMdd_HHmm}.pdf",
            s => PdfReports.Note(s, note, blocks, folderName, id => media.GetValueOrDefault(id), AppServices.Db.MediaPath, opt));
        if (share && Shell.Current?.CurrentPage is { } page) await FileExport.OfferAsync(page, path);
    }
}

/// <summary>One block of the note: a formatted label that turns into an editor when tapped.</summary>
public class BlockView : ContentView
{
    public NoteBlock Block { get; }
    private readonly NoteEditorPage _page;
    private readonly Label _marker = new() { VerticalOptions = LayoutOptions.Start, Margin = new Thickness(0, 6, 6, 0), MinimumWidthRequest = 18 };
    private readonly CheckBox _check = new() { VerticalOptions = LayoutOptions.Start, Color = Ui.Primary };
    private readonly Label _label = new() { LineBreakMode = LineBreakMode.WordWrap, Padding = new Thickness(2, 6) };
    private readonly Editor _editor = new() { AutoSize = EditorAutoSizeOption.TextChanges, IsVisible = false };
    private readonly Border _frame;
    private bool _splitting;

    public BlockView(NoteEditorPage page, NoteBlock block)
    {
        _page = page;
        Block = block;
        _frame = new Border { StrokeThickness = 0, Padding = new Thickness(4, 0), StrokeShape = new RoundRectangle { CornerRadius = 6 }, BackgroundColor = Colors.Transparent };
        Content = _frame;
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) =>
        {
            _page.Select(this);
            if (!Block.IsMedia && Block.Type != BlockType.Divider) BeginEdit();
        };
        _frame.GestureRecognizers.Add(tap);
        _check.CheckedChanged += (_, a) =>
        {
            if (Block.Checked == a.Value) return;
            Block.Checked = a.Value;
            Apply();
            _page.Changed();
        };
        _editor.Unfocused += (_, _) => CommitEdit();
        _editor.TextChanged += (_, a) =>
        {
            if (_splitting || a.NewTextValue is not { } text) return;
            var i = text.IndexOf('\n');
            if (i < 0) { Block.Text = text; _page.Changed(); return; }
            _splitting = true;
            var before = text[..i].TrimEnd('\r');
            var after = text[(i + 1)..];
            _editor.Text = before;
            CommitEdit();
            _splitting = false;
            _page.Split(this, before, after);
        };
        Build();
    }

    private void Build()
    {
        switch (Block.Type)
        {
            case BlockType.Divider:
                _frame.Content = new BoxView { HeightRequest = 1, Color = Ui.Muted, Margin = new Thickness(0, 12) };
                return;
            case BlockType.Image:
            case BlockType.Scan:
            case BlockType.Video:
                _ = BuildMediaAsync();
                return;
        }
        var g = new Grid { ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star)] };
        g.Add(new Grid { Children = { _marker, _check } }, 0);
        g.Add(new Grid { Children = { _label, _editor } }, 1);
        _frame.Content = g;
        Apply();
    }

    private async Task BuildMediaAsync()
    {
        var m = await _page.LoadMediaAsync(Block.MediaId);
        var caption = new Entry { Text = Block.Text != "" ? Block.Text : m?.Caption, Placeholder = "Caption", FontSize = 12, HorizontalTextAlignment = TextAlignment.Center };
        caption.TextChanged += (_, a) => { Block.Text = a.NewTextValue ?? ""; _page.Changed(); };
        View visual;
        if (Block.Type == BlockType.Video)
        {
            visual = Ui.Button($"▶ {(m == null ? "Video" : "Play video")}", async () => { if (m != null) await MediaService.OpenAsync(m); }, false);
        }
        else
        {
            var thumb = m == null ? null : MediaService.Thumbnail(m);
            var img = new Image { Source = thumb, Aspect = Aspect.AspectFit, HeightRequest = 240 };
            var open = new TapGestureRecognizer();
            open.Tapped += async (_, _) =>
            {
                _page.Select(this);
                if (Block.Type == BlockType.Scan) await AppShell.Push(new ScannerPage(Block.MediaId, ""));
                else if (m != null) await AppShell.Push(new MediaViewerPage(m, () => Task.CompletedTask));
            };
            img.GestureRecognizers.Add(open);
            visual = Block.Type == BlockType.Scan
                ? new VerticalStackLayout { Children = { img, Ui.Label("📄 Scanned document – tap to open", 12, true, Ui.Primary) } }
                : img;
            if (thumb == null) visual = Ui.Caption(m == null ? "(missing media)" : "(file downloading from server…)");
        }
        _frame.Content = new VerticalStackLayout { Spacing = 2, Padding = new Thickness(0, 6), Children = { visual, caption } };
    }

    public void RefreshMarker(IReadOnlyList<NoteBlock> blocks)
    {
        if (Block.IsMedia || Block.Type == BlockType.Divider) return;
        var i = blocks.ToList().IndexOf(Block);
        _check.IsVisible = Block.Type == BlockType.Checklist;
        _check.IsChecked = Block.Checked;
        _marker.IsVisible = Block.IsListItem && Block.Type != BlockType.Checklist;
        _marker.Text = i >= 0 ? NoteDocument.Marker(blocks, i) : "";
        _frame.Margin = new Thickness(Block.Indent * 22, 0, 0, 0);
    }

    /// <summary>Applies the block's style to the display label and the editor.</summary>
    public void Apply()
    {
        if (Block.IsMedia || Block.Type == BlockType.Divider) { Build(); return; }
        var s = Block.Style;
        var size = Block.EffectiveSize;
        var bold = s.Bold || Block.Type is BlockType.Heading1 or BlockType.Heading2 or BlockType.Heading3;
        var italic = s.Italic || Block.Type == BlockType.Quote;
        var attrs = (bold ? FontAttributes.Bold : FontAttributes.None) | (italic ? FontAttributes.Italic : FontAttributes.None);
        var family = FontCatalog.Resolve(s.FontFamily);
        var done = Block.Type == BlockType.Checklist && Block.Checked;

        _label.Text = Block.Text.Length == 0 ? Placeholder() : Block.Text;
        _label.FontSize = _editor.FontSize = size;
        _label.FontAttributes = _editor.FontAttributes = attrs;
        _label.FontFamily = _editor.FontFamily = family;
        _label.TextDecorations = (s.Underline ? TextDecorations.Underline : TextDecorations.None) |
                                 (s.Strikethrough || done ? TextDecorations.Strikethrough : TextDecorations.None);
        _label.HorizontalTextAlignment = _editor.HorizontalTextAlignment = s.Align switch
        {
            TextAlign.Center => TextAlignment.Center,
            TextAlign.Right => TextAlignment.End,
            _ => TextAlignment.Start,
        };
        Color? color = !string.IsNullOrEmpty(s.Color) ? Color.FromArgb(s.Color) : null;
        if (done || Block.Text.Length == 0) color = Ui.Muted;
        if (color != null) { _label.TextColor = color; _editor.TextColor = color; }
        else
        {
            _label.SetAppThemeColor(Label.TextColorProperty, Color.FromArgb("#111827"), Color.FromArgb("#E5E7EB"));
            _editor.SetAppThemeColor(Editor.TextColorProperty, Color.FromArgb("#111827"), Color.FromArgb("#E5E7EB"));
        }
        _label.BackgroundColor = !string.IsNullOrEmpty(s.Highlight) ? Color.FromArgb(s.Highlight) : Colors.Transparent;
        _marker.FontSize = size;
        _marker.TextColor = Ui.Primary;
        _label.Margin = Block.Type == BlockType.Quote ? new Thickness(10, 0, 0, 0) : 0;
    }

    private string Placeholder() => Block.Type switch
    {
        BlockType.Heading1 => "Heading",
        BlockType.Heading2 => "Sub-heading",
        BlockType.Heading3 => "Small heading",
        BlockType.Checklist => "To do",
        BlockType.Quote => "Quote / instruction",
        BlockType.Bullet or BlockType.Numbered or BlockType.Arrow => "List item",
        _ => "Type here…",
    };

    public void BeginEdit()
    {
        if (_editor.IsVisible) return;
        _editor.Text = Block.Text;
        _label.IsVisible = false;
        _editor.IsVisible = true;
        _editor.Focus();
    }

    public void CommitEdit()
    {
        if (!_editor.IsVisible) return;
        Block.Text = _editor.Text ?? "";
        _editor.IsVisible = false;
        _label.IsVisible = true;
        Apply();
    }

    public void SetSelected(bool selected)
    {
        if (selected) _frame.SetAppThemeColor(BackgroundColorProperty, Color.FromArgb("#EEF4FB"), Color.FromArgb("#222A33"));
        else _frame.BackgroundColor = Colors.Transparent;
    }
}
