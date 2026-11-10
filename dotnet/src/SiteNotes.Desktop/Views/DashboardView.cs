using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using SiteNotes.Core;
using SiteNotes.Core.Models;
using SiteNotes.Core.Reports;
using SiteNotes.Desktop.Services;
using SiteNotes.Desktop.UI;

namespace SiteNotes.Desktop.Views;

/// <summary>Home: key figures, 14-day manpower chart, what needs attention, recent reports.</summary>
public class DashboardView : ViewBase
{
    public override string Title => "Dashboard";

    public override async Task RefreshAsync()
    {
        if (Desk.Project == null)
        {
            Content = W.Scroll(W.Stack(
                W.Heading("Welcome to SiteNotes DPR"),
                W.Text("1. Create your project.   2. Add subcontractors, resources & rates, WBS and activities.   3. Fill the daily report each day: progress, man-days and cost add up by themselves."),
                new Border { Height = 12 },
                W.Wrap(
                    W.Button("Create project", () => MainWindow.Current.Open(new ProjectsView(), "new"), Glyph.Add, true),
                    W.Button("Load a sample project to explore", async () => { var p = await SampleData.CreateAsync(Desk.Db); await Desk.SetProjectAsync(p); await MainWindow.Current.FillProjectsAsync(); }, Glyph.Folder))));
            return;
        }
        var db = Desk.Db;
        var pid = Desk.ProjectId;
        var reports = (await db.ListForProjectAsync<DailyReport>(pid)).OrderByDescending(r => r.Date).ThenByDescending(r => r.ReportNo).ToList();
        var hind = await db.ListForProjectAsync<Hindrance>(pid);
        var reqs = await db.ListForProjectAsync<Requirement>(pid);
        var pp = await Task.Run(() => new ProgressService(db).ComputeAsync(pid));
        var cur = pp.Calendar.Currency;
        var month = reports.Where(r => r.Date >= new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1)).ToList();
        var latest = reports.FirstOrDefault();

        var quick = W.Wrap(
            W.Button("New DPR  (Ctrl+N)", DprFlow.NewAsync, Glyph.Add, true),
            W.Button("Today's DPR", DprFlow.TodayAsync, Glyph.Today),
            W.Button("Progress & cost", () => MainWindow.Current.Open(new ActivitiesView()), Glyph.Chart),
            W.Button("Report manager", () => MainWindow.Current.Open(new ReportManagerView()), Glyph.Print),
            W.Button("New note", () => MainWindow.Current.Open(new NotesView()), Glyph.Note),
            W.Button("Scan to PDF", () => MainWindow.Current.Open(new ScannerView(null)), Glyph.Scan));

        var stats = W.Wrap(
            W.Stat(latest == null ? "Manpower (no report yet)" : $"Manpower · {latest.Date:ddd dd-MMM}", latest?.TotalManpower.ToString() ?? "0"),
            W.Stat("Overall progress", $"{pp.OverallPercent:0.#} %", Theme.Success),
            W.Stat("Planned to date", $"{pp.PlannedPercent:0.#} %", Theme.Muted),
            W.Stat("Man-days this month", PdfReports.N(month.Sum(r => r.TotalManpower))),
            W.Stat($"Cost this month ({cur})", PdfReports.M(month.Sum(r => r.TotalCost)), Theme.Accent),
            W.Stat("Open hindrances", hind.Count(h => h.Status == HindranceStatus.Open).ToString(), Theme.Danger),
            W.Stat("Pending requirements", reqs.Count(r => r.Status is RequirementStatus.Pending or RequirementStatus.Approved or RequirementStatus.Ordered).ToString(), Theme.Accent),
            W.Stat("Activities delayed", pp.Activities.Count(a => a.State == WorkState.Delayed).ToString(), Theme.Danger));

        var attention = new StackPanel();
        foreach (var a in pp.Activities.Where(a => a.State == WorkState.Delayed).Take(6))
            attention.Children.Add(W.Text($"⏱  {a.Activity} – {a.Percent:0.#} % (plan {a.PlannedPercentToday:0.#} %), forecast {PdfReports.D(a.ForecastFinish)}", 13, false, Theme.Danger));
        foreach (var r in reqs.Where(r => r.IsOverdue).Take(6))
            attention.Children.Add(W.Text($"📦  R-{r.SerialNo:000} {r.Item} – needed by {r.RequiredBy:dd-MMM}", 13, false, Theme.Accent));
        foreach (var h in hind.Where(h => h.Status == HindranceStatus.Open).OrderBy(h => h.FromDate).Take(6))
            attention.Children.Add(W.Text($"⚠  H-{h.SerialNo:000} {h.Description} – open {h.DaysOpen} day(s)"));
        if (attention.Children.Count == 0) attention.Children.Add(W.Muted("Nothing overdue. 👍"));

        var recent = new StackPanel();
        foreach (var r in reports.Take(8))
        {
            var line = new TextBlock { Text = $"{Desk.Project.ReportPrefix}{r.ReportNo:000}  ·  {r.Date:ddd dd-MMM}  ·  {r.Status}  ·  {r.TotalManpower} manpower  ·  {cur} {PdfReports.M(r.TotalCost)}", Cursor = System.Windows.Input.Cursors.Hand, Foreground = Theme.Primary, Margin = new Thickness(0, 2, 0, 2) };
            var id = r.Id;
            line.MouseLeftButtonUp += (_, _) => MainWindow.Current.Open(new DprEditorView(id));
            recent.Children.Add(line);
        }
        if (reports.Count == 0) recent.Children.Add(W.Muted("No reports yet. Click New DPR."));

        var two = new Grid();
        two.ColumnDefinitions.Add(new ColumnDefinition());
        two.ColumnDefinitions.Add(new ColumnDefinition());
        var left = W.Card(W.Stack(W.Text("Manpower – last 14 days", 14, true, Theme.Primary), Chart(reports)));
        var right = W.Card(W.Stack(W.Text("Needs attention", 14, true, Theme.Primary), attention, new Border { Height = 12 }, W.Text("Recent reports", 14, true, Theme.Primary), recent));
        Grid.SetColumn(right, 1);
        two.Children.Add(left);
        two.Children.Add(right);

        Content = W.Scroll(W.Stack(W.Heading(Desk.Project.ToString()), quick, new Border { Height = 10 }, stats, two));
    }

    private static UIElement Chart(List<DailyReport> reports)
    {
        var days = Enumerable.Range(0, 14).Select(i => DateTime.Today.AddDays(i - 13)).ToList();
        var values = days.Select(d => reports.Where(r => r.Date.Date == d).Sum(r => r.TotalManpower)).ToList();
        var max = Math.Max(1, values.Max());
        var g = new Grid { Height = 190, Margin = new Thickness(0, 10, 0, 0) };
        g.RowDefinitions.Add(new RowDefinition());
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var i = 0; i < days.Count; i++)
        {
            g.ColumnDefinitions.Add(new ColumnDefinition());
            var col = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(3, 0, 3, 0) };
            if (values[i] > 0) col.Children.Add(new TextBlock { Text = values[i].ToString(), FontSize = 10, Foreground = Theme.Muted, HorizontalAlignment = HorizontalAlignment.Center });
            col.Children.Add(new Rectangle { Height = Math.Max(values[i] == 0 ? 1 : 3, 140.0 * values[i] / max), Fill = days[i] == DateTime.Today ? Theme.Accent : Theme.Primary, RadiusX = 2, RadiusY = 2, ToolTip = $"{days[i]:ddd dd-MMM}: {values[i]}" });
            Grid.SetColumn(col, i);
            g.Children.Add(col);
            var label = new TextBlock { Text = days[i].ToString("dd"), FontSize = 10, Foreground = Theme.Muted, HorizontalAlignment = HorizontalAlignment.Center };
            Grid.SetColumn(label, i);
            Grid.SetRow(label, 1);
            g.Children.Add(label);
        }
        return g;
    }
}
