using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using SiteNotes.Core;
using SiteNotes.Core.Models;
using SiteNotes.Core.Reports;
using SiteNotes.Desktop.Services;
using SiteNotes.Desktop.UI;

namespace SiteNotes.Desktop.Views;

public abstract class ViewBase : UserControl, IView
{
    public abstract string Title { get; }
    public virtual string Key => GetType().Name;
    public abstract Task RefreshAsync();
    protected ViewBase() => Loaded += async (_, _) => { if (!_loaded) { _loaded = true; await Dialogs.Guard(RefreshAsync); } };
    private bool _loaded;
}

/// <summary>
/// Editable spreadsheet-style list: type straight into the grid, every edited row saves itself.
/// Search box, column-header sorting, multi-select (Ctrl/Shift) with bulk actions, PDF/Excel export.
/// </summary>
public class GridView<T> : ViewBase where T : SyncEntity, new()
{
    private readonly string _title;
    private readonly Func<T> _create;
    private readonly Action<GridBuilder> _columns;
    private readonly Func<T, string> _searchText;
    private readonly ObservableCollection<T> _items = [];
    private readonly ICollectionView _view;
    private readonly DataGrid _grid;
    private readonly TextBox _search = new() { Width = 260, ToolTip = "Search" };
    private readonly TextBlock _count = W.Muted("");
    private readonly ComboBox _filter = new() { Width = 170, Margin = new Thickness(8, 0, 0, 0) };
    private readonly bool _projectScoped;
    private readonly ToolBar _toolbar;
    private bool _actionsAdded;

    public Func<IEnumerable<T>, IEnumerable<T>> Order { get; init; } = x => x;
    public (string Name, Func<T, bool> Match)[] Filters { get; init; } = [];
    public (string Name, Action<List<T>> Apply)[] BulkActions { get; init; } = [];
    public Func<List<T>, string, Task>? Pdf { get; init; }
    public Func<List<T>, string, Task>? Excel { get; init; }
    public Func<T, Task>? AfterSave { get; init; }
    public Func<T, Task>? OpenDetail { get; init; }
    /// <summary>Edit-in-Excel sheet of this module: adds “Export to edit” and “Import Excel…”.</summary>
    public Func<Task<ExchangeSheet<T>>>? Exchange { get; init; }
    /// <summary>Extra toolbar buttons acting on the selected row(s).</summary>
    public (string Glyph, string Text, Func<List<T>, Task> Run)[] RowActions { get; init; } = [];

    public override string Title => _title;
    public override string Key => "grid:" + _title;
    public DataGrid Grid => _grid;

    public GridView(string title, Func<T> create, Action<GridBuilder> columns, Func<T, string> searchText, bool projectScoped = true, string? hint = null)
    {
        _title = title;
        _create = create;
        _columns = columns;
        _searchText = searchText;
        _projectScoped = projectScoped;
        _view = CollectionViewSource.GetDefaultView(_items);
        _view.Filter = o => Matches((T)o);

        var gb = new GridBuilder(_view);
        columns(gb);
        _grid = gb.OnEdited(async o => await Dialogs.Guard(() => SaveRowAsync((T)o))).Grid;
        _grid.MouseDoubleClick += async (_, _) => { if (OpenDetail != null && _grid.SelectedItem is T t) await OpenDetail(t); };

        _search.TextChanged += (_, _) => Refilter();
        _filter.SelectionChanged += (_, _) => Refilter();

        _toolbar = W.ToolBar(
            (Glyph.Add, "Add", AddAsync),
            (Glyph.Delete, "Delete selected", DeleteAsync),
            (Glyph.Accept, "Actions on selected…", BulkAsync),
            null,
            (Glyph.Print, "PDF", () => ExportAsync(true)),
            (Glyph.Excel, "Excel", () => ExportAsync(false)),
            null,
            (Glyph.Refresh, "Refresh", RefreshAsync));

        var bar = new DockPanel { Margin = new Thickness(10, 8, 10, 6), LastChildFill = false };
        bar.Children.Add(W.Text("🔍 ", 13));
        bar.Children.Add(_search);
        bar.Children.Add(_filter);
        DockPanel.SetDock(_count, Dock.Right);
        bar.Children.Add(_count);

        var dock = new DockPanel();
        DockPanel.SetDock(_toolbar, Dock.Top);
        dock.Children.Add(_toolbar);
        DockPanel.SetDock(bar, Dock.Top);
        dock.Children.Add(bar);
        if (hint != null)
        {
            var h = W.Muted(hint);
            h.Margin = new Thickness(12, 0, 12, 6);
            DockPanel.SetDock(h, Dock.Top);
            dock.Children.Add(h);
        }
        _grid.Margin = new Thickness(10, 0, 10, 10);
        dock.Children.Add(_grid);
        Content = dock;
        Loaded += (_, _) =>
        {
            if (!_actionsAdded)
            {
                _actionsAdded = true;
                if (Exchange != null)
                {
                    var at2 = _toolbar.Items.Count - 2; // before the separator + Refresh
                    _toolbar.Items.Insert(at2++, new Separator());
                    foreach (var (glyph, text, tip, run) in new (string, string, string, Func<Task>)[]
                    {
                        (Glyph.Edit, "Export to edit", "Excel file of the rows shown (search, filter and sort applied; or the selected rows) to edit and import back", ExportEditAsync),
                        (Glyph.Table, "Import Excel…", "Import the edited file: changed rows are updated, new rows are added, nothing is deleted", ImportEditAsync),
                    })
                    {
                        var b = new Button { Content = W.IconText(glyph, text), Padding = new Thickness(8, 4, 8, 4), ToolTip = tip };
                        b.Click += async (_, _) => await Dialogs.Guard(run);
                        _toolbar.Items.Insert(at2++, b);
                    }
                }
                if (RowActions.Length > 0) _toolbar.Items.Insert(3, new Separator());
                var at = 4;
                foreach (var (glyph, text, run) in RowActions)
                {
                    var btn = new Button { Content = W.IconText(glyph, text), Padding = new Thickness(8, 4, 8, 4) };
                    btn.Click += async (_, _) => await Dialogs.Guard(async () =>
                    {
                        var sel = Selected();
                        if (sel.Count == 0) { Dialogs.Info("Select a row first."); return; }
                        await run(sel);
                    });
                    _toolbar.Items.Insert(at++, btn);
                }
            }
            if (_filter.Items.Count == 0)
            {
                _filter.Items.Add("All");
                foreach (var f in Filters) _filter.Items.Add(f.Name);
                _filter.SelectedIndex = 0;
                _filter.Visibility = Filters.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            }
        };
    }

    private bool Matches(T item)
    {
        var s = _search.Text.Trim();
        if (s.Length > 0 && !_searchText(item).Contains(s, StringComparison.OrdinalIgnoreCase)) return false;
        var f = _filter.SelectedItem as string;
        return f is null or "All" || Filters.FirstOrDefault(x => x.Name == f).Match?.Invoke(item) != false;
    }

    private void Refilter()
    {
        _grid.CommitEdit(DataGridEditingUnit.Row, true);
        _view.Refresh();
        _count.Text = $"{_view.Cast<object>().Count()} of {_items.Count}";
    }

    public override async Task RefreshAsync()
    {
        var list = _projectScoped ? await Desk.Db.ListForProjectAsync<T>(Desk.ProjectId) : await Desk.Db.ListAsync<T>();
        _items.Clear();
        foreach (var i in Order(list)) _items.Add(i);
        Refilter();
    }

    private async Task SaveRowAsync(T item)
    {
        await Desk.Db.SaveAsync(item);
        if (AfterSave != null) await AfterSave(item);
        Desk.Say($"Saved {_title.TrimEnd('s').ToLowerInvariant()} · {DateTime.Now:HH:mm:ss}");
    }

    public async Task AddAsync()
    {
        var item = _create();
        if (_projectScoped) item.ProjectId = Desk.ProjectId;
        await Desk.Db.SaveAsync(item);
        if (AfterSave != null) await AfterSave(item);
        _items.Add(item);
        Refilter();
        _grid.SelectedItem = item;
        _grid.ScrollIntoView(item);
        _grid.CurrentCell = new DataGridCellInfo(item, _grid.Columns.FirstOrDefault(c => !c.IsReadOnly) ?? _grid.Columns[0]);
        _grid.Focus();
        _grid.BeginEdit();
    }

    private List<T> Selected() => _grid.SelectedItems.OfType<T>().ToList();

    private async Task DeleteAsync()
    {
        var sel = Selected();
        if (sel.Count == 0) { Dialogs.Info("Select one or more rows first (Ctrl/Shift + click)."); return; }
        if (!Dialogs.Confirm($"Delete {sel.Count} row(s)? They are removed on every synced device.")) return;
        foreach (var s in sel) s.IsDeleted = true;
        await Desk.Db.SaveAllAsync(sel);
        foreach (var s in sel) _items.Remove(s);
        Refilter();
    }

    private async Task BulkAsync()
    {
        var sel = Selected();
        if (sel.Count == 0) { Dialogs.Info("Select one or more rows first (Ctrl/Shift + click)."); return; }
        if (BulkActions.Length == 0) { Dialogs.Info("Use Delete, PDF or Excel for the selected rows."); return; }
        var i = Dialogs.Choose("Selected rows", $"{sel.Count} selected – choose an action:", BulkActions.Select(b => b.Name).ToList());
        if (i < 0) return;
        BulkActions[i].Apply(sel);
        await Desk.Db.SaveAllAsync(sel);
        _grid.CommitEdit(DataGridEditingUnit.Row, true);
        _grid.Items.Refresh();
    }

    /// <summary>Rows as shown: selected rows if several are selected, else everything visible, in the grid's current sort order.</summary>
    private List<T> ShownRows()
    {
        _grid.CommitEdit(DataGridEditingUnit.Row, true);
        var sel = Selected();
        return sel.Count > 1 ? _view.Cast<T>().Where(sel.Contains).ToList() : _view.Cast<T>().ToList();
    }

    public Task ExportEditAsync() => ExchangeUi.ExportAsync(Exchange!, ShownRows(), $"Exported {DateTime.Now:dd-MMM-yyyy HH:mm} · {Desk.Project?.Name}");

    public async Task ImportEditAsync(string? file)
    {
        if (await ExchangeUi.ImportAsync(Exchange!, file, _projectScoped)) await RefreshAsync();
    }

    private Task ImportEditAsync() => ImportEditAsync(null);

    private async Task ExportAsync(bool pdf)
    {
        var sel = Selected();
        var rows = sel.Count > 1 ? sel : _view.Cast<T>().ToList();
        var desc = sel.Count > 1 ? $"{sel.Count} selected" : (_filter.SelectedItem as string ?? "All") + (_search.Text.Trim() == "" ? "" : $" · “{_search.Text.Trim()}”");
        var f = pdf ? Pdf : Excel;
        if (f == null) { await GridExport.ExcelAsync(_title, _grid, rows.Cast<object>().ToList(), pdf); return; }
        await f(rows, desc);
    }
}

/// <summary>Exports any grid's visible columns (used where there is no dedicated report).</summary>
public static class GridExport
{
    public static Task ExcelAsync(string title, DataGrid grid, List<object> rows, bool pdf)
    {
        var cols = grid.Columns.Where(c => c.Visibility == Visibility.Visible).ToList();
        string Cell(object row, DataGridColumn c)
        {
            var path = c switch
            {
                DataGridBoundColumn b => (b.Binding as Binding)?.Path.Path,
                DataGridComboBoxColumn cb => (cb.SelectedValueBinding as Binding)?.Path.Path ?? (cb.SelectedItemBinding as Binding)?.Path.Path,
                _ => c.SortMemberPath,
            };
            if (string.IsNullOrEmpty(path)) return "";
            var v = row.GetType().GetProperty(path)?.GetValue(row);
            if (c is DataGridComboBoxColumn { ItemsSource: IEnumerable<Pick> picks }) return picks.FirstOrDefault(p => p.Id == v as string)?.Name ?? "";
            return v switch { DateTime d => d.ToString("dd-MMM-yyyy"), double x => x.ToString("#,0.###"), decimal m => m.ToString("#,0.00"), null => "", _ => v.ToString() ?? "" };
        }
        var headers = cols.Select(c => c.Header?.ToString() ?? "").ToArray();
        var data = rows.Select(r => cols.Select(c => Cell(r, c)).ToArray()).ToList();
        var name = $"{title.Replace(' ', '_')}_{DateTime.Now:yyyyMMdd_HHmm}";
        if (pdf)
            return Dialogs.CreateAndOpenAsync(name + ".pdf", s =>
                PdfDoc.Render(s, Desk.Project?.Name ?? title, title, true, null, d =>
                {
                    d.NewPage();
                    d.Text(title.ToUpperInvariant(), 14, bold: true, color: PdfDoc.Accent);
                    d.Table(headers.Select(h => new PdfColumn(h, Math.Max(0.6f, Math.Min(3f, h.Length / 6f)))).ToList(), data.Select(r => r.Cast<string?>().ToArray()));
                }));
        return Dialogs.CreateAndOpenAsync(name + ".xlsx", s =>
        {
            using var wb = new ClosedXML.Excel.XLWorkbook();
            var ws = wb.Worksheets.Add(title.Length > 30 ? title[..30] : title);
            for (var c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];
            for (var r = 0; r < data.Count; r++)
                for (var c = 0; c < headers.Length; c++) ws.Cell(r + 2, c + 1).Value = data[r][c];
            ws.Range(1, 1, 1, headers.Length).Style.Font.SetBold().Fill.SetBackgroundColor(ClosedXML.Excel.XLColor.FromHtml("#1F4E79")).Font.SetFontColor(ClosedXML.Excel.XLColor.White);
            ws.Range(1, 1, Math.Max(1, data.Count + 1), headers.Length).SetAutoFilter();
            ws.SheetView.FreezeRows(1);
            ws.Columns().AdjustToContents(1, Math.Min(300, data.Count + 1), 8, 50);
            wb.SaveAs(s);
        });
    }
}

// ------------------------------------------------------------------ concrete master lists

public static class MasterViews
{
    // Loaded on a worker thread: blocking the UI thread on an async call would deadlock WPF.
    public static List<Pick> Subs() => Task.Run(async () => (await Desk.Db.ListForProjectAsync<Subcontractor>(Desk.ProjectId))
        .OrderBy(s => s.Name).Select(s => new Pick(s.Id, s.Name)).Prepend(new Pick("", "(own / none)")).ToList()).GetAwaiter().GetResult();

    public static List<Pick> Depts() => Task.Run(async () => (await Desk.Db.ListForProjectAsync<Department>(Desk.ProjectId))
        .OrderBy(s => s.Name).Select(s => new Pick(s.Id, s.Name)).Prepend(new Pick("", "(none)")).ToList()).GetAwaiter().GetResult();

    public static GridView<Subcontractor> Subcontractors() => new("Subcontractors", () => new Subcontractor { Name = "New agency" },
        g => g.Text("Name", nameof(Subcontractor.Name), 220)
              .Suggest("Trade / scope", nameof(Subcontractor.Trade), ["Civil", "Formwork", "Reinforcement", "Masonry", "Plaster", "Waterproofing", "Electrical", "Plumbing", "HVAC", "Fire fighting", "Painting", "Flooring", "Fabrication", "Labour supply"], 160)
              .Text("Contact person", nameof(Subcontractor.ContactPerson), 160)
              .Text("Phone", nameof(Subcontractor.Phone), 130)
              .Check("Active", nameof(Subcontractor.IsActive)),
        s => $"{s.Name} {s.Trade} {s.ContactPerson} {s.Phone}")
    {
        Order = l => l.OrderBy(s => s.Name),
        Exchange = () => ExchangeUi.Sheets.SubcontractorsAsync(),
        Filters = [("Active", s => s.IsActive), ("Inactive", s => !s.IsActive)],
        BulkActions = [("Mark active", l => l.ForEach(s => s.IsActive = true)), ("Mark inactive", l => l.ForEach(s => s.IsActive = false))],
    };

    public static GridView<Department> Departments() => new("Departments", () => new Department { Name = "New department" },
        g => g.Suggest("Department", nameof(Department.Name), Lists.DefaultDepartments, 240).Text("Head / in-charge", nameof(Department.Head)),
        d => $"{d.Name} {d.Head}")
    {
        Order = l => l.OrderBy(d => d.Name),
        Exchange = () => ExchangeUi.Sheets.DepartmentsAsync(),
    };

    public static GridView<SiteResource> Resources() => new("Resources & rates", () => new SiteResource { Name = "New resource" },
        g => g.Enum<ResourceType>("Type", nameof(SiteResource.Type), 110)
              .Text("Code", nameof(SiteResource.Code), 80)
              .Suggest("Name", nameof(SiteResource.Name), Lists.Trades.Concat(["JCB", "Tower crane", "Concrete pump", "Transit mixer", "RMC M25", "RMC M30", "TMT steel", "Cement", "Scaffolding hire", "Testing charges"]), 200)
              .Suggest("Unit", nameof(SiteResource.Unit), Lists.Units, 70)
              .Number("Rate", nameof(SiteResource.Rate), 100, "#,0.00")
              .Enum<RateBasis>("Rate basis", nameof(SiteResource.Basis), 100)
              .Lookup("Supplied by (agency)", nameof(SiteResource.SubcontractorId), Subs())
              .Lookup("Department (own staff)", nameof(SiteResource.DepartmentId), Depts())
              .Text("Remarks", nameof(SiteResource.Remarks)),
        r => $"{r.Code} {r.Name} {r.Type} {r.Remarks}", hint: "Manpower & machines: rate per day or per hour. Materials: per unit. Non-material (hire, testing, transport): type Other, per unit or lump sum.")
    {
        Order = l => l.OrderBy(r => r.Type).ThenBy(r => r.Name),
        Exchange = () => ExchangeUi.Sheets.ResourcesAsync(Desk.ProjectId),
        Filters = Enum.GetValues<ResourceType>().Select(t => (t == ResourceType.Other ? "Non-material" : t.ToString(), (Func<SiteResource, bool>)(r => r.Type == t))).ToArray(),
    };
}

public static class RegisterViews
{
    public static GridView<Hindrance> Hindrances() => new("Hindrance register", () => new Hindrance { FromDate = DateTime.Today },
        g => g.Number("No.", nameof(Hindrance.SerialNo), 50, "0", readOnly: true)
              .Date("From", nameof(Hindrance.FromDate))
              .Date("Resolved on", nameof(Hindrance.ToDate))
              .Suggest("Category", nameof(Hindrance.Category), Lists.HindranceCategories, 150)
              .Text("Description", nameof(Hindrance.Description), 0, wrap: true)
              .Text("Affected activity", nameof(Hindrance.AffectedActivity), 150)
              .Suggest("Responsible", nameof(Hindrance.ResponsibleParty), Lists.ResponsibleParties, 130)
              .Number("Hrs lost", nameof(Hindrance.HoursLost), 70)
              .Enum<HindranceStatus>("Status", nameof(Hindrance.Status), 95)
              .Text("Action taken", nameof(Hindrance.ActionTaken), 200),
        h => $"{h.SerialNo} {h.Category} {h.Description} {h.AffectedActivity} {h.ResponsibleParty} {h.ActionTaken}",
        hint: "Type in the grid. Select a hindrance and click “Photos & videos…” (or double-click it) to attach evidence photos and videos.")
    {
        Order = l => l.OrderByDescending(h => h.FromDate).ThenByDescending(h => h.SerialNo),
        Exchange = () => ExchangeUi.Sheets.HindrancesAsync(),
        AfterSave = async h => { if (h.SerialNo == 0) { h.SerialNo = await Desk.Db.NextSerialAsync<Hindrance>(h.ProjectId); await Desk.Db.SaveAsync(h); } },
        Filters = [("Open", h => h.Status == HindranceStatus.Open), ("Resolved", h => h.Status == HindranceStatus.Resolved),
                   .. Lists.ResponsibleParties.Select(p => (p, (Func<Hindrance, bool>)(h => h.ResponsibleParty == p)))],
        BulkActions = [("Mark resolved today", l => l.ForEach(h => { h.Status = HindranceStatus.Resolved; h.ToDate ??= DateTime.Today; })),
                       ("Mark open", l => l.ForEach(h => { h.Status = HindranceStatus.Open; h.ToDate = null; }))],
        OpenDetail = h => { MediaPanel.ShowWindow($"H-{h.SerialNo:000} {h.Category}", MediaOwner.Hindrance, h.Id); return Task.CompletedTask; },
        RowActions = [(Glyph.Photo, "Photos & videos…", l => { MediaPanel.ShowWindow($"H-{l[0].SerialNo:000} {l[0].Category}", MediaOwner.Hindrance, l[0].Id); return Task.CompletedTask; })],
        Pdf = async (rows, desc) => { var o = await Desk.ReportOptionsAsync(); await Dialogs.CreateAndOpenAsync($"Hindrance_register_{DateTime.Now:yyyyMMdd_HHmm}.pdf", s => PdfReports.HindranceRegister(s, Desk.Project, rows, desc, o)); },
        Excel = (rows, desc) => Dialogs.CreateAndOpenAsync($"Hindrance_register_{DateTime.Now:yyyyMMdd_HHmm}.xlsx", s => ExcelReports.HindranceRegister(s, Desk.Project, rows, desc)),
    };

    public static GridView<Requirement> Requirements() => new("Requirement register", () => new Requirement { RaisedDate = DateTime.Today, RaisedBy = Desk.Db.UserName },
        g => g.Number("No.", nameof(Requirement.SerialNo), 50, "0", readOnly: true)
              .Date("Raised", nameof(Requirement.RaisedDate))
              .Suggest("Category", nameof(Requirement.Category), Lists.RequirementCategories, 130)
              .Text("Item / description", nameof(Requirement.Item), 0, wrap: true)
              .Number("Qty", nameof(Requirement.Quantity), 70)
              .Suggest("Unit", nameof(Requirement.Unit), Lists.Units, 65)
              .Date("Required by", nameof(Requirement.RequiredBy))
              .Enum<Priority>("Priority", nameof(Requirement.Priority), 85)
              .Text("Raised by", nameof(Requirement.RaisedBy), 110)
              .Enum<RequirementStatus>("Status", nameof(Requirement.Status), 100)
              .Text("Remarks", nameof(Requirement.Remarks), 180),
        r => $"{r.SerialNo} {r.Category} {r.Item} {r.RaisedBy} {r.Remarks}")
    {
        Order = l => l.OrderBy(r => r.RequiredBy ?? DateTime.MaxValue),
        Exchange = () => ExchangeUi.Sheets.RequirementsAsync(),
        AfterSave = async r => { if (r.SerialNo == 0) { r.SerialNo = await Desk.Db.NextSerialAsync<Requirement>(r.ProjectId); await Desk.Db.SaveAsync(r); } },
        Filters = [("Open (not received)", r => r.Status is RequirementStatus.Pending or RequirementStatus.Approved or RequirementStatus.Ordered), ("Overdue", r => r.IsOverdue),
                   .. Enum.GetValues<RequirementStatus>().Select(st => (st.ToString(), (Func<Requirement, bool>)(r => r.Status == st)))],
        BulkActions = Enum.GetValues<RequirementStatus>().Select(st => ($"Mark {st}", (Action<List<Requirement>>)(l => l.ForEach(r => r.Status = st)))).ToArray(),
        OpenDetail = r => { MediaPanel.ShowWindow($"R-{r.SerialNo:000} {r.Item}", MediaOwner.Requirement, r.Id); return Task.CompletedTask; },
        RowActions = [(Glyph.Photo, "Photos & attachments…", l => { MediaPanel.ShowWindow($"R-{l[0].SerialNo:000} {l[0].Item}", MediaOwner.Requirement, l[0].Id); return Task.CompletedTask; })],
        Pdf = async (rows, desc) => { var o = await Desk.ReportOptionsAsync(); await Dialogs.CreateAndOpenAsync($"Requirement_register_{DateTime.Now:yyyyMMdd_HHmm}.pdf", s => PdfReports.RequirementRegister(s, Desk.Project, rows, desc, o)); },
        Excel = (rows, desc) => Dialogs.CreateAndOpenAsync($"Requirement_register_{DateTime.Now:yyyyMMdd_HHmm}.xlsx", s => ExcelReports.RequirementRegister(s, Desk.Project, rows, desc)),
    };
}
