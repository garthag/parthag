using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SiteNotes.Core;
using SiteNotes.Core.Models;
using SiteNotes.Core.Reports;
using SiteNotes.Desktop.Services;
using SiteNotes.Desktop.UI;

namespace SiteNotes.Desktop.Views;

/// <summary>
/// Notes: folders (left), notes &amp; scans in the folder (middle), rich editor with a formatting toolbox (right).
/// Headings, sub-headings, bullets, numbering, arrows, checklists (ticked = struck through), quotes, dividers,
/// fonts, sizes, colours, highlight, alignment, indent, pictures, videos and scans. Saves automatically.
/// </summary>
public class NotesView : ViewBase, ISaveable, IPrintable
{
    private readonly TreeView _folders = new() { Margin = new Thickness(0, 4, 0, 0) };
    private readonly ListBox _list = new() { Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBox _search = new() { ToolTip = "Search all notes" };
    private readonly TextBox _title = new() { FontSize = 20, FontWeight = FontWeights.SemiBold, BorderThickness = new Thickness(0, 0, 0, 1), Margin = new Thickness(0, 0, 0, 4) };
    private readonly TextBox _tags = new() { FontSize = 12, BorderThickness = new Thickness(0, 0, 0, 1), ToolTip = "Tags, separated by commas" };
    private readonly RichTextBox _editor = new() { AcceptsTab = true, IsDocumentEnabled = true, BorderThickness = new Thickness(0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, SpellCheck = { IsEnabled = true } };
    private readonly TextBlock _saved = W.Muted("");
    private readonly ContentControl _editorHost = new();
    private readonly DispatcherTimer _autosave;
    private string _folderId = "";
    private Note? _note;
    private bool _dirty;
    private bool _loading;

    public override string Title => "Notes";
    public bool IsDirty => _dirty;

    private record Entry(object Item, string Text)
    {
        public override string ToString() => Text;
    }

    public NotesView()
    {
        _autosave = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, async (_, _) => { if (_dirty) await Dialogs.Guard(SaveAsync); }, Dispatcher);
        _editor.TextChanged += (_, _) => { if (!_loading) _dirty = true; };
        _editor.PreviewKeyDown += OnKey;
        _title.TextChanged += (_, _) => { if (!_loading) _dirty = true; };
        _tags.TextChanged += (_, _) => { if (!_loading) _dirty = true; };
        _search.TextChanged += async (_, _) => await Dialogs.Guard(ListAsync);
        _folders.SelectedItemChanged += async (_, _) => { _folderId = (_folders.SelectedItem as TreeViewItem)?.Tag as string ?? ""; await Dialogs.Guard(ListAsync); };
        _list.SelectionChanged += async (_, _) => await Dialogs.Guard(OpenSelectedAsync);

        var left = new DockPanel { Margin = new Thickness(10, 6, 6, 10) };
        var leftTop = W.Wrap(W.Button("New folder", NewFolderAsync, Glyph.Folder), W.Button("Rename", RenameFolderAsync, Glyph.Edit));
        DockPanel.SetDock(leftTop, Dock.Top);
        left.Children.Add(leftTop);
        left.Children.Add(_folders);

        var mid = new DockPanel { Margin = new Thickness(6, 6, 6, 10) };
        var midTop = W.Stack(
            W.Wrap(W.Button("New note", NewNoteAsync, Glyph.Add, true), W.Button("Scan", () => MainWindow.Current.Open(new ScannerView(null, _folderId)), Glyph.Scan)),
            W.Wrap(W.Text("🔍 "), _search));
        _search.Width = 220;
        DockPanel.SetDock(midTop, Dock.Top);
        mid.Children.Add(midTop);
        var listMenu = new ContextMenu();
        foreach (var (text, act) in new (string, Func<Task>)[] { ("Pin / unpin", PinAsync), ("Move to folder…", MoveAsync), ("Duplicate", DuplicateAsync), ("Export PDF", PdfAsync), ("Delete", DeleteAsync) })
        {
            var mi = new MenuItem { Header = text };
            mi.Click += async (_, _) => await Dialogs.Guard(act);
            listMenu.Items.Add(mi);
        }
        _list.ContextMenu = listMenu;
        mid.Children.Add(_list);

        var cols = new Grid();
        cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        cols.ColumnDefinitions.Add(new ColumnDefinition());
        cols.Children.Add(left);
        Grid.SetColumn(mid, 1);
        cols.Children.Add(mid);
        Grid.SetColumn(_editorHost, 2);
        cols.Children.Add(_editorHost);
        Content = cols;
        _editorHost.Content = W.Muted("   Select a note or click New note.");
    }

    // ------------------------------------------------------------------ folders & list

    public override async Task RefreshAsync()
    {
        var folders = await Desk.Db.ListForProjectAsync<Folder>(Desk.ProjectId);
        _folders.Items.Clear();
        var root = new TreeViewItem { Header = "📁 All notes", Tag = "", IsExpanded = true };
        void Add(ItemsControl parent, string parentId, int depth)
        {
            foreach (var f in folders.Where(f => f.ParentId == parentId).OrderBy(f => f.Name))
            {
                var item = new TreeViewItem { Header = (f.Kind == FolderKind.Scans ? "🗂 " : "📁 ") + f.Name, Tag = f.Id, IsExpanded = depth < 1 };
                parent.Items.Add(item);
                if (f.Id == _folderId) item.IsSelected = true;
                if (depth < 10) Add(item, f.Id, depth + 1);
            }
        }
        Add(root, "", 0);
        _folders.Items.Add(root);
        if (_folderId == "") root.IsSelected = true;
        await ListAsync();
    }

    private async Task ListAsync()
    {
        var pid = Desk.ProjectId;
        var s = _search.Text.Trim();
        var notes = await Desk.Db.ListForProjectAsync<Note>(pid);
        var scans = await Desk.Db.ListForProjectAsync<ScanDocument>(pid);
        IEnumerable<Note> n = s != "" ? notes.Where(x => $"{x.Title} {x.PlainText} {x.Tags}".Contains(s, StringComparison.OrdinalIgnoreCase))
            : _folderId == "" ? notes : notes.Where(x => x.FolderId == _folderId);
        IEnumerable<ScanDocument> d = s != "" ? scans.Where(x => $"{x.Title} {x.Remarks}".Contains(s, StringComparison.OrdinalIgnoreCase))
            : _folderId == "" ? scans : scans.Where(x => x.FolderId == _folderId);
        var keep = _note?.Id;
        _list.ItemsSource = n.OrderByDescending(x => x.IsPinned).ThenByDescending(x => x.UpdatedAtUtc)
            .Select(x => new Entry(x, $"{(x.IsPinned ? "📌" : "📝")} {(x.Title == "" ? "Untitled" : x.Title)}\n     {x.UpdatedAtUtc.ToLocalTime():dd-MMM HH:mm}" + (x.Tags != "" ? $" · #{x.Tags}" : "")))
            .Concat(d.OrderByDescending(x => x.CreatedAtUtc).Select(x => new Entry(x, $"📄 {x.Title}\n     {x.PageCount} page(s) · {x.CreatedAtUtc.ToLocalTime():dd-MMM HH:mm}")))
            .ToList();
        if (keep != null) _list.SelectedItem = _list.Items.OfType<Entry>().FirstOrDefault(e => e.Item is Note x && x.Id == keep);
    }

    private async Task NewFolderAsync()
    {
        var name = Dialogs.Prompt("New folder", "Folder name (e.g. Inspections, MOM, Drawings)");
        if (string.IsNullOrWhiteSpace(name)) return;
        await Desk.Db.SaveAsync(new Folder { ProjectId = Desk.ProjectId, ParentId = _folderId, Name = name.Trim() });
        await RefreshAsync();
    }

    private async Task RenameFolderAsync()
    {
        if (_folderId == "" || await Desk.Db.GetAsync<Folder>(_folderId) is not { } f) return;
        var name = Dialogs.Prompt("Rename folder", "New name", f.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        f.Name = name.Trim();
        await Desk.Db.SaveAsync(f);
        await RefreshAsync();
    }

    private async Task OpenSelectedAsync()
    {
        if (_list.SelectedItem is not Entry e) return;
        if (e.Item is ScanDocument doc) { MainWindow.Current.Open(new ScannerView(doc.Id)); return; }
        if (e.Item is not Note n || n.Id == _note?.Id) return;
        if (_dirty) await SaveAsync();
        await LoadNoteAsync(n);
    }

    private async Task NewNoteAsync()
    {
        if (_dirty) await SaveAsync();
        var n = new Note { ProjectId = Desk.ProjectId, FolderId = _folderId, Title = "", ContentJson = NoteDocument.Serialize([new NoteBlock()]) };
        await LoadNoteAsync(n);
        _title.Focus();
    }

    // ------------------------------------------------------------------ editor

    private async Task LoadNoteAsync(Note n)
    {
        _loading = true;
        _note = n;
        var blocks = NoteDocument.Parse(n.ContentJson);
        var media = new Dictionary<string, MediaItem>();
        foreach (var id in blocks.Where(b => b.IsMedia && b.MediaId != "").Select(b => b.MediaId).Distinct())
        {
            var m = await Desk.Db.GetAsync<MediaItem>(id) ?? (await Desk.Db.ListAsync<MediaItem>(x => x.OwnerId == id)).OrderBy(x => x.PageNo).FirstOrDefault();
            if (m != null) media[id] = m;
        }
        _editor.Document = NoteFlow.ToFlow(blocks, id => media.GetValueOrDefault(id));
        _title.Text = n.Title;
        _tags.Text = n.Tags;
        _saved.Text = n.UpdatedBy == "" ? "" : $"Last edited {n.UpdatedAtUtc.ToLocalTime():dd-MMM-yyyy HH:mm} by {n.UpdatedBy}";
        if (_editorHost.Content is not DockPanel) _editorHost.Content = EditorLayout();
        _loading = false;
        _dirty = false;
        _autosave.Start();
    }

    private DockPanel EditorLayout()
    {
        var dock = new DockPanel { Margin = new Thickness(6, 0, 10, 10) };
        var tools = Toolbox();
        DockPanel.SetDock(tools, Dock.Top);
        dock.Children.Add(tools);
        var head = W.Stack(_title, W.Wrap(W.Muted("Tags "), _tags, _saved));
        _tags.Width = 260;
        head.Margin = new Thickness(0, 6, 0, 6);
        DockPanel.SetDock(head, Dock.Top);
        dock.Children.Add(head);
        dock.Children.Add(new Border { BorderBrush = Theme.Line, BorderThickness = new Thickness(1), Background = Brushes.White, Child = _editor });
        return dock;
    }

    private ToolBarTray Toolbox()
    {
        var tray = new ToolBarTray { Background = Brushes.White };
        ToolBar Bar(params UIElement[] items)
        {
            var t = new ToolBar();
            foreach (var i in items) t.Items.Add(i);
            tray.ToolBars.Add(t);
            return t;
        }
        Button Btn(string content, string tip, Action a, bool icon = false)
        {
            var b = new Button { Content = content, ToolTip = tip, Padding = new Thickness(6, 2, 6, 2), MinWidth = 28 };
            if (icon) b.FontFamily = Theme.Icons;
            b.Click += (_, _) => { a(); _editor.Focus(); _dirty = true; };
            return b;
        }
        var fonts = new ComboBox { Width = 110, ItemsSource = new[] { "Default", "Serif", "Mono", "Casual", "Cursive", "Condensed" }, ToolTip = "Font style" };
        fonts.SelectionChanged += (_, _) => { if (fonts.SelectedItem is string f) BlockStyle(b => b.Style.FontFamily = f); };
        var sizes = new ComboBox { Width = 60, IsEditable = true, ItemsSource = new double[] { 10, 11, 12, 13, 14, 15, 16, 18, 20, 22, 24, 28, 32, 36 }, ToolTip = "Font size" };
        sizes.SelectionChanged += (_, _) => { if (sizes.SelectedItem is double d) BlockStyle(b => b.Style.FontSize = d); };
        var colors = new ComboBox { Width = 90, ItemsSource = new[] { "Auto", "Red", "Orange", "Green", "Blue", "Purple", "Grey", "Brown" }, ToolTip = "Text colour" };
        string[] colorHex = ["", "#B91C1C", "#C2410C", "#15803D", "#1D4ED8", "#7E22CE", "#6B7280", "#92400E"];
        colors.SelectionChanged += (_, _) => { if (colors.SelectedIndex >= 0) BlockStyle(b => b.Style.Color = colorHex[colors.SelectedIndex]); };
        var marks = new ComboBox { Width = 95, ItemsSource = new[] { "No highlight", "Yellow", "Green", "Blue", "Pink", "Orange" }, ToolTip = "Highlight" };
        string[] markHex = ["", "#FEF08A", "#BBF7D0", "#BFDBFE", "#FBCFE8", "#FED7AA"];
        marks.SelectionChanged += (_, _) => { if (marks.SelectedIndex >= 0) BlockStyle(b => b.Style.Highlight = markHex[marks.SelectedIndex]); };

        Bar(Btn("¶", "Normal text", () => SetType(BlockType.Paragraph)), Btn("H1", "Heading", () => SetType(BlockType.Heading1)),
            Btn("H2", "Sub-heading", () => SetType(BlockType.Heading2)), Btn("H3", "Small heading", () => SetType(BlockType.Heading3)),
            Btn("❝", "Quote / instruction", () => SetType(BlockType.Quote)));
        Bar(Btn(Glyph.List, "Bullets", () => EditingCommands.ToggleBullets.Execute(null, _editor), true),
            Btn("1.", "Numbering", () => EditingCommands.ToggleNumbering.Execute(null, _editor)),
            Btn("➜", "Arrow list", () => SetType(BlockType.Arrow)),
            Btn("☐", "Checklist", () => SetType(BlockType.Checklist)),
            Btn("☑", "Tick / untick (Ctrl+Enter)", ToggleCheck),
            Btn("⇤", "Decrease indent", () => EditingCommands.DecreaseIndentation.Execute(null, _editor)),
            Btn("⇥", "Increase indent", () => EditingCommands.IncreaseIndentation.Execute(null, _editor)));
        Bar(Btn(Glyph.Bold, "Bold", () => BlockStyle(b => b.Style.Bold = !b.Style.Bold), true),
            Btn(Glyph.Italic, "Italic", () => BlockStyle(b => b.Style.Italic = !b.Style.Italic), true),
            Btn(Glyph.Underline, "Underline", () => BlockStyle(b => b.Style.Underline = !b.Style.Underline), true),
            Btn("S̶", "Strikethrough", () => BlockStyle(b => b.Style.Strikethrough = !b.Style.Strikethrough)),
            fonts, sizes, colors, marks);
        Bar(Btn(Glyph.AlignLeft, "Left", () => BlockStyle(b => b.Style.Align = TextAlign.Left), true),
            Btn(Glyph.AlignCenter, "Centre", () => BlockStyle(b => b.Style.Align = TextAlign.Center), true),
            Btn(Glyph.AlignRight, "Right", () => BlockStyle(b => b.Style.Align = TextAlign.Right), true));
        Bar(Btn(Glyph.Photo, "Insert pictures", () => _ = Dialogs.Guard(InsertPicturesAsync), true),
            Btn(Glyph.Video, "Insert video", () => _ = Dialogs.Guard(InsertVideoAsync), true),
            Btn(Glyph.Scan, "Insert a scanned document", () => _ = Dialogs.Guard(InsertScanAsync), true),
            Btn("―", "Divider line", () => InsertBlock(new BlockUIContainer(new Separator { Margin = new Thickness(0, 6, 0, 6) }) { Tag = new NoteBlock { Type = BlockType.Divider } })));
        Bar(Btn(Glyph.Save, "Save (Ctrl+S)", () => _ = Dialogs.Guard(SaveAsync), true), Btn(Glyph.Print, "PDF (Ctrl+P)", () => _ = Dialogs.Guard(PdfAsync), true));
        return tray;
    }

    private IEnumerable<Paragraph> SelectedParagraphs()
    {
        var start = _editor.Selection.Start.Paragraph;
        var end = _editor.Selection.End.Paragraph;
        if (start == null) yield break;
        var all = AllParagraphs(_editor.Document.Blocks).ToList();
        var i = all.IndexOf(start);
        var j = end == null ? i : all.IndexOf(end);
        if (i < 0) yield break;
        for (var k = i; k <= Math.Max(i, j); k++) yield return all[k];
    }

    private static IEnumerable<Paragraph> AllParagraphs(BlockCollection blocks)
    {
        foreach (var b in blocks)
        {
            if (b is Paragraph p) yield return p;
            else if (b is List l) foreach (var li in l.ListItems) foreach (var x in AllParagraphs(li.Blocks)) yield return x;
            else if (b is Section s) foreach (var x in AllParagraphs(s.Blocks)) yield return x;
        }
    }

    /// <summary>Changes the style of the whole paragraph(s) under the cursor (notes format by line, like the phone app).</summary>
    private void BlockStyle(Action<NoteBlock> change)
    {
        if (_loading) return;
        foreach (var p in SelectedParagraphs().ToList())
        {
            var b = NoteFlow.FromFlow(WrapCopy(p)).FirstOrDefault() ?? new NoteBlock();
            change(b);
            NoteFlow.ApplyStyle(p, b);
        }
    }

    /// <summary>Reads one paragraph as a block (through a temporary document so lists keep their marker).</summary>
    private static FlowDocument WrapCopy(Paragraph p)
    {
        var d = new FlowDocument();
        var copy = new Paragraph(new Run(NoteFlow.InlineText(p.Inlines)))
        {
            FontSize = p.FontSize, FontWeight = p.FontWeight, FontStyle = p.FontStyle, FontFamily = p.FontFamily, Foreground = p.Foreground,
            Background = p.Background, TextAlignment = p.TextAlignment, TextDecorations = p.TextDecorations, BorderThickness = p.BorderThickness, Margin = p.Margin,
        };
        if (p.Inlines.FirstInline is Run r)
        {
            if (r.ReadLocalValue(TextElement.FontSizeProperty) is double fs) copy.FontSize = fs;
            if (r.ReadLocalValue(TextElement.FontWeightProperty) is FontWeight fw) copy.FontWeight = fw;
            if (r.ReadLocalValue(TextElement.ForegroundProperty) is Brush fg) copy.Foreground = fg;
        }
        d.Blocks.Add(copy);
        return d;
    }

    private void SetType(BlockType type)
    {
        foreach (var p in SelectedParagraphs().ToList())
        {
            var b = NoteFlow.FromFlow(WrapCopy(p)).FirstOrDefault() ?? new NoteBlock();
            var text = b.Text;
            var newType = b.Type == type && type != BlockType.Paragraph ? BlockType.Paragraph : type; // same button again = back to normal
            b.Type = newType;
            if (newType is BlockType.Heading1 or BlockType.Heading2 or BlockType.Heading3 or BlockType.Paragraph) b.Style.FontSize = 0;
            var prefix = newType switch { BlockType.Checklist => NoteFlow.Unchecked, BlockType.Arrow => NoteFlow.Arrow, _ => "" };
            p.Inlines.Clear();
            p.Inlines.Add(new Run(prefix + text));
            NoteFlow.ApplyStyle(p, b);
        }
    }

    private void ToggleCheck()
    {
        foreach (var p in SelectedParagraphs().ToList())
        {
            var b = NoteFlow.FromFlow(WrapCopy(p)).FirstOrDefault();
            if (b is not { Type: BlockType.Checklist }) continue;
            b.Checked = !b.Checked;
            p.Inlines.Clear();
            p.Inlines.Add(new Run((b.Checked ? NoteFlow.Checked : NoteFlow.Unchecked) + b.Text));
            NoteFlow.ApplyStyle(p, b);
        }
        _dirty = true;
    }

    /// <summary>Enter continues a checklist / arrow list; Enter on an empty item ends it. Ctrl+Enter ticks.</summary>
    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { ToggleCheck(); e.Handled = true; return; }
        if (e.Key != System.Windows.Input.Key.Enter || Keyboard.Modifiers != ModifierKeys.None) return;
        var p = _editor.CaretPosition.Paragraph;
        if (p == null) return;
        var text = NoteFlow.InlineText(p.Inlines);
        var prefix = text.StartsWith(NoteFlow.Unchecked) || text.StartsWith(NoteFlow.Checked) ? NoteFlow.Unchecked : text.StartsWith(NoteFlow.Arrow) ? NoteFlow.Arrow : null;
        if (prefix == null) return;
        if (text.Trim().Length <= 1)
        {
            // empty item: end the list
            p.Inlines.Clear();
            p.Inlines.Add(new Run(""));
            NoteFlow.ApplyStyle(p, new NoteBlock());
            e.Handled = true;
            return;
        }
        Dispatcher.BeginInvoke(() =>
        {
            var np = _editor.CaretPosition.Paragraph;
            if (np == null || np == p) return;
            var nt = NoteFlow.InlineText(np.Inlines);
            if (nt.StartsWith(NoteFlow.Unchecked) || nt.StartsWith(NoteFlow.Checked) || nt.StartsWith(NoteFlow.Arrow)) return;
            np.ContentStart.InsertTextInRun(prefix);
            var b = new NoteBlock { Type = prefix == NoteFlow.Arrow ? BlockType.Arrow : BlockType.Checklist };
            NoteFlow.ApplyStyle(np, b);
            _editor.CaretPosition = np.ContentEnd;
        }, DispatcherPriority.Input);
    }

    private void InsertBlock(Block block)
    {
        var at = _editor.CaretPosition.Paragraph;
        var top = at as Block;
        while (top?.Parent is ListItem li) top = li.Parent as Block;
        var blocks = _editor.Document.Blocks;
        if (top != null && blocks.Contains(top)) blocks.InsertAfter(top, block);
        else blocks.Add(block);
        var after = new Paragraph();
        blocks.InsertAfter(block, after);
        _editor.CaretPosition = after.ContentStart;
        _dirty = true;
    }

    private async Task<string> EnsureNoteIdAsync()
    {
        if (_note == null) await NewNoteAsync();
        return _note!.Id;
    }

    private async Task InsertPicturesAsync()
    {
        var id = await EnsureNoteIdAsync();
        foreach (var f in Dialogs.OpenImages())
        {
            var m = await Desk.ImportMediaAsync(f, MediaOwner.Note, id, MediaKind.Photo);
            var b = new NoteBlock { Type = BlockType.Image, MediaId = m.Id, Text = "" };
            InsertBlock(Detach(NoteFlow.ToFlow([b], _ => m)));
        }
    }

    private async Task InsertVideoAsync()
    {
        var id = await EnsureNoteIdAsync();
        var d = new Microsoft.Win32.OpenFileDialog { Filter = "Videos|*.mp4;*.mov;*.avi;*.wmv|All files|*.*" };
        if (d.ShowDialog() != true) return;
        var m = await Desk.ImportMediaAsync(d.FileName, MediaOwner.Note, id, MediaKind.Video);
        var b = new NoteBlock { Type = BlockType.Video, MediaId = m.Id, Text = m.Caption };
        InsertBlock(Detach(NoteFlow.ToFlow([b], _ => m)));
    }

    private async Task InsertScanAsync()
    {
        var scans = (await Desk.Db.ListForProjectAsync<ScanDocument>(Desk.ProjectId)).OrderByDescending(s => s.CreatedAtUtc).ToList();
        var options = scans.Select(s => $"{s.Title} ({s.PageCount} pages, {s.CreatedAtUtc.ToLocalTime():dd-MMM})").Prepend("➕ Scan a new document…").ToList();
        var i = Dialogs.Choose("Insert scan", "Choose a scanned document (or scan a new one first):", options);
        if (i < 0) return;
        if (i == 0) { MainWindow.Current.Open(new ScannerView(null, _note?.FolderId ?? "")); return; }
        var doc = scans[i - 1];
        var first = (await Desk.Db.ListAsync<MediaItem>(x => x.OwnerId == doc.Id)).OrderBy(x => x.PageNo).FirstOrDefault();
        var b = new NoteBlock { Type = BlockType.Scan, MediaId = doc.Id, Text = doc.Title };
        InsertBlock(Detach(NoteFlow.ToFlow([b], _ => first)));
    }

    private static Block Detach(FlowDocument d)
    {
        var b = d.Blocks.FirstBlock;
        d.Blocks.Remove(b);
        return b;
    }

    public async Task SaveAsync()
    {
        if (_note == null) return;
        var blocks = NoteFlow.FromFlow(_editor.Document);
        _note.Title = _title.Text.Trim();
        if (_note.Title == "") _note.Title = blocks.FirstOrDefault(b => !b.IsMedia && b.Text.Trim() != "")?.Text.Trim() is { } t ? (t.Length > 60 ? t[..60] : t) : $"Note {DateTime.Now:dd-MMM HH:mm}";
        _note.Tags = string.Join(", ", _tags.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        _note.ContentJson = NoteDocument.Serialize(blocks);
        _note.PlainText = NoteDocument.ToPlainText(blocks);
        if (_note.ProjectId == "") _note.ProjectId = Desk.ProjectId;
        await Desk.Db.SaveAsync(_note);
        _dirty = false;
        _saved.Text = $"   Saved {DateTime.Now:HH:mm:ss}";
        await ListAsync();
    }

    public async Task PdfAsync()
    {
        if (_note == null) { Dialogs.Info("Open a note first."); return; }
        await SaveAsync();
        var blocks = NoteDocument.Parse(_note.ContentJson);
        var media = new Dictionary<string, MediaItem>();
        foreach (var id in blocks.Where(b => b.IsMedia && b.MediaId != "").Select(b => b.MediaId).Distinct())
        {
            var m = await Desk.Db.GetAsync<MediaItem>(id) ?? (await Desk.Db.ListAsync<MediaItem>(x => x.OwnerId == id)).OrderBy(x => x.PageNo).FirstOrDefault();
            if (m != null) media[id] = m;
        }
        var folder = _note.FolderId == "" ? "Notes" : (await Desk.Db.GetAsync<Folder>(_note.FolderId))?.Name ?? "Notes";
        var opt = await Desk.ReportOptionsAsync();
        var note = _note;
        await Dialogs.CreateAndOpenAsync($"Note_{note.Title}_{DateTime.Now:yyyyMMdd_HHmm}.pdf",
            s => PdfReports.Note(s, note, blocks, folder, id => media.GetValueOrDefault(id), Desk.Db.MediaPath, opt));
    }

    private Note? SelectedNote => (_list.SelectedItem as Entry)?.Item as Note;

    private async Task PinAsync()
    {
        if (SelectedNote is not { } n) return;
        n.IsPinned = !n.IsPinned;
        await Desk.Db.SaveAsync(n);
        await ListAsync();
    }

    private async Task MoveAsync()
    {
        if ((_list.SelectedItem as Entry)?.Item is not SyncEntity item) return;
        var folders = (await Desk.Db.ListForProjectAsync<Folder>(Desk.ProjectId)).OrderBy(f => f.Name).ToList();
        var i = Dialogs.Choose("Move", "Move to folder:", folders.Select(f => f.Name).Prepend("(top level)").ToList());
        if (i < 0) return;
        var target = i == 0 ? "" : folders[i - 1].Id;
        if (item is Note n) n.FolderId = target; else if (item is ScanDocument d) d.FolderId = target;
        await Desk.Db.SaveAsync(item);
        await ListAsync();
    }

    private async Task DuplicateAsync()
    {
        if (SelectedNote is not { } n) return;
        await Desk.Db.SaveAsync(new Note { ProjectId = n.ProjectId, FolderId = n.FolderId, Title = n.Title + " (copy)", ContentJson = n.ContentJson, PlainText = n.PlainText, Tags = n.Tags });
        await ListAsync();
    }

    private async Task DeleteAsync()
    {
        if ((_list.SelectedItem as Entry)?.Item is not SyncEntity item) return;
        if (!Dialogs.Confirm("Delete this item? It is removed on every synced device.")) return;
        await Desk.Db.DeleteAsync(item);
        if (item == _note) { _note = null; _dirty = false; _editorHost.Content = W.Muted("   Select a note or click New note."); }
        await ListAsync();
    }
}
