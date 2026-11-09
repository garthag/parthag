/** Spreadsheet menu, nightly job and the demo project. */
function onOpen() {
  SpreadsheetApp.getUi().createMenu('CONSTRUCT DPR')
    .addItem('1. Setup / repair sheets', 'menuSetup')
    .addItem('2. Load demo project', 'menuDemo')
    .addSeparator()
    .addItem('Process submissions & approvals now', 'menuProcess')
    .addItem('Create project folders in Drive…', 'menuFolders')
    .addItem('Run schedule (CPM)…', 'menuSchedule')
    .addItem('Apply schedule to planned dates…', 'menuApplyPlan')
    .addItem('Freeze baseline…', 'menuBaseline')
    .addItem('Rebuild PDF of selected DPR row', 'menuRebuildPdf')
    .addSeparator()
    .addItem('Rebuild dashboard tables', 'menuReports')
    .addItem('Check integrity of approved reports', 'menuIntegrity')
    .addToUi();
}

function alert_(msg) {
  try { SpreadsheetApp.getUi().alert(msg); } catch (e) { Logger.log(msg); }
}

function askProject_() {
  var ui = SpreadsheetApp.getUi();
  var res = ui.prompt('Project code', 'Enter the project code (e.g. DEMO-BF2):', ui.ButtonSet.OK_CANCEL);
  if (res.getSelectedButton() !== ui.Button.OK) return null;
  var p = rows_('Projects').filter(function (x) { return String(x.Code).toUpperCase() === res.getResponseText().trim().toUpperCase(); })[0];
  if (!p) alert_('No project with that code.');
  return p || null;
}

function menuSetup() { alert_(setup()); }
function menuDemo() { alert_('Demo project created: ' + loadDemo() + '. Open the DPR sheet to see seven approved reports.'); }
function menuProcess() { var r = processQueue(); alert_('Processed ' + r.dpr + ' report change(s), ' + r.quick + ' quick entr(ies), ' + r.boq + ' BOQ revision(s).'); }
function menuFolders() { var p = askProject_(); if (p) alert_('Folders ready: ' + withLock_(function () { return createProjectFolders_(p).getUrl(); })); }
function menuSchedule() { var p = askProject_(); if (p) alert_('Forecast finish: ' + withLock_(function () { return runSchedule_(p.ProjectID); })); }
function menuApplyPlan() { var p = askProject_(); if (p) { withLock_(function () { applyPlan_(p.ProjectID); }); alert_('Planned dates updated from the schedule. The baseline was not changed.'); } }
function menuBaseline() { var p = askProject_(); if (p) alert_('Baseline revision ' + withLock_(function () { return freezeBaseline_(p.ProjectID); }) + ' frozen.'); }
function menuReports() { var r = rebuildReports(); alert_('Dashboard tables rebuilt: ' + r.daily + ' daily rows, ' + r.scurve + ' S-curve points.'); }
function menuIntegrity() { var bad = integrityCheck(); alert_(bad.length ? 'Changed after approval: ' + bad.join(', ') : 'All approved reports match their snapshots.'); }
function menuRebuildPdf() {
  var sh = SpreadsheetApp.getActiveSheet();
  if (sh.getName() !== 'DPR') return alert_('Select a row on the DPR sheet first.');
  var id = sh.getRange(sh.getActiveRange().getRow(), headers_('DPR').indexOf('DPRID') + 1).getValue();
  alert_('PDF: ' + withLock_(function () { return rebuildPdfFor_(id); }));
}

/** Every night: reschedule, refresh dashboards, verify approved reports. */
function nightly() {
  withLock_(function () { rows_('Projects').forEach(function (p) { if (p.Status !== 'Closed') runSchedule_(p.ProjectID); }); });
  rebuildReports();
  integrityCheck();
}

/** Sample industrial project, created and approved through the real workflow. */
function loadDemo() {
  var me = Session.getEffectiveUser().getEmail();
  var demo = withLock_(function () {
    var start = CDPR_ENGINE.addDays(today_(), -21);
    var p = append_('Projects', [{ Code: 'DEMO-BF2', Name: 'Blast Furnace #2 — Skip Bridge (Demo)', Client: 'Demo Steel Ltd',
      MainContractor: 'Demo Industrial Constructions', WorkOrderNo: 'WO/BF2/017', StartDate: start, CompletionDate: CDPR_ENGINE.addDays(start, 180),
      Currency: 'INR', Timezone: 'Asia/Kolkata', AllowSelfApproval: true, Status: 'Active' }])[0];
    var id = p.ProjectID;
    var area = append_('Areas', [{ ProjectID: id, Code: 'SKB', Name: 'Skip Bridge', Kind: 'Area' }])[0];
    var w = append_('WBS', [{ ProjectID: id, Code: '1', Name: 'Civil' }, { ProjectID: id, Code: '2', Name: 'Structural steel' }]);
    var civ = append_('Contractors', [{ Name: 'Bharat Civil Works', Kind: 'Subcontractor' }, { Name: 'Sterling Steel Erectors', Kind: 'Subcontractor' }]);
    var b = append_('BOQ', [
      { ProjectID: id, ItemNo: 'C-01', Description: 'Excavation', Unit: 'm³', TenderQty: 1200, Rate: 350 },
      { ProjectID: id, ItemNo: 'S-02', Description: 'Erection of structural steel', Unit: 'MT', TenderQty: 450, Rate: 18000 }]);
    var a = append_('Activities', [
      { ProjectID: id, WBSID: w[0].WBSID, AreaID: area.AreaID, BOQID: b[0].BOQID, ContractorID: civ[0].ContractorID, Code: 'A1000', Name: 'Excavation — skip bridge foundations', Unit: 'm³', TotalQty: 1200, Weight: 4, Duration: 12, Status: 'Not started', Location: 'Grid A–D' },
      { ProjectID: id, WBSID: w[1].WBSID, AreaID: area.AreaID, BOQID: b[1].BOQID, ContractorID: civ[1].ContractorID, Code: 'A2010', Name: 'Erection — skip bridge structure', Unit: 'MT', TotalQty: 300, Weight: 15, Duration: 30, Status: 'Not started', Location: 'Skip bridge' },
      { ProjectID: id, WBSID: w[1].WBSID, Code: 'M9000', Name: 'Structure complete', Duration: 0, IsMilestone: true, TotalQty: 0, Weight: 0, Status: 'Not started' }]);
    append_('Dependencies', [{ ProjectID: id, PredecessorID: a[0].ActivityID, SuccessorID: a[1].ActivityID, Type: 'SS', Lag: 5 },
      { ProjectID: id, PredecessorID: a[1].ActivityID, SuccessorID: a[2].ActivityID, Type: 'FS', Lag: 0 }]);
    var eq = append_('Equipment', [{ ProjectID: id, Code: 'CR-01', Type: 'Mobile crane', Capacity: '100 T', CertExpiry: CDPR_ENGINE.addDays(today_(), 120) }])[0];
    var mat = append_('Materials', [{ ProjectID: id, Code: 'CEM-OPC53', Description: 'Cement OPC 53', Unit: 'bags', ReorderLevel: 300 }])[0];
    runSchedule_(id);
    applyPlan_(id);
    freezeBaseline_(id);
    return { id: id, area: area, a: a, civ: civ, eq: eq, mat: mat };
  });
  // Seven days of reports, each submitted and then approved, as on a real site.
  for (var i = 0; i < 7; i++) {
    var dprId = withLock_(function () {
      var m = demo;
      var dpr = append_('DPR', [{ ProjectID: m.id, Revision: 0, ReportDate: CDPR_ENGINE.addDays(today_(), i - 7), Shift: 'Day', AreaID: m.area.AreaID,
        Weather: i === 3 ? 'Rain 10:00–12:00' : 'Clear', PreparedBy: me, Status: 'Submitted', ToolboxTalks: 1,
        WorkCompleted: 'Excavation and steel erection progressed.', TomorrowPlan: 'Continue as per look-ahead.' }])[0];
      append_('DPR_Activities', [{ DPRID: dpr.DPRID, ProjectID: m.id, ActivityID: m.a[0].ActivityID, QtyToday: i === 3 ? 40 : 85, DelayReason: i === 3 ? 'Rain' : '' },
        { DPRID: dpr.DPRID, ProjectID: m.id, ActivityID: m.a[1].ActivityID, QtyToday: i < 3 ? 0 : 9 }]);
      append_('Manpower', [{ DPRID: dpr.DPRID, ProjectID: m.id, ContractorID: m.civ[0].ContractorID, Trade: 'Civil', Skilled: 18, Unskilled: 30, Supervisors: 2, HoursPerPerson: 8 },
        { DPRID: dpr.DPRID, ProjectID: m.id, ContractorID: m.civ[1].ContractorID, Trade: 'Structural', Skilled: 20 + i, Unskilled: 8, HoursPerPerson: 8, OvertimeHours: 10 }]);
      append_('EquipmentUse', [{ DPRID: dpr.DPRID, ProjectID: m.id, EquipmentID: m.eq.EquipmentID, Operator: 'R. Kumar', WorkingHours: 7, IdleHours: 2, BreakdownHours: 0 }]);
      append_('MaterialMoves', [{ DPRID: dpr.DPRID, ProjectID: m.id, MaterialID: m.mat.MaterialID, Received: i % 2 ? 0 : 600, Issued: 220, Consumed: 210, Inspection: 'Accepted', ChallanNo: 'CH-' + (5100 + i) }]);
      return dpr.DPRID;
    });
    processQueue(); // submission checks, DPR number
    withLock_(function () { update_('DPR', byKey_('DPR', dprId), { Status: 'Approved', ReviewedBy: me, ReviewComment: 'Demo approval' }); });
    processQueue(); // approval → snapshot, PDF
  }
  rebuildReports();
  return 'DEMO-BF2';
}
