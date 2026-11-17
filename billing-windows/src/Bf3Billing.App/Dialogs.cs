using System.Data;
using Bf3Billing.Core;

namespace Bf3Billing.App;

/// <summary>A simple labelled form for editing one record.</summary>
sealed class EditDialog : Form
{
    readonly TableLayoutPanel _table = new() { ColumnCount = 2, AutoSize = true, Padding = new Padding(16, 14, 16, 4), Margin = new Padding(0) };
    readonly Dictionary<TextBox, string> _numbers = new();

    /// <summary>Called on Save; return true to close. Show your own message when returning false.</summary>
    public Func<bool>? OnSave { get; set; }

    public EditDialog(string title)
    {
        Text = title;
        Font = Ui.Base;
        BackColor = Ui.Back;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(16, 6, 8, 14), Margin = new Padding(0) };
        var save = Ui.Btn("Save", (_, _) => Save(), primary: true);
        var cancel = Ui.Btn("Cancel", (_, _) => { DialogResult = DialogResult.Cancel; Close(); });
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);
        AcceptButton = save;
        CancelButton = cancel;
        var stack = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Location = new Point(0, 0), Margin = new Padding(0) };
        stack.Controls.Add(_table);
        stack.Controls.Add(buttons);
        Controls.Add(stack);
    }

    T Add<T>(string label, T control) where T : Control
    {
        control.Margin = new Padding(0, 3, 0, 5);
        control.AccessibleName = label;
        _table.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 7, 14, 0), ForeColor = Ui.Muted });
        _table.Controls.Add(control);
        return control;
    }

    public TextBox AddText(string label, string? value, int width = 320) => Add(label, new TextBox { Text = value ?? "", Width = width });

    public TextBox AddMultiline(string label, string? value) =>
        Add(label, new TextBox { Text = value ?? "", Width = 320, Height = 60, Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical });

    public TextBox AddNumber(string label, decimal value, string format = "#,##,##0.##")
    {
        var t = Add(label, new TextBox { Text = value.ToString(format, Fmt.Culture), Width = 150, TextAlign = HorizontalAlignment.Right });
        _numbers[t] = label;
        return t;
    }

    public ComboBox AddCombo(string label, IEnumerable<object> items, object? selected, bool editable = false, int width = 320)
    {
        var c = Add(label, new ComboBox { DropDownStyle = editable ? ComboBoxStyle.DropDown : ComboBoxStyle.DropDownList, Width = width });
        c.Items.AddRange(items.ToArray());
        if (selected != null)
        {
            if (editable) c.Text = selected.ToString();
            else c.SelectedItem = selected;
        }
        if (!editable && c.SelectedIndex < 0 && c.Items.Count > 0) c.SelectedIndex = 0;
        return c;
    }

    public DateTimePicker AddDate(string label, DateTime? value, bool optional = true)
    {
        var d = Add(label, Ui.Date(optional, 150));
        Ui.Set(d, value);
        return d;
    }

    public CheckBox AddCheck(string label, bool value) => Add(label, new CheckBox { Checked = value, AutoSize = true });

    public static decimal Number(TextBox t) => Fmt.TryParse(t.Text, out var v) ? v : 0;

    void Save()
    {
        foreach (var (box, label) in _numbers)
            if (!Fmt.TryParse(box.Text, out _))
            {
                MessageBox.Show(this, $"{label}: '{box.Text}' is not a number.", Text);
                box.Focus();
                return;
            }
        if (OnSave != null && !OnSave()) return;
        DialogResult = DialogResult.OK;
        Close();
    }
}

/// <summary>Shows a table in a resizable window with Export and Print.</summary>
sealed class TableDialog : Form
{
    public TableDialog(string title, string subtitle, DataTable table)
    {
        Text = title;
        Font = Ui.Base;
        BackColor = Ui.Back;
        Size = new Size(1100, 520);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        Padding = new Padding(12);
        var grid = Ui.Grid();
        var bar = Ui.Bar();
        bar.Controls.Add(Ui.Lbl(subtitle, Ui.Small, Ui.Muted));
        bar.Controls.Add(Ui.Btn("Export", (_, _) => Ui.ExportTables(this, title, table)));
        bar.Controls.Add(Ui.Btn("Print PDF", (_, _) => Ui.Preview(this, new TablePrinter(table, title, subtitle))));
        Controls.Add(grid);
        Controls.Add(bar);
        Load += (_, _) => Ui.Bind(grid, table);
    }
}
