using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SiteNotes.Core.Models;
using SiteNotes.Desktop.Services;
using SiteNotes.Desktop.UI;

namespace SiteNotes.Desktop.Views;

/// <summary>
/// Photos &amp; videos attached to a report, hindrance or requirement: add files (or drag &amp; drop),
/// double-click to open, right-click to caption or remove. Files sync to every device.
/// </summary>
public class MediaPanel : DockPanel
{
    private readonly MediaOwner _owner;
    private readonly Func<string> _ownerId;
    private readonly WrapPanel _tiles = new();
    public event Action<int>? CountChanged;

    public MediaPanel(MediaOwner owner, Func<string> ownerId)
    {
        _owner = owner;
        _ownerId = ownerId;
        AllowDrop = true;
        Drop += async (_, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] files) await Dialogs.Guard(() => AddAsync(files)); };
        var top = W.Wrap(
            W.Button("Add photos / videos…", () => AddAsync(Dialogs.OpenMedia()), Glyph.Photo, true),
            W.Muted("   or drag & drop files here · double-click to open · right-click to caption or remove"));
        top.Margin = new Thickness(0, 4, 0, 8);
        SetDock(top, Dock.Top);
        Children.Add(top);
        Children.Add(new ScrollViewer { Content = _tiles, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Loaded += async (_, _) => await Dialogs.Guard(LoadAsync);
    }

    private async Task AddAsync(IEnumerable<string> files)
    {
        foreach (var f in files.Where(File.Exists))
        {
            var video = Path.GetExtension(f).ToLowerInvariant() is ".mp4" or ".mov" or ".avi" or ".wmv" or ".mkv";
            await Desk.ImportMediaAsync(f, _owner, _ownerId(), video ? MediaKind.Video : MediaKind.Photo);
        }
        await LoadAsync();
    }

    public async Task LoadAsync()
    {
        var id = _ownerId();
        var items = (await Desk.Db.ListAsync<MediaItem>(m => m.OwnerId == id)).OrderBy(m => m.CapturedAtUtc).ToList();
        _tiles.Children.Clear();
        if (items.Count == 0) _tiles.Children.Add(W.Muted("No photos or videos yet."));
        foreach (var m in items)
        {
            var path = Desk.Db.MediaPath(m.FileName);
            UIElement visual = m.Kind == MediaKind.Video || !File.Exists(path)
                ? new TextBlock { Text = m.Kind == MediaKind.Video ? "▶ Video" : "(syncing…)", Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                : new Image { Source = DprEditorView.Thumb(path), Stretch = Stretch.UniformToFill };
            var caption = new TextBlock { Text = m.Caption, TextTrimming = TextTrimming.CharacterEllipsis, Width = 160, FontSize = 11 };
            var tile = new StackPanel { Margin = new Thickness(0, 0, 10, 10), Width = 160 };
            tile.Children.Add(new Border { Width = 160, Height = 120, Background = Theme.PrimaryDark, Child = visual, CornerRadius = new CornerRadius(4), ClipToBounds = true });
            tile.Children.Add(caption);
            tile.Children.Add(W.Muted(m.CapturedAtUtc.ToLocalTime().ToString("dd-MMM HH:mm"), 10));
            var item = m;
            tile.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) Dialogs.Open(path); };
            var menu = new ContextMenu();
            var cap = new MenuItem { Header = "Caption…" };
            cap.Click += async (_, _) => { var t = Dialogs.Prompt("Caption", "Caption", item.Caption); if (t != null) { item.Caption = t; await Desk.Db.SaveAsync(item); caption.Text = t; } };
            var del = new MenuItem { Header = "Remove" };
            del.Click += async (_, _) => { if (Dialogs.Confirm("Remove this photo/video?")) { await Desk.Db.DeleteAsync(item); await LoadAsync(); } };
            menu.Items.Add(cap);
            menu.Items.Add(del);
            tile.ContextMenu = menu;
            _tiles.Children.Add(tile);
        }
        CountChanged?.Invoke(items.Count);
    }

    /// <summary>Opens a window with the photos &amp; videos of one record.</summary>
    public static void ShowWindow(string title, MediaOwner owner, string ownerId)
    {
        var w = new Window
        {
            Title = $"Photos & videos – {title}", Width = 900, Height = 600, Owner = Application.Current.MainWindow,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = new Border { Padding = new Thickness(12), Child = new MediaPanel(owner, () => ownerId) },
        };
        if (Dialogs.Headless) return;
        w.Show();
    }
}
