using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using ConstructDpr.Shared.Api;
using ConstructDpr.Shared.Model;
using ConstructDpr.Shared.Security;

namespace ConstructDpr.App.Views;

public sealed record ActivityChoice(int Id, string Display);

/// <summary>Activities, BOQ quantities and predecessors. Edits are collected and sent in one save (validated by the API).</summary>
public partial class ActivitiesView : UserControl, IRefreshable
{
    private ObservableCollection<Activity> _acts = new();
    private List<Dependency> _deps = new();
    private ObservableCollection<Dependency> _links = new();
    private readonly HashSet<Activity> _changed = new();
    private readonly HashSet<int> _deleted = new(), _linksFor = new();

    public ActivitiesView()
    {
        InitializeComponent();
        TypeColumn.ItemsSource = Enum.GetValues<DependencyType>();
        Grid.CellEditEnding += (_, e) => { if (e.Row.Item is Activity a) _changed.Add(a); };
        Links.CellEditEnding += (_, _) => { if (Selected is { } a) _linksFor.Add(a.Id); };
    }

    private bool Dirty => _changed.Count + _deleted.Count + _linksFor.Count > 0;

    public async Task RefreshAsync()
    {
        if (Dirty) return; // never throw away the user's unsaved edits because of a live update
        if (AppState.Pid == 0) { Grid.ItemsSource = null; return; }
        var t1 = AppState.Api.ActivitiesAsync(AppState.Pid);
        var t2 = AppState.Api.DependenciesAsync(AppState.Pid);
        var keep = Selected?.Id;
        (_acts, _deps) = (new ObservableCollection<Activity>(await t1), await t2);
        Grid.ItemsSource = _acts;
        PredColumn.ItemsSource = _acts.Where(a => a.Id != 0).Select(a => new ActivityChoice(a.Id, $"{a.Code}  {a.Name}")).ToList();
        Grid.IsReadOnly = !AppState.Can(Module.Masters, Act.Edit);
        Links.IsReadOnly = !AppState.Can(Module.Schedule, Act.Edit);
        var value = _acts.Sum(a => a.TotalQty * a.Rate);
        Summary.Text = $"{_acts.Count} activities · BOQ value ₹ {value.ToString("#,##0", System.Globalization.CultureInfo.GetCultureInfo("en-IN"))} · " +
                       $"{_acts.Count(a => a.Critical)} critical · double-click a cell to edit, then Save changes";
        _linksOwner = 0;
        Grid.SelectedItem = _acts.FirstOrDefault(a => a.Id == keep) ?? _acts.FirstOrDefault();
        LoadLinks();
    }

    private Activity? Selected => Grid.SelectedItem as Activity;

    /// <summary>Selects an activity by code (used by the screenshot mode).</summary>
    public void Select(string code)
    {
        var a = _acts.FirstOrDefault(x => x.Code == code);
        if (a is not null) Grid.SelectedItem = a;
    }

    private void OnSelect(object sender, SelectionChangedEventArgs e) => LoadLinks();

    private int _linksOwner;

    /// <summary>Puts the predecessor rows being edited back into the project's list.</summary>
    private void KeepLinkEdits()
    {
        Links.CommitEdit(DataGridEditingUnit.Row, true);
        if (_linksOwner == 0 || !_linksFor.Contains(_linksOwner)) return;
        _deps.RemoveAll(d => d.SuccessorId == _linksOwner);
        _deps.AddRange(_links);
    }

    private void LoadLinks()
    {
        KeepLinkEdits();
        var a = Selected;
        _linksOwner = a?.Id ?? 0;
        LinksTitle.Text = a is null ? "Predecessors" : a.Id == 0 ? $"Predecessors of {a.Code} (save the new activity first)" : $"Predecessors of {a.Code} — {a.Name}";
        _links = a is null || a.Id == 0 ? new() : new ObservableCollection<Dependency>(_deps.Where(d => d.SuccessorId == a.Id));
        Links.ItemsSource = _links;
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        if (!AppState.Can(Module.Masters, Act.Create)) { Dialogs.Error("Your role cannot add activities."); return; }
        var next = _acts.Select(a => a.Code).Where(c => c.StartsWith('A') && int.TryParse(c[1..], out _)).Select(c => int.Parse(c[1..])).DefaultIfEmpty(1000).Max() + 10;
        var a = new Activity
        {
            ProjectId = AppState.Pid, Code = $"A{next}", Name = "New activity", Duration = 5, Unit = "nos", Weight = 1,
            SortOrder = _acts.Select(x => x.SortOrder).DefaultIfEmpty().Max() + 10,
        };
        _acts.Add(a);
        _changed.Add(a);
        Grid.SelectedItem = a;
        Grid.ScrollIntoView(a);
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } a) return;
        if (!Dialogs.Confirm($"Delete {a.Code} {a.Name} and its relationships? (Saved with Save changes.)")) return;
        _acts.Remove(a);
        _changed.Remove(a);
        if (a.Id != 0) _deleted.Add(a.Id);
        _deps.RemoveAll(d => d.PredecessorId == a.Id || d.SuccessorId == a.Id);
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (await SaveAsync()) Dialogs.Info("Saved. The schedule was recalculated.");
    }

    private async Task<bool> SaveAsync()
    {
        Grid.CommitEdit(DataGridEditingUnit.Row, true);
        Links.CommitEdit(DataGridEditingUnit.Row, true);
        if (!Dirty) return true;
        KeepLinkEdits();
        var changes = new ActivityChanges
        {
            Upserts = _changed.ToList(), Deletes = _deleted.ToList(), LinksFor = _linksFor.ToList(),
            Links = _deps.Where(d => _linksFor.Contains(d.SuccessorId)).ToList(),
        };
        var ok = await Dialogs.RunAsync(() => AppState.Api.SaveActivitiesAsync(AppState.Pid, changes));
        if (ok)
        {
            _changed.Clear(); _deleted.Clear(); _linksFor.Clear();
            await RefreshAsync();
        }
        return ok;
    }

    private void OnAddLink(object sender, RoutedEventArgs e)
    {
        if (Selected is not { Id: > 0 } a) { Dialogs.Info("Save the new activity first, then add its predecessors."); return; }
        var pred = _acts.Where(x => x.Id != 0 && x.Id != a.Id && _links.All(l => l.PredecessorId != x.Id))
            .OrderByDescending(x => x.SortOrder < a.SortOrder).ThenByDescending(x => x.SortOrder).FirstOrDefault();
        if (pred is null) return;
        _links.Add(new Dependency { ProjectId = AppState.Pid, PredecessorId = pred.Id, SuccessorId = a.Id, Type = DependencyType.FS });
        _linksFor.Add(a.Id);
    }

    private void OnRemoveLink(object sender, RoutedEventArgs e)
    {
        if (Links.SelectedItem is not Dependency d || Selected is not { } a) return;
        _links.Remove(d);
        _linksFor.Add(a.Id);
    }

    private async void OnSchedule(object sender, RoutedEventArgs e)
    {
        if (!await SaveAsync()) return;
        ScheduleSummary? r = null;
        if (await Dialogs.RunAsync(async () => r = await AppState.Api.RunScheduleAsync(AppState.Pid)))
        {
            await RefreshAsync();
            Dialogs.Info($"Schedule calculated (data date {r!.DataDate:dd MMM yyyy}).\n\nForecast finish: {r.ProjectFinish:dd MMM yyyy}\nCritical activities: {r.CriticalCount}");
        }
    }

    private async void OnBaseline(object sender, RoutedEventArgs e)
    {
        if (!await SaveAsync()) return;
        var name = Dialogs.Prompt("Freeze baseline", "The current CPM dates become the planned dates and a new baseline revision. Earlier baselines are kept.\n\nBaseline name:",
            $"Revised baseline {AppState.Today:dd MMM yyyy}");
        if (string.IsNullOrWhiteSpace(name)) return;
        Baseline? b = null;
        if (await Dialogs.RunAsync(async () => b = await AppState.Api.FreezeBaselineAsync(AppState.Pid, name)))
        {
            await RefreshAsync();
            Dialogs.Info($"Baseline revision {b!.Revision} \"{b.Name}\" frozen.");
        }
    }
}
