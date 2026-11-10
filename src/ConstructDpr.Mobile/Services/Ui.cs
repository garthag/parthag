using System.Globalization;
using ConstructDpr.Shared.Model;
using ConstructDpr.Shared.Scheduling;
using ConstructDpr.Shared.Security;
using Microsoft.Maui.Controls.Shapes;

namespace ConstructDpr.Mobile.Services;

/// <summary>Colours, text styles and small building blocks shared by every screen (all in C#).</summary>
public static class Ui
{
    public static readonly Color Brand = Color.FromArgb("#0F3D5E");
    public static readonly Color BrandDark = Color.FromArgb("#0B2236");
    public static readonly Color BrandMid = Color.FromArgb("#1F6FA3");
    public static readonly Color Accent = Color.FromArgb("#F5A524");
    public static readonly Color Page = Color.FromArgb("#F4F6F8");
    public static readonly Color CardColor = Colors.White;
    public static readonly Color Line = Color.FromArgb("#DDE3E9");
    public static readonly Color Text = Color.FromArgb("#1D2730");
    public static readonly Color Muted = Color.FromArgb("#5B6670");
    public static readonly Color Good = Color.FromArgb("#0F7B3E");
    public static readonly Color Warn = Color.FromArgb("#B26B00");
    public static readonly Color Bad = Color.FromArgb("#C62828");
    public static readonly Color Crit = Color.FromArgb("#D84B4B");

    private static readonly CultureInfo In = CultureInfo.GetCultureInfo("en-IN");

    public static string N(decimal? v, int dp = 2) => v is null ? "—" : Math.Round(v.Value, dp).ToString(dp == 0 ? "#,##0" : "#,##0." + new string('#', dp), In);
    public static string N(double? v, int dp = 1) => v is null ? "—" : Math.Round(v.Value, dp).ToString(dp == 0 ? "#,##0" : "#,##0." + new string('#', dp), In);
    public static string Money(decimal v) => "₹ " + Math.Round(v).ToString("#,##0", In);
    public static string D(DateOnly? d) => d?.ToString("dd MMM yyyy", CultureInfo.InvariantCulture) ?? "—";
    public static decimal? ParseDec(string? s) =>
        decimal.TryParse((s ?? "").Replace(",", "").Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : null;

    public static Color StatusColor(DprStatus s) => s switch
    {
        DprStatus.Approved => Good, DprStatus.Submitted => Color.FromArgb("#C98500"), DprStatus.Rejected => Bad, _ => Color.FromArgb("#6B7782"),
    };

    public static Label H1(string text) => new() { Text = text, FontSize = 22, FontAttributes = FontAttributes.Bold, TextColor = BrandDark };
    public static Label H2(string text) => new() { Text = text, FontSize = 16, FontAttributes = FontAttributes.Bold, TextColor = BrandMid, Margin = new Thickness(0, 4, 0, 4) };
    public static Label Small(string text, Color? color = null) => new() { Text = text, FontSize = 13, TextColor = color ?? Muted };
    public static Label Body(string text) => new() { Text = text, FontSize = 15, TextColor = Text };

    public static Border Card(View content, Color? leftAccent = null) => new()
    {
        Content = content,
        Padding = new Thickness(14),
        BackgroundColor = CardColor,
        Stroke = leftAccent ?? Line,
        StrokeThickness = leftAccent is null ? 1 : 2,
        StrokeShape = new RoundRectangle { CornerRadius = 10 },
        Margin = new Thickness(0, 0, 0, 10),
    };

    public static Border Badge(string text, Color color) => new()
    {
        Content = new Label { Text = text, TextColor = Colors.White, FontSize = 11, FontAttributes = FontAttributes.Bold },
        BackgroundColor = color, Padding = new Thickness(9, 2), StrokeThickness = 0,
        StrokeShape = new RoundRectangle { CornerRadius = 9 }, VerticalOptions = LayoutOptions.Center,
    };

    public static Button Primary(string text, EventHandler onClick) => Btn(text, onClick, Brand, Colors.White);
    public static Button Success(string text, EventHandler onClick) => Btn(text, onClick, Good, Colors.White);
    public static Button Danger(string text, EventHandler onClick) => Btn(text, onClick, Colors.White, Bad, Bad);
    public static Button Plain(string text, EventHandler onClick) => Btn(text, onClick, Colors.White, BrandMid, Color.FromArgb("#B9C6D2"));

    private static Button Btn(string text, EventHandler onClick, Color bg, Color fg, Color? border = null)
    {
        var b = new Button
        {
            Text = text, BackgroundColor = bg, TextColor = fg, CornerRadius = 8, FontSize = 15, FontAttributes = FontAttributes.Bold,
            BorderColor = border ?? bg, BorderWidth = border is null ? 0 : 1, Padding = new Thickness(14, 10), MinimumHeightRequest = 46,
        };
        b.Clicked += onClick;
        return b;
    }

    public static Entry Field(string? text, string placeholder = "", Keyboard? keyboard = null) => new()
    {
        Text = text, Placeholder = placeholder, Keyboard = keyboard ?? Keyboard.Default, FontSize = 16, TextColor = Text, BackgroundColor = Colors.White,
    };

    public static Entry Number(decimal? value, string placeholder = "0") =>
        Field(value?.ToString("0.###", CultureInfo.InvariantCulture), placeholder, Keyboard.Numeric);

    /// <summary>A label above a control.</summary>
    public static View Labelled(string label, View control) => new VerticalStackLayout { Spacing = 2, Children = { Small(label), control } };

    /// <summary>Page scaffold: grey background, scrolling content with padding.</summary>
    public static ScrollView Scroll(params View[] children)
    {
        var stack = new VerticalStackLayout { Padding = new Thickness(14), Spacing = 6 };
        foreach (var c in children) stack.Children.Add(c);
        return new ScrollView { Content = stack, BackgroundColor = Page };
    }

    public static VerticalStackLayout Stack(double spacing = 6) => new() { Spacing = spacing };

    public static Grid Row(View left, View right) => new()
    {
        ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
        ColumnSpacing = 8,
        Children = { left, Place(right, 1) },
    };

    public static View Place(View v, int column, int row = 0)
    {
        Grid.SetColumn(v, column);
        Grid.SetRow(v, row);
        return v;
    }

    /// <summary>Runs an action and shows rule violations, conflicts and connection problems as a message.</summary>
    public static async Task<bool> TryAsync(Page page, Func<Task> work, string? done = null)
    {
        try
        {
            await work();
            if (done is not null) await page.DisplayAlertAsync("CONSTRUCT DPR", done, "OK");
            return true;
        }
        catch (UnauthorizedAccessException ex)
        {
            await page.DisplayAlertAsync("Sign in again", ex.Message, "OK");
            await AppState.SignOutAsync();
        }
        catch (Exception ex) when (ex is WorkflowException or ScheduleException or ConflictException or ForbiddenException
                                       or NotFoundException or HttpRequestException or ArgumentException)
        {
            await page.DisplayAlertAsync("CONSTRUCT DPR", ex.Message, "OK");
        }
        return false;
    }

    public static string Role(ProjectRole r) => ConstructDpr.Shared.Security.Permissions.Label(r);
}
