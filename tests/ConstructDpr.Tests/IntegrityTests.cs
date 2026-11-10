using ConstructDpr.Shared.Model;

namespace ConstructDpr.Tests;

public class IntegrityTests
{
    [Fact]
    public async Task Every_approved_demo_report_verifies_after_reloading()
    {
        using var t = new DemoDb();
        var api = t.Api();
        foreach (var d in (await api.DprsAsync(t.Project.Id)).Where(d => d.Status == DprStatus.Approved))
            Assert.True(await api.VerifyDprAsync(t.Project.Id, d.Id), d.Number);
    }
}

public class DecimalFormTests
{
    [Fact]
    public void Trailing_zeros_do_not_change_the_number_form()
    {
        Assert.Equal("85", (85.000m / 1.000000000000000000000000000000000m).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("7.5", (7.500000m / 1.000000000000000000000000000000000m).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
