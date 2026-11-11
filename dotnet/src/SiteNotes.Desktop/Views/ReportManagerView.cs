using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SiteNotes.Core;
using SiteNotes.Core.Models;
using SiteNotes.Core.Reports;
using SiteNotes.Desktop.Services;
using SiteNotes.Desktop.UI;

namespace SiteNotes.Desktop.Views;

/// <summary>
/// Report manager: report type, days and filters, sections (tick + order), row sorting, photo layout,
/// logos, header lines, footer, page numbers, remarks. Saved per project; every PDF uses it.
/// </summary>
public class ReportManagerView : ViewBase
{
    private enum Kind { Day, AllInOne, Register, Workbook, ProgressPdf, ProgressXlsx, Manpower, DrawingPdf, DrawingXlsx, HindrancePdf, HindranceXlsx, RequirementPdf, RequirementXlsx }

    private static readonly (Kind, string)[] Kinds =
    [
        (Kind.Day, "Daily report (PDF)"), (Kind.AllInOne, "All daily reports in one PDF"), (Kind.Register, "DPR register summary (PDF)"),
        (Kind.Workbook, "DPR workbook (Excel)"), (Kind.ProgressPdf, "Progress & cost (PDF)"), (Kind.ProgressXlsx, "Progress & cost (Excel)"),
        (Kind.Manpower, "Manpower summary (PDF)"), (Kind.DrawingPdf, "Drawing register & work completion (PDF)"), (Kind.DrawingXlsx, "Drawing register (Excel)"), (Kind.HindrancePdf, "Hindrance register (PDF)"), (Kind.HindranceXlsx, "Hindrance register (Excel)"),
        (Kind.RequirementPdf, "Requirement register (PDF)"), (Kind.RequirementXlsx, "Requirement register (Excel)"),
    ];

    private Kind _kind = Kind.Day;
    private readonly ReportFilterPanel _filter = new();
    private readonly TextBlock _preview = W.Text("", 13, true, Theme.Primary);
    private readonly ListBox _matches = new() { Height = 150, SelectionMode = SelectionMode.Extended };
    private ReportProfile _profile = new();
    private ReportOptions _opt = new();
    private List<DailyReport> _found = [];

    public override string Title => "Report manager";

    public override async Task RefreshAsync()
    {
        (_profile, _opt) = await Desk.LoadReportOptionsAsync();
        _filter.Changed -= OnFilter;
        _filter.Changed += OnFilter;
        await _filter.LoadAsync();
        Build();
        await PreviewAsync();
    }

    private async void OnFilter() => await Dialogs.Guard(PreviewAsync);

    private void Build()
    {
        var left = W.Stack(
            Box("1. Report", W.Choice(Kinds.Select(k => (k.Item1, k.Item2)), _kind, k => { _kind = k; _ = PreviewAsync(); })),
            Box("2. Days, status, agency & sort", W.Stack(_filter,
                W.Field("Sort reports", W.Choice([(ReportSort.DateOldest, "Oldest first"), (ReportSort.DateNewest, "Newest first"), (ReportSort.ReportNo, "Report no."),
                    (ReportSort.ManpowerHigh, "Most manpower"), (ReportSort.Status, "Status")], _filter.Filter.Sort, s => { _filter.Filter.Sort = s; _ = PreviewAsync(); })),
                W.Field("Matching reports (for one daily report, select it here; for several, select them or leave empty for all)", _matches))),
            Box("3. What to include & in which order (daily reports)", Contents()),
            Box("Remarks", W.Stack(
                W.Field("Report title", W.Box(_opt.ReportTitle, v => _opt.ReportTitle = v)),
                W.Field("Extra note printed on the report", W.Box(_opt.ExtraRemarks, v => _opt.ExtraRemarks = v, true, 50)))));
        var right = W.Stack(Box("4. Header, footer & page numbers", Layout()));

        var cols = new Grid();
        cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
        cols.ColumnDefinitions.Add(new ColumnDefinition());
        cols.Children.Add(left);
        Grid.SetColumn(right, 1);
        cols.Children.Add(right);

        var tb = W.ToolBar(
            (Glyph.Print, "Generate", GenerateAsync),
            null,
            (Glyph.Save, "Save as default for this project", async () => { await Desk.SaveReportOptionsAsync(_profile, _opt); Desk.Say("Report settings saved – every PDF uses them now"); }),
            (Glyph.Refresh, "Reset to standard", () => { if (Dialogs.Confirm("Reset to the standard layout and all sections?")) { _opt = new ReportOptions(); Build(); } return Task.CompletedTask; }),
            (Glyph.Folder, "Open reports folder", () => { Dialogs.Open(Desk.ReportsFolder); return Task.CompletedTask; }));
        tb.Items.Add(W.Free(_preview));
        var dock = new DockPanel();
        DockPanel.SetDock(tb, Dock.Top);
        dock.Children.Add(tb);
        dock.Children.Add(W.Scroll(cols, 10));
        Content = dock;
    }

    private static GroupBox Box(string header, UIElement content) => new()
    {
        Header = new TextBlock { Text = header, FontWeight = FontWeights.SemiBold, Foreground = Theme.Primary }, Content = W.Free(content), Margin = new Thickness(0, 0, 10, 10), Background = Theme.Card,
    };

    private UIElement Contents()
    {
        var all = new ObservableCollection<DprSection>(_opt.Sections.Concat(Enum.GetValues<DprSection>().Where(s => !_opt.Sections.Contains(s))));
        var on = _opt.Sections.ToHashSet();
        var list = new ListBox { Height = 260 };
        void Commit() => _opt.Sections = all.Where(on.Contains).ToList();
        void Render()
        {
            list.Items.Clear();
            foreach (var s in all)
            {
                var cb = new CheckBox { Content = ReportOptions.Label(s), IsChecked = on.Contains(s), Tag = s, Margin = new Thickness(2) };
                var sec = s;
                cb.Checked += (_, _) => { on.Add(sec); Commit(); };
                cb.Unchecked += (_, _) => { on.Remove(sec); Commit(); };
                list.Items.Add(cb);
            }
        }
        Render();
        void Move(int d)
        {
            if (list.SelectedItem is not CheckBox { Tag: DprSection s }) return;
            var i = all.IndexOf(s);
            var j = i + d;
            if (j < 0 || j >= all.Count) return;
            all.Move(i, j);
            Commit();
            Render();
            list.SelectedIndex = j;
        }
        var order = W.Wrap(
            W.Button("Move up", () => Move(-1), Glyph.Up),
            W.Button("Move down", () => Move(1), Glyph.Down),
            W.Button("All", () => { foreach (var s in all) on.Add(s); Commit(); Render(); }),
            W.Button("Short (no photos/cost)", () => { on.Remove(DprSection.Photos); on.Remove(DprSection.Cost); on.Remove(DprSection.ManpowerSummary); Commit(); Render(); }),
            W.Button("Standard order", () => { var std = Enum.GetValues<DprSection>(); all.Clear(); foreach (var s in std) all.Add(s); Commit(); Render(); }));
        return W.Stack(
            W.Muted("Tick sections to print; select one and use Move up / down to change the order. Section numbers follow automatically."),
            list, order,
            W.Wrap(W.Check("Show costs", _opt.ShowCost, v => _opt.ShowCost = v), W.Check("Leave out empty sections", _opt.HideEmptySections, v => _opt.HideEmptySections = v)),
            W.Field("Sort activity rows", W.Choice([(ActivityRowSort.AsEntered, "As entered"), (ActivityRowSort.Name, "Name"), (ActivityRowSort.ProgressHigh, "Most progress"),
                (ActivityRowSort.ProgressLow, "Least progress"), (ActivityRowSort.TodayQtyHigh, "Most done today"), (ActivityRowSort.Agency, "Agency"), (ActivityRowSort.Location, "Location")],
                _opt.ActivitySort, v => _opt.ActivitySort = v)),
            W.Field("Sort manpower rows", W.Choice([(ManpowerRowSort.AsEntered, "As entered"), (ManpowerRowSort.Party, "Agency / department"), (ManpowerRowSort.Trade, "Trade"),
                (ManpowerRowSort.CountHigh, "Most people"), (ManpowerRowSort.Activity, "Activity")], _opt.ManpowerSort, v => _opt.ManpowerSort = v)),
            W.Field("Photos per row (standard 4:3 frames)", W.Choice([(1, "1 large"), (2, "2 standard"), (3, "3"), (4, "4 small")], _opt.PhotosPerRow, v => _opt.PhotosPerRow = v)),
            W.Field("Photo framing", W.Choice([(true, "Fill frame (crop edges)"), (false, "Whole photo (fit)")], _opt.PhotoCrop, v => _opt.PhotoCrop = v)),
            W.Field("Photos per report", W.Choice([(0, "All"), (4, "4"), (8, "8"), (12, "12"), (20, "20")], _opt.MaxPhotos, v => _opt.MaxPhotos = v)),
            W.Field("Daily report paper", W.Choice([(false, "Portrait"), (true, "Landscape")], _opt.Landscape, v => _opt.Landscape = v)));
    }

    private UIElement Layout()
    {
        var l = _opt.Layout;
        return W.Stack(
            W.Form(2, Logo("Company logo (left)", () => _opt.LogoFile, v => _opt.LogoFile = v), Logo("Client logo (right)", () => _opt.ClientLogoFile, v => _opt.ClientLogoFile = v)),
            W.Field("Company name", W.Box(l.CompanyName, v => l.CompanyName = v)),
            W.Field("Header lines – one per line (project ref, contract no., …)", W.Box(l.HeaderLines, v => l.HeaderLines = v, true, 70)),
            W.Field("Header alignment", W.Choice([(TextAlign.Left, "Left"), (TextAlign.Center, "Centre"), (TextAlign.Right, "Right")], l.HeaderAlign, v => l.HeaderAlign = v)),
            W.Field("Footer text", W.Box(l.FooterText, v => l.FooterText = v)),
            W.Check("Print date & time generated", l.ShowGeneratedStamp, v => l.ShowGeneratedStamp = v),
            W.Field("Page numbers", W.Choice([(PageNumberStyle.None, "None"), (PageNumberStyle.PageN, "Page 3"), (PageNumberStyle.PageNofTotal, "Page 3 of 9"), (PageNumberStyle.NSlashTotal, "3 / 9")], l.PageNumbers, v => l.PageNumbers = v)),
            W.Field("Page number position", W.Choice([(TextAlign.Left, "Left"), (TextAlign.Center, "Centre"), (TextAlign.Right, "Right")], l.PageNumberAlign, v => l.PageNumberAlign = v)),
            W.Muted("Tip: Save as default so every PDF (reports, registers, notes, progress) uses this header and footer."));
    }

    private static FrameworkElement Logo(string title, Func<string> get, Action<string> set)
    {
        var img = new Image { Height = 54, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 4) };
        void Show()
        {
            var p = get() == "" ? "" : Desk.Db.MediaPath(get());
            img.Source = File.Exists(p) ? DprEditorView.Thumb(p, 300) : null;
        }
        Show();
        return W.Card(W.Stack(W.Muted(title), img, W.Wrap(
            W.Button("Choose…", async () =>
            {
                var f = Dialogs.OpenImages(false, title).FirstOrDefault();
                if (f == null) return;
                var m = await Desk.ImportMediaAsync(f, MediaOwner.Note, "logo", MediaKind.Document, "Report logo");
                set(m.FileName);
                Show();
            }),
            W.Button("Remove", () => { set(""); Show(); }))), 8);
    }

    private async Task PreviewAsync()
    {
        _filter.Filter.ProjectId = Desk.ProjectId;
        _found = await Desk.Reports.FindAsync(_filter.Filter);
        _matches.ItemsSource = _found.Select(r => $"{Desk.Project?.ReportPrefix}{r.ReportNo:000} · {r.Date:ddd dd-MMM-yyyy} · {r.Status} · {r.TotalManpower} manpower").ToList();
        _preview.Text = $"   {_found.Count} report(s) match · {_filter.Filter.Describe()}";
    }

    private List<DailyReport> Chosen() => _matches.SelectedItems.Count == 0
        ? _found
        : _matches.SelectedItems.OfType<string>().Select(s => _found[_matches.Items.IndexOf(s)]).ToList();

    private async Task GenerateAsync()
    {
        var f = _filter.Filter;
        f.ProjectId = Desk.ProjectId;
        var project = Desk.Project;
        var desc = f.Describe();
        var opt = _opt.Resolve(Desk.Db.MediaPath);
        var cal = await Desk.Db.CalendarAsync(f.ProjectId);
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmm");

        if (_kind is Kind.Day or Kind.AllInOne or Kind.Register or Kind.Workbook or Kind.Manpower)
        {
            var reports = Chosen();
            if (_kind == Kind.Day)
            {
                if (reports.Count == 0) { Dialogs.Info("No daily reports match the filter."); return; }
                if (reports.Count > 1 && _matches.SelectedItems.Count != 1)
                {
                    var i = Dialogs.Choose("Which report?", "Pick the daily report:", reports.Take(60).Select(r => r.Title).ToList());
                    if (i < 0) return;
                    reports = [reports[i]];
                }
                else reports = [reports[0]];
            }
            if (reports.Count == 0) { Dialogs.Info("No daily reports match the filter."); return; }
            var bundles = await Desk.Db.LoadReportsAsync(f.ProjectId, f.ApplySort(reports).ToList());
            switch (_kind)
            {
                case Kind.Day:
                    await Dialogs.CreateAndOpenAsync($"{PdfReports.ReportLabel(bundles[0])}_{bundles[0].Report.Date:yyyy-MM-dd}.pdf", s => PdfReports.DailyReport(s, bundles[0], Desk.Db.MediaPath, cal, opt));
                    break;
                case Kind.AllInOne:
                    await Dialogs.CreateAndOpenAsync($"DPR_all_{stamp}.pdf", s => PdfReports.ReportRegister(s, project, bundles, desc, true, Desk.Db.MediaPath, cal, opt));
                    break;
                case Kind.Register:
                    await Dialogs.CreateAndOpenAsync($"DPR_register_{stamp}.pdf", s => PdfReports.ReportRegister(s, project, bundles, desc, false, Desk.Db.MediaPath, cal, opt));
                    break;
                case Kind.Manpower:
                    await Dialogs.CreateAndOpenAsync($"Manpower_summary_{stamp}.pdf", s => PdfReports.ManpowerSummary(s, project, bundles, desc, opt));
                    break;
                default:
                    await Dialogs.CreateAndOpenAsync($"DPR_workbook_{stamp}.xlsx", s => ExcelReports.ReportWorkbook(s, project, bundles, desc, cal));
                    break;
            }
            return;
        }
        if (_kind is Kind.ProgressPdf or Kind.ProgressXlsx)
        {
            var pp = await Task.Run(() => new ProgressService(Desk.Db).ComputeAsync(f.ProjectId, f.From, f.To, f.To));
            if (_kind == Kind.ProgressPdf) await Dialogs.CreateAndOpenAsync($"Progress_cost_{stamp}.pdf", s => ProgressReports.Pdf(s, project, pp, desc, true, opt));
            else await Dialogs.CreateAndOpenAsync($"Progress_cost_{stamp}.xlsx", s => ProgressReports.Excel(s, project, pp, desc));
            return;
        }
        if (_kind is Kind.DrawingPdf or Kind.DrawingXlsx)
        {
            var reg = await Task.Run(() => new DrawingService(Desk.Db).ComputeAsync(f.ProjectId, f.To));
            if (_kind == Kind.DrawingPdf) await Dialogs.CreateAndOpenAsync($"Drawing_register_{stamp}.pdf", s => DrawingReports.Pdf(s, project, reg, desc, opt));
            else await Dialogs.CreateAndOpenAsync($"Drawing_register_{stamp}.xlsx", s => DrawingReports.Excel(s, project, reg, desc));
            return;
        }
        if (_kind is Kind.HindrancePdf or Kind.HindranceXlsx)
        {
            var list = (await Desk.Db.ListForProjectAsync<Hindrance>(f.ProjectId)).Where(h => f.InRange(h.FromDate)).OrderBy(h => h.SerialNo).ToList();
            if (_kind == Kind.HindrancePdf) await Dialogs.CreateAndOpenAsync($"Hindrance_register_{stamp}.pdf", s => PdfReports.HindranceRegister(s, project, list, desc, opt));
            else await Dialogs.CreateAndOpenAsync($"Hindrance_register_{stamp}.xlsx", s => ExcelReports.HindranceRegister(s, project, list, desc));
            return;
        }
        var reqs = (await Desk.Db.ListForProjectAsync<Requirement>(f.ProjectId)).Where(r => f.InRange(r.RaisedDate)).OrderBy(r => r.SerialNo).ToList();
        if (_kind == Kind.RequirementPdf) await Dialogs.CreateAndOpenAsync($"Requirement_register_{stamp}.pdf", s => PdfReports.RequirementRegister(s, project, reqs, desc, opt));
        else await Dialogs.CreateAndOpenAsync($"Requirement_register_{stamp}.xlsx", s => ExcelReports.RequirementRegister(s, project, reqs, desc));
    }
}

/// <summary>User, sync server, data locations.</summary>
public class SettingsView : ViewBase
{
    public override string Title => "Settings & sync";

    public override Task RefreshAsync()
    {
        var user = Desk.Db.UserName;
        var url = Desk.SyncSettings.ServerUrl;
        var key = Desk.SyncSettings.ApiKey;
        var pwd = new PasswordBox { Password = key, MinHeight = 26 };
        var status = W.Text(Desk.SyncText + "\n" + Desk.Sync.LastMessage);
        Desk.SyncStatusChanged += () => status.Text = Desk.SyncText + "\n" + Desk.Sync.LastMessage;
        Content = W.Scroll(W.Stack(
            W.Heading("Settings & sync"),
            W.Card(W.Stack(
                W.Text("You", 14, true, Theme.Primary),
                W.Field("Your name (printed as “prepared by”, shown on edits)", W.Box(user, v => user = v)))),
            W.Card(W.Stack(
                W.Text("Real-time sync (optional)", 14, true, Theme.Primary),
                W.Muted("Run SiteNotes.Server on an office PC or a server (SQL Server or SQLite). Every PC and phone with the same address and key sees changes within seconds. Without a server the app works fully offline."),
                W.Form(2, W.Field("Server address", W.Box(url, v => url = v)), W.Field("Site key", pwd)),
                W.Wrap(
                    W.Button("Save & connect", async () => { await Desk.ReconfigureSyncAsync(url, pwd.Password, user); Desk.Say("Settings saved"); }, Glyph.Save, true),
                    W.Button("Sync now", async () => { await Desk.Sync.SyncNowAsync(); }, Glyph.Sync),
                    W.Button("Test connection", async () =>
                    {
                        if (string.IsNullOrWhiteSpace(url)) { Dialogs.Info("Enter the server address first."); return; }
                        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(8) };
                        try
                        {
                            var res = await http.GetAsync(url.TrimEnd('/') + "/api/health");
                            Dialogs.Info(res.IsSuccessStatusCode ? "✔ Server reachable." : $"Server answered {(int)res.StatusCode}.");
                        }
                        catch (Exception ex) { Dialogs.Info("Connection failed: " + ex.Message); }
                    }, Glyph.Link)),
                status)),
            W.Card(W.Stack(
                W.Text("Data", 14, true, Theme.Primary),
                W.Muted($"Database & photos: {Desk.DataFolder}"),
                W.Muted($"Reports: {Desk.ReportsFolder}"),
                W.Muted($"Device id: {Desk.SyncSettings.DeviceId}"),
                W.Wrap(
                    W.Button("Open data folder", () => Dialogs.Open(Desk.DataFolder), Glyph.Folder),
                    W.Button("Back up database…", () =>
                    {
                        var dest = Path.Combine(Desk.ReportsFolder, $"SiteNotes_backup_{DateTime.Now:yyyyMMdd_HHmm}.db3");
                        File.Copy(Path.Combine(Desk.DataFolder, "sitenotes.db3"), dest, true);
                        Dialogs.ShowInFolder(dest);
                    }, Glyph.Save),
                    W.Button("Load sample project", async () => { var p = await SampleData.CreateAsync(Desk.Db); await Desk.SetProjectAsync(p); await MainWindow.Current.FillProjectsAsync(); }, Glyph.Add)))),
            W.Muted("SiteNotes DPR · Windows (WPF) and Android · C# / .NET 10 · SQLite on each device, SQL Server on the sync server.")));
        return Task.CompletedTask;
    }
}
