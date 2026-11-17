using System.Drawing.Printing;
using Bf3Billing.Core;

namespace Bf3Billing.App;

/// <summary>Prints one bill on A4 portrait: header block, lines, totals and signature boxes.</summary>
sealed class BillPrinter : PrintDocument
{
    readonly Bill _b;
    readonly WorkOrder? _wo;
    readonly List<(BillLine Line, string Item, string Activity, string Scope)> _lines;
    readonly string _project;
    readonly BillTotals _t;
    int _next;
    int _page;

    static readonly Font F = new("Segoe UI", 8.5f);
    static readonly Font FB = new("Segoe UI", 8.5f, FontStyle.Bold);
    static readonly Font FS = new("Segoe UI", 7.5f);
    static readonly Font FT = new("Segoe UI Semibold", 14f);

    public BillPrinter(Bill b, WorkOrder? wo, List<(BillLine, string, string, string)> lines, string project)
    {
        _b = b; _wo = wo; _lines = lines; _project = project;
        _t = BillMath.Totals(b, lines.Select(l => l.Item1));
        DocumentName = $"Bill {b.BillNo} {wo?.WoNo}";
        DefaultPageSettings.Landscape = false;
        DefaultPageSettings.Margins = new Margins(45, 45, 45, 55);
    }

    protected override void OnBeginPrint(PrintEventArgs e) { _next = 0; _page = 0; base.OnBeginPrint(e); }

    protected override void OnPrintPage(PrintPageEventArgs e)
    {
        var g = e.Graphics!;
        var a = e.MarginBounds;
        float y = a.Top;
        _page++;
        var lh = F.GetHeight(g);

        g.DrawString($"{_project} – {_b.BillType}", FT, Brushes.Black, a.Left, y);
        g.DrawString($"Status: {_b.Status}", FB, Brushes.Black, new RectangleF(a.Left, y + 4, a.Width, lh), new StringFormat { Alignment = StringAlignment.Far });
        y += FT.GetHeight(g) + 6;

        if (_page == 1)
        {
            var info = new (string, string)[]
            {
                ("Bill no.", _b.BillNo), ("Bill date", Fmt.Date(_b.BillDate)),
                ("Contractor", _wo?.ContractorName ?? ""), ("Work order", _wo?.WoNo ?? ""),
                ("Service", _wo?.Service ?? ""), ("WO value", _wo == null ? "" : "₹ " + Fmt.Money(_wo.Value)),
                ("Period", _b.PeriodFrom.HasValue || _b.PeriodTo.HasValue ? $"{Fmt.Date(_b.PeriodFrom)} to {Fmt.Date(_b.PeriodTo)}" : ""),
                ("Remarks", _b.Remarks),
            };
            var half = a.Width / 2f;
            for (var i = 0; i < info.Length; i += 2)
            {
                for (var j = 0; j < 2 && i + j < info.Length; j++)
                {
                    var x = a.Left + j * half;
                    g.DrawString(info[i + j].Item1, FS, Brushes.DimGray, x, y);
                    g.DrawString(info[i + j].Item2, F, Brushes.Black, new RectangleF(x + 70, y - 1, half - 75, lh + 2), new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap });
                }
                y += lh + 4;
            }
            y += 6;
        }

        // Lines table
        float[] w = { 24, 0, 80, 34, 60, 64, 82 };
        w[1] = a.Width - w.Sum();
        string[] heads = { "#", "Item / scope", "Activity", "Unit", "Qty", "Rate ₹", "Amount ₹" };
        bool[] right = { true, false, false, false, true, true, true };
        void Cells(string[] vals, Font f, float h, Brush? back = null)
        {
            float x = a.Left;
            if (back != null) g.FillRectangle(back, a.Left, y, a.Width, h);
            for (var i = 0; i < vals.Length; i++)
            {
                g.DrawString(vals[i], f, Brushes.Black, new RectangleF(x + 3, y + 2, w[i] - 6, h - 2),
                    new StringFormat { Alignment = right[i] ? StringAlignment.Far : StringAlignment.Near, Trimming = StringTrimming.EllipsisCharacter });
                x += w[i];
            }
            g.DrawLine(Pens.Gainsboro, a.Left, y + h, a.Right, y + h);
            y += h;
        }
        using (var hb = new SolidBrush(Ui.HeaderBack)) Cells(heads, FB, lh + 6, hb);

        var reserve = 210f; // totals and signatures on the last page
        var firstOnPage = true;
        while (_next < _lines.Count)
        {
            var (l, item, act, scope) = _lines[_next];
            var itemH = g.MeasureString(item, F, (int)w[1] - 6).Height;
            var scopeH = l.IsBom && scope.Length > 0 ? g.MeasureString(scope, FS, (int)w[1] - 6).Height : 0;
            var h = itemH + scopeH + 6;
            var need = _next == _lines.Count - 1 ? h + reserve : h;
            if (y + need > a.Bottom && !firstOnPage) break;
            firstOnPage = false;
            float x = a.Left;
            var vals = new[] { (_next + 1).ToString(Fmt.Culture), "", act, l.Unit, l.Qty.ToString(l.IsBom ? "N3" : "#,##0.###", Fmt.Culture),
                l.Rate.ToString("#,##,##0.00", Fmt.Culture), Fmt.Money(l.Amount) };
            for (var i = 0; i < vals.Length; i++)
            {
                if (i == 1)
                {
                    g.DrawString(item, F, Brushes.Black, new RectangleF(x + 3, y + 2, w[i] - 6, itemH));
                    if (scopeH > 0) g.DrawString(scope, FS, scope.Contains("OVER") ? Brushes.Firebrick : Brushes.DimGray, new RectangleF(x + 3, y + 2 + itemH, w[i] - 6, scopeH));
                }
                else
                    g.DrawString(vals[i], F, Brushes.Black, new RectangleF(x + 3, y + 2, w[i] - 6, h - 2),
                        new StringFormat { Alignment = right[i] ? StringAlignment.Far : StringAlignment.Near });
                x += w[i];
            }
            g.DrawLine(Pens.Gainsboro, a.Left, y + h, a.Right, y + h);
            y += h;
            _next++;
        }

        if (_next < _lines.Count)
        {
            Footer(g, a);
            e.HasMorePages = true;
            return;
        }

        // Totals
        y += 10;
        var tx = a.Right - 300f;
        void Tot(string label, decimal v, bool bold = false)
        {
            g.DrawString(label, bold ? FB : F, Brushes.Black, tx, y);
            g.DrawString("₹ " + Fmt.Money(v), bold ? FB : F, Brushes.Black, new RectangleF(tx, y, 300, lh + 2), new StringFormat { Alignment = StringAlignment.Far });
            y += lh + 3;
        }
        Tot("Gross", _t.Gross, true);
        Tot($"+ GST {_b.GstPct.ToString("0.00", Fmt.Culture)}%", _t.Gst);
        Tot($"− Retention {_b.RetentionPct.ToString("0.00", Fmt.Culture)}%", _t.Retention);
        Tot($"− TDS {_b.TdsPct.ToString("0.00", Fmt.Culture)}%", _t.Tds);
        Tot("− Other deduction", _t.Other);
        g.DrawLine(Pens.Black, tx, y, a.Right, y); y += 2;
        Tot("Net payable", _t.Net, true);
        Tot("Paid" + (_b.PaidOn.HasValue ? $" on {Fmt.Date(_b.PaidOn)}" : ""), _t.Paid);
        Tot("Balance due", _t.Due, true);

        // Signatures
        y = Math.Max(y + 30, a.Bottom - 60);
        var sw = a.Width / 3f;
        foreach (var (s, i) in new[] { "Prepared by", "Checked by", "Approved by" }.Select((s, i) => (s, i)))
        {
            var x = a.Left + i * sw;
            g.DrawLine(Pens.Black, x + 10, y + 30, x + sw - 20, y + 30);
            g.DrawString(s, FS, Brushes.DimGray, x + 10, y + 34);
        }
        Footer(g, a);
        e.HasMorePages = false;
    }

    void Footer(Graphics g, Rectangle a) =>
        g.DrawString($"Bill {_b.BillNo} · {_wo?.WoNo} · page {_page} · printed {DateTime.Now:dd-MM-yyyy HH:mm}", FS, Brushes.DimGray, a.Left, a.Bottom + 14);
}
