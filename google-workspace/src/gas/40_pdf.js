/**
 * Approved DPR → Google Doc → PDF in 04_DPR/YYYY/MM/Approved PDFs.
 * Built only from the approval snapshot, so the PDF matches what was approved.
 */
function buildPdf_(doc, project) {
  var c = doc.content, x = doc.context, h = c.header;
  var title = h.DPRNo + ' Rev ' + h.Revision + ' — ' + h.ReportDate;
  var gdoc = DocumentApp.create(title);
  var body = gdoc.getBody();
  body.setMarginTop(36).setMarginBottom(36).setMarginLeft(36).setMarginRight(36);
  var H = DocumentApp.ParagraphHeading;
  var n = function (v, d) { return v === '' || v === null || v === undefined ? '—' : round_(num_(v), d === undefined ? 3 : d).toLocaleString('en-IN'); };
  var table = function (head, rows) {
    if (!rows.length) { body.appendParagraph('None recorded.').editAsText().setItalic(true); return; }
    var t = body.appendTable([head].concat(rows.map(function (r) { return r.map(function (v) { return v === null || v === undefined || v === '' ? '—' : String(v); }); })));
    t.editAsText().setFontSize(8);
    t.getRow(0).editAsText().setBold(true);
  };
  var section = function (no, name) { body.appendParagraph(no + '. ' + name).setHeading(H.HEADING2); };
  var para = function (label, text) { if (text) body.appendParagraph(label + ': ' + text).editAsText().setFontSize(9); };

  body.appendParagraph('Daily Progress Report').setHeading(H.TITLE);
  body.appendParagraph(x.project.Code + ' — ' + x.project.Name).setHeading(H.SUBTITLE);

  section(1, 'Project and contract');
  table(['Project', 'Client', 'Work order'], [[x.project.Code + ' ' + x.project.Name, x.project.Client, x.project.WorkOrderNo]]);
  section(2, 'Report');
  table(['DPR no.', 'Revision', 'Date', 'Shift', 'Area', 'Prepared by', 'Approved by', 'Status'],
    [[h.DPRNo, h.Revision, h.ReportDate, h.Shift, x.area, h.PreparedBy, h.ReviewedBy, x.status]]);
  if (x.revisionReason) para('Revision reason', x.revisionReason);

  section(3, 'Summary');
  var head = c.manpower.reduce(function (s, m) { return s + num_(m.Headcount); }, 0);
  para('Summary', c.activities.length + ' activities; ' + head + ' people; ' + c.equipment.length + ' equipment; ' +
    x.safety.length + ' safety records; ' + x.quality.length + ' QA/QC records.');
  para('Work completed', h.WorkCompleted);
  para('Achievements', h.Achievements);
  section(4, 'Weather and site conditions');
  para('Weather', h.Weather);
  para('Site conditions', h.SiteConditions);

  section(5, 'Activity progress (planned vs actual, quantities)');
  table(['Activity', 'Unit', 'Approved', 'Previous', 'Reported', 'Accepted', 'Cumulative', 'Balance', 'Plan %', 'Actual %', 'Var.'],
    c.activities.map(function (l) {
      var a = x.activities[l.ActivityID] || {};
      return [a.Code + ' ' + a.Name, a.Unit, n(a.TotalQty), n(l.PrevCum), n(l.QtyToday), n(l.QtyAccepted), n(l.CumQty), n(l.Balance),
        n(l.PlannedPct, 1), n(l.ActualPct, 1), n(l.Variance, 1)];
    }));
  var delays = c.activities.filter(function (l) { return l.DelayReason || l.OverrunReason; });
  if (delays.length) table(['Activity', 'Delay reason', 'Authorised overrun', 'Next-day target'], delays.map(function (l) {
    return [(x.activities[l.ActivityID] || {}).Code, l.DelayReason, l.OverrunReason, n(l.NextDayTarget)];
  }));

  section(6, 'Manpower');
  table(['Agency', 'Trade', 'Skilled', 'Semi', 'Unskilled', 'Sup.', 'Engg.', 'Safety', 'Other', 'Total', 'Man-h', 'Absent'],
    c.manpower.map(function (m) { return [x.contractors[m.ContractorID], m.Trade, m.Skilled, m.SemiSkilled, m.Unskilled, m.Supervisors, m.Engineers, m.SafetyStaff, m.Others, m.Headcount, n(m.ManHours, 1), m.Absent]; }));
  section(7, 'Equipment');
  table(['Equipment', 'Operator', 'Working h', 'Idle h', 'Breakdown h', 'Util. %', 'Breakdown reason'],
    c.equipment.map(function (e) { var q = x.equipment[e.EquipmentID] || {}; return [q.Code + ' ' + q.Type, e.Operator, n(e.WorkingHours, 1), n(e.IdleHours, 1), n(e.BreakdownHours, 1), n(e.UtilisationPct, 1), e.BreakdownReason]; }));
  section(8, 'Materials');
  table(['Material', 'Unit', 'Opening', 'Received', 'Rejected', 'Issued', 'Consumed', 'Returned', 'Closing', 'Challan', 'Heat/batch'],
    c.materials.map(function (m) { var q = x.materials[m.MaterialID] || {}; return [q.Code + ' ' + q.Description, q.Unit, n(m.Opening), n(m.Received), n(m.Rejected), n(m.Issued), n(m.Consumed), n(m.Returned), n(m.Closing), m.ChallanNo, m.HeatBatchNo]; }));

  section(9, 'Safety');
  para('Toolbox talks', h.ToolboxTalks);
  para('Permits issued', h.PermitsIssued);
  table(['Category', 'Permit', 'Description', 'Severity', 'Action', 'Responsible', 'Status'],
    x.safety.map(function (s) { return [s.Category, s.PermitType, s.Description, s.Severity, s.Action, s.Responsible, s.Status]; }));
  section(10, 'QA/QC');
  table(['Kind', 'Ref', 'Description', 'Hold point', 'Outcome', 'Responsible', 'Status'],
    x.quality.map(function (q) { return [q.Kind, q.RefNo, q.Description, yes_(q.HoldPoint) ? 'Yes' : 'No', q.Outcome, q.Responsible, q.Status]; }));
  section(11, 'Delays, constraints and decisions');
  para('Delays', h.DelayReasons);
  para('Constraints', h.ConstraintsText);
  para('Decisions required', h.DecisionsRequired);
  table(['Category', 'Constraint', 'Responsible', 'Needed by', 'Status'], x.constraints.map(function (k) { return [k.Category, k.Description, k.Responsible, k.TargetDate, k.Status]; }));
  section(12, "Tomorrow's plan");
  para('Planned work', h.TomorrowPlan);
  para('Resources required', h.ResourcesRequired);
  para('Work not completed', h.WorkNotCompleted);

  section(13, 'Photographs');
  var shown = 0;
  c.evidence.filter(function (e) { return e.Type === 'Photo' && e.Photo; }).slice(0, 20).forEach(function (e) {
    var file = resolveAppFile_(e.Photo);
    if (!file) { body.appendParagraph('Photo not found in Drive: ' + e.Photo).editAsText().setItalic(true); return; }
    var img = body.appendImage(file.getBlob());
    var w = img.getWidth(), hh = img.getHeight(), scale = Math.min(1, 420 / w);
    img.setWidth(Math.round(w * scale)).setHeight(Math.round(hh * scale));
    body.appendParagraph((e.Caption || file.getName()) + ' — ' + (e.CapturedBy || '') + ' ' + (e.CapturedAt || '')).editAsText().setFontSize(8);
    shown++;
  });
  if (!shown) body.appendParagraph('No photos attached.').editAsText().setItalic(true);

  section(14, 'Approval history and document control');
  table(['When', 'Action', 'By', 'From', 'To', 'Comment'], doc.history.map(function (r) { return [r.At, r.Action, r.By, r.FromStatus, r.ToStatus, r.Comment]; }));
  body.appendParagraph('DPR ID ' + h.DPRID + ' · revision ' + h.Revision + ' · generated ' + doc.generatedAt +
    ' · the SHA-256 of the approved data is recorded in the Snapshots sheet.').editAsText().setFontSize(7);

  gdoc.saveAndClose();
  var docFile = DriveApp.getFileById(gdoc.getId());
  var pdfBlob = docFile.getAs('application/pdf').setName(h.DPRNo + '_R' + h.Revision + '_' + h.ReportDate + '.pdf');
  var pdf = dprFolder_(project, h.ReportDate, 'Approved PDFs').createFile(pdfBlob);
  docFile.setTrashed(true);
  return pdf;
}

/** Shortcuts to the report's evidence inside the project's dated folders (no duplicate storage). */
function linkEvidence_(d, project, dateIso) {
  where_('Evidence', function (e) { return e.DPRID === d.DPRID; }).forEach(function (e) {
    var file = resolveAppFile_(e.Type === 'Photo' ? e.Photo : e.File);
    if (!file) return;
    var kind = e.Type === 'Photo' ? 'Photographs' : e.Type === 'Video' ? 'Videos' : e.Type === 'Voice note' ? 'Voice Notes' : 'Daily Reports';
    var hash = sha256Bytes_(file.getBlob().getBytes());
    if (!e.DriveFileID) {
      try { DriveApp.createShortcut(file.getId()).moveTo(dprFolder_(project, dateIso, kind)); } catch (err) { audit_('Evidence', e.EvidenceID, 'shortcut_failed', err.message); }
    }
    update_('Evidence', e, { DriveFileID: file.getId(), SHA256: hash });
  });
}

function sha256Bytes_(bytes) {
  return Utilities.computeDigest(Utilities.DigestAlgorithm.SHA_256, bytes)
    .map(function (b) { return ('0' + (b & 0xff).toString(16)).slice(-2); }).join('');
}

/** Menu: rebuild the PDF of a selected approved DPR (e.g. after a Drive error). */
function rebuildPdfFor_(dprId) {
  var d = byKey_('DPR', dprId);
  if (!d || d.ProcessedStatus !== 'Approved') throw new Error('Select an approved DPR row.');
  var project = byKey_('Projects', d.ProjectID);
  var pdf = buildPdf_(dprDocument_(d), project);
  update_('DPR', d, { PdfURL: pdf.getUrl() });
  var s = where_('Snapshots', function (x) { return x.DPRID === d.DPRID; })[0];
  if (s) update_('Snapshots', s, { PdfFileID: pdf.getId() });
  return pdf.getUrl();
}
