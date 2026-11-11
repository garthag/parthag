using SiteNotes.App.Services;
using SiteNotes.App.UI;
using SiteNotes.Core;
using SiteNotes.Core.Models;
using SiteNotes.Core.Reports;

namespace SiteNotes.App.Pages;

/// <summary>
/// Report manager: pick the report, the days, what goes in it and in which order, how rows are sorted,
/// and the page layout (logos, header lines, footer, page numbers, remarks). Choices are saved per project
/// and used by every PDF the app makes.
/// </summary>
public class ReportCenterPage : ContentPage
{
    private enum Kind { DayPdf, RegisterPdf, RegisterFullPdf, WorkbookXlsx, ProgressPdf, ProgressXlsx, ManpowerPdf, DrawingPdf, DrawingXlsx, HindrancePdf, HindranceXlsx, RequirementPdf, RequirementXlsx }

    private static readonly (Kind, string)[] Kinds =
    [
        (Kind.DayPdf, "📄 Daily report (PDF)"),
        (Kind.RegisterFullPdf, "📚 All daily reports in one PDF"),
        (Kind.RegisterPdf, "📋 DPR register summary (PDF)"),
        (Kind.WorkbookXlsx, "📊 DPR workbook (Excel)"),
        (Kind.ProgressPdf, "📈 Progress & cost (PDF)"),
        (Kind.ProgressXlsx, "📈 Progress & cost (Excel)"),
        (Kind.ManpowerPdf, "👷 Manpower summary (PDF)"),
        (Kind.DrawingPdf, "📐 Drawing register & work completion (PDF)"),
        (Kind.DrawingXlsx, "📐 Drawing register (Excel)"),
        (Kind.HindrancePdf, "⚠️ Hindrance register (PDF)"),
        (Kind.HindranceXlsx, "⚠️ Hindrance register (Excel)"),
        (Kind.RequirementPdf, "📦 Requirement register (PDF)"),
        (Kind.RequirementXlsx, "📦 Requirement register (Excel)"),
    ];

    private readonly FilterBar _filter = new(compact: false);
    private readonly ChipGroup<Kind> _kind = new(Kinds, [Kind.DayPdf]);
    private readonly Label _preview = Ui.Label("", 13, true, Ui.Primary);
    private readonly VerticalStackLayout _body = new() { Spacing = 8, Padding = new Thickness(12, 4, 12, 90) };
    private ReportProfile _profile = new();
    private ReportOptions _opt = new();
    private List<DailyReport> _matches = [];
    private bool _built;

    public ReportCenterPage()
    {
        Title = "Report manager";
        this.Page();
        ToolbarItems.Add(new ToolbarItem("💾 Save as default", null, () => _ = Ui.Guard(async () =>
        {
            await ReportProfiles.SaveAsync(_profile, _opt);
            await DisplayAlertAsync("Saved", "These report settings are now the default for this project (all PDFs use them).", "OK");
        })));
        ToolbarItems.Add(new ToolbarItem("Reset", null, () => _ = Ui.Guard(async () =>
        {
            if (!await DisplayAlertAsync("Reset settings?", "Back to the standard layout and all sections.", "Reset", "Cancel")) return;
            _opt = new ReportOptions();
            BuildOptions();
        })) { Order = ToolbarItemOrder.Secondary });

        _filter.Changed += async () => await Ui.Guard(PreviewAsync);
        _kind.Changed += () => { BuildOptions(); _ = PreviewAsync(); };

        var generate = Ui.Button("🖨  Generate", () => Ui.Guard(GenerateAsync));
        generate.FontSize = 16;
        var bottom = new Grid
        {
            ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)], Padding = new Thickness(12, 8), ColumnSpacing = 10,
            VerticalOptions = LayoutOptions.End,
        };
        bottom.SetAppThemeColor(BackgroundColorProperty, Colors.White, Ui.DarkSurface);
        bottom.Add(_preview, 0);
        bottom.Add(generate, 1);
        _preview.VerticalOptions = LayoutOptions.Center;

        var g = new Grid { RowDefinitions = [new(GridLength.Star), new(GridLength.Auto)] };
        g.Add(new ScrollView { Content = _body }, 0, 0);
        g.Add(bottom, 0, 1);
        Content = g;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (!_built)
        {
            _built = true;
            (_profile, _opt) = await ReportProfiles.LoadAsync();
            BuildOptions();
        }
        await _filter.LoadAsync();
        await PreviewAsync();
    }

    private Kind K => _kind.Value;
    private bool IsDpr => K is Kind.DayPdf or Kind.RegisterFullPdf;
    private bool IsPdf => K is not (Kind.WorkbookXlsx or Kind.ProgressXlsx or Kind.HindranceXlsx or Kind.RequirementXlsx);

    // ------------------------------------------------------------------ option panels

    private readonly VerticalStackLayout _dynamic = new() { Spacing = 8 };

    private void BuildOptions()
    {
        if (_body.Children.Count == 0)
        {
            _body.Children.Add(new Section("1. Report").With(_kind));
            _body.Children.Add(new Section("2. Days, filter & sort").With(_filter));
            _body.Children.Add(_dynamic);
        }
        _dynamic.Children.Clear();
        if (IsDpr) _dynamic.Children.Add(ContentsSection());
        if (IsPdf) _dynamic.Children.Add(LayoutSection());
        if (IsPdf) _dynamic.Children.Add(new Section("Remarks on the report", false).With(
            Ui.Field("Report title", Ui.Entry(_opt.ReportTitle, "DAILY PROGRESS REPORT", changed: v => _opt.ReportTitle = v)),
            Ui.Field("Extra remarks / note printed on the report", Ui.Memo(_opt.ExtraRemarks, "e.g. Issued for client review", v => _opt.ExtraRemarks = v, 60))));
    }

    /// <summary>Sections to include (tick) and their order (▲ ▼), plus row sorting and photo layout.</summary>
    private View ContentsSection()
    {
        var list = new VerticalStackLayout { Spacing = 2 };
        var all = _opt.Sections.Concat(Enum.GetValues<DprSection>().Where(s => !_opt.Sections.Contains(s))).ToList();
        var on = _opt.Sections.ToHashSet();

        void Render()
        {
            list.Children.Clear();
            for (var i = 0; i < all.Count; i++)
            {
                var s = all[i];
                var idx = i;
                var cb = new CheckBox { IsChecked = on.Contains(s), Color = Ui.Primary, VerticalOptions = LayoutOptions.Center };
                cb.CheckedChanged += (_, a) => { if (a.Value) on.Add(s); else on.Remove(s); Commit(); };
                var label = Ui.Label(ReportOptions.Label(s), 14);
                label.VerticalOptions = LayoutOptions.Center;
                var tap = new TapGestureRecognizer();
                tap.Tapped += (_, _) => cb.IsChecked = !cb.IsChecked;
                label.GestureRecognizers.Add(tap);
                var row = new Grid { ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto)], ColumnSpacing = 4 };
                row.Add(cb, 0);
                row.Add(label, 1);
                row.Add(Ui.Tool("▲", () => { if (idx > 0) { (all[idx - 1], all[idx]) = (all[idx], all[idx - 1]); Commit(); Render(); } }, "Move up", 34), 2);
                row.Add(Ui.Tool("▼", () => { if (idx < all.Count - 1) { (all[idx + 1], all[idx]) = (all[idx], all[idx + 1]); Commit(); Render(); } }, "Move down", 34), 3);
                list.Children.Add(row);
            }
        }
        void Commit() => _opt.Sections = all.Where(on.Contains).ToList();
        Render();

        var quick = new HorizontalStackLayout
        {
            Spacing = 8,
            Children =
            {
                Ui.Button("All", () => { on.Clear(); foreach (var s in all) on.Add(s); Commit(); Render(); }, false),
                Ui.Button("Short (no photos)", () => { on.Remove(DprSection.Photos); on.Remove(DprSection.ManpowerSummary); on.Remove(DprSection.Cost); Commit(); Render(); }, false),
                Ui.Button("Standard order", () => { all = [.. Enum.GetValues<DprSection>()]; Commit(); Render(); }, false),
            }
        };

        var actSort = new ChipGroup<ActivityRowSort>([
            (ActivityRowSort.AsEntered, "As entered"), (ActivityRowSort.Name, "Name"), (ActivityRowSort.ProgressHigh, "Most progress"),
            (ActivityRowSort.ProgressLow, "Least progress"), (ActivityRowSort.TodayQtyHigh, "Most done today"), (ActivityRowSort.Agency, "Agency"),
            (ActivityRowSort.Location, "Location"),
        ], [_opt.ActivitySort]);
        actSort.Changed += () => _opt.ActivitySort = actSort.Value;
        var mpSort = new ChipGroup<ManpowerRowSort>([
            (ManpowerRowSort.AsEntered, "As entered"), (ManpowerRowSort.Party, "Agency / department"), (ManpowerRowSort.Trade, "Trade"),
            (ManpowerRowSort.CountHigh, "Most people"), (ManpowerRowSort.Activity, "Activity"),
        ], [_opt.ManpowerSort]);
        mpSort.Changed += () => _opt.ManpowerSort = mpSort.Value;
        var perRow = new ChipGroup<int>([(1, "1 large"), (2, "2"), (3, "3"), (4, "4 small")], [_opt.PhotosPerRow]);
        perRow.Changed += () => _opt.PhotosPerRow = perRow.Value;
        var crop = new ChipGroup<bool>([(true, "Same size (4:3, cropped)"), (false, "Whole photo")], [_opt.PhotoCrop]);
        crop.Changed += () => _opt.PhotoCrop = crop.Value;
        var maxPhotos = new ChipGroup<int>([(0, "All"), (4, "4"), (8, "8"), (12, "12"), (20, "20")], [_opt.MaxPhotos]);
        maxPhotos.Changed += () => _opt.MaxPhotos = maxPhotos.Value;

        return new Section("3. What to include & order").With(
            Ui.Caption("Tick the sections to print. Use ▲ ▼ to change their order. Numbering follows automatically."),
            list, quick,
            Toggle("Show costs", _opt.ShowCost, v => _opt.ShowCost = v),
            Toggle("Leave out empty sections", _opt.HideEmptySections, v => _opt.HideEmptySections = v),
            Ui.Field("Sort activity rows by", actSort),
            Ui.Field("Sort manpower rows by", mpSort),
            Ui.Field("Photos per row", perRow),
            Ui.Field("Photo framing", crop),
            Ui.Field("Photos per report", maxPhotos));
    }

    private View LayoutSection()
    {
        var l = _opt.Layout;
        var logo = LogoPicker("Company logo (left)", () => _opt.LogoFile, v => _opt.LogoFile = v);
        var clientLogo = LogoPicker("Client logo (right)", () => _opt.ClientLogoFile, v => _opt.ClientLogoFile = v);
        var align = new ChipGroup<TextAlign>([(TextAlign.Left, "⯇ Left"), (TextAlign.Center, "≡ Centre"), (TextAlign.Right, "Right ⯈")], [l.HeaderAlign]);
        align.Changed += () => l.HeaderAlign = align.Value;
        var pages = new ChipGroup<PageNumberStyle>([
            (PageNumberStyle.None, "None"), (PageNumberStyle.PageN, "Page 3"), (PageNumberStyle.PageNofTotal, "Page 3 of 9"), (PageNumberStyle.NSlashTotal, "3 / 9"),
        ], [l.PageNumbers]);
        pages.Changed += () => l.PageNumbers = pages.Value;
        var pagePos = new ChipGroup<TextAlign>([(TextAlign.Left, "Left"), (TextAlign.Center, "Centre"), (TextAlign.Right, "Right")], [l.PageNumberAlign]);
        pagePos.Changed += () => l.PageNumberAlign = pagePos.Value;
        var orient = new ChipGroup<bool>([(false, "Portrait"), (true, "Landscape")], [_opt.Landscape]);
        orient.Changed += () => _opt.Landscape = orient.Value;

        return new Section("4. Header, footer & page numbers", false).With(
            Ui.Columns(2, logo, clientLogo),
            Ui.Field("Company name", Ui.Entry(l.CompanyName, "Printed at the top", changed: v => l.CompanyName = v)),
            Ui.Field("Header lines (one per line: project ref, contract no., …)", Ui.Memo(l.HeaderLines, "Project ref: WTP/2026/014\nContract no: 45/2026", v => l.HeaderLines = v, 60)),
            Ui.Field("Header alignment", align),
            Ui.Field("Footer text", Ui.Entry(l.FooterText, "e.g. Confidential · Prepared by site team", changed: v => l.FooterText = v)),
            Toggle("Print date & time generated", l.ShowGeneratedStamp, v => l.ShowGeneratedStamp = v),
            Ui.Field("Page numbers", pages),
            Ui.Field("Page number position", pagePos),
            Ui.Field("Daily report paper", orient));
    }

    private static View Toggle(string text, bool value, Action<bool> changed)
    {
        var sw = new Switch { IsToggled = value, OnColor = Ui.Primary, VerticalOptions = LayoutOptions.Center };
        sw.Toggled += (_, a) => changed(a.Value);
        var g = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)] };
        var label = Ui.Label(text, 14);
        label.VerticalOptions = LayoutOptions.Center;
        g.Add(label, 0);
        g.Add(sw, 1);
        return g;
    }

    private static View LogoPicker(string title, Func<string> get, Action<string> set)
    {
        var img = new Image { HeightRequest = 50, Aspect = Aspect.AspectFit, HorizontalOptions = LayoutOptions.Start };
        void Show()
        {
            var path = get() == "" ? "" : AppServices.Db.MediaPath(get());
            img.Source = File.Exists(path) ? ImageSource.FromFile(path) : null;
            img.IsVisible = img.Source != null;
        }
        Show();
        return Ui.Card(new VerticalStackLayout
        {
            Spacing = 4,
            Children =
            {
                Ui.Caption(title), img,
                new HorizontalStackLayout
                {
                    Spacing = 6,
                    Children =
                    {
                        Ui.Button("Choose…", async () => { if (await ReportProfiles.PickLogoAsync() is { } f) { set(f); Show(); } }, false),
                        Ui.Button("Remove", () => { set(""); Show(); }, false, Ui.Muted),
                    }
                }
            }
        }, 8);
    }

    // ------------------------------------------------------------------ generate

    private async Task PreviewAsync()
    {
        _filter.Filter.ProjectId = AppServices.ProjectId;
        _matches = await AppServices.Reports.FindAsync(_filter.Filter);
        _preview.Text = K switch
        {
            Kind.HindrancePdf or Kind.HindranceXlsx => $"Hindrances · {_filter.Describe()}",
            Kind.DrawingPdf or Kind.DrawingXlsx => $"Drawing register up to {(_filter.Filter.To ?? DateTime.Today):dd-MMM-yyyy}",
            Kind.RequirementPdf or Kind.RequirementXlsx => $"Requirements · {_filter.Describe()}",
            Kind.ProgressPdf or Kind.ProgressXlsx => $"Progress to {(_filter.Filter.To ?? DateTime.Today):dd-MMM-yyyy}",
            _ => $"{_matches.Count} report(s) · {_filter.Describe()}",
        };
    }

    private async Task GenerateAsync()
    {
        if (!await AppShell.RequireProjectAsync()) return;
        var f = _filter.Filter;
        f.ProjectId = AppServices.ProjectId;
        var project = AppServices.CurrentProject;
        var desc = _filter.Describe();
        var opt = _opt.Resolve(AppServices.Db.MediaPath);
        var cal = await AppServices.Db.CalendarAsync(f.ProjectId);
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmm");
        string? path = null;

        switch (K)
        {
            case Kind.DayPdf or Kind.RegisterPdf or Kind.RegisterFullPdf or Kind.WorkbookXlsx or Kind.ManpowerPdf:
            {
                var reports = _matches;
                if (K == Kind.DayPdf)
                {
                    if (reports.Count == 0) { await DisplayAlertAsync("No reports", "No daily reports match the filter.", "OK"); return; }
                    var labels = reports.OrderByDescending(r => r.Date).Take(25).Select(r => r.Title).ToArray();
                    var choice = labels.Length == 1 ? labels[0] : await DisplayActionSheetAsync("Which report?", "Cancel", null, labels);
                    reports = reports.Where(r => r.Title == choice).Take(1).ToList();
                    if (reports.Count == 0) return;
                }
                if (reports.Count == 0) { await DisplayAlertAsync("No reports", "No daily reports match the filter.", "OK"); return; }
                var bundles = await AppServices.Db.LoadReportsAsync(f.ProjectId, reports);
                path = K switch
                {
                    Kind.DayPdf => await FileExport.CreateAsync($"{PdfReports.ReportLabel(bundles[0])}_{bundles[0].Report.Date:yyyy-MM-dd}.pdf",
                        s => PdfReports.DailyReport(s, bundles[0], AppServices.Db.MediaPath, cal, opt)),
                    Kind.RegisterPdf => await FileExport.CreateAsync($"DPR_register_{stamp}.pdf", s => PdfReports.ReportRegister(s, project, bundles, desc, false, AppServices.Db.MediaPath, cal, opt)),
                    Kind.RegisterFullPdf => await FileExport.CreateAsync($"DPR_all_{stamp}.pdf", s => PdfReports.ReportRegister(s, project, bundles, desc, true, AppServices.Db.MediaPath, cal, opt)),
                    Kind.ManpowerPdf => await FileExport.CreateAsync($"Manpower_summary_{stamp}.pdf", s => PdfReports.ManpowerSummary(s, project, bundles, desc, opt)),
                    _ => await FileExport.CreateAsync($"DPR_workbook_{stamp}.xlsx", s => ExcelReports.ReportWorkbook(s, project, bundles, desc, cal)),
                };
                break;
            }
            case Kind.ProgressPdf or Kind.ProgressXlsx:
            {
                var pp = await Task.Run(() => new ProgressService(AppServices.Db).ComputeAsync(f.ProjectId, f.From, f.To, f.To));
                path = K == Kind.ProgressPdf
                    ? await FileExport.CreateAsync($"Progress_cost_{stamp}.pdf", s => ProgressReports.Pdf(s, project, pp, desc, true, opt))
                    : await FileExport.CreateAsync($"Progress_cost_{stamp}.xlsx", s => ProgressReports.Excel(s, project, pp, desc));
                break;
            }
            case Kind.DrawingPdf or Kind.DrawingXlsx:
            {
                var reg = await Task.Run(() => new DrawingService(AppServices.Db).ComputeAsync(f.ProjectId, f.To));
                path = K == Kind.DrawingPdf
                    ? await FileExport.CreateAsync($"Drawing_register_{stamp}.pdf", s => DrawingReports.Pdf(s, project, reg, desc, opt))
                    : await FileExport.CreateAsync($"Drawing_register_{stamp}.xlsx", s => DrawingReports.Excel(s, project, reg, desc));
                break;
            }
            case Kind.HindrancePdf or Kind.HindranceXlsx:
            {
                var list = (await AppServices.Db.ListForProjectAsync<Hindrance>(f.ProjectId)).Where(h => f.InRange(h.FromDate)).OrderBy(h => h.SerialNo).ToList();
                path = K == Kind.HindrancePdf
                    ? await FileExport.CreateAsync($"Hindrance_register_{stamp}.pdf", s => PdfReports.HindranceRegister(s, project, list, desc, opt))
                    : await FileExport.CreateAsync($"Hindrance_register_{stamp}.xlsx", s => ExcelReports.HindranceRegister(s, project, list, desc));
                break;
            }
            default:
            {
                var list = (await AppServices.Db.ListForProjectAsync<Requirement>(f.ProjectId)).Where(r => f.InRange(r.RaisedDate)).OrderBy(r => r.SerialNo).ToList();
                path = K == Kind.RequirementPdf
                    ? await FileExport.CreateAsync($"Requirement_register_{stamp}.pdf", s => PdfReports.RequirementRegister(s, project, list, desc, opt))
                    : await FileExport.CreateAsync($"Requirement_register_{stamp}.xlsx", s => ExcelReports.RequirementRegister(s, project, list, desc));
                break;
            }
        }
        if (path != null) await FileExport.OfferAsync(this, path);
    }
}
