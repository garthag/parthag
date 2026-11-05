using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ConstructDpr.Core.Model;
using ConstructDpr.Core.Services;
using ConstructDpr.Reports;

namespace ConstructDpr.App.Views;

public sealed record DprItem(int Id, string Title, string Sub, string Status, Brush Brush);

public partial class DprView : UserControl, IRefreshable
{
    private Dpr? _dpr;
    private bool _loading;

    public DprView()
    {
        InitializeComponent();
        NewShift.ItemsSource = Enum.GetValues<Shift>();
        NewShift.SelectedItem = Shift.Day;
        NewDate.SelectedDate = DateTime.Today;
        NewDate.DisplayDateEnd = DateTime.Today;
    }

    public static Brush StatusBrush(DprStatus s) => new SolidColorBrush(s switch
    {
        DprStatus.Approved => Color.FromRgb(0x0F, 0x7B, 0x3E),
        DprStatus.Submitted => Color.FromRgb(0xC9, 0x85, 0x00),
        DprStatus.Rejected => Color.FromRgb(0xC6, 0x28, 0x28),
        _ => Color.FromRgb(0x6B, 0x77, 0x82),
    });

    public void Refresh() => LoadList(_dpr?.Id);

    /// <summary>Opens a report by id (used by the screenshot mode and after creating a report).</summary>
    public void Open(int id) => LoadList(id);

    private void LoadList(int? select)
    {
        _loading = true;
        var items = AppState.Dprs.List(AppState.Project.Id).Select(d => new DprItem(d.Id,
            d.Number + (d.Revision > 0 ? $"  R{d.Revision}" : ""),
            $"{d.ReportDate:ddd dd MMM yyyy} · {d.Shift} · {d.PreparedBy}", d.Status.ToString().ToUpperInvariant(), StatusBrush(d.Status))).ToList();
        List.ItemsSource = items;
        _loading = false;
        var target = items.FirstOrDefault(i => i.Id == select) ?? items.FirstOrDefault();
        List.SelectedItem = target;
        if (target is null) Show(null);
    }

    private void OnPick(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || List.SelectedItem is not DprItem item) return;
        if (_dpr is { IsEditable: true } previous && previous.Id != item.Id) AppState.Dprs.Save(previous); // drafts autosave
        Show(AppState.Dprs.Load(item.Id));
    }

    private void Show(Dpr? d)
    {
        _dpr = d;
        Editor.IsEnabled = d is not null;
        if (d is null) { Title.Text = "No reports yet"; Meta.Text = "Create the first daily report on the left."; return; }
        AppState.Dprs.Recalculate(d, d.Status is DprStatus.Submitted or DprStatus.Approved);

        Title.Text = $"{d.Number}{(d.Revision > 0 ? $" · Revision {d.Revision}" : "")}";
        Meta.Text = $"{d.ReportDate:dddd, dd MMM yyyy} · {d.Shift} shift · prepared by {d.PreparedBy}" +
                    (d.ApprovedBy is null ? "" : $" · approved by {d.ApprovedBy} on {d.ApprovedAt?.ToLocalTime():dd MMM yyyy HH:mm}");
        Badge.Background = StatusBrush(d.Status);
        BadgeText.Text = d.Status.ToString().ToUpperInvariant();

        var (notice, tone) = d.Status switch
        {
            DprStatus.Approved when AppState.Dprs.VerifyIntegrity(d) => ($"✔ Approved and locked. Snapshot SHA-256 {d.SnapshotHash?[..16]}… verified. Corrections are made as a new revision.", "Good"),
            DprStatus.Approved => ("⚠ The stored content no longer matches its approval snapshot (SHA-256). The database file may have been altered outside the app.", "Bad"),
            DprStatus.Rejected => ("Rejected: " + d.ReviewComment + "  — correct and submit again.", "Bad"),
            DprStatus.Submitted => ("Waiting for review. The reviewer can adjust the Accepted quantity before approving; the reported quantity is kept.", "Warn"),
            _ when d.ReviewComment is not null => (d.ReviewComment, "Muted"),
            _ => ("", ""),
        };
        Notice.Visibility = notice.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        NoticeText.Text = notice;
        if (tone.Length > 0)
        {
            var c = ((SolidColorBrush)FindResource(tone)).Color;
            Notice.Background = new SolidColorBrush(Color.FromArgb(0x1C, c.R, c.G, c.B));
            NoticeText.Foreground = new SolidColorBrush(c);
        }

        var editable = d.IsEditable;
        var reviewing = d.Status == DprStatus.Submitted;
        SaveBtn.IsEnabled = SubmitBtn.IsEnabled = editable;
        ApproveBtn.IsEnabled = RejectBtn.IsEnabled = reviewing;
        ReviseBtn.IsEnabled = d.Status == DprStatus.Approved;
        AddLineBar.IsEnabled = AddCrewBtn.IsEnabled = AddEqBtn.IsEnabled = AddMatBtn.IsEnabled = editable;
        Lines.IsReadOnly = !(editable || reviewing);
        TodayCol.IsReadOnly = DelayCol.IsReadOnly = !editable;
        AcceptedCol.IsReadOnly = !reviewing;
        Crew.IsReadOnly = Equipment.IsReadOnly = Materials.IsReadOnly = !editable;
        Narrative.IsEnabled = editable;

        ActivityPick.ItemsSource = AppState.Activities().Where(a => a.Status != ActivityStatus.Completed && a.Status != ActivityStatus.Cancelled)
            .Select(a => new ActivityChoice(a.Id, $"{a.Code}  {a.Name}  ({a.Unit})")).ToList();
        Narrative.DataContext = d;
        Bind();
    }

    private void Bind()
    {
        if (_dpr is null) return;
        Lines.ItemsSource = null; Lines.ItemsSource = _dpr.Activities;
        Crew.ItemsSource = null; Crew.ItemsSource = _dpr.Manpower;
        Equipment.ItemsSource = null; Equipment.ItemsSource = _dpr.Equipment;
        Materials.ItemsSource = null; Materials.ItemsSource = _dpr.Materials;
        CrewTotal.Text = $"Total {_dpr.Manpower.Sum(m => m.Headcount)} workers · {_dpr.Manpower.Sum(m => m.ManHours):0} man-hours";
    }

    private void OnCellEdited(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit || sender is not DataGrid grid) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_dpr is null) return;
            grid.CommitEdit(DataGridEditingUnit.Row, true);
            AppState.Dprs.Recalculate(_dpr, _dpr.Status == DprStatus.Submitted);
            Bind();
        }, DispatcherPriority.Background);
    }

    private void Commit()
    {
        foreach (var g in new[] { Lines, Crew, Equipment, Materials }) g.CommitEdit(DataGridEditingUnit.Row, true);
    }

    private void OnNew(object sender, RoutedEventArgs e)
    {
        var date = DateOnly.FromDateTime(NewDate.SelectedDate ?? DateTime.Today);
        var svc = AppState.Dprs;
        var d = svc.Create(AppState.Project.Id, date, (Shift)NewShift.SelectedItem);
        // Start from the previous report's crews and equipment: on site these change little from day to day.
        var last = svc.List(AppState.Project.Id).Where(x => x.Id != d.Id && x.ReportDate <= date).OrderByDescending(x => x.ReportDate).ThenByDescending(x => x.Revision).FirstOrDefault();
        if (last is not null)
        {
            var prev = svc.Load(last.Id);
            d.Manpower.AddRange(prev.Manpower.Select(m => new ManpowerLine { Agency = m.Agency, Trade = m.Trade, Skilled = m.Skilled, SemiSkilled = m.SemiSkilled, Unskilled = m.Unskilled, Supervisors = m.Supervisors, Hours = m.Hours }));
            d.Equipment.AddRange(prev.Equipment.Select(q => new EquipmentLine { Code = q.Code, Type = q.Type }));
            d.Materials.AddRange(prev.Materials.Select(m => new MaterialLine { Code = m.Code, Description = m.Description, Unit = m.Unit, Opening = m.Closing }));
            svc.Save(d);
        }
        AppState.NotifyChanged();
        LoadList(d.Id);
    }

    private void OnAddLine(object sender, RoutedEventArgs e)
    {
        if (_dpr is null || ActivityPick.SelectedValue is not int id) return;
        if (_dpr.Activities.Any(l => l.ActivityId == id)) { Dialogs.Error("That activity is already in this report."); return; }
        _dpr.Activities.Add(new DprActivityLine { ActivityId = id, Activity = AppState.Db.Activities.Find(id) });
        AppState.Dprs.Recalculate(_dpr, false);
        Bind();
        Tabs.SelectedIndex = 0;
    }

    private void OnAddCrew(object sender, RoutedEventArgs e) { _dpr?.Manpower.Add(new ManpowerLine { Agency = "Agency", Trade = "Trade" }); Bind(); }
    private void OnAddEquipment(object sender, RoutedEventArgs e) { _dpr?.Equipment.Add(new EquipmentLine { Code = "EQ-", Type = "Type" }); Bind(); }
    private void OnAddMaterial(object sender, RoutedEventArgs e) { _dpr?.Materials.Add(new MaterialLine { Code = "MAT-", Description = "Description", Unit = "nos" }); Bind(); }

    private void Act(Action<DprService, Dpr> action, string? done = null)
    {
        if (_dpr is null) return;
        Commit();
        try
        {
            action(AppState.Dprs, _dpr);
            AppState.NotifyChanged();
            LoadList(_dpr.Id);
            if (done is not null) Dialogs.Info(done);
        }
        catch (WorkflowException ex) { Dialogs.Error(ex.Message); }
    }

    private void OnSave(object sender, RoutedEventArgs e) => Act((s, d) => s.Save(d), "Draft saved.");
    private void OnSubmit(object sender, RoutedEventArgs e) => Act((s, d) => { s.Save(d); s.Submit(d); }, "Submitted for review.");
    private void OnApprove(object sender, RoutedEventArgs e) => Act((s, d) => s.Approve(d), "Approved. The report is now locked and the activity progress is updated.");

    private void OnReject(object sender, RoutedEventArgs e)
    {
        var reason = Dialogs.Prompt("Reject report", "Reason for rejection (the preparer sees this):");
        if (reason is not null) Act((s, d) => s.Reject(d, reason));
    }

    private void OnRevise(object sender, RoutedEventArgs e)
    {
        if (_dpr is null) return;
        var reason = Dialogs.Prompt("Start revision", $"{_dpr.Number} is approved and locked. A new draft revision will be created; the approved revision stays unchanged.\n\nReason for the revision:");
        if (reason is null) return;
        try
        {
            var rev = AppState.Dprs.StartRevision(_dpr, reason);
            AppState.NotifyChanged();
            LoadList(rev.Id);
        }
        catch (WorkflowException ex) { Dialogs.Error(ex.Message); }
    }

    private void OnPdf(object sender, RoutedEventArgs e)
    {
        if (_dpr is null) return;
        Commit();
        Dialogs.SaveAndOpen(DprPdfReport.Generate(_dpr, AppState.Project), $"{_dpr.Number}-R{_dpr.Revision}.pdf", "PDF (*.pdf)|*.pdf");
    }

    private void OnExcel(object sender, RoutedEventArgs e)
    {
        if (_dpr is null) return;
        Commit();
        Dialogs.SaveAndOpen(ExcelService.ExportDpr(_dpr, AppState.Project), $"{_dpr.Number}-R{_dpr.Revision}.xlsx", "Excel workbook (*.xlsx)|*.xlsx");
    }
}
