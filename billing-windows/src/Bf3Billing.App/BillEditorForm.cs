using Bf3Billing.Core;

namespace Bf3Billing.App;

/// <summary>The bill editor: header, BOM and free lines with a live scope check, and totals.</summary>
sealed class BillEditorForm : Form
{
    readonly BillingRepository _repo;
    Bill _bill;
    readonly List<BillLine> _lines = new();
    readonly List<WorkOrder> _wos;
    readonly List<Activity> _acts;
    readonly List<CustomField> _lineFields;
    const string ExtraCol = "x:";
    readonly Dictionary<long, BomItem> _bom = new();
    bool _dirty;
    bool _loading;
    bool _ready;

    public event Action? Saved;

    // Header fields
    readonly ComboBox _wo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 360 };
    readonly TextBox _contractor = new() { Width = 210, ReadOnly = true, TabStop = false };
    readonly TextBox _billNo = new() { Width = 110 };
    readonly ComboBox _type = new() { DropDownStyle = ComboBoxStyle.DropDown, Width = 130 };
    readonly DateTimePicker _date = Ui.Date(false);
    readonly DateTimePicker _from = Ui.Date(true);
    readonly DateTimePicker _to = Ui.Date(true);
    readonly ComboBox _status = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
    readonly TextBox _gst = Num(70);
    readonly TextBox _ret = Num(70);
    readonly TextBox _tds = Num(70);
    readonly TextBox _other = Num(100);
    readonly TextBox _paid = Num(110);
    readonly DateTimePicker _paidOn = Ui.Date(true);
    readonly TextBox _remarks = new() { Width = 420 };

    readonly DataGridView _grid = Ui.Grid();
    readonly Label _warn = new() { AutoSize = false, Dock = DockStyle.Fill, ForeColor = Ui.Warn, Padding = new Padding(0, 6, 20, 0) };
    readonly TableLayoutPanel _totals = new() { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Right };
    readonly Button _delete;
    readonly Button _duplicate;

    const string CNo = "No", CItem = "Item", CRemarks = "Remarks", CAct = "Activity", CUnit = "Unit", CQty = "Qty",
        CRate = "Rate", CAmount = "Amount", CScope = "Scope", CRemove = "Remove";

    static TextBox Num(int width) => new() { Width = width, TextAlign = HorizontalAlignment.Right };

    public BillEditorForm(BillingRepository repo, long? billId, long? workOrderId)
    {
        _repo = repo;
        _wos = repo.WorkOrders();
        _acts = repo.Activities();
        _lineFields = repo.CustomFields(Entities.BillLine);
        foreach (var b in repo.BomItems()) _bom[b.Id] = b;

        Font = Ui.Base;
        BackColor = Ui.Back;
        Size = new Size(1240, 800);
        MinimumSize = new Size(900, 600);
        StartPosition = FormStartPosition.CenterParent;
        KeyPreview = true;
        ShowInTaskbar = false;

        // ── Header ──
        _wo.Items.AddRange(_wos.Cast<object>().ToArray());
        _type.Items.AddRange(BillTypes.All);
        _status.Items.AddRange(BillStatus.All);
        foreach (var (c, n) in new (Control, string)[] { (_wo, "Work order"), (_billNo, "Bill number"), (_type, "Bill type"), (_date, "Bill date"),
                     (_from, "Period from"), (_to, "Period to"), (_status, "Status"), (_gst, "GST percent"), (_ret, "Retention percent"),
                     (_tds, "TDS percent"), (_other, "Other deduction in rupees"), (_paid, "Paid in rupees"), (_paidOn, "Paid on"), (_remarks, "Remarks") })
            c.AccessibleName = n;

        var header = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(14, 12, 14, 0) };
        header.Controls.AddRange(new[]
        {
            Ui.Field("Work order", _wo), Ui.Field("Contractor", _contractor), Ui.Field("Bill no.", _billNo), Ui.Field("Type", _type),
            Ui.Field("Bill date", _date), Ui.Field("Period from", _from), Ui.Field("Period to", _to), Ui.Field("Status", _status),
            Ui.Field("GST %", _gst), Ui.Field("Retention %", _ret), Ui.Field("TDS %", _tds), Ui.Field("Other deduction ₹", _other),
            Ui.Field("Paid ₹", _paid), Ui.Field("Paid on", _paidOn), Ui.Field("Remarks", _remarks),
        });

        // ── Lines toolbar ──
        var linesBar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(14, 4, 14, 6), WrapContents = true };
        linesBar.Controls.Add(Ui.Lbl("Bill lines", Ui.Section));
        linesBar.Controls.Add(Ui.Lbl("BOM lines are billed in tonnes against a drawing or item. Free lines are for anything else.", Ui.Small, Ui.Muted));
        linesBar.Controls.Add(Ui.Btn("+ BOM line", (_, _) => AddBomLines(), primary: true));
        linesBar.Controls.Add(Ui.Btn("+ Free line", (_, _) => AddFreeLine()));
        linesBar.Controls.Add(Ui.Btn("Remove selected", (_, _) => RemoveSelected()));

        // ── Grid ──
        BuildGrid();
        var gridHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 0, 14, 0) };
        gridHost.Controls.Add(_grid);

        // ── Totals and warning ──
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 190, Padding = new Padding(14, 8, 14, 0) };
        _totals.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
        _totals.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        bottom.Controls.Add(_warn);
        bottom.Controls.Add(_totals);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(14, 8, 6, 12) };
        var save = Ui.Btn("Save changes", (_, _) => Save(close: true), primary: true);
        buttons.Controls.Add(save);
        buttons.Controls.Add(Ui.Btn("Cancel", (_, _) => Close()));
        buttons.Controls.Add(new Panel { Width = 24, Height = 1 });
        buttons.Controls.Add(Ui.Btn("Print PDF", (_, _) => Print()));
        buttons.Controls.Add(_duplicate = Ui.Btn("Duplicate", (_, _) => Duplicate()));
        buttons.Controls.Add(_delete = Ui.Btn("Delete", (_, _) => Delete()));
        AcceptButton = null;

        Controls.Add(gridHost);
        Controls.Add(linesBar);
        Controls.Add(header);
        Controls.Add(bottom);
        Controls.Add(buttons);

        // ── Events ──
        _wo.SelectedIndexChanged += (_, _) => WorkOrderChanged();
        foreach (var t in new[] { _gst, _ret, _tds, _other, _paid })
        {
            t.Validating += (_, e) =>
            {
                if (!Fmt.TryParse(t.Text, out var v)) { e.Cancel = true; Ui.Ask(this, $"'{t.Text}' is not a number.", Text); return; }
                t.Text = t == _other || t == _paid ? Fmt.Money(v) : v.ToString("0.##", Fmt.Culture);
            };
            t.TextChanged += (_, _) => { MarkDirty(); UpdateTotals(); };
        }
        foreach (var c in new Control[] { _billNo, _type, _remarks }) c.TextChanged += (_, _) => MarkDirty();
        foreach (var d in new[] { _date, _from, _to, _paidOn }) d.ValueChanged += (_, _) => MarkDirty();
        _status.SelectedIndexChanged += (_, _) => MarkDirty();
        KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.S) { e.Handled = true; Save(close: false); }
            if (e.KeyCode == Keys.Escape && !_grid.IsCurrentCellInEditMode) { e.Handled = true; Close(); }
        };
        FormClosing += (_, e) =>
        {
            if (_dirty && DialogResult != DialogResult.OK &&
                Ui.Ask(this, "Discard the changes to this bill?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                e.Cancel = true;
        };

        Shown += (_, _) => BeginInvoke(() => { _ready = true; _dirty = false; if (Text.EndsWith(" *")) Text = Text[..^2]; });

        // ── Load ──
        if (billId is long id && repo.GetBill(id) is { } loaded)
        {
            _bill = loaded.Bill;
            _lines.AddRange(loaded.Lines);
        }
        else
        {
            var wo = workOrderId ?? 0;
            _bill = repo.NewBill(wo);
        }
        LoadFields();
    }

    // ───────────────────────── Loading ─────────────────────────

    void LoadFields()
    {
        _loading = true;
        _wo.SelectedItem = _wos.FirstOrDefault(w => w.Id == _bill.WorkOrderId);
        _contractor.Text = (_wo.SelectedItem as WorkOrder)?.ContractorName ?? "";
        _billNo.Text = _bill.BillNo;
        _type.Text = _bill.BillType;
        _date.Value = _bill.BillDate;
        Ui.Set(_from, _bill.PeriodFrom);
        Ui.Set(_to, _bill.PeriodTo);
        _status.SelectedItem = _bill.Status;
        _gst.Text = _bill.GstPct.ToString("0.##", Fmt.Culture);
        _ret.Text = _bill.RetentionPct.ToString("0.##", Fmt.Culture);
        _tds.Text = _bill.TdsPct.ToString("0.##", Fmt.Culture);
        _other.Text = Fmt.Money(_bill.OtherDeduction);
        _paid.Text = Fmt.Money(_bill.PaidAmount);
        Ui.Set(_paidOn, _bill.PaidOn);
        _remarks.Text = _bill.Remarks;
        _delete.Enabled = _duplicate.Enabled = _bill.Id != 0;
        Text = _bill.Id == 0 ? "New bill" : $"Bill {_bill.BillNo}";
        FillGrid();
        _loading = false;
        _dirty = false;
    }

    void MarkDirty()
    {
        // Controls raise change events while their windows are created; only count changes once the form is on screen.
        if (_loading || !_ready) return;
        _dirty = true;
        if (!Text.EndsWith('*')) Text += " *";
    }

    void WorkOrderChanged()
    {
        if (_wo.SelectedItem is not WorkOrder w) return;
        _contractor.Text = w.ContractorName;
        if (_loading) return;
        MarkDirty();
        if (_bill.Id == 0) _billNo.Text = _repo.NextBillNo(w.Id);
    }

    // ───────────────────────── Grid ─────────────────────────

    void BuildGrid()
    {
        var g = _grid;
        g.ReadOnly = false;
        g.SelectionMode = DataGridViewSelectionMode.CellSelect;
        g.MultiSelect = true;
        g.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        g.EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;
        g.CellBorderStyle = DataGridViewCellBorderStyle.Single;
        g.RowTemplate.Height = 30;

        DataGridViewTextBoxColumn Text(string name, string header, int width, bool ro = false, bool right = false, bool fill = false)
        {
            var c = new DataGridViewTextBoxColumn { Name = name, HeaderText = header, Width = width, ReadOnly = ro, SortMode = DataGridViewColumnSortMode.NotSortable };
            if (right) { c.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight; c.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleRight; }
            if (ro) c.DefaultCellStyle.ForeColor = Ui.Ink;
            if (fill) { c.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill; c.FillWeight = 100; c.MinimumWidth = width; }
            return c;
        }

        g.Columns.Add(Text(CNo, "#", 36, ro: true, right: true));
        g.Columns.Add(Text(CItem, "Item", 300, fill: true));
        g.Columns.Add(Text(CRemarks, "Remarks", 150));
        foreach (var f in _lineFields) g.Columns.Add(Text(ExtraCol + f.Name, f.Name, 110));
        var act = new DataGridViewComboBoxColumn
        {
            Name = CAct, HeaderText = "Activity", Width = 150, FlatStyle = FlatStyle.Flat,
            DisplayStyle = DataGridViewComboBoxDisplayStyle.ComboBox, SortMode = DataGridViewColumnSortMode.NotSortable,
        };
        act.Items.Add("");
        act.Items.AddRange(_acts.Select(a => (object)a.Name).ToArray());
        g.Columns.Add(act);
        g.Columns.Add(Text(CUnit, "Unit", 56));
        g.Columns.Add(Text(CQty, "Qty", 90, right: true));
        g.Columns.Add(Text(CRate, "Rate ₹", 100, right: true));
        g.Columns.Add(Text(CAmount, "Amount ₹", 120, ro: true, right: true));
        var scope = Text(CScope, "Scope and progress", 280, ro: true);
        scope.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        scope.DefaultCellStyle.Font = Ui.Small;
        g.Columns.Add(scope);
        g.Columns.Add(new DataGridViewButtonColumn { Name = CRemove, HeaderText = "", Text = "✕", UseColumnTextForButtonValue = true, Width = 34, FlatStyle = FlatStyle.System });
        g.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;

        g.CellValidating += (_, e) =>
        {
            var col = g.Columns[e.ColumnIndex].Name;
            if (col is CQty or CRate && !Fmt.TryParse(e.FormattedValue?.ToString(), out decimal _))
            {
                g.Rows[e.RowIndex].ErrorText = $"{g.Columns[e.ColumnIndex].HeaderText}: '{e.FormattedValue}' is not a number.";
                e.Cancel = true;
            }
        };
        g.CellEndEdit += (_, e) => { g.Rows[e.RowIndex].ErrorText = ""; CellChanged(e.RowIndex, e.ColumnIndex); };
        g.CurrentCellDirtyStateChanged += (_, _) =>
        {
            // Apply a combo box choice at once instead of when the cell is left.
            if (g.IsCurrentCellDirty && g.CurrentCell is DataGridViewComboBoxCell) g.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        g.CellValueChanged += (_, e) =>
        {
            if (!_loading && e.RowIndex >= 0 && g.Columns[e.ColumnIndex].Name == CAct) CellChanged(e.RowIndex, e.ColumnIndex);
        };
        g.CellContentClick += (_, e) =>
        {
            if (e.RowIndex < 0) return;
            var col = g.Columns[e.ColumnIndex].Name;
            if (col == CRemove) RemoveLine(e.RowIndex);
        };
        g.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0 && g.Columns[e.ColumnIndex].Name == CItem && _lines[e.RowIndex].IsBom) ChangeBomItem(e.RowIndex);
        };
        g.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete && !g.IsCurrentCellInEditMode && g.CurrentCell?.OwningColumn.Name == CNo) { e.Handled = true; RemoveSelected(); }
            if (e.KeyCode == Keys.Enter && g.CurrentCell is { } c && c.OwningColumn.Name == CItem && _lines[c.RowIndex].IsBom) { e.Handled = true; ChangeBomItem(c.RowIndex); }
        };
        g.DataError += (_, e) => e.ThrowException = false;
    }

    void FillGrid()
    {
        var was = _loading;
        _loading = true;
        _grid.Rows.Clear();
        for (var i = 0; i < _lines.Count; i++)
        {
            _grid.Rows.Add();
            WriteRow(i);
        }
        _loading = was;
        UpdateScope();
    }

    void WriteRow(int i)
    {
        var l = _lines[i];
        var r = _grid.Rows[i];
        r.Cells[CNo].Value = (i + 1).ToString(Fmt.Culture);
        if (l.IsBom)
        {
            r.Cells[CItem].Value = _bom.TryGetValue(l.BomItemId!.Value, out var b) ? b.Label : "(BOM row deleted)";
            r.Cells[CItem].ReadOnly = true;
            r.Cells[CItem].ToolTipText = "Double-click to choose another drawing or item";
            r.Cells[CItem].Style.BackColor = ColorTranslator.FromHtml("#EEF3FC");
            r.Cells[CUnit].ReadOnly = true;
            r.Cells[CUnit].Style.ForeColor = Ui.Muted;
        }
        else
        {
            r.Cells[CItem].Value = l.Description;
            r.Cells[CItem].ReadOnly = false;
            r.Cells[CItem].ToolTipText = "Description of the work";
        }
        r.Cells[CRemarks].Value = l.Remarks;
        if (_lineFields.Count > 0)
        {
            var x = Extras.Parse(l.Extra);
            foreach (var f in _lineFields) r.Cells[ExtraCol + f.Name].Value = x.GetValueOrDefault(f.Name, "");
        }
        r.Cells[CAct].Value = l.ActivityId is long a ? _acts.FirstOrDefault(x => x.Id == a)?.Name ?? "" : "";
        r.Cells[CUnit].Value = l.Unit;
        r.Cells[CQty].Value = l.IsBom || l.Unit is "t" or "MT" ? l.Qty.ToString("N3", Fmt.Culture) : l.Qty.ToString("#,##0.###", Fmt.Culture);
        r.Cells[CRate].Value = l.Rate.ToString("#,##,##0.##", Fmt.Culture);
        r.Cells[CAmount].Value = Fmt.Money(l.Amount);
    }

    void CellChanged(int row, int col)
    {
        if (_loading || row < 0 || row >= _lines.Count) return;
        var l = _lines[row];
        var cell = _grid.Rows[row].Cells[col];
        var text = cell.Value?.ToString() ?? "";
        switch (_grid.Columns[col].Name)
        {
            case CItem when !l.IsBom: l.Description = text.Trim(); break;
            case CRemarks: l.Remarks = text.Trim(); break;
            case var name when name.StartsWith(ExtraCol, StringComparison.Ordinal):
                var values = Extras.Parse(l.Extra);
                values[name[ExtraCol.Length..]] = text.Trim();
                l.Extra = Extras.Write(values);
                break;
            case CUnit when !l.IsBom: l.Unit = text.Trim(); break;
            case CQty: l.Qty = Fmt.TryParse(text, out var q) ? q : 0; break;
            case CRate: l.Rate = Fmt.TryParse(text, out var r) ? r : 0; break;
            case CAct:
                var a = _acts.FirstOrDefault(x => x.Name == text);
                l.ActivityId = a?.Id;
                if (a != null)
                {
                    if (!l.IsBom && l.Unit.Length == 0) l.Unit = a.DefaultUnit;
                    if (l.Rate == 0) ApplyRate(l);
                }
                break;
            default: return;
        }
        _loading = true;
        WriteRow(row);
        _loading = false;
        MarkDirty();
        UpdateScope();
    }

    void ApplyRate(BillLine l)
    {
        if (_wo.SelectedItem is not WorkOrder w || l.ActivityId is not long a) return;
        var rate = _repo.FindRate(w.Id, a);
        if (rate == null) return;
        l.Rate = rate.Amount;
        if (!l.IsBom && rate.Unit.Length > 0) l.Unit = rate.Unit;
    }

    /// <summary>Recomputes the "Scope and progress" column, the warning and the totals.</summary>
    void UpdateScope()
    {
        var bomLines = _lines.Where(l => l.IsBom).ToList();
        var keys = bomLines.Select(l => (l.BomItemId!.Value, l.ActivityId ?? 0)).Distinct().ToList();
        var scope = _repo.ScopeFor(_bill.Id, keys);
        var over = 0;
        var thisBill = bomLines.GroupBy(l => (l.BomItemId!.Value, l.ActivityId ?? 0)).ToDictionary(g => g.Key, g => g.Sum(l => l.Qty));
        for (var i = 0; i < _lines.Count; i++)
        {
            var l = _lines[i];
            var cell = _grid.Rows[i].Cells[CScope];
            if (!l.IsBom)
            {
                cell.Value = "free line";
                cell.Style.ForeColor = Ui.Muted;
                continue;
            }
            var key = (l.BomItemId!.Value, l.ActivityId ?? 0);
            var (s, earlier) = scope[key];
            var info = new ScopeInfo(s, earlier, thisBill[key]);
            var actName = _acts.FirstOrDefault(a => a.Id == l.ActivityId)?.Name ?? "no activity";
            var text = $"Scope {Fmt.Tonnes(s)} t · {actName}: earlier {Fmt.Tonnes(earlier)} · this {Fmt.Tonnes(info.This)} · {Fmt.Pct(info.Percent)}% billed";
            if (info.Over)
            {
                over++;
                text += $" · OVER SCOPE ({Fmt.Tonnes(info.Cumulative)} of {Fmt.Tonnes(s)} t)";
            }
            cell.Value = text;
            cell.Style.ForeColor = info.Over ? Ui.Bad : Ui.Muted;
        }
        _warn.Text = over > 0
            ? $"{over} line(s) would take the cumulative billing above the BOM scope. You can still save; the line is flagged."
            : _lines.Any(l => l.IsBom && l.ActivityId == null) ? "Choose an activity for every BOM line: the scope is tracked per activity." : "";
        UpdateTotals();
    }

    void UpdateTotals()
    {
        if (!TryReadHeader(out var b, showErrors: false)) return;
        var t = BillMath.Totals(b, _lines);
        _totals.SuspendLayout();
        _totals.Controls.Clear();
        void Row(string label, decimal v, bool bold = false)
        {
            _totals.Controls.Add(new Label { Text = label, AutoSize = true, Font = bold ? Ui.Bold : Ui.Base, Margin = new Padding(0, 2, 0, 2) });
            _totals.Controls.Add(new Label { Text = "₹ " + Fmt.Money(v), AutoSize = true, Font = bold ? Ui.Bold : Ui.Base, Anchor = AnchorStyles.Right, Margin = new Padding(0, 2, 0, 2) });
        }
        Row("Gross", t.Gross, true);
        Row($"+ GST {b.GstPct.ToString("0.00", Fmt.Culture)}%", t.Gst);
        Row($"− Retention {b.RetentionPct.ToString("0.00", Fmt.Culture)}%", t.Retention);
        Row($"− TDS {b.TdsPct.ToString("0.00", Fmt.Culture)}%", t.Tds);
        Row("− Other deduction", t.Other);
        Row("Net payable", t.Net, true);
        Row("Paid", t.Paid);
        Row("Balance due", t.Due, true);
        _totals.ResumeLayout();
    }

    // ───────────────────────── Line actions ─────────────────────────

    long? DefaultBomActivity()
    {
        var service = (_wo.SelectedItem as WorkOrder)?.Service ?? "";
        var bomActs = _acts.Where(a => a.IsBom).ToList();
        return (bomActs.FirstOrDefault(a => service.Contains(a.Name.Split(' ')[0], StringComparison.OrdinalIgnoreCase)) ?? bomActs.FirstOrDefault())?.Id;
    }

    void AddBomLines()
    {
        if (_bom.Count == 0)
        {
            Ui.Info(this, "The BOM is empty. Import it on the Data tab (Import BOM from Excel) first, or add a free line.");
            return;
        }
        using var p = new BomPickerForm(_repo);
        if (p.ShowDialog(this) != DialogResult.OK) return;
        var act = DefaultBomActivity();
        foreach (var item in p.Selected)
        {
            var l = new BillLine { BomItemId = item.Id, ActivityId = act, Unit = "t" };
            ApplyRate(l);
            l.Unit = "t";
            _lines.Add(l);
        }
        FillGrid();
        MarkDirty();
        FocusCell(_lines.Count - 1, CQty);
    }

    void ChangeBomItem(int row)
    {
        using var p = new BomPickerForm(_repo, single: true);
        if (p.ShowDialog(this) != DialogResult.OK || p.Selected.Count == 0) return;
        _lines[row].BomItemId = p.Selected[0].Id;
        FillGrid();
        MarkDirty();
    }

    void AddFreeLine()
    {
        var a = _acts.FirstOrDefault(x => !x.IsBom);
        var l = new BillLine { ActivityId = a?.Id, Unit = a?.DefaultUnit ?? "" };
        ApplyRate(l);
        _lines.Add(l);
        FillGrid();
        MarkDirty();
        FocusCell(_lines.Count - 1, CItem);
        _grid.BeginEdit(true);
    }

    void FocusCell(int row, string col)
    {
        if (row < 0 || row >= _grid.Rows.Count) return;
        _grid.CurrentCell = _grid.Rows[row].Cells[col];
        _grid.Focus();
    }

    void RemoveLine(int row)
    {
        if (row < 0 || row >= _lines.Count) return;
        _lines.RemoveAt(row);
        FillGrid();
        MarkDirty();
    }

    void RemoveSelected()
    {
        var rows = _grid.SelectedCells.Cast<DataGridViewCell>().Select(c => c.RowIndex).Distinct().OrderByDescending(i => i).ToList();
        if (rows.Count == 0) return;
        foreach (var i in rows) _lines.RemoveAt(i);
        FillGrid();
        MarkDirty();
    }

    // ───────────────────────── Save, delete, print ─────────────────────────

    bool TryReadHeader(out Bill b, bool showErrors)
    {
        b = new Bill
        {
            Id = _bill.Id, CreatedAt = _bill.CreatedAt,
            WorkOrderId = (_wo.SelectedItem as WorkOrder)?.Id ?? 0,
            BillNo = _billNo.Text.Trim(), BillType = _type.Text.Trim().Length > 0 ? _type.Text.Trim() : "RA bill",
            BillDate = _date.Value.Date, PeriodFrom = Ui.Value(_from), PeriodTo = Ui.Value(_to),
            Status = _status.SelectedItem as string ?? BillStatus.Draft, PaidOn = Ui.Value(_paidOn), Remarks = _remarks.Text.Trim(),
        };
        foreach (var (box, name) in new[] { (_gst, "GST %"), (_ret, "Retention %"), (_tds, "TDS %"), (_other, "Other deduction"), (_paid, "Paid") })
        {
            if (!Fmt.TryParse(box.Text, out var v))
            {
                if (showErrors) { Ui.Ask(this, $"{name}: '{box.Text}' is not a number.", Text); box.Focus(); }
                return false;
            }
            if (box == _gst) b.GstPct = v;
            else if (box == _ret) b.RetentionPct = v;
            else if (box == _tds) b.TdsPct = v;
            else if (box == _other) b.OtherDeduction = v;
            else b.PaidAmount = v;
        }
        return true;
    }

    bool Save(bool close)
    {
        _grid.EndEdit();
        if (!ValidateChildren() || !TryReadHeader(out var b, showErrors: true)) return false;

        if (b.Status == BillStatus.Paid && b.PaidAmount == 0)
        {
            var net = BillMath.Totals(b, _lines).Net;
            var answer = Ui.Ask(this, $"The bill is marked Paid but the paid amount is 0.{Environment.NewLine}Set paid = net payable (₹ {Fmt.Money(net)}) on {Fmt.Date(b.PaidOn ?? DateTime.Today)}?",
                Text, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (answer == DialogResult.Cancel) return false;
            if (answer == DialogResult.Yes) { b.PaidAmount = net; b.PaidOn ??= DateTime.Today; }
        }
        if (_lines.Count == 0 && !Ui.Confirm(this, "This bill has no lines. Save it anyway?")) return false;

        if (!Ui.Try(this, () => _repo.SaveBill(b, _lines.Select(l => l.Clone())))) return false;
        _bill = _repo.GetBill(b.Id)!.Value.Bill;
        var reloaded = _repo.GetBill(b.Id)!.Value.Lines;
        _lines.Clear();
        _lines.AddRange(reloaded);
        Saved?.Invoke();
        if (close) { DialogResult = DialogResult.OK; Close(); }
        else LoadFields();
        return true;
    }

    void Delete()
    {
        if (_bill.Id == 0) return;
        if (!Ui.Confirm(this, $"Delete bill {_bill.BillNo} and its {_lines.Count} line(s)? This cannot be undone.")) return;
        if (!Ui.Try(this, () => _repo.DeleteBill(_bill.Id))) return;
        Saved?.Invoke();
        DialogResult = DialogResult.OK;
        Close();
    }

    void Duplicate()
    {
        if (_bill.Id == 0) return;
        if (_dirty)
        {
            if (!Ui.Confirm(this, "Save the changes before making a copy?")) return;
            if (!Save(close: false)) return;
        }
        long newId = 0;
        if (!Ui.Try(this, () => newId = _repo.DuplicateBill(_bill.Id))) return;
        Saved?.Invoke();
        var copy = _repo.GetBill(newId)!.Value;
        _bill = copy.Bill;
        _lines.Clear();
        _lines.AddRange(copy.Lines);
        LoadFields();
        Ui.Info(this, $"Copied as {_bill.BillNo} (Draft, unpaid). You are now editing the copy.");
    }

    void Print()
    {
        _grid.EndEdit();
        if (!TryReadHeader(out var b, showErrors: true)) return;
        var wo = _wo.SelectedItem as WorkOrder;
        var lines = _lines.Select((l, i) => (Line: l,
            Item: l.IsBom ? (_bom.TryGetValue(l.BomItemId!.Value, out var x) ? x.Label : "") : l.Description,
            Activity: _acts.FirstOrDefault(a => a.Id == l.ActivityId)?.Name ?? "",
            Scope: _grid.Rows[i].Cells[CScope].Value?.ToString() ?? "")).ToList();
        Ui.Preview(this, new BillPrinter(b, wo, lines, _repo.GetSetting("ProjectName") ?? "BF#3"));
    }
}
