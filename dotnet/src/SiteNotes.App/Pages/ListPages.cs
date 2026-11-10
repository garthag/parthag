using SiteNotes.App.Services;
using SiteNotes.App.UI;
using SiteNotes.Core;
using SiteNotes.Core.Models;
using SiteNotes.Core.Reports;

namespace SiteNotes.App.Pages;

/// <summary>A row shown in a searchable list.</summary>
public record ListRow(object Item, string Title, string Subtitle, string Chip = "", Color? ChipColor = null, string Detail = "");

/// <summary>
/// Generic searchable, sortable, filterable list with Add / PDF / Excel in the toolbar.
/// Used for projects, subcontractors, departments, activities, hindrances and requirements.
/// </summary>
public abstract class ListPage<T> : ContentPage where T : SyncEntity, new()
{
    private readonly CollectionView _list = new() { SelectionMode = SelectionMode.Single, EmptyView = Ui.Caption("Nothing here yet. Tap ＋ Add.") };
    private readonly Entry _search = new() { Placeholder = "🔍 Search", ClearButtonVisibility = ClearButtonVisibility.WhileEditing };
    private readonly Picker _sort = new() { Title = "Sort" };
    private readonly Picker _filter = new() { Title = "Filter" };
    private readonly Label _count = Ui.Caption("");
    protected List<T> All = [];

    protected virtual bool ProjectScoped => true;
    protected virtual string[] SortOptions => ["Name"];
    protected virtual string[] FilterOptions => [];
    protected abstract ListRow ToRow(T item);
    protected abstract Page EditPage(T item);
    protected abstract T Create();
    protected virtual IEnumerable<T> Sort(IEnumerable<T> items, string sort) => items.OrderBy(i => ToRow(i).Title);
    protected virtual bool Filter(T item, string filter) => true;
    protected virtual Task ExportPdfAsync(List<T> items, string filterText) => Task.CompletedTask;
    protected virtual Task ExportExcelAsync(List<T> items, string filterText) => Task.CompletedTask;
    protected virtual bool CanExport => false;
    protected string SortChoice => _sort.SelectedItem as string ?? SortOptions[0];
    protected string FilterChoice => _filter.SelectedItem as string ?? "All";

    protected ListPage(string title)
    {
        Title = title;
        this.Page();
        _list.ItemTemplate = new DataTemplate(() =>
        {
            var title = new Label { FontSize = 15, FontAttributes = FontAttributes.Bold, LineBreakMode = LineBreakMode.TailTruncation };
            title.SetBinding(Label.TextProperty, nameof(ListRow.Title));
            title.SetAppThemeColor(Label.TextColorProperty, Color.FromArgb("#111827"), Color.FromArgb("#E5E7EB"));
            var sub = new Label { FontSize = 12, TextColor = Ui.Muted, MaxLines = 2, LineBreakMode = LineBreakMode.TailTruncation };
            sub.SetBinding(Label.TextProperty, nameof(ListRow.Subtitle));
            var detail = new Label { FontSize = 12, TextColor = Ui.Primary };
            detail.SetBinding(Label.TextProperty, nameof(ListRow.Detail));
            var chip = new Label { FontSize = 11, FontAttributes = FontAttributes.Bold, VerticalOptions = LayoutOptions.Start };
            chip.SetBinding(Label.TextProperty, nameof(ListRow.Chip));
            chip.SetBinding(Label.TextColorProperty, nameof(ListRow.ChipColor));
            var g = new Grid { ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)], ColumnSpacing = 8 };
            g.Add(new VerticalStackLayout { Spacing = 2, Children = { title, sub, detail } }, 0);
            g.Add(chip, 1);
            return new ContentView { Padding = new Thickness(12, 4), Content = Ui.Card(g, 12) };
        });
        _list.SelectionChanged += async (_, e) =>
        {
            if (e.CurrentSelection.FirstOrDefault() is ListRow row)
            {
                _list.SelectedItem = null;
                await AppShell.Push(EditPage((T)row.Item));
            }
        };

        ToolbarItems.Add(new ToolbarItem("＋ Add", null, async () =>
        {
            if (ProjectScoped && !await AppShell.RequireProjectAsync()) return;
            await AppShell.Push(EditPage(Create()));
        }));

        _search.TextChanged += (_, _) => Render();
        _sort.ItemsSource = SortOptions;
        _sort.SelectedIndex = 0;
        _sort.SelectedIndexChanged += (_, _) => Render();
        _filter.ItemsSource = new[] { "All" }.Concat(FilterOptions).ToList();
        _filter.SelectedIndex = 0;
        _filter.SelectedIndexChanged += (_, _) => Render();
        _filter.IsVisible = FilterOptions.Length > 0;

        var bar = new Grid
        {
            Padding = new Thickness(12, 8, 12, 0), ColumnSpacing = 8,
            ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(new GridLength(130)), new ColumnDefinition(new GridLength(130))],
        };
        bar.Add(_search, 0);
        bar.Add(_sort, 1);
        bar.Add(_filter, 2);

        var grid = new Grid { RowDefinitions = [new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star)] };
        grid.Add(bar, 0, 0);
        _count.Margin = new Thickness(16, 2);
        grid.Add(_count, 0, 1);
        grid.Add(_list, 0, 2);
        Content = grid;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (CanExport && ToolbarItems.Count == 1)
        {
            ToolbarItems.Add(new ToolbarItem("PDF", null, () => _ = Ui.Guard(() => ExportPdfAsync(Visible().ToList(), DescribeFilter()))));
            ToolbarItems.Add(new ToolbarItem("Excel", null, () => _ = Ui.Guard(() => ExportExcelAsync(Visible().ToList(), DescribeFilter()))));
        }
        await ReloadAsync();
        AppServices.DataChanged += OnData;
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        AppServices.DataChanged -= OnData;
    }

    private async void OnData() => await ReloadAsync();

    protected async Task ReloadAsync()
    {
        All = ProjectScoped ? await AppServices.Db.ListForProjectAsync<T>(AppServices.ProjectId) : await AppServices.Db.ListAsync<T>();
        Render();
    }

    private string DescribeFilter() =>
        string.Join(" · ", new[] { FilterChoice == "All" ? "All" : FilterChoice, string.IsNullOrWhiteSpace(_search.Text) ? "" : $"“{_search.Text}”" }
            .Where(s => s != ""));

    private IEnumerable<T> Visible()
    {
        var s = _search.Text?.Trim() ?? "";
        var f = FilterChoice;
        var q = All.Where(i => f == "All" || Filter(i, f));
        if (s.Length > 0)
            q = q.Where(i =>
            {
                var r = ToRow(i);
                return r.Title.Contains(s, StringComparison.OrdinalIgnoreCase) || r.Subtitle.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                       r.Detail.Contains(s, StringComparison.OrdinalIgnoreCase);
            });
        return Sort(q, SortChoice);
    }

    private void Render()
    {
        var rows = Visible().Select(ToRow).ToList();
        _list.ItemsSource = rows;
        _count.Text = $"{rows.Count} of {All.Count}";
    }
}

// ------------------------------------------------------------------ concrete lists

public class ProjectListPage : ListPage<Project>
{
    public ProjectListPage() : base("Projects") { }
    protected override bool ProjectScoped => false;
    protected override Project Create() => new();
    protected override Page EditPage(Project p) => EntityForms.ProjectForm(p);
    protected override ListRow ToRow(Project p) => new(p, p.ToString(), $"{p.Client} · {p.Location}",
        p.Id == AppServices.ProjectId ? "CURRENT" : "", Ui.Success, $"Start {p.StartDate:dd-MMM-yyyy}");
}

public class SubcontractorListPage : ListPage<Subcontractor>
{
    public SubcontractorListPage() : base("Subcontractors") { }
    protected override string[] FilterOptions => ["Active", "Inactive"];
    protected override bool Filter(Subcontractor s, string f) => f == "Active" ? s.IsActive : !s.IsActive;
    protected override Subcontractor Create() => new() { ProjectId = AppServices.ProjectId };
    protected override Page EditPage(Subcontractor s) => EntityForms.SubcontractorForm(s);
    protected override ListRow ToRow(Subcontractor s) => new(s, s.Name, $"{s.Trade} · {s.ContactPerson} {s.Phone}",
        s.IsActive ? "" : "INACTIVE", Ui.Muted);
}

public class DepartmentListPage : ListPage<Department>
{
    public DepartmentListPage() : base("Departments")
    {
        ToolbarItems.Add(new ToolbarItem("Add standard", null, async () =>
        {
            if (!await AppShell.RequireProjectAsync()) return;
            var have = All.Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            await AppServices.Db.SaveAllAsync(Lists.DefaultDepartments.Where(n => !have.Contains(n))
                .Select(n => new Department { ProjectId = AppServices.ProjectId, Name = n }));
            await ReloadAsync();
        }));
    }
    protected override Department Create() => new() { ProjectId = AppServices.ProjectId };
    protected override Page EditPage(Department d) => EntityForms.DepartmentForm(d);
    protected override ListRow ToRow(Department d) => new(d, d.Name, string.IsNullOrWhiteSpace(d.Head) ? "" : $"Head: {d.Head}");
}

public class ActivityListPage : ListPage<Activity>
{
    public ActivityListPage() : base("Activities") { }
    protected override string[] SortOptions => ["Code", "Name", "Planned start"];
    protected override Activity Create() => new() { ProjectId = AppServices.ProjectId };
    protected override Page EditPage(Activity a) => EntityForms.ActivityForm(a);
    protected override IEnumerable<Activity> Sort(IEnumerable<Activity> items, string sort) => sort switch
    {
        "Name" => items.OrderBy(a => a.Name),
        "Planned start" => items.OrderBy(a => a.PlannedStart ?? DateTime.MaxValue),
        _ => items.OrderBy(a => a.Code).ThenBy(a => a.Name),
    };
    protected override ListRow ToRow(Activity a) => new(a, a.ToString(), $"{a.Location} · {PdfReports.N(a.PlannedQty)} {a.Unit}",
        "", null, a.PlannedStart == null ? "" : $"{a.PlannedStart:dd-MMM} → {a.PlannedFinish:dd-MMM}");
}

public class HindranceListPage : ListPage<Hindrance>
{
    public HindranceListPage() : base("Hindrance register") { }
    protected override string[] SortOptions => ["Newest", "Oldest", "Hours lost", "Serial no"];
    protected override string[] FilterOptions => ["Open", "Resolved", .. Lists.ResponsibleParties];
    protected override bool Filter(Hindrance h, string f) => f switch
    {
        "Open" => h.Status == HindranceStatus.Open,
        "Resolved" => h.Status == HindranceStatus.Resolved,
        _ => h.ResponsibleParty == f,
    };
    protected override IEnumerable<Hindrance> Sort(IEnumerable<Hindrance> items, string sort) => sort switch
    {
        "Oldest" => items.OrderBy(h => h.FromDate),
        "Hours lost" => items.OrderByDescending(h => h.HoursLost),
        "Serial no" => items.OrderBy(h => h.SerialNo),
        _ => items.OrderByDescending(h => h.FromDate).ThenByDescending(h => h.SerialNo),
    };
    protected override Hindrance Create() => new() { ProjectId = AppServices.ProjectId };
    protected override Page EditPage(Hindrance h) => EntityForms.HindranceForm(h);
    protected override ListRow ToRow(Hindrance h) => new(h, $"H-{h.SerialNo:000} · {h.Category}", h.Description,
        h.Status.ToString().ToUpperInvariant(), Ui.StatusColor(h.Status.ToString()),
        $"{h.FromDate:dd-MMM-yyyy} · {h.DaysOpen} day(s) · {PdfReports.N(h.HoursLost)} hrs lost · {h.ResponsibleParty}");
    protected override bool CanExport => true;
    protected override async Task ExportPdfAsync(List<Hindrance> items, string filterText)
    {
        var path = await FileExport.CreateAsync($"Hindrance_register_{DateTime.Now:yyyyMMdd_HHmm}.pdf",
            s => PdfReports.HindranceRegister(s, AppServices.CurrentProject, items, filterText));
        await FileExport.OfferAsync(this, path);
    }
    protected override async Task ExportExcelAsync(List<Hindrance> items, string filterText)
    {
        var path = await FileExport.CreateAsync($"Hindrance_register_{DateTime.Now:yyyyMMdd_HHmm}.xlsx",
            s => ExcelReports.HindranceRegister(s, AppServices.CurrentProject, items, filterText));
        await FileExport.OfferAsync(this, path);
    }
}

public class RequirementListPage : ListPage<Requirement>
{
    public RequirementListPage() : base("Requirement register") { }
    protected override string[] SortOptions => ["Required by", "Newest", "Priority", "Serial no"];
    protected override string[] FilterOptions => ["Open (not received)", "Overdue", .. Enum.GetNames<RequirementStatus>(), .. Lists.RequirementCategories];
    protected override bool Filter(Requirement r, string f) => f switch
    {
        "Open (not received)" => r.Status is RequirementStatus.Pending or RequirementStatus.Approved or RequirementStatus.Ordered,
        "Overdue" => r.IsOverdue,
        _ when Enum.TryParse<RequirementStatus>(f, out var st) => r.Status == st,
        _ => r.Category == f,
    };
    protected override IEnumerable<Requirement> Sort(IEnumerable<Requirement> items, string sort) => sort switch
    {
        "Newest" => items.OrderByDescending(r => r.RaisedDate),
        "Priority" => items.OrderByDescending(r => r.Priority).ThenBy(r => r.RequiredBy),
        "Serial no" => items.OrderBy(r => r.SerialNo),
        _ => items.OrderBy(r => r.RequiredBy ?? DateTime.MaxValue),
    };
    protected override Requirement Create() => new() { ProjectId = AppServices.ProjectId, RaisedBy = AppServices.Db.UserName };
    protected override Page EditPage(Requirement r) => EntityForms.RequirementForm(r);
    protected override ListRow ToRow(Requirement r) => new(r, $"R-{r.SerialNo:000} · {r.Item}",
        $"{r.Category} · {PdfReports.N(r.Quantity)} {r.Unit} · {r.Priority} priority",
        r.IsOverdue ? "OVERDUE" : r.Status.ToString().ToUpperInvariant(), r.IsOverdue ? Ui.Danger : Ui.StatusColor(r.Status.ToString()),
        $"Raised {r.RaisedDate:dd-MMM} · needed by {(r.RequiredBy is { } d ? d.ToString("dd-MMM-yyyy") : "-")}");
    protected override bool CanExport => true;
    protected override async Task ExportPdfAsync(List<Requirement> items, string filterText)
    {
        var path = await FileExport.CreateAsync($"Requirement_register_{DateTime.Now:yyyyMMdd_HHmm}.pdf",
            s => PdfReports.RequirementRegister(s, AppServices.CurrentProject, items, filterText));
        await FileExport.OfferAsync(this, path);
    }
    protected override async Task ExportExcelAsync(List<Requirement> items, string filterText)
    {
        var path = await FileExport.CreateAsync($"Requirement_register_{DateTime.Now:yyyyMMdd_HHmm}.xlsx",
            s => ExcelReports.RequirementRegister(s, AppServices.CurrentProject, items, filterText));
        await FileExport.OfferAsync(this, path);
    }
}

public class ResourceListPage : ListPage<Resource>
{
    public ResourceListPage() : base("Resources & rates") { }
    protected override string[] SortOptions => ["Type", "Name", "Rate"];
    protected override string[] FilterOptions => Enum.GetNames<ResourceType>();
    protected override bool Filter(Resource r, string f) => r.Type.ToString() == f;
    protected override IEnumerable<Resource> Sort(IEnumerable<Resource> items, string sort) => sort switch
    {
        "Name" => items.OrderBy(r => r.Name),
        "Rate" => items.OrderByDescending(r => r.Rate),
        _ => items.OrderBy(r => r.Type).ThenBy(r => r.Name),
    };
    protected override Resource Create() => new() { ProjectId = AppServices.ProjectId };
    protected override Page EditPage(Resource r) => EntityForms.ResourceForm(r);
    protected override ListRow ToRow(Resource r) => new(r, r.ToString(), $"{RateText(r.Rate, r.Basis, r.Unit)}",
        r.Type == ResourceType.Other ? "NON-MATERIAL" : r.Type.ToString().ToUpperInvariant(), TypeColor(r.Type), r.Remarks);

    public static string RateText(decimal rate, RateBasis basis, string unit) => basis switch
    {
        RateBasis.PerHour => $"{PdfReports.M(rate)} per hour",
        RateBasis.PerDay => $"{PdfReports.M(rate)} per day",
        RateBasis.PerUnit => $"{PdfReports.M(rate)} per {unit}",
        _ => $"{PdfReports.M(rate)} lump sum",
    };

    public static Color TypeColor(ResourceType t) => t switch
    {
        ResourceType.Manpower => Ui.Primary,
        ResourceType.Machinery => Ui.Accent,
        ResourceType.Material => Ui.Success,
        _ => Ui.Muted,
    };
}

public class WbsListPage : ListPage<WbsNode>
{
    private Dictionary<string, WbsNode> _map = [];
    public WbsListPage() : base("WBS – plant / unit / area") { }
    protected override string[] SortOptions => ["Tree", "Code"];
    protected override string[] FilterOptions => Enum.GetNames<WbsLevel>();
    protected override bool Filter(WbsNode w, string f) => w.Level.ToString() == f;
    protected override IEnumerable<WbsNode> Sort(IEnumerable<WbsNode> items, string sort)
    {
        _map = All.ToDictionary(w => w.Id);
        return sort == "Code" ? items.OrderBy(w => w.Code) : items.OrderBy(w => ProgressService.WbsPath(w.Id, _map));
    }
    protected override WbsNode Create() => new() { ProjectId = AppServices.ProjectId };
    protected override Page EditPage(WbsNode w) => EntityForms.WbsForm(w, All);
    protected override ListRow ToRow(WbsNode w)
    {
        var depth = 0;
        var p = w.ParentId;
        while (p != "" && _map.TryGetValue(p, out var n) && depth < 10) { depth++; p = n.ParentId; }
        return new(w, new string(' ', depth * 4) + (depth > 0 ? "└ " : "") + w, w.Level.ToString(), "", null,
            depth > 0 ? ProgressService.WbsPath(w.ParentId, _map) : "");
    }
}
