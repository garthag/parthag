using Microsoft.Maui.Controls.Shapes;
using SiteNotes.App.Pages;
using SiteNotes.App.Services;
using SiteNotes.Core.Models;

namespace SiteNotes.App.UI;

/// <summary>Text box with a ▾ button listing common values; any other text can still be typed.</summary>
public class Combo : Grid
{
    public Entry Entry { get; }

    public Combo(string? value, IEnumerable<string> options, Action<string> changed, string placeholder = "")
    {
        var opts = options.ToArray();
        ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)];
        Entry = new Entry { Text = value, Placeholder = placeholder };
        Entry.TextChanged += (_, a) => changed(a.NewTextValue ?? "");
        var pick = new Button { Text = "▾", WidthRequest = 36, Padding = 0, BackgroundColor = Colors.Transparent, TextColor = Ui.Primary, FontSize = 16 };
        pick.Clicked += async (_, _) =>
        {
            var page = Shell.Current?.CurrentPage ?? Application.Current?.Windows[0].Page;
            if (page == null || opts.Length == 0) return;
            var choice = await page.DisplayActionSheetAsync(placeholder == "" ? "Choose" : placeholder, "Cancel", null, opts);
            if (choice != null && choice != "Cancel") Entry.Text = choice;
        };
        this.Add(Entry, 0);
        this.Add(pick, 1);
    }
}

/// <summary>Collapsible card section with a header and an optional count badge.</summary>
public class Section : VerticalStackLayout
{
    private readonly VerticalStackLayout _body = new() { Spacing = 8, Padding = new Thickness(0, 8, 0, 0) };
    private readonly Label _arrow = new() { Text = "▾", FontSize = 16, TextColor = Ui.Primary, VerticalOptions = LayoutOptions.Center };
    private readonly Label _badge = new() { FontSize = 12, TextColor = Ui.Muted, VerticalOptions = LayoutOptions.Center };

    public Section(string title, bool expanded = true)
    {
        var header = new Grid
        {
            ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Auto)],
            ColumnSpacing = 8,
        };
        header.Add(new Label { Text = title, FontSize = 16, FontAttributes = FontAttributes.Bold, TextColor = Ui.Primary }, 0);
        header.Add(_badge, 1);
        header.Add(_arrow, 2);
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => Expanded = !Expanded;
        header.GestureRecognizers.Add(tap);
        Children.Add(Ui.Card(new VerticalStackLayout { Children = { header, _body } }));
        Expanded = expanded;
    }

    public bool Expanded
    {
        get => _body.IsVisible;
        set { _body.IsVisible = value; _arrow.Text = value ? "▾" : "▸"; }
    }

    public string Badge { set => _badge.Text = value; }

    public Section With(params View[] views)
    {
        foreach (var v in views) _body.Children.Add(v);
        return this;
    }

    public void ClearBody() => _body.Children.Clear();
}

/// <summary>Editable list of rows (manpower, machines…). Each row is a card with fields and a delete button.</summary>
public class RowList<T> : VerticalStackLayout where T : class
{
    private readonly List<T> _items;
    private readonly Func<T, View> _buildRow;
    private readonly Action _changed;
    private readonly VerticalStackLayout _rows = new() { Spacing = 6 };
    private readonly string _emptyText;

    /// <summary>Wraps a plain factory for an add button.</summary>
    public static Func<Task<T?>> Now(Func<T?> f) => () => Task.FromResult(f());

    public RowList(List<T> items, Func<T, View> buildRow, Action changed, string emptyText, params (string Text, Func<Task<T?>> Factory)[] addButtons)
    {
        _items = items;
        _buildRow = buildRow;
        _changed = changed;
        _emptyText = emptyText;
        Spacing = 8;
        Children.Add(_rows);
        var buttons = new FlexLayout { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap };
        foreach (var (text, factory) in addButtons)
        {
            var b = Ui.Button(text, async () =>
            {
                var item = await factory();
                if (item == null) return;
                _items.Add(item);
                Rebuild();
                _changed();
            }, primary: false);
            b.Margin = new Thickness(0, 0, 8, 4);
            buttons.Children.Add(b);
        }
        Children.Add(buttons);
        Rebuild();
    }

    public void Add(T item)
    {
        _items.Add(item);
        Rebuild();
        _changed();
    }

    public void Rebuild()
    {
        _rows.Children.Clear();
        if (_items.Count == 0) _rows.Children.Add(Ui.Caption(_emptyText));
        for (var i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            var del = Ui.Tool("✕", () => { _items.Remove(item); Rebuild(); _changed(); }, "Remove row", 34);
            del.TextColor = Ui.Danger;
            var header = new Grid { ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)] };
            header.Add(new Label { Text = $"#{i + 1}", FontAttributes = FontAttributes.Bold, TextColor = Ui.Muted, VerticalOptions = LayoutOptions.Center }, 0);
            header.Add(del, 1);
            var card = new Border
            {
                Padding = 10, StrokeThickness = 1, StrokeShape = new RoundRectangle { CornerRadius = 8 },
                Content = new VerticalStackLayout { Spacing = 6, Children = { header, _buildRow(item) } },
            };
            card.SetAppThemeColor(Border.StrokeProperty, Color.FromArgb("#D8DEE6"), Color.FromArgb("#353C45"));
            _rows.Children.Add(card);
        }
    }
}

/// <summary>Thumbnails of the photos/videos attached to something, with camera / gallery / video buttons.</summary>
public class MediaStrip : VerticalStackLayout
{
    private readonly MediaOwner _owner;
    private readonly Func<string> _ownerId;
    private readonly FlexLayout _thumbs = new() { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap };
    private List<MediaItem> _items = [];
    public event Action<int>? CountChanged;

    public MediaStrip(MediaOwner owner, Func<string> ownerId)
    {
        _owner = owner;
        _ownerId = ownerId;
        Spacing = 8;
        Children.Add(_thumbs);
        Children.Add(new FlexLayout
        {
            Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap,
            Children =
            {
                Spaced(Ui.Button("📷 Photo", async () => { if (await MediaService.CapturePhotoAsync(_owner, _ownerId()) is { } m) await AddAsync(m); }, false)),
                Spaced(Ui.Button("🖼 Gallery", async () => { foreach (var m in await MediaService.PickPhotosAsync(_owner, _ownerId())) await AddAsync(m); }, false)),
                Spaced(Ui.Button("🎥 Video", async () => { if (await MediaService.CaptureVideoAsync(_owner, _ownerId()) is { } m) await AddAsync(m); }, false)),
            }
        });
        _ = LoadAsync();
    }

    private static View Spaced(View v) { v.Margin = new Thickness(0, 0, 8, 4); return v; }

    public async Task LoadAsync()
    {
        var id = _ownerId();
        _items = (await AppServices.Db.ListAsync<MediaItem>(m => m.OwnerId == id)).OrderBy(m => m.CapturedAtUtc).ToList();
        Render();
    }

    private Task AddAsync(MediaItem m)
    {
        // No caption prompt: capture stays one tap. Tap a thumbnail to add a caption later.
        _items.Add(m);
        Render();
        return Task.CompletedTask;
    }

    private static Image ThumbImage(MediaItem m)
    {
        var img = new Image { Aspect = Aspect.AspectFill };
        _ = Task.Run(() => MediaService.Thumbnail(m)).ContinueWith(t =>
        {
            if (t.Result is { } path) MainThread.BeginInvokeOnMainThread(() => img.Source = ImageSource.FromFile(path));
        }, TaskScheduler.Default);
        return img;
    }

    private void Render()
    {
        _thumbs.Children.Clear();
        foreach (var m in _items)
        {
            View content = m.Kind == MediaKind.Video
                ? new Label { Text = "▶\nVideo", HorizontalTextAlignment = TextAlignment.Center, VerticalTextAlignment = TextAlignment.Center, TextColor = Colors.White, FontSize = 14 }
                : ThumbImage(m);
            var tile = new Border
            {
                WidthRequest = 104, HeightRequest = 104, Margin = new Thickness(0, 0, 6, 6), StrokeThickness = 0,
                StrokeShape = new RoundRectangle { CornerRadius = 8 }, BackgroundColor = Ui.PrimaryDark, Content = content,
            };
            ToolTipProperties.SetText(tile, m.Caption);
            var tap = new TapGestureRecognizer();
            var item = m;
            tap.Tapped += async (_, _) =>
            {
                await AppShell.Push(new MediaViewerPage(item, async () => { await LoadAsync(); }));
            };
            tile.GestureRecognizers.Add(tap);
            _thumbs.Children.Add(tile);
        }
        CountChanged?.Invoke(_items.Count);
    }
}
