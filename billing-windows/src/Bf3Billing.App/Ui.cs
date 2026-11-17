using System.Data;
using System.Drawing.Printing;
using Bf3Billing.Core;

namespace Bf3Billing.App;

/// <summary>Colours, fonts and small builders shared by every screen.</summary>
static class Ui
{
    public static readonly Color Blue = ColorTranslator.FromHtml("#1D5FC2");
    public static readonly Color BlueText = ColorTranslator.FromHtml("#DCE8FB");
    public static readonly Color Back = ColorTranslator.FromHtml("#F4F5F7");
    public static readonly Color CardBorder = ColorTranslator.FromHtml("#DFE3E9");
    public static readonly Color HeaderBack = ColorTranslator.FromHtml("#EEF0F3");
    public static readonly Color AltRow = ColorTranslator.FromHtml("#F8F9FB");
    public static readonly Color Ink = ColorTranslator.FromHtml("#1B1F24");
    public static readonly Color Muted = ColorTranslator.FromHtml("#626D7E");
    public static readonly Color Bad = ColorTranslator.FromHtml("#B3261E");
    public static readonly Color Warn = ColorTranslator.FromHtml("#9A5200");
    public static readonly Color Link = ColorTranslator.FromHtml("#0066CC");
    public static readonly Color BarGreen = ColorTranslator.FromHtml("#06B025");

    public static readonly Font Base = new("Segoe UI", 9.75f);
    public static readonly Font Small = new("Segoe UI", 9f);
    public static readonly Font Bold = new("Segoe UI", 9.75f, FontStyle.Bold);
    public static readonly Font Section = new("Segoe UI Semibold", 10.5f);
    public static readonly Font Kpi = new("Segoe UI Semibold", 16f);
    public static readonly Font Title = new("Segoe UI Semibold", 13.5f);

    public static Button Btn(string text, EventHandler onClick, bool primary = false)
    {
        var b = new Button
        {
            Text = text, AutoSize = true, MinimumSize = new Size(80, 30), Margin = new Padding(0, 0, 8, 0),
            Padding = new Padding(6, 0, 6, 0), UseVisualStyleBackColor = !primary, Font = Base,
        };
        if (primary)
        {
            b.BackColor = Blue; b.ForeColor = Color.White; b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = Blue;
        }
        b.Click += onClick;
        return b;
    }

    public static Label Lbl(string text, Font? font = null, Color? color = null) => new()
    {
        Text = text, AutoSize = true, Font = font ?? Base, ForeColor = color ?? Ink, Margin = new Padding(0, 7, 6, 0),
    };

    /// <summary>A small grey caption above an input, as in the bill editor.</summary>
    public static Control Field(string caption, Control input)
    {
        var p = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 12, 8) };
        p.Controls.Add(new Label { Text = caption, AutoSize = true, Font = Small, ForeColor = Muted, Margin = new Padding(0, 0, 0, 2) });
        input.Margin = new Padding(0);
        p.Controls.Add(input);
        return p;
    }

    public static FlowLayoutPanel Bar() => new()
    {
        Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(0, 0, 0, 8), BackColor = Color.Transparent,
    };

    public static ComboBox Combo(int width) => new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = width, Font = Base, Margin = new Padding(0, 3, 8, 0) };

    public static DateTimePicker Date(bool optional, int width = 130)
    {
        var d = new DateTimePicker
        {
            Format = DateTimePickerFormat.Custom, CustomFormat = "dd-MM-yyyy", Width = width, Font = Base, ShowCheckBox = optional,
            Margin = new Padding(0, 3, 8, 0),
        };
        if (optional) d.Checked = false;
        return d;
    }

    public static DateTime? Value(DateTimePicker d) => !d.ShowCheckBox || d.Checked ? d.Value.Date : null;

    public static void Set(DateTimePicker d, DateTime? v)
    {
        if (v.HasValue) { d.Value = v.Value; if (d.ShowCheckBox) d.Checked = true; }
        else if (d.ShowCheckBox) d.Checked = false;
    }

    /// <summary>A white card with a rounded border, sized to its content.</summary>
    public sealed class Card : Panel
    {
        public Card()
        {
            BackColor = Color.White; Padding = new Padding(14); Margin = new Padding(0, 0, 0, 14);
            DoubleBuffered = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var pen = new Pen(CardBorder);
            e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }

    public static DataGridView Grid(bool fill = true)
    {
        var g = new DataGridView
        {
            Dock = fill ? DockStyle.Fill : DockStyle.None,
            ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, RowHeadersVisible = false,
            BackgroundColor = Color.White, BorderStyle = BorderStyle.FixedSingle, GridColor = CardBorder,
            EnableHeadersVisualStyles = false, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells, Font = Base,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal, StandardTab = true,
        };
        g.ColumnHeadersDefaultCellStyle.BackColor = HeaderBack;
        g.ColumnHeadersDefaultCellStyle.Font = Bold;
        g.ColumnHeadersDefaultCellStyle.Padding = new Padding(4, 4, 4, 4);
        g.ColumnHeadersDefaultCellStyle.SelectionBackColor = HeaderBack;
        g.AlternatingRowsDefaultCellStyle.BackColor = AltRow;
        g.DefaultCellStyle.SelectionBackColor = ColorTranslator.FromHtml("#CCE0FA");
        g.DefaultCellStyle.SelectionForeColor = Ink;
        g.DefaultCellStyle.FormatProvider = Fmt.Culture;
        g.RowTemplate.Height = 26;
        return g;
    }

    /// <summary>
    /// Shows a table in a grid. Columns whose names start with "_" are hidden (ids).
    /// Decimal columns are right-aligned and formatted: tonnes with 3 decimals, % with 1, money with 2.
    /// </summary>
    public static void Bind(DataGridView g, DataTable t, string money = "N2")
    {
        g.DataSource = t;
        foreach (DataGridViewColumn c in g.Columns)
        {
            c.Visible = !c.Name.StartsWith('_');
            c.SortMode = DataGridViewColumnSortMode.Automatic;
            var type = t.Columns[c.Name]?.DataType;
            if (type == typeof(DateTime)) { c.DefaultCellStyle.Format = "dd-MM-yyyy"; c.DefaultCellStyle.FormatProvider = Fmt.Culture; }
            if (type == typeof(decimal) || type == typeof(int) || type == typeof(long) || type == typeof(double))
            {
                c.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
                c.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleRight;
                c.DefaultCellStyle.FormatProvider = Fmt.Culture;
                if (type == typeof(decimal)) c.DefaultCellStyle.Format = NumberFormat(c.Name, money);
            }
        }
    }

    public static string NumberFormat(string column, string money = "N2") =>
        column.EndsWith(" t", StringComparison.Ordinal) || column is "Qty" or "Quantity" or "Tonnes" or "BOM t" ? "N3" :
        column.Contains('%') || column == "Used" ? "0.0" : money;

    /// <summary>Sizes a grid to show all its rows (for grids stacked inside a scrolling page).</summary>
    public static void FitHeight(DataGridView g)
    {
        var h = g.ColumnHeadersHeight + g.Rows.Cast<DataGridViewRow>().Sum(r => r.Height) + 4;
        if (g.Controls.OfType<HScrollBar>().Any(s => s.Visible)) h += SystemInformation.HorizontalScrollBarHeight;
        g.Height = Math.Max(h, g.ColumnHeadersHeight + 30);
    }

    public static long? SelectedId(DataGridView g, string column = "_Id")
    {
        var row = g.CurrentRow ?? (g.SelectedRows.Count > 0 ? g.SelectedRows[0] : null);
        if (row?.DataBoundItem is DataRowView v && v.Row.Table.Columns.Contains(column) && v[column] is long id) return id;
        return null;
    }

    public static void Error(IWin32Window owner, Exception ex)
    {
        var msg = ex is BillingException ? ex.Message : ex.Message + Environment.NewLine + Environment.NewLine + ex.GetType().Name;
        MessageBox.Show(owner, msg, "BF#3 Billing", MessageBoxButtons.OK, ex is BillingException ? MessageBoxIcon.Warning : MessageBoxIcon.Error);
    }

    public static void Info(IWin32Window owner, string msg) =>
        MessageBox.Show(owner, msg, "BF#3 Billing", MessageBoxButtons.OK, MessageBoxIcon.Information);

    public static bool Confirm(IWin32Window owner, string msg) =>
        MessageBox.Show(owner, msg, "BF#3 Billing", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;

    /// <summary>Runs an action and shows any failure as a message instead of closing the app.</summary>
    public static bool Try(IWin32Window owner, Action a)
    {
        try { a(); return true; }
        catch (Exception ex) when (ex is BillingException or IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException or FormatException)
        {
            Error(owner, ex);
            return false;
        }
    }

    public static string? SaveFile(IWin32Window owner, string title, string fileName, string filter = "Excel workbook (*.xlsx)|*.xlsx")
    {
        using var d = new SaveFileDialog { Title = title, FileName = fileName, Filter = filter, OverwritePrompt = true };
        return d.ShowDialog(owner) == DialogResult.OK ? d.FileName : null;
    }

    public static string? OpenFile(IWin32Window owner, string title, string filter = "Excel workbook (*.xlsx;*.xlsm)|*.xlsx;*.xlsm")
    {
        using var d = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        return d.ShowDialog(owner) == DialogResult.OK ? d.FileName : null;
    }

    public static void ExportTables(IWin32Window owner, string fileName, params DataTable[] tables)
    {
        var path = SaveFile(owner, "Export to Excel", SafeFileName(fileName) + ".xlsx");
        if (path == null) return;
        if (Try(owner, () => ExcelIO.WriteTables(path, tables)))
            OfferOpen(owner, path, "Saved to Excel.");
    }

    public static void OfferOpen(IWin32Window owner, string path, string message)
    {
        if (MessageBox.Show(owner, message + Environment.NewLine + path + Environment.NewLine + Environment.NewLine + "Open it now?",
                "BF#3 Billing", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
            OpenShell(path);
    }

    public static void OpenShell(string path)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { }
    }

    public static string SafeFileName(string s) => string.Concat(s.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '-' : ch));

    /// <summary>Shows a print preview; choose "Microsoft Print to PDF" in the print dialog to save a PDF.</summary>
    public static void Preview(IWin32Window owner, PrintDocument doc)
    {
        using var d = new PrintPreviewDialog { Document = doc, Width = 1100, Height = 800, UseAntiAlias = true, ShowIcon = false };
        d.Text = "Print preview – choose 'Microsoft Print to PDF' to save a PDF";
        d.ShowDialog(owner);
    }
}

/// <summary>A horizontal progress bar drawn like the design (grey track, green fill).</summary>
sealed class ProgressStrip : Control
{
    decimal _percent;
    public decimal Percent { get => _percent; set { _percent = value; Invalidate(); } }

    public ProgressStrip() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true); Height = 14; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var r = new Rectangle(0, 1, Width - 1, Height - 3);
        using var track = new SolidBrush(ColorTranslator.FromHtml("#E6E6E6"));
        using var border = new Pen(ColorTranslator.FromHtml("#BCBCBC"));
        e.Graphics.FillRectangle(track, r);
        var w = (int)(r.Width * Math.Clamp(_percent, 0, 100) / 100m);
        using var fill = new SolidBrush(_percent > 100 ? Ui.Bad : Ui.BarGreen);
        if (w > 0) e.Graphics.FillRectangle(fill, r.X, r.Y, w, r.Height);
        e.Graphics.DrawRectangle(border, r);
    }
}

/// <summary>Prints any table over as many landscape pages as needed.</summary>
sealed class TablePrinter : PrintDocument
{
    readonly DataTable _t;
    readonly string _title;
    readonly string _subtitle;
    int _row;
    int _page;
    float[] _widths = Array.Empty<float>();
    Font _font = new("Segoe UI", 8.5f);
    Font _bold = new("Segoe UI", 8.5f, FontStyle.Bold);

    public TablePrinter(DataTable t, string title, string subtitle)
    {
        _t = t; _title = title; _subtitle = subtitle;
        DocumentName = title;
        DefaultPageSettings.Landscape = true;
        DefaultPageSettings.Margins = new Margins(40, 40, 40, 50);
    }

    protected override void OnBeginPrint(PrintEventArgs e) { _row = 0; _page = 0; _widths = Array.Empty<float>(); base.OnBeginPrint(e); }

    static bool Numeric(DataColumn c) => c.DataType == typeof(decimal) || c.DataType == typeof(int) || c.DataType == typeof(long);

    string Cell(DataRow r, DataColumn c)
    {
        var v = r[c];
        if (v is decimal d) return d.ToString(Ui.NumberFormat(c.ColumnName), Fmt.Culture);
        if (v is DateTime dt) return Fmt.Date(dt);
        return v?.ToString() ?? "";
    }

    protected override void OnPrintPage(PrintPageEventArgs e)
    {
        var g = e.Graphics!;
        var area = e.MarginBounds;
        var cols = _t.Columns.Cast<DataColumn>().Where(c => !c.ColumnName.StartsWith('_')).ToList();

        if (_widths.Length == 0)
        {
            // Measure the widest text in each column, then shrink the font until the table fits the page width.
            for (var size = 8.5f; size >= 5.5f; size -= 0.5f)
            {
                _font = new Font("Segoe UI", size); _bold = new Font("Segoe UI", size, FontStyle.Bold);
                _widths = cols.Select(c => Math.Max(g.MeasureString(c.ColumnName, _bold).Width,
                    _t.Rows.Cast<DataRow>().Take(400).Select(r => g.MeasureString(Cell(r, c), _font).Width).DefaultIfEmpty(0).Max()) + 8).ToArray();
                if (_widths.Sum() <= area.Width) break;
            }
            var scale = Math.Min(1f, area.Width / Math.Max(1f, _widths.Sum()));
            _widths = _widths.Select(w => w * scale).ToArray();
        }

        _page++;
        float y = area.Top;
        using (var tf = new Font("Segoe UI Semibold", 13f)) { g.DrawString(_title, tf, Brushes.Black, area.Left, y); y += tf.GetHeight(g) + 2; }
        g.DrawString(_subtitle, _font, Brushes.DimGray, area.Left, y);
        y += _font.GetHeight(g) + 8;

        var rowH = _font.GetHeight(g) + 6;
        void DrawRow(IEnumerable<string> cells, Font f, Brush? back)
        {
            float x = area.Left;
            if (back != null) g.FillRectangle(back, area.Left, y, _widths.Sum(), rowH);
            var i = 0;
            foreach (var text in cells)
            {
                var rect = new RectangleF(x + 3, y + 3, _widths[i] - 6, rowH - 3);
                var fmt = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap, Alignment = Numeric(cols[i]) ? StringAlignment.Far : StringAlignment.Near };
                g.DrawString(text, f, Brushes.Black, rect, fmt);
                x += _widths[i++];
            }
            g.DrawLine(Pens.Gainsboro, area.Left, y + rowH, area.Left + _widths.Sum(), y + rowH);
            y += rowH;
        }

        using var headBack = new SolidBrush(Ui.HeaderBack);
        DrawRow(cols.Select(c => c.ColumnName), _bold, headBack);
        while (_row < _t.Rows.Count && y + rowH <= area.Bottom)
        {
            var r = _t.Rows[_row++];
            var isTotal = r[0] as string == "Total";
            DrawRow(cols.Select(c => Cell(r, c)), isTotal ? _bold : _font, null);
        }
        g.DrawString($"Page {_page} · printed {DateTime.Now:dd-MM-yyyy HH:mm}", _font, Brushes.DimGray, area.Left, area.Bottom + 12);
        e.HasMorePages = _row < _t.Rows.Count;
    }
}
