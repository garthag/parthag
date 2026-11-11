using Microsoft.Maui.Controls.Shapes;

namespace SiteNotes.App.UI;

/// <summary>Colours and small builders so pages read like markup, in plain C#.</summary>
public static class Ui
{
    public static readonly Color Primary = Color.FromArgb("#1F4E79");
    public static readonly Color PrimaryDark = Color.FromArgb("#163A5C");
    public static readonly Color Accent = Color.FromArgb("#F59E0B");
    public static readonly Color Danger = Color.FromArgb("#B91C1C");
    public static readonly Color Success = Color.FromArgb("#15803D");
    public static readonly Color Muted = Color.FromArgb("#6B7280");
    public static readonly Color LightSurface = Colors.White;
    public static readonly Color DarkSurface = Color.FromArgb("#1E2329");
    public static readonly Color LightPage = Color.FromArgb("#F3F5F8");
    public static readonly Color DarkPage = Color.FromArgb("#121418");

    public static bool IsDesktop => DeviceInfo.Idiom == DeviceIdiom.Desktop;

    public static T Page<T>(this T page) where T : Page
    {
        page.SetAppThemeColor(VisualElement.BackgroundColorProperty, LightPage, DarkPage);
        return page;
    }

    public static Label Label(string text = "", double size = 14, bool bold = false, Color? color = null) =>
        new Label { Text = text, FontSize = size, FontAttributes = bold ? FontAttributes.Bold : FontAttributes.None, TextColor = color, LineBreakMode = LineBreakMode.WordWrap }
            .Themed(color);

    private static Label Themed(this Label l, Color? color)
    {
        if (color == null) l.SetAppThemeColor(Microsoft.Maui.Controls.Label.TextColorProperty, Color.FromArgb("#111827"), Color.FromArgb("#E5E7EB"));
        return l;
    }

    public static Label Title(string text) => Label(text, 18, true);
    public static Label Caption(string text) => Label(text, 12, false, Muted);

    public static Label SectionHeader(string text) => new Label
    {
        Text = text.ToUpperInvariant(), FontSize = 12, FontAttributes = FontAttributes.Bold, TextColor = Primary,
        Margin = new Thickness(4, 14, 0, 4), CharacterSpacing = 0.8,
    };

    public static Border Card(View content, double padding = 12)
    {
        var b = new Border
        {
            Content = content,
            Padding = padding,
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 10 },
            Shadow = new Shadow { Opacity = 0.08f, Radius = 6, Offset = new Point(0, 2), Brush = Colors.Black },
            Margin = new Thickness(0, 4),
        };
        b.SetAppThemeColor(VisualElement.BackgroundColorProperty, LightSurface, DarkSurface);
        return b;
    }

    public static Button Button(string text, Func<Task> onClick, bool primary = true, Color? color = null)
    {
        var b = new Button
        {
            Text = text,
            CornerRadius = 8,
            Padding = new Thickness(14, 8),
            FontSize = 14,
            BackgroundColor = primary ? color ?? Primary : Colors.Transparent,
            TextColor = primary ? Colors.White : color ?? Primary,
            BorderColor = primary ? Colors.Transparent : color ?? Primary,
            BorderWidth = primary ? 0 : 1,
        };
        b.Clicked += async (_, _) => await Guard(onClick);
        return b;
    }

    public static Button Button(string text, Action onClick, bool primary = true, Color? color = null) =>
        Button(text, () => { onClick(); return Task.CompletedTask; }, primary, color);

    /// <summary>Small square toolbar button (formatting toolbox, row actions).</summary>
    public static Button Tool(string glyph, Func<Task> onClick, string? tooltip = null, double width = 40)
    {
        var b = new Button
        {
            Text = glyph, FontSize = 15, Padding = 0, WidthRequest = width, HeightRequest = 38, MinimumWidthRequest = width,
            CornerRadius = 6, BorderWidth = 0,
        };
        b.SetAppThemeColor(VisualElement.BackgroundColorProperty, Color.FromArgb("#E8EEF5"), Color.FromArgb("#2A313A"));
        b.SetAppThemeColor(Microsoft.Maui.Controls.Button.TextColorProperty, Primary, Color.FromArgb("#D6E4F5"));
        if (tooltip != null) ToolTipProperties.SetText(b, tooltip);
        b.Clicked += async (_, _) => await Guard(onClick);
        return b;
    }

    public static Button Tool(string glyph, Action onClick, string? tooltip = null, double width = 40) =>
        Tool(glyph, () => { onClick(); return Task.CompletedTask; }, tooltip, width);

    /// <summary>Label above an input.</summary>
    public static View Field(string label, View input) =>
        new VerticalStackLayout { Spacing = 2, Children = { Caption(label), input } };

    public static Entry Entry(string? text, string placeholder = "", Keyboard? keyboard = null, Action<string>? changed = null)
    {
        var e = new Entry { Text = text, Placeholder = placeholder, Keyboard = keyboard ?? Keyboard.Text, ClearButtonVisibility = ClearButtonVisibility.WhileEditing };
        if (changed != null) e.TextChanged += (_, a) => changed(a.NewTextValue ?? "");
        return e;
    }

    public static Entry Number(double value, Action<double> changed, string placeholder = "0")
    {
        var e = new Entry { Text = value == 0 ? "" : value.ToString("0.##"), Placeholder = placeholder, Keyboard = Keyboard.Numeric, HorizontalTextAlignment = TextAlignment.End };
        e.TextChanged += (_, a) => changed(double.TryParse(a.NewTextValue, out var v) ? v : 0);
        return e;
    }

    public static Editor Memo(string? text, string placeholder, Action<string> changed, double height = 90)
    {
        var e = new Editor { Text = text, Placeholder = placeholder, AutoSize = EditorAutoSizeOption.TextChanges, MinimumHeightRequest = height };
        e.TextChanged += (_, a) => changed(a.NewTextValue ?? "");
        return e;
    }

    public static Picker Picker(IList<string> items, string? selected, Action<string> changed, string title = "Select")
    {
        var list = items.ToList();
        if (!string.IsNullOrEmpty(selected) && !list.Contains(selected)) list.Insert(0, selected);
        var p = new Picker { Title = title, ItemsSource = list, SelectedItem = selected };
        p.SelectedIndexChanged += (_, _) => { if (p.SelectedItem is string s) changed(s); };
        return p;
    }

    public static Picker EnumPicker<T>(T value, Action<T> changed) where T : struct, Enum
    {
        var names = Enum.GetNames<T>();
        var p = new Picker { ItemsSource = names, SelectedItem = value.ToString() };
        p.SelectedIndexChanged += (_, _) => { if (p.SelectedItem is string s) changed(Enum.Parse<T>(s)); };
        return p;
    }

    public static DatePicker Date(DateTime value, Action<DateTime> changed)
    {
        var d = new DatePicker { Date = value, Format = "dd-MMM-yyyy" };
        d.DateSelected += (_, a) => changed(a.NewDate ?? value);
        return d;
    }

    /// <summary>Optional date: a switch plus a date picker.</summary>
    public static View OptionalDate(DateTime? value, Action<DateTime?> changed)
    {
        var sw = new Switch { IsToggled = value != null, VerticalOptions = LayoutOptions.Center };
        var d = new DatePicker { Date = value ?? DateTime.Today, Format = "dd-MMM-yyyy", IsEnabled = value != null };
        sw.Toggled += (_, a) => { d.IsEnabled = a.Value; changed(a.Value ? d.Date : null); };
        d.DateSelected += (_, a) => { if (sw.IsToggled) changed(a.NewDate); };
        return new HorizontalStackLayout { Spacing = 6, Children = { sw, d } };
    }

    /// <summary>Lays out views in a responsive grid: 1 column on phones, more on wide screens.</summary>
    public static View Columns(int desktopColumns, params View[] views)
    {
        var cols = IsDesktop || DeviceDisplay.Current.MainDisplayInfo.Width / Math.Max(1, DeviceDisplay.Current.MainDisplayInfo.Density) > 700
            ? desktopColumns : Math.Min(2, desktopColumns);
        var g = new Grid { ColumnSpacing = 10, RowSpacing = 8 };
        for (var c = 0; c < cols; c++) g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        for (var i = 0; i < views.Length; i++)
        {
            if (i % cols == 0) g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            g.Add(views[i], i % cols, i / cols);
        }
        return g;
    }

    public static View Chip(string text, Color color)
    {
        var b = new Border
        {
            StrokeThickness = 0, BackgroundColor = color.WithAlpha(0.15f), Padding = new Thickness(8, 2),
            StrokeShape = new RoundRectangle { CornerRadius = 10 }, VerticalOptions = LayoutOptions.Center,
            Content = new Label { Text = text, FontSize = 11, TextColor = color, FontAttributes = FontAttributes.Bold },
        };
        return b;
    }

    public static View Stat(string label, string value, Color? color = null)
    {
        var v = new VerticalStackLayout
        {
            Spacing = 0,
            Children =
            {
                new Label { Text = value, FontSize = 24, FontAttributes = FontAttributes.Bold, TextColor = color ?? Primary },
                Caption(label),
            }
        };
        return Card(v, 10);
    }

    public static ScrollView Scroll(View content, double padding = 12) =>
        new() { Content = new VerticalStackLayout { Padding = padding, Spacing = 6, Children = { content } } };

    public static async Task Guard(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex)
        {
            Console.WriteLine("SITENOTES_ERROR " + ex);
            // the failing code may have run on a worker thread: a dialog can only be shown from the UI thread
            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                var page = Application.Current?.Windows.FirstOrDefault()?.Page;
                if (page != null) await page.DisplayAlertAsync("Something went wrong", ex.Message, "OK");
            });
        }
    }

    public static Task Toast(string message)
    {
        return MainThread.InvokeOnMainThreadAsync(() =>
        {
            var page = Application.Current?.Windows.FirstOrDefault()?.Page;
            return page?.DisplayAlertAsync("", message, "OK") ?? Task.CompletedTask;
        });
    }

    public static Color StatusColor(string status) => status switch
    {
        "Approved" or "Resolved" or "Received" => Success,
        "Submitted" or "Ordered" => Primary,
        "Open" or "Pending" or "Urgent" => Danger,
        "Cancelled" => Muted,
        _ => Accent,
    };
}
