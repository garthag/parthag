using BSL.Core.Excel;
using BSL.Core.Import;

namespace BSL.Desktop.Forms;

public enum WorkbookKind { Bom, Contractor }

/// <summary>
/// Analyse → review → import. For the BOM workbook it shows every sheet with lines, sets, weight and the
/// reconciliation with the sheet's own total, plus every data issue; the standardised workbook can be saved
/// to Excel without touching the database.
/// </summary>
public sealed class ImportWizardForm : Form
{
    readonly WorkbookKind _kind;
    readonly TextBox _file = new() { Width = 560, ReadOnly = true };
    readonly CheckedListBox _sheets = new() { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
    readonly DataGridView _summary = Grid();
    readonly DataGridView _issues = Grid();
    readonly CheckBox _replace = new() { Text = "Replace existing data of the imported sheets / BOQ", Checked = true, AutoSize = true };
    readonly CheckBox _cBoq = new() { Text = "BOQ items", Checked = true, AutoSize = true };
    readonly CheckBox _cRa = new() { Text = "RA bill quantities", Checked = true, AutoSize = true };
    readonly CheckBox _cScope = new() { Text = "Drawing scope sheet", Checked = true, AutoSize = true };
    readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 24, ForeColor = Color.DimGray, Padding = new Padding(6, 4, 0, 0) };
    readonly Button _btnStd, _btnImport;
    BomWorkbook? _bom;
    ContractorWorkbook? _con;

    public ImportWizardForm(WorkbookKind kind)
    {
        _kind = kind;
        Text = kind == WorkbookKind.Bom ? "Import / standardise BOM workbook" : "Import contractor workbook (contractors, work orders, BOQ, RA bills, drawing scope)";
        Width = 1180; Height = 760; StartPosition = FormStartPosition.CenterParent;

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(6) };
        top.Controls.AddRange([new Label { Text = "Workbook:", AutoSize = true, Padding = new Padding(0, 7, 0, 0) }, _file,
                               Ui.Button("Browse…", (_, _) => Browse()), Ui.Button("Analyse", (_, _) => Analyse())]);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        var t1 = new TabPage(kind == WorkbookKind.Bom ? "Sheets & reconciliation" : "Summary");
        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 250 };
        split.Panel1.Controls.Add(_sheets);
        split.Panel1.Controls.Add(new Label { Text = "Sheets to import:", Dock = DockStyle.Top, Height = 20 });
        split.Panel2.Controls.Add(_summary);
        split.Panel1Collapsed = kind != WorkbookKind.Bom;
        t1.Controls.Add(split);
        var t2 = new TabPage("Data issues found");
        t2.Controls.Add(_issues);
        tabs.TabPages.AddRange([t1, t2]);

        var opts = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 34, Padding = new Padding(6) };
        opts.Controls.Add(_replace);
        if (kind == WorkbookKind.Contractor) opts.Controls.AddRange([_cBoq, _cRa, _cScope]);

        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(4) };
        _btnImport = Ui.Button("Import into database", (_, _) => DoImport(), 170);
        _btnStd = Ui.Button("Save standardised Excel…", (_, _) => SaveStandard(), 190);
        bar.Controls.AddRange([Ui.Button("Close", (_, _) => Close()), _btnImport, _btnStd, Ui.Button("Export issues…", (_, _) => ExportIssues(), 130)]);
        _btnImport.Enabled = _btnStd.Enabled = false;
        _btnStd.Visible = kind == WorkbookKind.Bom;

        Controls.Add(tabs); Controls.Add(top); Controls.Add(opts); Controls.Add(bar); Controls.Add(_status);
    }

    static DataGridView Grid() => new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, RowHeadersVisible = false,
        BackgroundColor = SystemColors.Window, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells
    };

    void Browse()
    {
        var f = Ui.OpenExcel();
        if (f == null) return;
        _file.Text = f;
        Analyse();
    }

    async void Analyse()
    {
        if (!File.Exists(_file.Text)) { Browse(); return; }
        _status.Text = "Reading workbook … (large sheets take a few seconds)";
        UseWaitCursor = true; Enabled = false;
        try
        {
            if (_kind == WorkbookKind.Bom)
            {
                var path = _file.Text;
                _bom = await Task.Run(() => BomWorkbookReader.Read(path));
                _sheets.Items.Clear();
                foreach (var p in _bom.Packages) _sheets.Items.Add(p.SheetName, true);
                _summary.DataSource = _bom.Packages.Select(p => new
                {
                    Sheet = p.SheetName, p.Title, HeaderRow = p.HeaderRow, Lines = p.Rows.Count, Excluded = p.Rows.Count(r => r.IsExcluded),
                    Drawings = p.Rows.Select(r => r.DrawingNo).Distinct().Count(), Sets = p.NoOfSets,
                    WtPerSet_MT = Math.Round(p.CountedWeightKg / 1000, 3),
                    WtAllSets_MT = Math.Round(p.CountedWeightKg * p.NoOfSets / 1000, 3),
                    SheetTotal_MT = p.SheetTotalMt is { } s ? Math.Round(s, 3) : (double?)null,
                    Difference_MT = p.SheetTotalMt is { } s2 ? Math.Round(p.CountedWeightKg / 1000 - s2, 3) : (double?)null,
                    FlaggedLines = p.Rows.Count(r => r.Flags.Count > 0)
                }).ToList();
                _issues.DataSource = _bom.Issues.OrderBy(i => i.Severity == "Warning" ? 0 : 1).ThenBy(i => i.Sheet).ThenBy(i => i.Row).ToList();
                _status.Text = $"{_bom.Packages.Count} sheets, {_bom.AllRows.Count():N0} lines, total {_bom.Packages.Sum(p => p.CountedWeightKg * p.NoOfSets) / 1000:N3} MT (all sets). " +
                               $"{_bom.Issues.Count(i => i.Severity == "Warning")} warnings.";
            }
            else
            {
                var path = _file.Text;
                _con = await Task.Run(() => ContractorWorkbookReader.Read(path));
                _summary.DataSource = new[]
                {
                    new { Item = "Contractors (unique code)", Count = _con.Contractors.Count, Detail = string.Join(", ", _con.Contractors.Select(c => c.Code)) },
                    new { Item = "Work orders (Unique_No)", Count = _con.WorkOrders.Count, Detail = string.Join(", ", _con.WorkOrders.Select(w => w.UniqueNo)) },
                    new { Item = "BOQ items", Count = _con.Boq.Count(b => !b.IsGroupHeader), Detail = $"{_con.Boq.Count(b => b.IsGroupHeader)} group headers" },
                    new { Item = "RA quantities", Count = _con.Boq.Sum(b => b.Ra.Count), Detail = $"RA nos: {string.Join(", ", _con.Boq.SelectMany(b => b.Ra.Keys).Distinct().OrderBy(x => x))}" },
                    new { Item = "Drawing scope rows", Count = _con.DrawingScope.Count, Detail = "" }
                }.ToList();
                _issues.DataSource = _con.Issues.OrderBy(i => i.Severity == "Warning" ? 0 : 1).ToList();
                _status.Text = $"Read OK. {_con.Issues.Count(i => i.Severity == "Warning")} warnings – see 'Data issues found'.";
            }
            _btnImport.Enabled = _btnStd.Enabled = true;
        }
        catch (Exception ex) { Ui.Error(ex); _status.Text = "Error: " + ex.Message; }
        finally { UseWaitCursor = false; Enabled = true; }
    }

    void SaveStandard()
    {
        if (_bom == null) return;
        var path = Ui.Save(Path.GetFileNameWithoutExtension(_file.Text) + "_STANDARDISED.xlsx");
        if (path == null) return;
        Ui.Busy(this, () => StandardBomWriter.Write(_bom, path));
        Ui.OpenFile(path);
    }

    void ExportIssues()
    {
        var issues = _bom?.Issues ?? _con?.Issues;
        if (issues == null) return;
        var path = Ui.Save("Data_issues.xlsx");
        if (path == null) return;
        using var wb = new ClosedXML.Excel.XLWorkbook();
        var ws = wb.AddWorksheet("Issues");
        ws.Cell(1, 1).InsertTable(issues.Select(i => new { i.Sheet, i.Row, i.Severity, i.Code, i.Message }));
        ws.Columns().AdjustToContents(1, 200, 8, 100);
        wb.SaveAs(path);
        Ui.OpenFile(path);
    }

    async void DoImport()
    {
        var svc = new ImportService(Program.Db);
        svc.Progress += s => BeginInvoke(() => _status.Text = s);
        UseWaitCursor = true; Enabled = false;
        try
        {
            ImportSummary res;
            if (_kind == WorkbookKind.Bom && _bom != null)
            {
                var sel = _sheets.CheckedItems.Cast<string>().ToList();
                if (sel.Count == 0) { Ui.Error("Tick at least one sheet."); return; }
                if (!Ui.Confirm($"Import {sel.Count} sheet(s) into the database?" + (_replace.Checked ? "\nExisting BOM lines of these packages are replaced." : ""))) return;
                var bom = _bom; var replace = _replace.Checked;
                res = await Task.Run(() => svc.ImportBom(bom, replace, sel));
            }
            else if (_con != null)
            {
                if (_replace.Checked && _cBoq.Checked && !Ui.Confirm("Replace ALL BOQ items and RA bills with the workbook content?")) return;
                var opt = new ImportService.ContractorImportOptions(Boq: _cBoq.Checked, RaBills: _cRa.Checked && _cBoq.Checked,
                                                                    DrawingScope: _cScope.Checked, ReplaceBoq: _replace.Checked);
                var con = _con;
                res = await Task.Run(() => svc.ImportContractors(con, opt));
            }
            else return;
            _status.Text = res.Text;
            Ui.Info(res.Text + "\n\nWarnings were saved in Settings > Import history.");
        }
        catch (Exception ex) { Ui.Error(ex); }
        finally { UseWaitCursor = false; Enabled = true; }
    }
}
