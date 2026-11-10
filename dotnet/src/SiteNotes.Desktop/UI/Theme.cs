using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SiteNotes.Desktop.UI;

/// <summary>Colours, fonts and default control styles, defined in C#.</summary>
public static class Theme
{
    public static readonly SolidColorBrush Primary = Freeze(Color.FromRgb(0x1F, 0x4E, 0x79));
    public static readonly SolidColorBrush PrimaryDark = Freeze(Color.FromRgb(0x16, 0x3A, 0x5C));
    public static readonly SolidColorBrush PrimaryLight = Freeze(Color.FromRgb(0xDC, 0xE8, 0xF5));
    public static readonly SolidColorBrush Accent = Freeze(Color.FromRgb(0xF5, 0x9E, 0x0B));
    public static readonly SolidColorBrush Success = Freeze(Color.FromRgb(0x15, 0x80, 0x3D));
    public static readonly SolidColorBrush Danger = Freeze(Color.FromRgb(0xB9, 0x1C, 0x1C));
    public static readonly SolidColorBrush Muted = Freeze(Color.FromRgb(0x6B, 0x72, 0x80));
    public static readonly SolidColorBrush Page = Freeze(Color.FromRgb(0xF3, 0xF5, 0xF8));
    public static readonly SolidColorBrush Card = Freeze(Colors.White);
    public static readonly SolidColorBrush Line = Freeze(Color.FromRgb(0xD8, 0xDE, 0xE6));
    public static readonly SolidColorBrush Zebra = Freeze(Color.FromRgb(0xF7, 0xF9, 0xFB));
    public static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private static SolidColorBrush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public static SolidColorBrush StatusBrush(string status) => status switch
    {
        "Approved" or "Resolved" or "Received" or "Completed" => Success,
        "Submitted" or "Ordered" or "InProgress" or "In progress" => Primary,
        "Open" or "Pending" or "Urgent" or "Delayed" => Danger,
        "Cancelled" or "NotStarted" or "Not started" => Muted,
        _ => Accent,
    };

    public static void Apply(Application app)
    {
        var r = app.Resources;
        r[typeof(Window)] = Style<Window>(s =>
        {
            s.Set(Control.FontFamilyProperty, new FontFamily("Segoe UI"));
            s.Set(Control.FontSizeProperty, 13.0);
            s.Set(Control.BackgroundProperty, Page);
        });
        r[typeof(Button)] = Style<Button>(s =>
        {
            s.Set(Control.PaddingProperty, new Thickness(12, 5, 12, 5));
            s.Set(FrameworkElement.MarginProperty, new Thickness(0, 0, 6, 0));
            s.Set(FrameworkElement.CursorProperty, System.Windows.Input.Cursors.Hand);
        });
        r[typeof(TextBox)] = Style<TextBox>(s =>
        {
            s.Set(Control.PaddingProperty, new Thickness(4, 3, 4, 3));
            s.Set(FrameworkElement.MinHeightProperty, 26.0);
            s.Set(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center);
        });
        r[typeof(ComboBox)] = Style<ComboBox>(s => s.Set(FrameworkElement.MinHeightProperty, 26.0));
        r[typeof(DatePicker)] = Style<DatePicker>(s => s.Set(FrameworkElement.MinHeightProperty, 26.0));
        r[typeof(DataGrid)] = Style<DataGrid>(s =>
        {
            s.Set(DataGrid.AutoGenerateColumnsProperty, false);
            s.Set(DataGrid.RowHeightProperty, 28.0);
            s.Set(DataGrid.GridLinesVisibilityProperty, DataGridGridLinesVisibility.Horizontal);
            s.Set(DataGrid.HorizontalGridLinesBrushProperty, Line);
            s.Set(DataGrid.HeadersVisibilityProperty, DataGridHeadersVisibility.Column);
            s.Set(DataGrid.AlternatingRowBackgroundProperty, Zebra);
            s.Set(DataGrid.CanUserResizeRowsProperty, false);
            s.Set(Control.BorderBrushProperty, Line);
            s.Set(Control.BackgroundProperty, Card);
            s.Set(DataGrid.SelectionUnitProperty, DataGridSelectionUnit.FullRow);
            s.Set(DataGrid.EnableRowVirtualizationProperty, true);
            s.Set(VirtualizingPanel.IsVirtualizingProperty, true);
            s.Set(VirtualizingPanel.VirtualizationModeProperty, VirtualizationMode.Recycling);
        });
        r[typeof(System.Windows.Controls.Primitives.DataGridColumnHeader)] = Style<System.Windows.Controls.Primitives.DataGridColumnHeader>(s =>
        {
            s.Set(Control.BackgroundProperty, Primary);
            s.Set(Control.ForegroundProperty, Brushes.White);
            s.Set(Control.FontWeightProperty, FontWeights.SemiBold);
            s.Set(Control.PaddingProperty, new Thickness(6, 5, 6, 5));
            s.Set(Control.BorderBrushProperty, PrimaryDark);
            s.Set(Control.BorderThicknessProperty, new Thickness(0, 0, 1, 0));
        });
        r[typeof(TabItem)] = Style<TabItem>(s => s.Set(Control.PaddingProperty, new Thickness(10, 4, 6, 4)));
        r[typeof(GroupBox)] = Style<GroupBox>(s =>
        {
            s.Set(Control.PaddingProperty, new Thickness(8));
            s.Set(FrameworkElement.MarginProperty, new Thickness(0, 0, 0, 10));
            s.Set(Control.BorderBrushProperty, Line);
            s.Set(Control.FontWeightProperty, FontWeights.Normal);
        });
    }

    private static Style Style<T>(Action<StyleBuilder> build)
    {
        var s = new Style(typeof(T));
        build(new StyleBuilder(s));
        s.Seal();
        return s;
    }

    private sealed class StyleBuilder(Style style)
    {
        public void Set(DependencyProperty p, object value) => style.Setters.Add(new Setter(p, value));
    }

    /// <summary>A monochrome icon from the Windows icon font as an ImageSource (for ribbon buttons).</summary>
    public static ImageSource Icon(string glyph, double size = 32, Brush? brush = null)
    {
        var text = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(Icons, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), size, brush ?? Primary, 1.0);
        var geo = text.BuildGeometry(new Point(0, 0));
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, size, size))));
        drawing.Children.Add(new GeometryDrawing(brush ?? Primary, null, geo));
        var img = new DrawingImage(drawing);
        img.Freeze();
        return img;
    }
}

/// <summary>Icon glyphs (Segoe MDL2 / Fluent icon font, built into Windows 10 and 11).</summary>
public static class Glyph
{
    public const string Home = "", Add = "", Save = "", Delete = "", Calendar = "", Settings = "",
        Sync = "", Camera = "", Filter = "", Refresh = "", People = "", Folder = "",
        Warning = "", Document = "", Print = "", Edit = "", Up = "", Down = "",
        Bold = "", Italic = "", Underline = "", List = "", AlignLeft = "", AlignCenter = "",
        AlignRight = "", Font = "", FontColor = "", Highlight = "", Share = "", Photo = "",
        Video = "", Table = "", Chart = "", Calculator = "", Hierarchy = "", Package = "",
        Contact = "", Org = "", Note = "", Scan = "", Checkbox = "", Today = "", Open = "",
        Excel = "", Accept = "", Link = "";
}
