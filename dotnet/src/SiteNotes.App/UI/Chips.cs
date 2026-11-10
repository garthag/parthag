using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Layouts;

namespace SiteNotes.App.UI;

/// <summary>
/// A row of tap-to-select chips. Single mode works like radio buttons; multi mode like check boxes.
/// One tap, no drop-down: the fastest way to pick on a phone and with a mouse.
/// </summary>
public class ChipGroup<T> : FlexLayout where T : notnull
{
    private readonly List<(T Value, Border Chip, Label Text)> _chips = [];
    private readonly HashSet<T> _selected = [];
    private readonly bool _multi;
    private readonly bool _allowNone;

    public event Action? Changed;

    public ChipGroup(IEnumerable<(T Value, string Text)> items, IEnumerable<T> selected, bool multi = false, bool allowNone = false)
    {
        Wrap = FlexWrap.Wrap;
        _multi = multi;
        _allowNone = allowNone || multi;
        foreach (var s in selected) _selected.Add(s);
        foreach (var (value, text) in items)
        {
            var label = new Label { Text = text, FontSize = 13, VerticalTextAlignment = TextAlignment.Center };
            var chip = new Border
            {
                Padding = new Thickness(12, 6), Margin = new Thickness(0, 0, 6, 6), StrokeThickness = 1,
                StrokeShape = new RoundRectangle { CornerRadius = 16 }, Content = label, MinimumHeightRequest = 34,
            };
            var tap = new TapGestureRecognizer();
            var v = value;
            tap.Tapped += (_, _) => Toggle(v);
            chip.GestureRecognizers.Add(tap);
            _chips.Add((value, chip, label));
            Children.Add(chip);
        }
        Paint();
    }

    public T? Value => _selected.FirstOrDefault();
    public IReadOnlyCollection<T> Values => _selected;

    public void Set(IEnumerable<T> values)
    {
        _selected.Clear();
        foreach (var v in values) _selected.Add(v);
        Paint();
    }

    private void Toggle(T v)
    {
        if (_multi)
        {
            if (!_selected.Remove(v)) _selected.Add(v);
        }
        else
        {
            if (_selected.Contains(v)) { if (_allowNone) _selected.Clear(); else return; }
            else { _selected.Clear(); _selected.Add(v); }
        }
        Paint();
        Changed?.Invoke();
    }

    private void Paint()
    {
        foreach (var (value, chip, text) in _chips)
        {
            var on = _selected.Contains(value);
            chip.BackgroundColor = on ? Ui.Primary : Colors.Transparent;
            chip.Stroke = on ? Ui.Primary : Color.FromArgb("#B8C4D0");
            if (on) text.TextColor = Colors.White;
            else text.SetAppThemeColor(Label.TextColorProperty, Ui.Primary, Color.FromArgb("#C7D7EA"));
            text.FontAttributes = on ? FontAttributes.Bold : FontAttributes.None;
        }
    }
}

public static class Chips
{
    public static ChipGroup<T> Enum<T>(T selected, Func<T, string>? text = null) where T : struct, System.Enum =>
        new(System.Enum.GetValues<T>().Select(v => (v, text?.Invoke(v) ?? Split(v.ToString()))), [selected]);

    /// <summary>"PageNofTotal" → "Page nof total"; readable default labels for enums.</summary>
    public static string Split(string s) =>
        string.Concat(s.Select((c, i) => i > 0 && char.IsUpper(c) && !char.IsUpper(s[i - 1]) ? " " + char.ToLower(c) : c.ToString()));
}
