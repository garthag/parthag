using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using SiteNotes.Desktop.Services;

namespace SiteNotes.Desktop.UI;

/// <summary>Small builders so views read like markup, in plain C#.</summary>
public static class W
{
    public static TextBlock Text(string text, double size = 13, bool bold = false, Brush? color = null, bool wrap = true) => new()
    {
        Text = text, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        Foreground = color ?? Brushes.Black, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center,
    };

    public static TextBlock Muted(string text, double size = 12) => Text(text, size, false, Theme.Muted);

    public static TextBlock Heading(string text) => new()
    {
        Text = text, FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = Theme.Primary, Margin = new Thickness(0, 0, 0, 8),
    };

    /// <summary>Label above a control.</summary>
    public static FrameworkElement Field(string label, UIElement input)
    {
        var p = new StackPanel { Margin = new Thickness(0, 0, 12, 8) };
        p.Children.Add(Muted(label, 11.5));
        p.Children.Add(input);
        return p;
    }

    /// <summary>Lays out fields in a grid with <paramref name="columns"/> equal columns.</summary>
    public static Grid Form(int columns, params FrameworkElement[] items)
    {
        var g = new Grid();
        for (var c = 0; c < columns; c++) g.ColumnDefinitions.Add(new ColumnDefinition());
        for (var i = 0; i < items.Length; i++)
        {
            if (i % columns == 0) g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(items[i], i / columns);
            Grid.SetColumn(items[i], i % columns);
            g.Children.Add(items[i]);
        }
        return g;
    }

    public static TextBox Box(string? value, Action<string> changed, bool multiline = false, double height = 0)
    {
        var t = new TextBox { Text = value ?? "" };
        if (multiline) { t.AcceptsReturn = true; t.TextWrapping = TextWrapping.Wrap; t.MinHeight = height > 0 ? height : 60; t.VerticalContentAlignment = VerticalAlignment.Top; t.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; }
        t.TextChanged += (_, _) => changed(t.Text);
        return t;
    }

    public static TextBox Number(double value, Action<double> changed)
    {
        var t = new TextBox { Text = value == 0 ? "" : value.ToString("0.###", CultureInfo.CurrentCulture), TextAlignment = TextAlignment.Right };
        t.TextChanged += (_, _) => changed(double.TryParse(t.Text, NumberStyles.Any, CultureInfo.CurrentCulture, out var v) ? v : 0);
        return t;
    }

    public static TextBox Money(decimal value, Action<decimal> changed)
    {
        var t = new TextBox { Text = value == 0 ? "" : value.ToString("0.00", CultureInfo.CurrentCulture), TextAlignment = TextAlignment.Right };
        t.TextChanged += (_, _) => changed(decimal.TryParse(t.Text, NumberStyles.Any, CultureInfo.CurrentCulture, out var v) ? v : 0);
        return t;
    }

    public static DatePicker Date(DateTime? value, Action<DateTime?> changed)
    {
        var d = new DatePicker { SelectedDate = value, SelectedDateFormat = DatePickerFormat.Short };
        d.SelectedDateChanged += (_, _) => changed(d.SelectedDate);
        return d;
    }

    public static ComboBox Combo<T>(IEnumerable<T> items, T? selected, Action<T> changed, Func<T, string>? text = null)
    {
        var list = items.ToList();
        var c = new ComboBox { ItemsSource = list.Select(i => new ComboItem<T>(i, text?.Invoke(i) ?? i?.ToString() ?? "")).ToList() };
        c.SelectedIndex = selected == null ? -1 : list.FindIndex(i => Equals(i, selected));
        c.SelectionChanged += (_, _) => { if (c.SelectedItem is ComboItem<T> ci) changed(ci.Value); };
        return c;
    }

    /// <summary>Editable combo: pick a common value or type your own.</summary>
    public static ComboBox Suggest(string? value, IEnumerable<string> options, Action<string> changed)
    {
        var c = new ComboBox { IsEditable = true, ItemsSource = options.ToList(), Text = value ?? "" };
        c.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => changed(c.Text)));
        c.SelectionChanged += (_, _) => { if (c.SelectedItem is string s) changed(s); };
        return c;
    }

    public static CheckBox Check(string text, bool value, Action<bool> changed)
    {
        var c = new CheckBox { Content = text, IsChecked = value, Margin = new Thickness(0, 4, 12, 4), VerticalAlignment = VerticalAlignment.Center };
        c.Checked += (_, _) => changed(true);
        c.Unchecked += (_, _) => changed(false);
        return c;
    }

    public static Button Button(string text, Func<Task> action, string? glyph = null, bool primary = false)
    {
        var b = new Button { Content = glyph == null ? text : IconText(glyph, text, primary ? Brushes.White : Theme.Primary) };
        if (primary) { b.Background = Theme.Primary; b.Foreground = Brushes.White; b.BorderBrush = Theme.PrimaryDark; }
        b.Click += async (_, _) => await Dialogs.Guard(action);
        return b;
    }

    public static Button Button(string text, Action action, string? glyph = null, bool primary = false) =>
        Button(text, () => { action(); return Task.CompletedTask; }, glyph, primary);

    public static StackPanel IconText(string glyph, string text, Brush? color = null)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal };
        p.Children.Add(new TextBlock { Text = glyph, FontFamily = Theme.Icons, FontSize = 14, Foreground = color ?? Theme.Primary, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        p.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        return p;
    }

    /// <summary>Toolbar with buttons (icon + label). Null entries become separators.</summary>
    public static ToolBar ToolBar(params (string Glyph, string Text, Func<Task> Action)?[] buttons)
    {
        var t = new ToolBar { Background = Brushes.White, Padding = new Thickness(4, 2, 4, 2) };
        ToolBarTray.SetIsLocked(t, true);
        foreach (var b in buttons)
        {
            if (b == null) { t.Items.Add(new Separator()); continue; }
            var (glyph, text, action) = b.Value;
            var btn = new Button { Content = IconText(glyph, text), Padding = new Thickness(8, 4, 8, 4), ToolTip = text };
            btn.Click += async (_, _) => await Dialogs.Guard(action);
            t.Items.Add(btn);
        }
        return t;
    }

    public static Border Card(UIElement content, double padding = 12) => new()
    {
        Background = Theme.Card, BorderBrush = Theme.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
        Padding = new Thickness(padding), Margin = new Thickness(0, 0, 10, 10), Child = content,
    };

    /// <summary>Big number tile.</summary>
    public static Border Stat(string label, string value, Brush? color = null)
    {
        var p = new StackPanel();
        p.Children.Add(new TextBlock { Text = value, FontSize = 24, FontWeight = FontWeights.SemiBold, Foreground = color ?? Theme.Primary });
        p.Children.Add(Muted(label));
        var c = Card(p, 10);
        c.MinWidth = 150;
        return c;
    }

    public static WrapPanel Wrap(params UIElement[] items)
    {
        var w = new WrapPanel();
        foreach (var i in items) w.Children.Add(i);
        return w;
    }

    public static ScrollViewer Scroll(UIElement content, double padding = 14) => new()
    {
        Content = new Border { Padding = new Thickness(padding), Child = content },
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
    };

    public static StackPanel Stack(params UIElement[] items)
    {
        var s = new StackPanel();
        foreach (var i in items) s.Children.Add(i);
        return s;
    }

    /// <summary>Row of toggle buttons that behave like radio buttons (one tap selects).</summary>
    public static WrapPanel Choice<T>(IEnumerable<(T Value, string Text)> items, T selected, Action<T> changed) where T : notnull
    {
        var p = new WrapPanel();
        var buttons = new List<(T, ToggleButton)>();
        foreach (var (value, text) in items)
        {
            var b = new ToggleButton { Content = text, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 4, 4), IsChecked = Equals(value, selected) };
            var v = value;
            b.Click += (_, _) =>
            {
                foreach (var (_, other) in buttons) other.IsChecked = ReferenceEquals(other, b);
                changed(v);
            };
            buttons.Add((value, b));
            p.Children.Add(b);
        }
        return p;
    }

    /// <summary>Row of toggle buttons for multi-selection.</summary>
    public static WrapPanel Multi<T>(IEnumerable<(T Value, string Text)> items, ICollection<T> selected, Action changed)
    {
        var p = new WrapPanel();
        foreach (var (value, text) in items)
        {
            var b = new ToggleButton { Content = text, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 4, 4), IsChecked = selected.Contains(value) };
            var v = value;
            b.Click += (_, _) =>
            {
                if (b.IsChecked == true) { if (!selected.Contains(v)) selected.Add(v); }
                else selected.Remove(v);
                changed();
            };
            p.Children.Add(b);
        }
        return p;
    }
}

public record ComboItem<T>(T Value, string Text)
{
    public override string ToString() => Text;
}

/// <summary>Id + display name for lookup columns.</summary>
public record Pick(string Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>Builds an editable DataGrid column by column, binding straight to model properties.</summary>
public class GridBuilder
{
    public DataGrid Grid { get; }

    public GridBuilder(IEnumerable items, bool editable = true, bool multiSelect = true)
    {
        Grid = new DataGrid
        {
            ItemsSource = items, IsReadOnly = !editable, CanUserAddRows = false, CanUserDeleteRows = false,
            SelectionMode = multiSelect ? DataGridSelectionMode.Extended : DataGridSelectionMode.Single,
            ClipboardCopyMode = DataGridClipboardCopyMode.IncludeHeader,
        };
    }

    private static Binding Bind(string path, string? format = null) => new(path)
    {
        Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus, StringFormat = format,
        ConverterCulture = CultureInfo.CurrentCulture,
    };

    private DataGridLength Len(double w) => w <= 0 ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(w);

    public GridBuilder Text(string header, string path, double width = 0, bool readOnly = false, bool wrap = false)
    {
        var col = new DataGridTextColumn { Header = header, Binding = readOnly ? new Binding(path) { Mode = BindingMode.OneWay } : Bind(path), Width = Len(width), IsReadOnly = readOnly };
        if (wrap) col.ElementStyle = new Style(typeof(TextBlock)) { Setters = { new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap) } };
        Grid.Columns.Add(col);
        return this;
    }

    public GridBuilder Number(string header, string path, double width = 80, string format = "#,0.###", bool readOnly = false)
    {
        var right = new Style(typeof(TextBlock)) { Setters = { new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right), new Setter(FrameworkElement.MarginProperty, new Thickness(0, 0, 4, 0)) } };
        Grid.Columns.Add(new DataGridTextColumn
        {
            Header = header, Binding = readOnly ? new Binding(path) { Mode = BindingMode.OneWay, StringFormat = format } : Bind(path, format),
            Width = Len(width), IsReadOnly = readOnly, ElementStyle = right,
        });
        return this;
    }

    public GridBuilder Check(string header, string path, double width = 70)
    {
        Grid.Columns.Add(new DataGridCheckBoxColumn { Header = header, Binding = new Binding(path) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = Len(width) });
        return this;
    }

    public GridBuilder Enum<T>(string header, string path, double width = 110) where T : struct, System.Enum
    {
        Grid.Columns.Add(new DataGridComboBoxColumn { Header = header, ItemsSource = System.Enum.GetValues<T>(), SelectedItemBinding = new Binding(path) { Mode = BindingMode.TwoWay }, Width = Len(width) });
        return this;
    }

    /// <summary>Drop-down of another table (stores the id).</summary>
    public GridBuilder Lookup(string header, string idPath, IEnumerable<Pick> items, double width = 160)
    {
        Grid.Columns.Add(new DataGridComboBoxColumn
        {
            Header = header, ItemsSource = items.ToList(), DisplayMemberPath = nameof(Pick.Name), SelectedValuePath = nameof(Pick.Id),
            SelectedValueBinding = new Binding(idPath) { Mode = BindingMode.TwoWay }, Width = Len(width),
        });
        return this;
    }

    /// <summary>Editable text with a list of common values.</summary>
    public GridBuilder Suggest(string header, string path, IEnumerable<string> options, double width = 140)
    {
        var display = new FrameworkElementFactory(typeof(TextBlock));
        display.SetBinding(TextBlock.TextProperty, new Binding(path));
        display.SetValue(FrameworkElement.MarginProperty, new Thickness(4, 0, 4, 0));
        display.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        var edit = new FrameworkElementFactory(typeof(ComboBox));
        edit.SetValue(ComboBox.IsEditableProperty, true);
        edit.SetValue(ItemsControl.ItemsSourceProperty, options.ToList());
        edit.SetBinding(ComboBox.TextProperty, new Binding(path) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        Grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = header, Width = Len(width), SortMemberPath = path,
            CellTemplate = new DataTemplate { VisualTree = display },
            CellEditingTemplate = new DataTemplate { VisualTree = edit },
        });
        return this;
    }

    public GridBuilder Date(string header, string path, double width = 115)
    {
        var display = new FrameworkElementFactory(typeof(TextBlock));
        display.SetBinding(TextBlock.TextProperty, new Binding(path) { StringFormat = "dd-MMM-yyyy" });
        display.SetValue(FrameworkElement.MarginProperty, new Thickness(4, 0, 4, 0));
        display.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        var edit = new FrameworkElementFactory(typeof(DatePicker));
        edit.SetBinding(DatePicker.SelectedDateProperty, new Binding(path) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        Grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = header, Width = Len(width), SortMemberPath = path,
            CellTemplate = new DataTemplate { VisualTree = display },
            CellEditingTemplate = new DataTemplate { VisualTree = edit },
        });
        return this;
    }

    /// <summary>Read-only progress bar column bound to a 0–100 value.</summary>
    public GridBuilder Progress(string header, string path, double width = 130)
    {
        var grid = new FrameworkElementFactory(typeof(Grid));
        var bar = new FrameworkElementFactory(typeof(ProgressBar));
        bar.SetBinding(RangeBase.ValueProperty, new Binding(path) { Mode = BindingMode.OneWay });
        bar.SetValue(RangeBase.MaximumProperty, 100.0);
        bar.SetValue(FrameworkElement.HeightProperty, 16.0);
        bar.SetValue(Control.ForegroundProperty, Theme.Primary);
        bar.SetValue(FrameworkElement.MarginProperty, new Thickness(3, 0, 3, 0));
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(path) { Mode = BindingMode.OneWay, StringFormat = "{0:0.#} %" });
        text.SetValue(TextBlock.FontSizeProperty, 11.0);
        text.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        text.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        grid.AppendChild(bar);
        grid.AppendChild(text);
        Grid.Columns.Add(new DataGridTemplateColumn { Header = header, Width = Len(width), IsReadOnly = true, SortMemberPath = path, CellTemplate = new DataTemplate { VisualTree = grid } });
        return this;
    }

    /// <summary>Coloured status text (read-only).</summary>
    public GridBuilder Status(string header, string path, double width = 100)
    {
        var t = new FrameworkElementFactory(typeof(TextBlock));
        t.SetBinding(TextBlock.TextProperty, new Binding(path) { Mode = BindingMode.OneWay });
        t.SetBinding(TextBlock.ForegroundProperty, new Binding(path) { Mode = BindingMode.OneWay, Converter = StatusBrushConverter.Instance });
        t.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        t.SetValue(FrameworkElement.MarginProperty, new Thickness(4, 0, 4, 0));
        t.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        Grid.Columns.Add(new DataGridTemplateColumn { Header = header, Width = Len(width), IsReadOnly = true, SortMemberPath = path, CellTemplate = new DataTemplate { VisualTree = t } });
        return this;
    }

    /// <summary>Commits the edit in progress and runs <paramref name="after"/> once the row is updated (for totals, saving).</summary>
    public GridBuilder OnEdited(Action<object> after)
    {
        Grid.CellEditEnding += (_, e) =>
        {
            if (e.EditAction != DataGridEditAction.Commit) return;
            var item = e.Row.Item;
            Grid.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => after(item));
        };
        return this;
    }
}

public sealed class StatusBrushConverter : IValueConverter
{
    public static readonly StatusBrushConverter Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Theme.StatusBrush(value?.ToString() ?? "");
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
