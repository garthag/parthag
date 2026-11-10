using ConstructDpr.Mobile.Services;
using ConstructDpr.Shared.Api;
using ConstructDpr.Shared.Calculations;
using ConstructDpr.Shared.Model;
using ConstructDpr.Shared.Security;

namespace ConstructDpr.Mobile.Pages;

/// <summary>
/// One daily report on the phone: today's quantities, crews, equipment, materials, narrative and site photos;
/// submit, approve (with accepted quantities), reject, revise and share the PDF. Rules are checked by the server.
/// </summary>
public sealed class DprPage : ProjectPage
{
    private int _id;
    private Dpr? _d;
    private List<Activity> _acts = new();
    private List<PhotoInfo> _photos = new();
    private bool _dirty, _remoteChange;
    private readonly Dictionary<int, (Label Cum, Label Bal, Label Pct)> _figures = new();

    public DprPage(int id) : base("Daily report") => _id = id;

    private string MyName => AppState.Me?.Email ?? "";
    private bool Editable => _d?.IsEditable == true && (AppState.Can(Module.Dpr, Act.Edit) ||
        (AppState.Can(Module.Dpr, Act.Create) && string.Equals(_d.PreparedBy, MyName, StringComparison.OrdinalIgnoreCase)));
    private bool Reviewing => _d?.Status == DprStatus.Submitted && AppState.Can(Module.Dpr, Act.Approve);

    protected override bool Wants(LiveChange c)
    {
        if (c.Entity == "all" || (c.Entity == "dpr" && c.Id == _id && c.By != MyName))
        {
            if (!_dirty) return true;
            _remoteChange = true; // never throw away what the user is typing; show a notice instead
            Build();
        }
        return false;
    }

    protected override async Task LoadAsync()
    {
        var t1 = AppState.Api.DprAsync(AppState.Pid, _id);
        var t2 = AppState.Api.ActivitiesAsync(AppState.Pid);
        var t3 = AppState.Api.PhotosAsync(AppState.Pid, _id);
        (_d, _acts, _photos) = (await t1, await t2, await t3);
        (_dirty, _remoteChange) = (false, false);
        Title = _d.Number + (_d.Revision > 0 ? $" R{_d.Revision}" : "");
        Build();
        if (_d.Status == DprStatus.Approved && !await AppState.Api.VerifyDprAsync(AppState.Pid, _id))
            await DisplayAlertAsync("Integrity warning", "The stored content of this approved report no longer matches its approval fingerprint (SHA-256).", "OK");
    }

    private void Touched()
    {
        _dirty = true;
        if (_d is null) return;
        foreach (var l in _d.Activities.Where(l => l.Activity is not null))
        {
            Progress.ApplyLine(l, l.Activity!, l.PrevCum, _d.ReportDate, _d.Status == DprStatus.Submitted);
            if (_figures.TryGetValue(l.ActivityId, out var f))
            {
                (f.Cum.Text, f.Bal.Text, f.Pct.Text) = (Ui.N(l.CumQty), Ui.N(l.Balance), Ui.N(l.ActualPct, 1));
                f.Cum.TextColor = l.Activity!.TotalQty > 0 && l.CumQty > l.Activity.TotalQty ? Ui.Bad : Ui.Text;
            }
        }
    }

    private void Build()
    {
        if (_d is null) return;
        var d = _d;
        var page = Ui.Scroll();
        var s = (VerticalStackLayout)page.Content;
        _figures.Clear();

        s.Children.Add(Ui.Row(Ui.H1(d.Number + (d.Revision > 0 ? $" · R{d.Revision}" : "")), Ui.Badge(d.Status.ToString().ToUpperInvariant(), Ui.StatusColor(d.Status))));
        s.Children.Add(Ui.Small($"{d.ReportDate:dddd, dd MMM yyyy} · {d.Shift} shift · prepared by {d.PreparedBy}" +
                                (d.ApprovedBy is null ? "" : $" · approved by {d.ApprovedBy}")));
        var (notice, color) = d.Status switch
        {
            DprStatus.Approved => ("✔ Approved and locked. Corrections are made as a new revision.", Ui.Good),
            DprStatus.Rejected => ("Rejected: " + d.ReviewComment + " — correct and submit again.", Ui.Bad),
            DprStatus.Submitted => (Reviewing ? "Waiting for your review. Adjust the Accepted quantity if needed, then approve or reject." : "Waiting for review.", Ui.Warn),
            _ => (d.ReviewComment ?? "", Ui.Muted),
        };
        if (_remoteChange) (notice, color) = ("Someone changed this report on another device. Your unsaved changes are still here; tap ↻ to load their version (your changes are then discarded).", Ui.Warn);
        if (notice.Length > 0) s.Children.Add(new Label { Text = notice, TextColor = color, FontSize = 14, Margin = new Thickness(0, 4) });

        var buttons = new FlexLayout { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap, JustifyContent = Microsoft.Maui.Layouts.FlexJustify.Start };
        void Add(Button b) { b.Margin = new Thickness(0, 0, 8, 8); buttons.Children.Add(b); }
        if (Editable)
        {
            Add(Ui.Plain("Save", async (_, _) => await Run(() => AppState.Api.SaveDprAsync(AppState.Pid, d), "Saved.")));
            if (AppState.Can(Module.Dpr, Act.Submit)) Add(Ui.Primary("Submit", async (_, _) => await Run(() => AppState.Api.SubmitDprAsync(AppState.Pid, d), "Submitted for review.")));
        }
        if (Reviewing)
            Add(Ui.Success("✔ Approve", async (_, _) => await Run(() => AppState.Api.ApproveDprAsync(AppState.Pid, _id,
                d.Activities.Where(l => l.QtyAccepted is not null).ToDictionary(l => l.ActivityId, l => l.QtyAccepted!.Value)), "Approved.")));
        if (d.Status == DprStatus.Submitted && AppState.Can(Module.Dpr, Act.Reject))
            Add(Ui.Danger("Reject", async (_, _) =>
            {
                var reason = await DisplayPromptAsync("Reject report", "Reason (the preparer sees this):");
                if (!string.IsNullOrWhiteSpace(reason)) await Run(() => AppState.Api.RejectDprAsync(AppState.Pid, _id, reason), "Rejected.");
            }));
        if (d.Status == DprStatus.Approved && AppState.Can(Module.Dpr, Act.Create | Act.Edit))
            Add(Ui.Plain("Revise", async (_, _) =>
            {
                var reason = await DisplayPromptAsync("Start revision", "The approved report stays unchanged; a new draft revision is made. Reason:");
                if (!string.IsNullOrWhiteSpace(reason)) await Run(() => AppState.Api.ReviseDprAsync(AppState.Pid, _id, reason), "Revision started.");
            }));
        if (AppState.Can(Module.Reports, Act.Export))
            Add(Ui.Plain("PDF", async (_, _) =>
            {
                if (_dirty) { await DisplayAlertAsync("CONSTRUCT DPR", "Save first, so the PDF shows your latest changes.", "OK"); return; }
                await Ui.TryAsync(this, async () => await AppState.ShareFileAsync(await AppState.Api.ReportAsync(AppState.Pid, ReportKind.DprPdf, _id)));
            }));
        Add(Ui.Plain("↻", async (_, _) => await ReloadAsync()));
        s.Children.Add(buttons);

        s.Children.Add(WorkSection(d));
        s.Children.Add(CrewSection(d));
        s.Children.Add(EquipmentSection(d));
        s.Children.Add(MaterialSection(d));
        s.Children.Add(PhotoSection());
        s.Children.Add(NarrativeSection(d));
        Content = page;
    }

    private async Task Run(Func<Task<Dpr>> work, string done)
    {
        Dpr? result = null;
        if (!await Ui.TryAsync(this, async () => result = await work())) return;
        _dirty = false;
        if (result!.Id != _id) { _id = result.Id; } // a revision opens in place
        await ReloadAsync();
        await DisplayAlertAsync("CONSTRUCT DPR", done, "OK");
    }

    // ───────────── sections ─────────────

    private View WorkSection(Dpr d)
    {
        var box = Ui.Stack(8);
        box.Children.Add(Ui.H2($"Work done ({d.Activities.Count})"));
        if (Editable)
        {
            var open = _acts.Where(a => a.Status is not (ActivityStatus.Completed or ActivityStatus.Cancelled) && d.Activities.All(l => l.ActivityId != a.Id)).ToList();
            var pick = new Picker { Title = "Add an activity…", ItemsSource = open.Select(a => $"{a.Code} — {a.Name} ({a.Unit})").ToList(), FontSize = 15 };
            pick.SelectedIndexChanged += async (_, _) =>
            {
                if (pick.SelectedIndex < 0) return;
                var a = open[pick.SelectedIndex];
                d.Activities.Add(new DprActivityLine { ActivityId = a.Id, Activity = a });
                await Ui.TryAsync(this, async () =>
                {
                    var calc = await AppState.Api.RecalculateDprAsync(AppState.Pid, d); // previous quantity from approved reports
                    foreach (var l in d.Activities)
                        if (calc.Activities.FirstOrDefault(c => c.ActivityId == l.ActivityId) is { } c) l.PrevCum = c.PrevCum;
                });
                Touched();
                Build();
            };
            box.Children.Add(pick);
        }
        foreach (var l in d.Activities)
        {
            var a = l.Activity!;
            var cum = Fig(Ui.N(l.CumQty));
            var bal = Fig(Ui.N(l.Balance));
            var pct = Fig(Ui.N(l.ActualPct, 1));
            _figures[l.ActivityId] = (cum, bal, pct);
            var figs = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Star), new(GridLength.Star) }, RowSpacing = 2 };
            figs.Add(Pair("BOQ", Fig($"{Ui.N(a.TotalQty)} {a.Unit}")), 0, 0);
            figs.Add(Pair("Previous", Fig(Ui.N(l.PrevCum))), 1, 0);
            figs.Add(Pair("Cumulative", cum), 2, 0);
            figs.Add(Pair("Balance", bal), 0, 1);
            figs.Add(Pair("Actual %", pct), 1, 1);
            figs.Add(Pair("Plan %", Fig(Ui.N(l.PlannedPct, 1))), 2, 1);
            figs.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            figs.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            var inputs = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Star) }, ColumnSpacing = 10 };
            if (Editable)
            {
                var today = Ui.Number(l.QtyToday);
                today.TextChanged += (_, e) => { if (Ui.ParseDec(e.NewTextValue) is { } v && v >= 0) { l.QtyToday = v; Touched(); } };
                inputs.Add(Ui.Labelled($"Today ({a.Unit})", today), 0, 0);
            }
            else inputs.Add(Pair($"Today ({a.Unit})", Fig(Ui.N(l.QtyToday))), 0, 0);
            if (Reviewing)
            {
                var acc = Ui.Number(l.QtyAccepted ?? l.QtyToday);
                acc.TextChanged += (_, e) => { if (Ui.ParseDec(e.NewTextValue) is { } v && v >= 0) { l.QtyAccepted = v; Touched(); } };
                inputs.Add(Ui.Labelled("Accepted", acc), 1, 0);
            }
            else if (d.Status != DprStatus.Draft) inputs.Add(Pair("Accepted", Fig(Ui.N(l.QtyAccepted))), 1, 0);

            var card = Ui.Stack(6);
            card.Children.Add(Ui.Row(new Label { Text = $"{a.Code}  {a.Name}", FontAttributes = FontAttributes.Bold, FontSize = 15, TextColor = Ui.Text },
                Editable ? Ui.Danger("✕", (_, _) => { d.Activities.Remove(l); Touched(); Build(); }) : new Label()));
            card.Children.Add(figs);
            card.Children.Add(inputs);
            if (Editable)
            {
                var delay = Ui.Field(l.DelayReason, "Delay / remarks (optional)");
                delay.TextChanged += (_, e) => { l.DelayReason = e.NewTextValue; Touched(); };
                card.Children.Add(delay);
            }
            else if (!string.IsNullOrEmpty(l.DelayReason)) card.Children.Add(Ui.Small("Delay: " + l.DelayReason));
            box.Children.Add(Ui.Card(card));
        }
        return box;
    }

    private View CrewSection(Dpr d)
    {
        var box = Ui.Stack(8);
        box.Children.Add(Ui.H2($"Manpower ({d.Manpower.Sum(m => m.Headcount)} workers)"));
        foreach (var m in d.Manpower)
        {
            var card = Ui.Stack(6);
            card.Children.Add(new Label { Text = $"{m.Agency} · {m.Trade}", FontAttributes = FontAttributes.Bold, TextColor = Ui.Text, FontSize = 15 });
            var g = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Star), new(GridLength.Star), new(GridLength.Star) }, ColumnSpacing = 6 };
            View Count(string label, int value, Action<int> set)
            {
                if (!Editable) return Pair(label, Fig(value.ToString()));
                var e = Ui.Field(value.ToString(), "0", Keyboard.Numeric);
                e.TextChanged += (_, a) => { if (int.TryParse(a.NewTextValue, out var v) && v >= 0) { set(v); _dirty = true; } };
                return Ui.Labelled(label, e);
            }
            g.Add(Count("Skilled", m.Skilled, v => m.Skilled = v), 0, 0);
            g.Add(Count("Semi", m.SemiSkilled, v => m.SemiSkilled = v), 1, 0);
            g.Add(Count("Unskilled", m.Unskilled, v => m.Unskilled = v), 2, 0);
            g.Add(Count("Supv.", m.Supervisors, v => m.Supervisors = v), 3, 0);
            card.Children.Add(g);
            box.Children.Add(Ui.Card(card));
        }
        if (Editable)
            box.Children.Add(Ui.Plain("＋ Crew", async (_, _) =>
            {
                var agency = await DisplayPromptAsync("New crew", "Agency / subcontractor:");
                if (string.IsNullOrWhiteSpace(agency)) return;
                var trade = await DisplayPromptAsync("New crew", "Trade (e.g. Civil, Structural, Rigging):") ?? "";
                d.Manpower.Add(new ManpowerLine { Agency = agency.Trim(), Trade = trade.Trim() });
                _dirty = true;
                Build();
            }));
        return box;
    }

    private View EquipmentSection(Dpr d)
    {
        var box = Ui.Stack(8);
        if (d.Equipment.Count == 0 && !Editable) return box;
        box.Children.Add(Ui.H2("Equipment (hours)"));
        foreach (var e in d.Equipment)
        {
            var card = Ui.Stack(6);
            card.Children.Add(new Label { Text = $"{e.Code} · {e.Type}", FontAttributes = FontAttributes.Bold, TextColor = Ui.Text, FontSize = 15 });
            var g = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Star), new(GridLength.Star) }, ColumnSpacing = 6 };
            View Hours(string label, decimal value, Action<decimal> set)
            {
                if (!Editable) return Pair(label, Fig(Ui.N(value, 1)));
                var x = Ui.Number(value);
                x.TextChanged += (_, a) => { if (Ui.ParseDec(a.NewTextValue) is { } v && v >= 0) { set(v); _dirty = true; } };
                return Ui.Labelled(label, x);
            }
            g.Add(Hours("Working", e.WorkingHours, v => e.WorkingHours = v), 0, 0);
            g.Add(Hours("Idle", e.IdleHours, v => e.IdleHours = v), 1, 0);
            g.Add(Hours("Breakdown", e.BreakdownHours, v => e.BreakdownHours = v), 2, 0);
            card.Children.Add(g);
            if (Editable)
            {
                var reason = Ui.Field(e.BreakdownReason, "Breakdown reason (needed with breakdown hours)");
                reason.TextChanged += (_, a) => { e.BreakdownReason = a.NewTextValue; _dirty = true; };
                card.Children.Add(reason);
            }
            box.Children.Add(Ui.Card(card));
        }
        return box;
    }

    private View MaterialSection(Dpr d)
    {
        var box = Ui.Stack(8);
        if (d.Materials.Count == 0) return box;
        box.Children.Add(Ui.H2("Materials"));
        foreach (var m in d.Materials)
        {
            var closing = Fig(Ui.N(m.Closing));
            var card = Ui.Stack(6);
            card.Children.Add(new Label { Text = $"{m.Code} · {m.Description} ({m.Unit})", FontAttributes = FontAttributes.Bold, TextColor = Ui.Text, FontSize = 15 });
            var g = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Star), new(GridLength.Star), new(GridLength.Star) }, ColumnSpacing = 6 };
            View Qty(string label, decimal value, Action<decimal> set)
            {
                if (!Editable) return Pair(label, Fig(Ui.N(value)));
                var x = Ui.Number(value);
                x.TextChanged += (_, a) => { if (Ui.ParseDec(a.NewTextValue) is { } v && v >= 0) { set(v); _dirty = true; closing.Text = Ui.N(m.Closing); closing.TextColor = m.Closing < 0 ? Ui.Bad : Ui.Text; } };
                return Ui.Labelled(label, x);
            }
            g.Add(Pair("Opening", Fig(Ui.N(m.Opening))), 0, 0);
            g.Add(Qty("Received", m.Received, v => m.Received = v), 1, 0);
            g.Add(Qty("Issued", m.Issued, v => m.Issued = v), 2, 0);
            g.Add(Pair("Closing", closing), 3, 0);
            card.Children.Add(g);
            box.Children.Add(Ui.Card(card));
        }
        return box;
    }

    private View PhotoSection()
    {
        var box = Ui.Stack(8);
        box.Children.Add(Ui.H2($"Site photos ({_photos.Count})"));
        if (Editable)
        {
            var row = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Star) }, ColumnSpacing = 8 };
            row.Add(Ui.Primary("📷 Take photo", async (_, _) => await AddPhotoAsync(camera: true)), 0, 0);
            row.Add(Ui.Plain("Choose photos", async (_, _) => await AddPhotoAsync(camera: false)), 1, 0);
            box.Children.Add(row);
        }
        var grid = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Star) }, ColumnSpacing = 8, RowSpacing = 8 };
        for (var i = 0; i < _photos.Count; i++)
        {
            var p = _photos[i];
            if (i % 2 == 0) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var image = new Image { HeightRequest = 130, Aspect = Aspect.AspectFill, BackgroundColor = Ui.Line };
            _ = LoadThumbAsync(image, p.Id);
            var cell = Ui.Stack(2);
            cell.Children.Add(image);
            cell.Children.Add(Ui.Small(p.Caption ?? p.FileName, Ui.Text));
            cell.Children.Add(Ui.Small($"{p.TakenBy} · {p.At.ToLocalTime():dd MMM HH:mm}"));
            if (Editable)
                cell.Children.Add(Ui.Danger("Remove", async (_, _) =>
                {
                    if (!await DisplayAlertAsync("Remove photo", $"Remove \"{p.Caption ?? p.FileName}\"?", "Remove", "Cancel")) return;
                    if (await Ui.TryAsync(this, () => AppState.Api.DeletePhotoAsync(AppState.Pid, p.Id)))
                    {
                        _photos = await AppState.Api.PhotosAsync(AppState.Pid, _id);
                        Build();
                    }
                }));
            grid.Add(cell, i % 2, i / 2);
        }
        box.Children.Add(grid);
        return box;
    }

    private static async Task LoadThumbAsync(Image image, int photoId)
    {
        try
        {
            var f = await AppState.Api.PhotoAsync(AppState.Pid, photoId);
            image.Source = ImageSource.FromStream(() => new MemoryStream(f.Content));
        }
        catch (Exception ex) when (ex is HttpRequestException or NotFoundException) { }
    }

    private async Task AddPhotoAsync(bool camera)
    {
        if (_d is null) return;
        if (_dirty && !await Ui.TryAsync(this, async () => { _d = await AppState.Api.SaveDprAsync(AppState.Pid, _d); _dirty = false; })) return;
        // The phone shrinks and rotates the picture before it is sent: quick over a weak site connection.
        var options = new MediaPickerOptions { Title = "Site photo", MaximumWidth = 1600, MaximumHeight = 1600, CompressionQuality = 85, RotateImage = true };
        List<FileResult> files;
        try
        {
            if (camera)
            {
                if (!MediaPicker.Default.IsCaptureSupported) { await DisplayAlertAsync("Camera", "This phone has no camera available.", "OK"); return; }
                var shot = await MediaPicker.Default.CapturePhotoAsync(options);
                files = shot is null ? new() : new() { shot };
            }
            else files = (await MediaPicker.Default.PickPhotosAsync(options)).Where(f => f is not null).ToList()!;
        }
        catch (PermissionException)
        {
            await DisplayAlertAsync("Permission needed", "Allow CONSTRUCT DPR to use the camera and photos in Android Settings → Apps.", "OK");
            return;
        }
        if (files.Count == 0) return;
        var caption = await DisplayPromptAsync("Caption", "What does the photo show? (optional)", "OK", "Skip");
        foreach (var f in files)
        {
            await Ui.TryAsync(this, async () =>
            {
                await using var stream = await f.OpenReadAsync();
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                await AppState.Api.AddPhotoAsync(AppState.Pid, _id, Path.ChangeExtension(f.FileName, ".jpg"), ms.ToArray(), string.IsNullOrWhiteSpace(caption) ? null : caption);
            });
        }
        _photos = await AppState.Api.PhotosAsync(AppState.Pid, _id);
        Build();
    }

    private View NarrativeSection(Dpr d)
    {
        var box = Ui.Stack(8);
        box.Children.Add(Ui.H2("Site & narrative"));
        View Text(string label, string? value, Action<string?> set, bool multi = false)
        {
            if (!Editable) return Pair(label, new Label { Text = string.IsNullOrWhiteSpace(value) ? "—" : value, TextColor = Ui.Text, FontSize = 15 });
            InputView input = multi
                ? new Editor { Text = value, AutoSize = EditorAutoSizeOption.TextChanges, MinimumHeightRequest = 80, FontSize = 16, BackgroundColor = Colors.White }
                : Ui.Field(value);
            input.TextChanged += (_, e) => { set(e.NewTextValue); _dirty = true; };
            return Ui.Labelled(label, input);
        }
        box.Children.Add(Text("Weather", d.Weather, v => d.Weather = v));
        box.Children.Add(Text("Site conditions", d.SiteConditions, v => d.SiteConditions = v));
        box.Children.Add(Text("Work completed today", d.WorkCompleted, v => d.WorkCompleted = v, true));
        box.Children.Add(Text("Delays and hindrances", d.DelayReasons, v => d.DelayReasons = v, true));
        box.Children.Add(Text("Plan for tomorrow", d.TomorrowPlan, v => d.TomorrowPlan = v, true));
        box.Children.Add(Text("Safety (toolbox talks, permits, incidents)", d.SafetyNotes, v => d.SafetyNotes = v, true));
        return Ui.Card(box);
    }

    private static Label Fig(string text) => new() { Text = text, FontAttributes = FontAttributes.Bold, FontSize = 15, TextColor = Ui.Text };
    private static View Pair(string label, View value) => new VerticalStackLayout { Children = { Ui.Small(label), value } };
}
