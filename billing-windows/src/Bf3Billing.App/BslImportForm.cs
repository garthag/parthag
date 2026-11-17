using Bf3Billing.Core;
using Bf3Billing.Core.Bsl;

namespace Bf3Billing.App;

/// <summary>Imports the BSL master BOM and contractor master workbooks after showing what was found.</summary>
sealed class BslImportForm : Form
{
    readonly BillingRepository _repo;
    readonly TextBox _bom = new() { Width = 620, ReadOnly = true, AccessibleName = "BOM workbook" };
    readonly TextBox _con = new() { Width = 620, ReadOnly = true, AccessibleName = "Contractor master workbook" };
    readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    readonly Label _status = new() { Dock = DockStyle.Top, AutoSize = false, Height = 44, Padding = new Padding(0, 6, 0, 0) };
    readonly Button _check, _save, _import;
    BslPlan? _plan;
    public bool Changed { get; private set; }

    public BslImportForm(BillingRepository repo, string? bomPath = null, string? contractorPath = null)
    {
        _repo = repo;
        Text = "Import BSL workbooks – master BOM and contractor master";
        Font = Ui.Base;
        BackColor = Ui.Back;
        Size = new Size(1200, 780);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        Padding = new Padding(12);

        var files = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3 };
        files.Controls.Add(Ui.Lbl("Master BOM (BSL-MASTER BOM-….xlsx)"));
        files.Controls.Add(_bom);
        files.Controls.Add(Ui.Btn("Choose…", (_, _) => Pick(_bom)));
        files.Controls.Add(Ui.Lbl("Contractor master (Contractor_Master_BSL_….xlsx)"));
        files.Controls.Add(_con);
        files.Controls.Add(Ui.Btn("Choose…", (_, _) => Pick(_con)));
        _bom.Text = bomPath ?? "";
        _con.Text = contractorPath ?? "";

        var bar = Ui.Bar();
        _check = Ui.Btn("Check the workbooks", (_, _) => Check(), primary: true);
        _save = Ui.Btn("Save check to Excel…", (_, _) => SaveCheck());
        _import = Ui.Btn("Import", (_, _) => Import(), primary: true);
        bar.Controls.AddRange(new Control[] { _check, _save, _import });
        bar.Controls.Add(Ui.Lbl("Nothing is saved until you press Import. Importing again later updates what is already there; RA bills already saved are kept.", Ui.Small, Ui.Muted));

        Controls.Add(_tabs);
        Controls.Add(_status);
        Controls.Add(bar);
        Controls.Add(files);
        _status.Text = "Choose one or both workbooks, then check them.";
        Enable();
    }

    void Pick(TextBox box)
    {
        var p = Ui.OpenFile(this, "Choose the workbook");
        if (p != null) { box.Text = p; _plan = null; _tabs.TabPages.Clear(); Enable(); }
    }

    void Enable()
    {
        _check.Enabled = _bom.Text.Length > 0 || _con.Text.Length > 0;
        _save.Enabled = _import.Enabled = _plan != null;
    }

    internal void Check()
    {
        Cursor = Cursors.WaitCursor;
        _status.Text = "Reading the workbooks… (a large BOM takes a few seconds)";
        Application.DoEvents();
        try
        {
            _plan = BslImport.Analyze(_bom.Text.Length > 0 ? _bom.Text : null, _con.Text.Length > 0 ? _con.Text : null);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            _plan = null;
            _status.Text = "The workbook could not be read: " + ex.Message + " (is it open in Excel?)";
            Enable();
            return;
        }
        finally { Cursor = Cursors.Default; }

        _tabs.TabPages.Clear();
        void Page(string title, System.Data.DataTable t)
        {
            var g = Ui.Grid();
            var page = new TabPage($"{title} ({t.Rows.Count})");
            page.Controls.Add(g);
            _tabs.TabPages.Add(page);
            Ui.Bind(g, t, "N0");
            g.CellFormatting += (_, e) =>
            {
                if (e.RowIndex < 0 || g.Rows[e.RowIndex].DataBoundItem is not System.Data.DataRowView v) return;
                var text = string.Join(" ", v.Row.ItemArray.OfType<string>());
                if (text.Contains("Warning") || text.Contains("Differs")) e.CellStyle!.BackColor = ColorTranslator.FromHtml("#FFF1D6");
            };
        }
        if (_plan.Bom != null) Page("BOM sheets", _plan.Reconciliation());
        if (_plan.Contractors != null) Page("RA bills to add", _plan.RaBills());
        Page("What was found", _plan.IssueTable());

        var parts = new List<string>();
        if (_plan.Bom != null)
            parts.Add($"BOM: {_plan.Bom.Packages.Count} sheets, {_plan.Drawings().Count():N0} drawings, {_plan.Bom.Packages.Sum(p => p.Rows.Count):N0} lines.");
        if (_plan.Contractors is { } c)
            parts.Add($"Contractor master: {c.Contractors.Count} contractors, {c.WorkOrders.Count} work orders, {c.Boq.Count(b => !b.IsGroupHeader)} BOQ items, " +
                      $"{c.DrawingScope.Count} scope rows, {_plan.Extras.Count} extra items, {_plan.RaBills().Rows.Count} RA bills.");
        var warn = _plan.Issues.Count(i => i.Severity == "Warning");
        _status.Text = string.Join("  ", parts) + $"  {warn} warning(s) – see 'What was found'.";
        Enable();
    }

    void SaveCheck()
    {
        if (_plan == null) return;
        var path = Ui.SaveFile(this, "Save the check", "BSL import check.xlsx");
        if (path != null && Ui.Try(this, () => BslImport.WriteReport(_plan, path))) Ui.OfferOpen(this, path, "Saved.");
    }

    void Import()
    {
        if (_plan == null) return;
        if (!Ui.Confirm(this, "Import into the data file now?\n\n" + _repo.Db.FilePath + "\n\nTip: Data › Back up now first, if this file already holds your own bills.")) return;
        BslResult? r = null;
        Cursor = Cursors.WaitCursor;
        try { if (!Ui.Try(this, () => r = BslImport.Apply(_repo, _plan))) return; }
        finally { Cursor = Cursors.Default; }
        Changed = true;
        Ui.Info(this, "Imported.\n\n" + r!.Summary);
        Close();
    }
}
