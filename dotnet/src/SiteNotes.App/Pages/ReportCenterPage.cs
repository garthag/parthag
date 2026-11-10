using SiteNotes.App.Services;
using SiteNotes.App.UI;
using SiteNotes.Core;
using SiteNotes.Core.Models;
using SiteNotes.Core.Reports;

namespace SiteNotes.App.Pages;

/// <summary>One place to generate every report as PDF or Excel with the same filters.</summary>
public class ReportCenterPage : ContentPage
{
    private static readonly string[] Types =
    [
        "Daily progress report – one day (PDF)",
        "DPR register – summary (PDF)",
        "DPR register + every full report (PDF)",
        "DPR workbook – all sections (Excel)",
        "Activity progress & cost (PDF)",
        "Activity progress & cost (Excel)",
        "Manpower summary – subcontractor / department / trade (PDF)",
        "Hindrance register (PDF)",
        "Hindrance register (Excel)",
        "Requirement register (PDF)",
        "Requirement register (Excel)",
    ];

    private readonly FilterBar _filter = new();
    private readonly Picker _type = new() { Title = "Report", ItemsSource = Types, SelectedIndex = 0 };
    private readonly Label _preview = Ui.Caption("");

    public ReportCenterPage()
    {
        Title = "Report centre";
        this.Page();
        _filter.Changed += async () => await PreviewAsync();
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Spacing = 10, Padding = new Thickness(0, 0, 0, 20),
                Children =
                {
                    new VerticalStackLayout { Padding = new Thickness(12, 8, 12, 0), Children = { Ui.Field("Report type", _type) } },
                    _filter,
                    new VerticalStackLayout
                    {
                        Padding = 12, Spacing = 10,
                        Children =
                        {
                            _preview,
                            Ui.Button("🖨 Generate", () => Ui.Guard(GenerateAsync)),
                            Ui.Caption($"Files are saved in: {FileExport.ReportsFolder}"),
                        }
                    }
                }
            }
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _filter.LoadAsync();
        await PreviewAsync();
    }

    private async Task PreviewAsync()
    {
        _filter.Filter.ProjectId = AppServices.ProjectId;
        var n = (await AppServices.Reports.FindAsync(_filter.Filter)).Count;
        _preview.Text = $"{n} daily report(s) match: {_filter.Describe()}";
    }

    private async Task GenerateAsync()
    {
        if (!await AppShell.RequireProjectAsync()) return;
        var f = _filter.Filter;
        f.ProjectId = AppServices.ProjectId;
        var project = AppServices.CurrentProject;
        var desc = _filter.Describe();
        var cal = await AppServices.Db.CalendarAsync(f.ProjectId);
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmm");
        var i = _type.SelectedIndex;
        string path;

        if (i <= 3)
        {
            var bundles = await AppServices.Reports.LoadBundlesAsync(f);
            if (bundles.Count == 0) { await DisplayAlertAsync("No reports", "No daily reports match the filter.", "OK"); return; }
            path = i switch
            {
                0 => await PickOneAsync(bundles, cal),
                1 => await FileExport.CreateAsync($"DPR_register_{stamp}.pdf", s => PdfReports.ReportRegister(s, project, bundles, desc, false, AppServices.Db.MediaPath, cal)),
                2 => await FileExport.CreateAsync($"DPR_full_{stamp}.pdf", s => PdfReports.ReportRegister(s, project, bundles, desc, true, AppServices.Db.MediaPath, cal)),
                _ => await FileExport.CreateAsync($"DPR_workbook_{stamp}.xlsx", s => ExcelReports.ReportWorkbook(s, project, bundles, desc, cal)),
            };
            if (path == "") return;
        }
        else if (i <= 5)
        {
            var pp = await Task.Run(() => new ProgressService(AppServices.Db).ComputeAsync(f.ProjectId, f.From, f.To, f.To));
            path = i == 4
                ? await FileExport.CreateAsync($"Progress_cost_{stamp}.pdf", s => ProgressReports.Pdf(s, project, pp, desc))
                : await FileExport.CreateAsync($"Progress_cost_{stamp}.xlsx", s => ProgressReports.Excel(s, project, pp, desc));
        }
        else if (i == 6)
        {
            var bundles = await AppServices.Reports.LoadBundlesAsync(f);
            path = await FileExport.CreateAsync($"Manpower_summary_{stamp}.pdf", s => PdfReports.ManpowerSummary(s, project, bundles, desc));
        }
        else if (i <= 8)
        {
            var list = (await AppServices.Db.ListForProjectAsync<Hindrance>(f.ProjectId)).Where(h => f.InRange(h.FromDate)).OrderBy(h => h.SerialNo).ToList();
            path = i == 7
                ? await FileExport.CreateAsync($"Hindrance_register_{stamp}.pdf", s => PdfReports.HindranceRegister(s, project, list, desc))
                : await FileExport.CreateAsync($"Hindrance_register_{stamp}.xlsx", s => ExcelReports.HindranceRegister(s, project, list, desc));
        }
        else
        {
            var list = (await AppServices.Db.ListForProjectAsync<Requirement>(f.ProjectId)).Where(r => f.InRange(r.RaisedDate)).OrderBy(r => r.SerialNo).ToList();
            path = i == 9
                ? await FileExport.CreateAsync($"Requirement_register_{stamp}.pdf", s => PdfReports.RequirementRegister(s, project, list, desc))
                : await FileExport.CreateAsync($"Requirement_register_{stamp}.xlsx", s => ExcelReports.RequirementRegister(s, project, list, desc));
        }
        await FileExport.OfferAsync(this, path);
    }

    private async Task<string> PickOneAsync(List<DprBundle> bundles, WorkCalendar cal)
    {
        var labels = bundles.Select(b => b.Report.Title).ToArray();
        var choice = labels.Length == 1 ? labels[0] : await DisplayActionSheetAsync("Which report?", "Cancel", null, labels);
        var b = bundles.FirstOrDefault(x => x.Report.Title == choice);
        if (b == null) return "";
        return await FileExport.CreateAsync($"DPR_{b.Report.ReportNo}_{b.Report.Date:yyyy-MM-dd}.pdf", s => PdfReports.DailyReport(s, b, AppServices.Db.MediaPath, cal));
    }
}
