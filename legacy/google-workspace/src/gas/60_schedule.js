/**
 * Primavera-style scheduling with the same CPM engine as the web edition:
 * FS/SS/FF/SF links with lag/lead, working calendar + holidays, actual dates,
 * early dates, total float and critical path written back to Activities.
 */
function runSchedule_(projectId) {
  var project = byKey_('Projects', projectId);
  var acts = where_('Activities', function (a) { return a.ProjectID === projectId && a.Status !== 'Cancelled'; });
  if (!acts.length) return null;
  var deps = where_('Dependencies', function (x) { return x.ProjectID === projectId; });
  var holidays = where_('Holidays', function (x) { return x.ProjectID === projectId; }).map(function (x) { return iso_(x.Date); });
  var workDays = setting_('WORK_DAYS', '1,2,3,4,5,6').split(',').map(Number);
  var start = iso_(project.StartDate) || acts.map(function (a) { return iso_(a.PlannedStart); }).filter(String).sort()[0] || today_();
  var out = CDPR_ENGINE.schedule(
    acts.map(function (a) {
      return { id: a.ActivityID, code: a.Code, duration: num_(a.Duration), isMilestone: yes_(a.IsMilestone),
        actualStart: iso_(a.ActualStart) || null, actualFinish: iso_(a.ActualFinish) || null };
    }),
    deps.map(function (x) { return { predecessorId: x.PredecessorID, successorId: x.SuccessorID, type: x.Type || 'FS', lag: num_(x.Lag) }; }),
    { projectStart: start, dataDate: today_(), calendar: { workDays: workDays, holidays: holidays } });
  acts.forEach(function (a) {
    var r = out.activities.get(a.ActivityID);
    update_('Activities', a, { EarlyStart: r.earlyStart, EarlyFinish: r.earlyFinish, TotalFloat: r.totalFloat, Critical: r.critical });
  });
  audit_('Projects', projectId, 'schedule', 'Forecast finish ' + out.projectFinish + ', ' + out.criticalPath.length + ' critical activities');
  return out.projectFinish;
}

/** Copies the CPM early dates into the planned dates (the baseline is not touched). */
function applyPlan_(projectId) {
  where_('Activities', function (a) { return a.ProjectID === projectId && iso_(a.EarlyStart); }).forEach(function (a) {
    update_('Activities', a, { PlannedStart: iso_(a.EarlyStart), PlannedFinish: iso_(a.EarlyFinish) });
  });
}

/** Freezes the current planned dates as a new baseline revision (kept forever in Baselines). */
function freezeBaseline_(projectId) {
  var acts = where_('Activities', function (a) { return a.ProjectID === projectId && a.Status !== 'Cancelled'; });
  var missing = acts.filter(function (a) { return !iso_(a.PlannedStart) || !iso_(a.PlannedFinish); });
  if (missing.length) throw new Error('Planned dates missing for: ' + missing.map(function (a) { return a.Code; }).join(', ') + '. Run the schedule and "Apply schedule to plan" first.');
  var rev = where_('Baselines', function (b) { return b.ProjectID === projectId; }).reduce(function (m, b) { return Math.max(m, num_(b.Revision) + 1); }, 0);
  var at = now_();
  append_('Baselines', acts.map(function (a) {
    return { ProjectID: projectId, Revision: rev, FrozenAt: at, ActivityID: a.ActivityID, Start: iso_(a.PlannedStart), Finish: iso_(a.PlannedFinish), TotalQty: num_(a.TotalQty) };
  }));
  acts.forEach(function (a) { update_('Activities', a, { BaselineStart: iso_(a.PlannedStart), BaselineFinish: iso_(a.PlannedFinish) }); });
  audit_('Baselines', projectId, 'frozen', 'Revision ' + rev);
  return rev;
}
