namespace BSL.Desktop.Forms;

/// <summary>Simple text input dialog.</summary>
public static class InputDialog
{
    public static string? Show(string title, string prompt, string value = "", bool multiline = false)
    {
        using var f = new Form
        {
            Text = title, Width = 640, Height = multiline ? 380 : 190, StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.SizableToolWindow, MinimizeBox = false, MaximizeBox = false
        };
        var lbl = new Label { Text = prompt, Dock = DockStyle.Top, Height = multiline ? 120 : 36, Padding = new Padding(8) };
        var tb = new TextBox { Text = value, Dock = DockStyle.Fill, Multiline = multiline, Font = new Font("Consolas", 10f) };
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.RightToLeft };
        var ok = Ui.Button("OK", (_, _) => f.DialogResult = DialogResult.OK);
        var cancel = Ui.Button("Cancel", (_, _) => f.DialogResult = DialogResult.Cancel);
        bar.Controls.AddRange([cancel, ok]);
        f.Controls.Add(tb); f.Controls.Add(lbl); f.Controls.Add(bar);
        f.AcceptButton = multiline ? null : ok;
        f.CancelButton = cancel;
        return f.ShowDialog() == DialogResult.OK ? tb.Text : null;
    }
}

/// <summary>Pick one value from a list.</summary>
public static class PickDialog
{
    public static string? Show(string title, string prompt, IList<string> items, string? selected = null)
    {
        using var f = new Form
        {
            Text = title, Width = 420, Height = 170, StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedToolWindow
        };
        var lbl = new Label { Text = prompt, Dock = DockStyle.Top, Height = 30, Padding = new Padding(8, 8, 8, 0) };
        var cb = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
        cb.Items.AddRange(items.Cast<object>().ToArray());
        cb.SelectedItem = selected != null && items.Contains(selected) ? selected : items.FirstOrDefault();
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.RightToLeft };
        var ok = Ui.Button("OK", (_, _) => f.DialogResult = DialogResult.OK);
        var cancel = Ui.Button("Cancel", (_, _) => f.DialogResult = DialogResult.Cancel);
        bar.Controls.AddRange([cancel, ok]);
        f.Controls.Add(cb); f.Controls.Add(lbl); f.Controls.Add(bar);
        f.AcceptButton = ok; f.CancelButton = cancel;
        return f.ShowDialog() == DialogResult.OK ? cb.SelectedItem as string : null;
    }
}
