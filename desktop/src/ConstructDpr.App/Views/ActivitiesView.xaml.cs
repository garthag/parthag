using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using ConstructDpr.Core.Model;
using ConstructDpr.Core.Scheduling;
using ConstructDpr.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace ConstructDpr.App.Views;

public sealed record ActivityChoice(int Id, string Display);

public partial class ActivitiesView : UserControl, IRefreshable
{
    private ObservableCollection<Activity> _acts = new();
    private ObservableCollection<Dependency> _links = new();

    public ActivitiesView()
    {
        InitializeComponent();
        TypeColumn.ItemsSource = Enum.GetValues<DependencyType>();
    }

    public void Refresh()
    {
        _acts = new ObservableCollection<Activity>(AppState.Activities());
        Grid.ItemsSource = _acts;
        PredColumn.ItemsSource = _acts.Select(a => new ActivityChoice(a.Id, $"{a.Code}  {a.Name}")).ToList();
        var value = _acts.Sum(a => a.TotalQty * a.Rate);
        Summary.Text = $"{_acts.Count} activities · BOQ value ₹ {value.ToString("#,##0", System.Globalization.CultureInfo.GetCultureInfo("en-IN"))} · " +
                       $"{_acts.Count(a => a.Critical)} critical · double-click a cell to edit, then Save changes";
        if (_acts.Count > 0 && Grid.SelectedItem is null) Grid.SelectedIndex = 0;
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

    private void LoadLinks()
    {
        var a = Selected;
        LinksTitle.Text = a is null ? "Predecessors" : $"Predecessors of {a.Code} — {a.Name}";
        _links = a is null ? new() : new ObservableCollection<Dependency>(AppState.Db.Dependencies.Where(d => d.SuccessorId == a.Id).ToList());
        Links.ItemsSource = _links;
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        var next = _acts.Select(a => a.Code).Where(c => c.StartsWith('A') && int.TryParse(c[1..], out _)).Select(c => int.Parse(c[1..])).DefaultIfEmpty(1000).Max() + 10;
        var a = new Activity
        {
            ProjectId = AppState.Project.Id, Code = $"A{next}", Name = "New activity", Duration = 5, Unit = "nos",
            SortOrder = _acts.Select(x => x.SortOrder).DefaultIfEmpty().Max() + 10,
        };
        AppState.Db.Activities.Add(a);
        _acts.Add(a);
        Grid.SelectedItem = a;
        Grid.ScrollIntoView(a);
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } a) return;
        if (AppState.Db.DprActivityLines.Any(l => l.ActivityId == a.Id))
        {
            Dialogs.Error($"{a.Code} is used in daily reports and cannot be deleted (its progress history must be kept).");
            return;
        }
        if (!Dialogs.Confirm($"Delete {a.Code} {a.Name} and its relationships?")) return;
        AppState.Db.Dependencies.RemoveRange(AppState.Db.Dependencies.Where(d => d.PredecessorId == a.Id || d.SuccessorId == a.Id));
        AppState.Db.Activities.Remove(a);
        Save();
        Refresh();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (Save()) { Refresh(); Dialogs.Info("Saved."); }
    }

    private bool Save()
    {
        Grid.CommitEdit(DataGridEditingUnit.Row, true);
        Links.CommitEdit(DataGridEditingUnit.Row, true);
        var problems = new List<string>();
        foreach (var dup in _acts.GroupBy(a => a.Code.Trim(), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)) problems.Add($"Code {dup.Key} is used more than once.");
        foreach (var a in _acts)
        {
            if (string.IsNullOrWhiteSpace(a.Code) || string.IsNullOrWhiteSpace(a.Name)) problems.Add("Every activity needs a code and a name.");
            if (a.Duration < 0 || a.TotalQty < 0 || a.Rate < 0) problems.Add($"{a.Code}: duration, quantity and rate cannot be negative.");
            if (a.TotalQty > 0 && a.AcceptedQty > a.TotalQty) problems.Add($"{a.Code}: BOQ quantity is below the {a.AcceptedQty} already approved.");
        }
        if (problems.Count > 0) { Dialogs.Error(string.Join("\n", problems.Distinct())); return false; }
        try
        {
            AppState.Db.SaveChanges();
            AppState.NotifyChanged();
            return true;
        }
        catch (DbUpdateException ex)
        {
            Dialogs.Error("Could not save: " + ex.GetBaseException().Message);
            return false;
        }
    }

    private void OnAddLink(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } a) return;
        if (a.Id == 0 && !Save()) return;
        var pred = _acts.Where(x => x.Id != a.Id && !_links.Any(l => l.PredecessorId == x.Id)).OrderByDescending(x => x.SortOrder < a.SortOrder).ThenByDescending(x => x.SortOrder).FirstOrDefault();
        if (pred is null) return;
        var link = new Dependency { ProjectId = AppState.Project.Id, PredecessorId = pred.Id, SuccessorId = a.Id, Type = DependencyType.FS };
        AppState.Db.Dependencies.Add(link);
        _links.Add(link);
    }

    private void OnRemoveLink(object sender, RoutedEventArgs e)
    {
        if (Links.SelectedItem is not Dependency d) return;
        AppState.Db.Dependencies.Remove(d);
        _links.Remove(d);
    }

    private void OnSchedule(object sender, RoutedEventArgs e)
    {
        if (!Save()) return;
        try
        {
            var result = AppState.Schedule.Run(AppState.Project.Id, AppState.Today);
            AppState.NotifyChanged();
            Refresh();
            Dialogs.Info($"Schedule calculated (data date {AppState.Today:dd MMM yyyy}).\n\nForecast finish: {result.ProjectFinish:dd MMM yyyy}\nCritical activities: {_acts.Count(a => a.Critical)}");
        }
        catch (ScheduleException ex) { Dialogs.Error(ex.Message); }
    }

    private void OnBaseline(object sender, RoutedEventArgs e)
    {
        if (!Save()) return;
        var name = Dialogs.Prompt("Freeze baseline", "The current CPM dates become the planned dates and a new baseline revision. Earlier baselines are kept.\n\nBaseline name:",
            $"Revised baseline {AppState.Today:dd MMM yyyy}");
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            AppState.Schedule.Run(AppState.Project.Id, AppState.Today);
            AppState.Schedule.ApplyToPlan(AppState.Project.Id);
            var b = AppState.Schedule.FreezeBaseline(AppState.Project.Id, name);
            AppState.NotifyChanged();
            Refresh();
            Dialogs.Info($"Baseline revision {b.Revision} \"{b.Name}\" frozen.");
        }
        catch (Exception ex) when (ex is ScheduleException or WorkflowException) { Dialogs.Error(ex.Message); }
    }
}
