using ConstructDpr.Mobile.Services;
using ConstructDpr.Shared.Api;
using ConstructDpr.Shared.Model;
using ConstructDpr.Shared.Security;

namespace ConstructDpr.Mobile.Pages;

/// <summary>Schedule for a phone screen: one row per activity with its bar on the project timeline (drawn in C#).</summary>
public sealed class SchedulePage : ProjectPage
{
    public SchedulePage() : base("Schedule") { }

    protected override bool Wants(LiveChange c) => c.Entity is "activities" or "dpr" or "all" or "project";

    protected override async Task LoadAsync()
    {
        var acts = (await AppState.Api.ActivitiesAsync(AppState.Pid))
            .Where(a => a.Status != ActivityStatus.Cancelled && (a.EarlyStart ?? a.PlannedStart) is not null)
            .OrderBy(a => a.EarlyStart ?? a.PlannedStart).ThenBy(a => a.SortOrder).ToList();
        var page = Ui.Scroll();
        var s = (VerticalStackLayout)page.Content;
        var finish = acts.Max(a => a.EarlyFinish);
        s.Children.Add(Ui.Small($"{acts.Count} activities · {acts.Count(a => a.Critical)} critical · forecast finish {Ui.D(finish)}"));
        if (AppState.Can(Module.Schedule, Act.Edit))
            s.Children.Add(Ui.Success("▶ Recalculate CPM", async (_, _) =>
            {
                if (await Ui.TryAsync(this, async () => await AppState.Api.RunScheduleAsync(AppState.Pid))) await ReloadAsync();
            }));
        if (AppState.Can(Module.Reports, Act.Export))
            s.Children.Add(Ui.Plain("Share schedule PDF (A3)", async (_, _) =>
                await Ui.TryAsync(this, async () => await AppState.ShareFileAsync(await AppState.Api.ReportAsync(AppState.Pid, ReportKind.SchedulePdf)))));
        if (acts.Count == 0) { s.Children.Add(Ui.Body("No scheduled activities yet.")); Content = page; return; }

        var from = acts.Min(a => new[] { a.EarlyStart ?? a.PlannedStart, a.BaselineStart }.Where(d => d is not null).Min()!.Value);
        var to = acts.Max(a => new[] { a.EarlyFinish ?? a.PlannedFinish, a.BaselineFinish }.Where(d => d is not null).Max()!.Value);
        foreach (var a in acts)
        {
            var bar = new GraphicsView { HeightRequest = 26, Drawable = new BarDrawable(a, from, to, AppState.Today) };
            s.Children.Add(Ui.Card(new VerticalStackLayout
            {
                Spacing = 4,
                Children =
                {
                    Ui.Row(new Label { Text = $"{a.Code}  {a.Name}", FontAttributes = FontAttributes.Bold, FontSize = 14, TextColor = a.Critical ? Ui.Bad : Ui.Text },
                        a.Critical ? Ui.Badge("CRITICAL", Ui.Crit) : Ui.Small($"float {a.TotalFloat} d")),
                    Ui.Small($"{Ui.D(a.EarlyStart ?? a.PlannedStart)} → {Ui.D(a.EarlyFinish ?? a.PlannedFinish)} · {Ui.N(a.ProgressPct, 0)}% done"),
                    bar,
                },
            }));
        }
        s.Children.Add(Ui.Small("Bar: blue = planned, red = critical path, dark fill = approved progress, grey line = baseline, orange line = today."));
        Content = page;
    }

    /// <summary>One activity's bar on the project timeline.</summary>
    private sealed class BarDrawable(Activity a, DateOnly from, DateOnly to, DateOnly today) : IDrawable
    {
        public void Draw(ICanvas canvas, RectF r)
        {
            var span = Math.Max(1, to.DayNumber - from.DayNumber + 1);
            float X(DateOnly d) => r.Left + r.Width * (d.DayNumber - from.DayNumber) / span;
            canvas.FillColor = Color.FromArgb("#EEF1F4");
            canvas.FillRoundedRectangle(r.Left, r.Top + 4, r.Width, 14, 4);
            if (a.BaselineStart is { } bs && a.BaselineFinish is { } bf)
            {
                canvas.FillColor = Color.FromArgb("#B5BEC7");
                canvas.FillRectangle(X(bs), r.Bottom - 5, Math.Max(2, X(bf.AddDays(1)) - X(bs)), 3);
            }
            var s = a.EarlyStart ?? a.PlannedStart;
            var f = a.EarlyFinish ?? a.PlannedFinish;
            if (s is { } start && f is { } fin)
            {
                var x = X(start);
                var w = Math.Max(4, X(fin.AddDays(1)) - x);
                canvas.FillColor = a.Critical ? Ui.Crit : Ui.BrandMid;
                canvas.FillRoundedRectangle(x, r.Top + 4, w, 14, 3);
                canvas.FillColor = a.Critical ? Color.FromArgb("#9B1C1C") : Ui.Brand;
                canvas.FillRoundedRectangle(x, r.Top + 4, Math.Max(0, w * (float)Math.Min(1, a.ProgressPct / 100m)), 14, 3);
            }
            if (today >= from && today <= to)
            {
                canvas.StrokeColor = Ui.Accent;
                canvas.StrokeSize = 2;
                canvas.DrawLine(X(today), r.Top, X(today), r.Bottom);
            }
        }
    }
}
