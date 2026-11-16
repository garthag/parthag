using System.Data;

namespace BSL.Core.Data;

/// <summary>Links activities to work orders, BOQ, contractors, rates, packages and drawings; applies activity rates.</summary>
public sealed class ActivityService(Database db)
{
    /// <summary>Runs dbo.usp_LinkActivities (fills blanks only) and returns a readable summary.</summary>
    public string LinkActivities()
    {
        var r = db.Query("EXEC dbo.usp_LinkActivities").Rows[0];
        string N(string c) => r[c] is DBNull ? "0" : Convert.ToString(r[c])!;
        var text = $"Activities linked: {N("WorkOrders")} work orders, {N("BoqItems")} BOQ items, {N("ContractorActivities")} contractor-activity links, " +
                   $"{N("WorkOrderRates")} work-order rates, {N("ActivityDefaults")} activity default rates, {N("Packages")} packages, {N("Drawings")} drawings.";
        db.Audit("Activity", null, "LINK", text);
        return text;
    }

    /// <summary>Fills BOQ item rates from work-order activity rates (else activity defaults).</summary>
    public int ApplyRates(bool overwrite)
    {
        var n = Convert.ToInt32(db.Scalar("EXEC dbo.usp_ApplyActivityRates @Overwrite = @o", ("@o", overwrite)));
        db.Audit("ContractItem", null, "APPLY_RATES", $"{n} items, overwrite={overwrite}");
        return n;
    }
}
