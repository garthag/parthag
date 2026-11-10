using SiteNotes.Core.Models;

namespace SiteNotes.Core;

/// <summary>
/// Cost rules (all money in decimal, rounded to 2 places at row level so totals add up exactly):
/// <list type="bullet">
/// <item>Manpower: per hour = nos × paid hours × rate; per day = nos × rate × paid hours ÷ hours per day.
///   Paid hours = normal hours + overtime hours × overtime factor (from the work calendar).</item>
/// <item>Machinery: per hour = nos × chargeable hours × rate; per day = nos × rate × chargeable hours ÷ hours per day.
///   Chargeable hours = working hours, plus idle hours for hired machines.</item>
/// <item>Material / other: quantity consumed × rate. Lump sum = the rate itself.</item>
/// </list>
/// </summary>
public static class CostCalculator
{
    public static decimal Round(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    public static decimal PaidHours(double hours, WorkCalendar cal)
    {
        var h = (decimal)Math.Max(0, hours);
        var normal = Math.Min(h, (decimal)cal.HoursPerDay);
        var overtime = h - normal;
        return normal + overtime * cal.OvertimeFactor;
    }

    private static decimal TimeCost(int count, decimal paidHours, decimal rate, RateBasis basis, WorkCalendar cal) => basis switch
    {
        RateBasis.PerHour => count * paidHours * rate,
        RateBasis.PerDay => cal.HoursPerDay <= 0 ? 0 : count * rate * paidHours / (decimal)cal.HoursPerDay,
        RateBasis.PerUnit => count * rate,
        RateBasis.LumpSum => rate,
        _ => 0,
    };

    public static decimal Cost(ReportManpower m, WorkCalendar cal) =>
        Round(TimeCost(m.Count, PaidHours(m.Hours, cal), m.Rate, m.Basis, cal));

    public static double ChargeableHours(ReportMachine m) =>
        m.WorkingHours + (m.Ownership == MachineOwnership.Hired ? m.IdleHours : 0);

    public static decimal Cost(ReportMachine m, WorkCalendar cal) =>
        Round(TimeCost(m.Count, (decimal)ChargeableHours(m), m.Rate, m.Basis, cal));

    public static decimal Cost(ReportMaterial m) => Round(m.Basis switch
    {
        RateBasis.LumpSum => m.Rate,
        _ => (decimal)m.Consumed * m.Rate,
    });

    /// <summary>Planned cost of an assigned resource for an activity of <paramref name="durationDays"/> working days.</summary>
    public static decimal PlannedCost(ActivityResource r, int durationDays, WorkCalendar cal)
    {
        var days = Math.Max(1, durationDays);
        switch (r.Type)
        {
            case ResourceType.Manpower:
            case ResourceType.Machinery:
                var hours = r.PlannedHours > 0 ? (decimal)r.PlannedHours : days * (decimal)cal.HoursPerDay;
                var qty = (decimal)r.PlannedQty;
                return Round(r.Basis switch
                {
                    RateBasis.PerHour => qty * hours * r.Rate,
                    RateBasis.PerDay => cal.HoursPerDay <= 0 ? 0 : qty * r.Rate * hours / (decimal)cal.HoursPerDay,
                    RateBasis.PerUnit => qty * r.Rate,
                    _ => r.Rate,
                });
            default:
                return Round(r.Basis == RateBasis.LumpSum ? r.Rate : (decimal)r.PlannedQty * r.Rate);
        }
    }

    /// <summary>Total cost of one day's report.</summary>
    public static CostBreakdown ForReport(DprBundle b, WorkCalendar cal)
    {
        var c = new CostBreakdown();
        foreach (var m in b.Manpower) c.Manpower += Cost(m, cal);
        foreach (var m in b.Machines) c.Machinery += Cost(m, cal);
        foreach (var m in b.Materials)
        {
            if (m.Kind == ResourceType.Other) c.Other += Cost(m);
            else c.Material += Cost(m);
        }
        c.ManHours = b.Manpower.Sum(m => m.Count * m.Hours);
        c.MachineHours = b.Machines.Sum(m => m.Count * ChargeableHours(m));
        return c;
    }
}

/// <summary>Cost split by resource type. Add() makes it easy to roll up across days/activities.</summary>
public class CostBreakdown
{
    public decimal Manpower { get; set; }
    public decimal Machinery { get; set; }
    public decimal Material { get; set; }
    public decimal Other { get; set; }
    public double ManHours { get; set; }
    public double MachineHours { get; set; }
    public decimal Total => Manpower + Machinery + Material + Other;

    public void Add(CostBreakdown o)
    {
        Manpower += o.Manpower; Machinery += o.Machinery; Material += o.Material; Other += o.Other;
        ManHours += o.ManHours; MachineHours += o.MachineHours;
    }
}
