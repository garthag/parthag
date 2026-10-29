/**
 * Flat tables for Looker Studio dashboards (rebuilt nightly and from the menu).
 * Only approved data (latest revision of each DPR) is included.
 */
function rebuildReports() {
  return withLock_(function () {
    var daily = [], curve = [];
    rows_('Projects').forEach(function (p) {
      var eff = effectiveDprs_(p.ProjectID);
      var byId = {};
      eff.forEach(function (d) { byId[d.DPRID] = d; });
      var people = {}, hours = {}, eqh = {};
      where_('Manpower', function (m) { return byId[m.DPRID]; }).forEach(function (m) {
        var k = iso_(byId[m.DPRID].ReportDate);
        people[k] = (people[k] || 0) + num_(m.Headcount);
        hours[k] = (hours[k] || 0) + num_(m.ManHours);
      });
      where_('EquipmentUse', function (e) { return byId[e.DPRID]; }).forEach(function (e) {
        var k = iso_(byId[e.DPRID].ReportDate);
        eqh[k] = (eqh[k] || 0) + num_(e.WorkingHours);
      });
      var progress = [];
      var seenDates = {};
      where_('DPR_Activities', function (l) { return byId[l.DPRID]; }).forEach(function (l) {
        var a = byKey_('Activities', l.ActivityID) || {};
        var date = iso_(byId[l.DPRID].ReportDate);
        var qty = l.QtyAccepted !== '' ? num_(l.QtyAccepted) : num_(l.QtyToday);
        var first = !seenDates[date];
        seenDates[date] = true;
        daily.push({ ProjectCode: p.Code, Date: date, ActivityCode: a.Code, Activity: a.Name, Unit: a.Unit, AcceptedQty: qty,
          Headcount: first ? people[date] || 0 : 0, ManHours: first ? round_(hours[date] || 0, 1) : 0,
          EquipmentHours: first ? round_(eqh[date] || 0, 1) : 0, DelayReason: l.DelayReason });
        progress.push({ activityId: l.ActivityID, date: date, qty: qty });
      });
      var acts = where_('Activities', function (a) { return a.ProjectID === p.ProjectID && a.Status !== 'Cancelled'; });
      var curveActs = acts.map(function (a) {
        return { id: a.ActivityID, weight: num_(a.Weight) || 1, start: iso_(a.BaselineStart) || iso_(a.PlannedStart) || null,
          finish: iso_(a.BaselineFinish) || iso_(a.PlannedFinish) || null, totalQty: num_(a.TotalQty) };
      });
      var starts = curveActs.map(function (c) { return c.start; }).filter(Boolean).sort();
      var ends = curveActs.map(function (c) { return c.finish; }).filter(Boolean).sort();
      if (!starts.length) return;
      var from = iso_(p.StartDate) || starts[0];
      var to = [ends[ends.length - 1], today_()].sort()[1];
      CDPR_ENGINE.sCurve(curveActs, progress, from, to, today_()).forEach(function (pt) {
        curve.push({ ProjectCode: p.Code, Date: pt.date, PlannedPct: pt.planned, ActualPct: pt.actual === null ? '' : pt.actual });
      });
    });
    replaceAll_('Report_Daily', daily);
    replaceAll_('Report_SCurve', curve);
    return { daily: daily.length, scurve: curve.length };
  });
}

function replaceAll_(name, objs) {
  var sh = sheet_(name);
  if (sh.getLastRow() > 1) sh.getRange(2, 1, sh.getLastRow() - 1, sh.getLastColumn()).clearContent();
  delete cache_[name];
  append_(name, objs);
}
