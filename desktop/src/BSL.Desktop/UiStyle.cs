namespace BSL.Desktop;

/// <summary>
/// House style for every window: ARIAL 10, ALL CAPITALS, fitted to the screen.
/// Attach once to a form; controls added later (tabs, grids, dialogs, status text) are styled automatically.
/// Text boxes type in capitals except where the control's Tag is "nocaps" (e-mail, passwords, server names, formulas).
/// </summary>
public static class UiStyle
{
    public const string FontName = "Arial";
    public const float FontSize = 10f;
    public const string NoCaps = "nocaps";

    static readonly HashSet<Control> Hooked = [];

    public static Font Font(FontStyle style = FontStyle.Regular, float size = FontSize) => new(FontName, size, style);

    public static string U(string? s) => string.IsNullOrEmpty(s) ? s ?? "" : s.Contains('@') ? s : s.ToUpperInvariant();

    public static void Attach(Control root)
    {
        if (root is Form f) FitToScreen(f);
        Apply(root);
    }

    static void FitToScreen(Form f)
    {
        var area = Screen.FromControl(f).WorkingArea;
        if (f.Width > area.Width) f.Width = area.Width;
        if (f.Height > area.Height) f.Height = area.Height;
        if (f.Text.Length > 0) f.Text = U(f.Text);
    }

    static void Apply(Control c)
    {
        // font: Arial everywhere; normal text 10 pt, larger headings keep their size
        var size = c.Font.Size > FontSize + 0.6f ? c.Font.Size : FontSize;
        if (c.Font.Name != FontName || Math.Abs(c.Font.Size - size) > 0.1f) c.Font = new Font(FontName, size, c.Font.Style);

        switch (c)
        {
            case Label or ButtonBase or GroupBox or TabPage or LinkLabel:
                if (c is Label l) l.UseMnemonic = false;
                Caps(c);
                break;
            case TextBox tb when !Equals(tb.Tag, NoCaps) && !tb.UseSystemPasswordChar && !tb.ReadOnly:
                tb.CharacterCasing = CharacterCasing.Upper;
                break;
            case ListControl lc when !Equals(lc.Tag, NoCaps):
                // list / combo items shown in capitals (the stored item and value are unchanged)
                lc.FormattingEnabled = true;
                if (!Hooked.Contains(lc)) lc.Format += (_, e) => { if (e.Value is string sv) e.Value = U(sv); else if (e.Value != null && e.DesiredType == typeof(string)) e.Value = U(e.Value.ToString()); };
                if (c is ComboBox cb2 && cb2.DropDownStyle != ComboBoxStyle.DropDownList && cb2.DataSource == null && !Hooked.Contains(cb2))
                    cb2.TextUpdate += (_, _) =>
                    {
                        var u = cb2.Text.ToUpperInvariant();
                        if (u != cb2.Text) { var i = cb2.SelectionStart; cb2.Text = u; cb2.SelectionStart = i; }
                    };
                break;
            case TreeView tv:
                CapsNodes(tv.Nodes);
                break;
            case DataGridView g:
                StyleGrid(g);
                break;
            case ToolStrip ts:
                foreach (ToolStripItem it in ts.Items) CapsItem(it);
                if (Hooked.Add(ts)) ts.ItemAdded += (_, e) => CapsItem(e.Item!);
                break;
        }
        foreach (Control k in c.Controls) Apply(k);
        if (Hooked.Add(c))
        {
            c.ControlAdded += (_, e) => { if (e.Control != null) Apply(e.Control); };
            c.Disposed += (_, _) => Hooked.Remove(c);
        }
    }

    static void Caps(Control c)
    {
        c.Text = U(c.Text);
        if (Hooked.Contains(c)) return;
        c.TextChanged += (_, _) => { var u = U(c.Text); if (u != c.Text) c.Text = u; };
    }

    static void CapsNodes(TreeNodeCollection nodes)
    {
        foreach (TreeNode n in nodes)
        {
            n.Text = U(n.Text);
            if (n.NodeFont != null) n.NodeFont = new Font(FontName, FontSize, n.NodeFont.Style);
            if (n.ToolTipText.Length > 0) n.ToolTipText = U(n.ToolTipText);
            CapsNodes(n.Nodes);
        }
    }

    public static void CapsItem(ToolStripItem it)
    {
        it.Font = new Font(FontName, FontSize, it.Font.Style);
        // keep "&&" (a literal ampersand) intact while upper-casing
        it.Text = U(it.Text);
        if (!string.IsNullOrEmpty(it.ToolTipText)) it.ToolTipText = U(it.ToolTipText);
        it.TextChanged += (_, _) => { var u = U(it.Text); if (u != it.Text) it.Text = u; };
        if (it is ToolStripDropDownItem dd)
        {
            foreach (ToolStripItem k in dd.DropDownItems) CapsItem(k);
            dd.DropDown.ItemAdded += (_, e) => CapsItem(e.Item!);
        }
    }

    static void StyleGrid(DataGridView g)
    {
        g.DefaultCellStyle.Font = Font();
        g.ColumnHeadersDefaultCellStyle.Font = Font(FontStyle.Bold);
        g.RowTemplate.Height = 24;
        foreach (DataGridViewColumn col in g.Columns) col.HeaderText = U(col.HeaderText);
        if (Hooked.Contains(g)) return;
        g.ColumnAdded += (_, e) => e.Column.HeaderText = U(e.Column.HeaderText);
        g.DataBindingComplete += (_, _) => { foreach (DataGridViewColumn col in g.Columns) col.HeaderText = U(col.HeaderText); };
        g.ColumnHeaderMouseClick += (_, _) => { foreach (DataGridViewColumn col in g.Columns) col.HeaderText = U(col.HeaderText); };
        g.CellFormatting += (_, e) =>
        {
            if (e.Value is string s && s.Length > 0 && !IsCaseSensitive(g.Columns[e.ColumnIndex]))
            {
                var u = U(s);
                if (u != s) { e.Value = u; e.FormattingApplied = true; }
            }
        };
        g.EditingControlShowing += (_, e) =>
        {
            if (e.Control is TextBox tb)
                tb.CharacterCasing = g.CurrentCell != null && IsCaseSensitive(g.Columns[g.CurrentCell.ColumnIndex])
                    ? CharacterCasing.Normal : CharacterCasing.Upper;
        };
    }

    static bool IsCaseSensitive(DataGridViewColumn c)
    {
        var n = (c.DataPropertyName + " " + c.Name).ToUpperInvariant();
        return n.Contains("EMAIL") || n.Contains("MAIL") || n.Contains("FORMULA") || n.Contains("PATH");
    }
}

/// <summary>Base form: applies the house style (Arial 10, capitals, fit to screen) when shown.</summary>
public class StyledForm : Form
{
    public StyledForm()
    {
        Font = UiStyle.Font();
        StartPosition = FormStartPosition.CenterParent;
    }

    protected override void OnLoad(EventArgs e)
    {
        UiStyle.Attach(this);
        base.OnLoad(e);
    }
}
