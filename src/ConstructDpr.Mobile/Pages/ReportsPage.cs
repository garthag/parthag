using ConstructDpr.Mobile.Services;
using ConstructDpr.Shared.Api;
using ConstructDpr.Shared.Model;
using ConstructDpr.Shared.Security;

namespace ConstructDpr.Mobile.Pages;

/// <summary>All daily reports (newest first), or only those waiting for approval.</summary>
public sealed class ReportsPage(bool approvals) : ProjectPage(approvals ? "Approvals" : "Daily reports")
{
    private string _filter = "All";

    protected override bool Wants(LiveChange c) => c.Entity is "dpr" or "all";

    protected override async Task LoadAsync()
    {
        var all = await AppState.Api.DprsAsync(AppState.Pid);
        var list = approvals ? all.Where(d => d.Status == DprStatus.Submitted).OrderBy(d => d.ReportDate).ToList()
                             : all.Where(d => _filter == "All" || d.Status.ToString() == _filter).ToList();
        var page = Ui.Scroll();
        var stack = (VerticalStackLayout)page.Content;
        if (approvals)
        {
            stack.Children.Add(Ui.Small(AppState.Can(Module.Dpr, Act.Approve)
                ? "Reports submitted for review. Open one, check the quantities, then approve or reject."
                : $"Your role ({Ui.Role(AppState.Role)}) does not approve reports. You can still read them."));
        }
        else
        {
            if (AppState.Can(Module.Dpr, Act.Create))
                stack.Children.Add(Ui.Primary("＋  New report", async (_, _) => await Navigation.PushAsync(new NewReportPage())));
            var filters = new HorizontalStackLayout { Spacing = 6 };
            foreach (var f in new[] { "All", "Draft", "Submitted", "Approved", "Rejected" })
            {
                var b = f == _filter ? Ui.Primary(f, (_, _) => { }) : Ui.Plain(f, (_, _) => { });
                b.FontSize = 12;
                b.Padding = new Thickness(10, 4);
                b.MinimumHeightRequest = 34;
                b.Clicked += async (_, _) => { _filter = f; await ReloadAsync(); };
                filters.Children.Add(b);
            }
            stack.Children.Add(new ScrollView { Orientation = ScrollOrientation.Horizontal, Content = filters });
        }
        foreach (var d in list)
        {
            var row = Ui.Card(Ui.Row(
                new VerticalStackLayout
                {
                    Children =
                    {
                        new Label { Text = d.Number + (d.Revision > 0 ? $" · R{d.Revision}" : ""), FontAttributes = FontAttributes.Bold, FontSize = 15, TextColor = Ui.Text },
                        Ui.Small($"{d.ReportDate:ddd dd MMM yyyy} · {d.Shift} · {d.PreparedBy}"),
                    },
                },
                Ui.Badge(d.Status.ToString().ToUpperInvariant(), Ui.StatusColor(d.Status))));
            var id = d.Id;
            row.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(async () => await Navigation.PushAsync(new DprPage(id))) });
            stack.Children.Add(row);
        }
        if (list.Count == 0) stack.Children.Add(Ui.Body(approvals ? "Nothing waiting for approval. ✔" : "No reports here yet."));
        Content = page;
    }
}

/// <summary>Date and shift for a new report. Crews, equipment and material stock are copied from the previous report.</summary>
public sealed class NewReportPage : ContentPage
{
    public NewReportPage()
    {
        Title = "New report";
        BackgroundColor = Ui.Page;
        var date = new DatePicker { Date = DateTime.Today, MaximumDate = DateTime.Today, FontSize = 16 };
        var shift = new Picker { ItemsSource = Enum.GetNames<Shift>(), SelectedIndex = 0, FontSize = 16 };
        Content = Ui.Scroll(
            Ui.H1("New daily report"),
            Ui.Small("Crews, equipment and material stock are copied from the previous report, so you only change what is different today."),
            Ui.Card(new VerticalStackLayout
            {
                Spacing = 10,
                Children =
                {
                    Ui.Labelled("Report date", date),
                    Ui.Labelled("Shift", shift),
                    Ui.Primary("Create report", async (_, _) =>
                    {
                        Dpr? d = null;
                        if (await Ui.TryAsync(this, async () => d = await AppState.Api.CreateDprAsync(AppState.Pid,
                                DateOnly.FromDateTime(date.Date ?? DateTime.Today), Enum.Parse<Shift>((string)shift.SelectedItem))))
                        {
                            Navigation.InsertPageBefore(new DprPage(d!.Id), this);
                            await Navigation.PopAsync();
                        }
                    }),
                },
            }));
    }
}
