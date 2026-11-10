using System.Text;
using ClosedXML.Excel;
using ConstructDpr.Core;
using ConstructDpr.Core.Data;
using ConstructDpr.Core.Services;
using ConstructDpr.Shared.Model;
using ConstructDpr.Shared.Security;

namespace ConstructDpr.Tests;

public class PeriodAndAccountTests
{
    [Fact]
    public void Period_summary_counts_only_approved_reports_in_the_period()
    {
        using var t = new DemoDb();
        var from = t.Today.AddDays(-7);
        var s = new PeriodService(t.Db).Build(t.Project.Id, from, t.Today);
        var approvedInPeriod = t.Db.Dprs.Count(d => d.Status == DprStatus.Approved && d.ReportDate >= from && d.ReportDate <= t.Today);
        Assert.Equal(approvedInPeriod, s.ApprovedReports);
        Assert.Equal(1, s.NotYetApproved); // today's submitted report
        var ids = t.Db.Dprs.Where(d => d.Status == DprStatus.Approved && d.ReportDate >= from).Select(d => d.Id).ToList();
        var a1010 = t.Db.Activities.Single(a => a.Code == "A1010");
        var expected = t.Db.DprActivityLines.Where(l => ids.Contains(l.DprId) && l.ActivityId == a1010.Id).ToList().Sum(l => l.QtyAccepted ?? l.QtyToday);
        var row = s.Activities.Single(r => r.Code == "A1010");
        Assert.Equal(expected, row.InPeriod);
        Assert.Equal(a1010.AcceptedQty, row.Cumulative);
        Assert.True(s.Crews.Sum(c => c.ManDays) > 0);
        Assert.Contains(s.Equipment, e => e.Code == "CR-01" && e.UtilisationPct is > 0 and < 100);
        Assert.True(s.ValueInPeriod > 0 && s.ValueToDate >= s.ValueInPeriod);
        Assert.Throws<WorkflowException>(() => new PeriodService(t.Db).Build(t.Project.Id, t.Today, t.Today.AddDays(-1)));
    }

    [Fact]
    public async Task Period_report_downloads_as_pdf_and_excel()
    {
        using var t = new DemoDb();
        var api = t.Api();
        var pdf = await api.PeriodReportAsync(t.Project.Id, t.Today.AddDays(-30), t.Today, excel: false);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(pdf.Content, 0, 4));
        Assert.Contains("progress", pdf.FileName);
        var xlsx = await api.PeriodReportAsync(t.Project.Id, t.Today.AddDays(-30), t.Today, excel: true);
        using var wb = new XLWorkbook(new MemoryStream(xlsx.Content));
        Assert.Contains(wb.Worksheets, w => w.Name == "Quantities");
        Assert.True(wb.Worksheet("Quantities").Tables.Any());
    }

    [Fact]
    public async Task Administrator_resets_a_forgotten_password()
    {
        using var t = new DemoDb();
        var accounts = new DirectAccounts(() => new AppDbContext(AppDbContext.Options(DbProvider.Sqlite, $"Data Source={t.Path}")));
        var pm = accounts.Register("pm@site.test", "office-pass-1", "PM");
        var sup = accounts.Register("sup@site.test", "office-pass-2", "Supervisor");
        var p = await t.Api(pm).CreateDemoProjectAsync();
        var member = await t.Api(pm).AddMemberAsync(p.Id, "sup@site.test", ProjectRole.Supervisor);

        await Assert.ThrowsAsync<ForbiddenException>(() => t.Api(sup).ResetPasswordAsync(p.Id, member.Id));
        var temp = await t.Api(pm).ResetPasswordAsync(p.Id, member.Id);
        Assert.Equal(10, temp.Length);
        Assert.Throws<UnauthorizedAccessException>(() => accounts.SignIn("sup@site.test", "office-pass-2")); // old password gone
        Assert.Equal(sup.UserId, accounts.SignIn("sup@site.test", temp).UserId);
        var own = (await t.Api(pm).MembersAsync(p.Id)).Single(m => m.Email == "pm@site.test");
        await Assert.ThrowsAsync<WorkflowException>(() => t.Api(pm).ResetPasswordAsync(p.Id, own.Id));
    }

    [Fact]
    public async Task The_owner_of_a_single_pc_can_add_phone_users()
    {
        using var t = new DemoDb();
        var accounts = new DirectAccounts(() => new AppDbContext(AppDbContext.Options(DbProvider.Sqlite, $"Data Source={t.Path}")));
        var phone = accounts.Register("site@phone.test", "phone-pass-1", "Site");
        await t.Api().AddMemberAsync(t.Project.Id, "site@phone.test", ProjectRole.SiteEngineer); // "This PC only" owner
        Assert.Single(await t.Api(phone).ProjectsAsync());
    }
}
