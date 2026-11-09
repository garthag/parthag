/**
 * DPR workflow. AppSheet actions only change DPR.Status (and set ReviewedBy /
 * ReviewComment). This processor — run every few minutes by a trigger, or on
 * demand from the menu — validates each change, applies the business rules,
 * and reverts anything that is not allowed, with the reason in DPR.Issues.
 *
 *   Draft → Submitted → Approved            (approved = frozen snapshot)
 *                     ↘ Rejected → Submitted …
 *   Approved → "Revision requested" → new Draft revision (original stays Approved)
 */
function processQueue() {
  return withLock_(function () {
    var done = { dpr: 0, quick: 0, boq: 0 };
    rows_('DPR').slice().forEach(function (d) {
      if (String(d.Status) !== String(d.ProcessedStatus)) { handleDpr_(d); done.dpr++; }
    });
    rows_('QuickEntry').slice().forEach(function (q) {
      if (q.Status === 'New') { proposeQuickEntry_(q); done.quick++; }
      else if (q.Status === 'Confirmed') { applyQuickEntry_(q); done.quick++; }
    });
    rows_('BOQRevisions').slice().forEach(function (r) {
      if (r.Status === 'Approved' && !r.AppliedAt) { applyBoqRevision_(r); done.boq++; }
    });
    if (done.dpr || done.boq) rows_('Projects').forEach(function (p) { recalcProject_(p.ProjectID); });
    return done;
  });
}

function handleDpr_(d) {
  var from = d.ProcessedStatus || 'Draft';
  var to = d.Status;
  var revert = function (msg) {
    update_('DPR', d, { Status: from, Issues: msg });
    log_(d, 'blocked', from, to, msg);
    notify_(d.PreparedBy, d, 'Action not accepted: ' + msg);
  };
  if (to === 'Draft') {
    update_('DPR', d, { ProcessedStatus: 'Draft' });
  } else if (to === 'Submitted') {
    if (['Draft', 'Rejected'].indexOf(from) < 0) return revert('Only draft or rejected reports can be submitted.');
    var v = validate_(d, false);
    if (v.errors.length) {
      update_('DPR', d, { Status: 'Draft', ProcessedStatus: 'Draft', Issues: 'Returned: ' + v.errors.join(' ') });
      log_(d, 'returned', 'Submitted', 'Draft', v.errors.join(' '));
      return notify_(d.PreparedBy, d, 'Your report was returned: ' + v.errors.join(' '));
    }
    if (!d.DPRNo) update_('DPR', d, { DPRNo: nextDprNo_(d.ProjectID) });
    computeLines_(d, false);
    update_('DPR', d, { ProcessedStatus: 'Submitted', SubmittedAt: now_(), Issues: v.warnings.join(' ') });
    log_(d, 'submit', from, 'Submitted', '');
    notifyApprovers_(d, 'DPR ' + d.DPRNo + ' (' + iso_(d.ReportDate) + ') is waiting for your review.');
  } else if (to === 'Approved') {
    if (from !== 'Submitted') return revert('Only submitted reports can be approved.');
    var who = user_(d.ReviewedBy);
    if (!who || APPROVER_ROLES.indexOf(who.Role) < 0) return revert('Approver ' + (d.ReviewedBy || '(unknown)') + ' does not have an approver role.');
    var project = byKey_('Projects', d.ProjectID);
    var self = String(d.ReviewedBy).toLowerCase() === String(d.PreparedBy).toLowerCase();
    if (self && !(project && yes_(project.AllowSelfApproval))) return revert('You cannot approve a report you prepared (project policy).');
    var a = validate_(d, true);
    if (a.errors.length) return revert(a.errors.join(' '));
    approve_(d, project);
  } else if (to === 'Rejected') {
    if (from !== 'Submitted') return revert('Only submitted reports can be rejected.');
    var r = user_(d.ReviewedBy);
    if (!r || APPROVER_ROLES.indexOf(r.Role) < 0) return revert('Reviewer does not have an approver role.');
    if (!String(d.ReviewComment || '').trim()) return revert('A reason (Review comment) is required to reject.');
    update_('DPR', d, { ProcessedStatus: 'Rejected', Issues: '' });
    log_(d, 'reject', from, 'Rejected', d.ReviewComment);
    notify_(d.PreparedBy, d, 'Your report was rejected: ' + d.ReviewComment);
  } else if (to === 'Revision requested') {
    if (from !== 'Approved') return revert('Only approved reports can be revised.');
    if (!String(d.RevisionReason || '').trim()) return revert('Enter a revision reason first.');
    var nd = startRevision_(d);
    update_('DPR', d, { Status: 'Approved', ProcessedStatus: 'Approved' });
    log_(d, 'revise', 'Approved', 'Approved', 'Revision ' + nd.Revision + ' started: ' + d.RevisionReason);
  } else {
    revert('Unknown status ' + to);
  }
}

// ----------------------------------------------------------------- checks
function validate_(d, approving) {
  var errors = [], warnings = [];
  if (!iso_(d.ReportDate)) errors.push('Report date is missing.');
  if (iso_(d.ReportDate) > today_()) errors.push('Report date is in the future.');
  var lines = where_('DPR_Activities', function (l) { return l.DPRID === d.DPRID; });
  if (!lines.length && !String(d.WorkCompleted || '').trim()) errors.push('Add at least one activity or describe the work completed.');
  if (!approving) {
    var dup = where_('DPR', function (x) {
      return x.DPRID !== d.DPRID && x.ProjectID === d.ProjectID && num_(x.Revision) === 0 && num_(d.Revision) === 0 &&
        iso_(x.ReportDate) === iso_(d.ReportDate) && x.Shift === d.Shift && String(x.AreaID) === String(d.AreaID) &&
        ['Submitted', 'Approved'].indexOf(x.ProcessedStatus) >= 0;
    });
    if (dup.length) errors.push('A report for this date, shift and area already exists (' + (dup[0].DPRNo || dup[0].DPRID) + ').');
  }
  lines.forEach(function (l) {
    var a = byKey_('Activities', l.ActivityID);
    if (!a || a.ProjectID !== d.ProjectID) { errors.push('A line refers to an activity outside this project.'); return; }
    var qty = approving && l.QtyAccepted !== '' ? num_(l.QtyAccepted) : num_(l.QtyToday);
    if (qty < 0) errors.push(a.Code + ': quantity cannot be negative.');
    var cum = priorAccepted_(d, l.ActivityID) + qty;
    var total = num_(a.TotalQty);
    if (cum > total + 1e-9 && !yes_(l.OverrunAuthorised)) {
      errors.push(a.Code + ': cumulative ' + round_(cum) + ' exceeds approved ' + total + ' ' + (a.Unit || '') + ' — record an authorised overrun with a reason.');
    }
    if (yes_(l.OverrunAuthorised) && !String(l.OverrunReason || '').trim()) errors.push(a.Code + ': overrun reason is required.');
  });
  where_('EquipmentUse', function (e) { return e.DPRID === d.DPRID; }).forEach(function (e) {
    var eq = byKey_('Equipment', e.EquipmentID) || {};
    var hrs = num_(e.WorkingHours) + num_(e.IdleHours) + num_(e.BreakdownHours);
    if (hrs > 24) errors.push((eq.Code || 'Equipment') + ': hours exceed 24.');
    if (num_(e.BreakdownHours) > 0 && !String(e.BreakdownReason || '').trim()) errors.push((eq.Code || 'Equipment') + ': breakdown reason required.');
    if (eq.CertExpiry && iso_(eq.CertExpiry) < iso_(d.ReportDate)) warnings.push(eq.Code + ': certificate expired ' + iso_(eq.CertExpiry) + '.');
  });
  where_('MaterialMoves', function (m) { return m.DPRID === d.DPRID; }).forEach(function (m) {
    var mat = byKey_('Materials', m.MaterialID) || {};
    if (num_(m.Rejected) > num_(m.Received)) errors.push((mat.Code || 'Material') + ': rejected exceeds received.');
    var closing = stockBefore_(d, m.MaterialID) + num_(m.Received) + num_(m.Returned) - num_(m.Issued) - num_(m.Rejected);
    // Earlier receipts may still be awaiting approval when this report is
    // submitted, so negative stock only blocks the approval itself.
    if (closing < -1e-9) (approving ? errors : warnings).push((mat.Code || 'Material') + ': issue exceeds approved stock (closing would be ' + round_(closing) + ')' + (approving ? '.' : ' — approve earlier reports first.'));
  });
  if (!String(d.Weather || '').trim()) warnings.push('Weather not recorded.');
  return { errors: errors, warnings: warnings };
}

// ------------------------------------------------- effective (approved) data
/** Latest approved revision for each DPR number. */
function effectiveDprs_(projectId) {
  var best = {};
  rows_('DPR').forEach(function (d) {
    if (d.ProjectID !== projectId || d.ProcessedStatus !== 'Approved' || !d.DPRNo) return;
    if (!best[d.DPRNo] || num_(d.Revision) > num_(best[d.DPRNo].Revision)) best[d.DPRNo] = d;
  });
  return Object.keys(best).map(function (k) { return best[k]; });
}

function isBefore_(a, b) {
  var da = iso_(a.ReportDate), db = iso_(b.ReportDate);
  if (da !== db) return da < db;
  return String(a.DPRNo || '') < String(b.DPRNo || '￿');
}

function priorAccepted_(d, activityId) {
  var ids = {};
  effectiveDprs_(d.ProjectID).forEach(function (x) { if (x.DPRNo !== d.DPRNo && isBefore_(x, d)) ids[x.DPRID] = true; });
  return where_('DPR_Activities', function (l) { return ids[l.DPRID] && l.ActivityID === activityId; })
    .reduce(function (s, l) { return s + (l.QtyAccepted !== '' ? num_(l.QtyAccepted) : num_(l.QtyToday)); }, 0);
}

function stockBefore_(d, materialId) {
  var ids = {};
  effectiveDprs_(d.ProjectID).forEach(function (x) { if (x.DPRNo !== d.DPRNo && isBefore_(x, d)) ids[x.DPRID] = true; });
  return where_('MaterialMoves', function (m) { return ids[m.DPRID] && m.MaterialID === materialId; })
    .reduce(function (s, m) { return s + num_(m.Received) + num_(m.Returned) - num_(m.Issued) - num_(m.Rejected); }, 0);
}

function plannedPct_(a, dateIso) {
  var s = iso_(a.BaselineStart) || iso_(a.PlannedStart), f = iso_(a.BaselineFinish) || iso_(a.PlannedFinish);
  if (!s || !f) return '';
  if (dateIso < s) return 0;
  if (dateIso >= f) return 100;
  var days = CDPR_ENGINE.daysBetween(s, f) + 1;
  return round_((CDPR_ENGINE.daysBetween(s, dateIso) + 1) * 100 / days, 2);
}

/** Fills the calculated columns of a DPR's lines. */
function computeLines_(d, approving) {
  var date = iso_(d.ReportDate);
  where_('DPR_Activities', function (l) { return l.DPRID === d.DPRID; }).forEach(function (l) {
    var a = byKey_('Activities', l.ActivityID);
    var total = num_(a.TotalQty);
    var qty = approving && l.QtyAccepted !== '' ? num_(l.QtyAccepted) : num_(l.QtyToday);
    var prev = priorAccepted_(d, l.ActivityID);
    var cum = prev + qty;
    var pct = total > 0 ? round_(cum * 100 / total, 2) : '';
    var plan = plannedPct_(a, date);
    var patch = { PrevCum: round_(prev), CumQty: round_(cum), Balance: round_(total - cum), ActualPct: pct, PlannedPct: plan,
      Variance: pct === '' || plan === '' ? '' : round_(pct - plan, 2) };
    if (approving && l.QtyAccepted === '') patch.QtyAccepted = num_(l.QtyToday);
    update_('DPR_Activities', l, patch);
  });
  where_('Manpower', function (m) { return m.DPRID === d.DPRID; }).forEach(function (m) {
    var head = ['Skilled', 'SemiSkilled', 'Unskilled', 'Supervisors', 'Engineers', 'SafetyStaff', 'Others']
      .reduce(function (s, k) { return s + num_(m[k]); }, 0);
    update_('Manpower', m, { Headcount: head, ManHours: round_(head * (m.HoursPerPerson === '' ? 8 : num_(m.HoursPerPerson)) + num_(m.OvertimeHours), 2) });
  });
  where_('EquipmentUse', function (e) { return e.DPRID === d.DPRID; }).forEach(function (e) {
    var t = num_(e.WorkingHours) + num_(e.IdleHours) + num_(e.BreakdownHours);
    update_('EquipmentUse', e, { UtilisationPct: t ? round_(num_(e.WorkingHours) * 100 / t, 1) : '' });
  });
  where_('MaterialMoves', function (m) { return m.DPRID === d.DPRID; }).forEach(function (m) {
    var open = stockBefore_(d, m.MaterialID);
    update_('MaterialMoves', m, { Opening: round_(open), Closing: round_(open + num_(m.Received) + num_(m.Returned) - num_(m.Issued) - num_(m.Rejected)) });
  });
}

function nextDprNo_(projectId) {
  var p = byKey_('Projects', projectId);
  var max = 0;
  rows_('DPR').forEach(function (d) {
    if (d.ProjectID !== projectId || !d.DPRNo) return;
    var n = Number(String(d.DPRNo).split('-').pop());
    if (n > max) max = n;
  });
  return (p ? p.Code : 'DPR') + '-DPR-' + ('000' + (max + 1)).slice(-4);
}

// --------------------------------------------------------------- approval
function approve_(d, project) {
  computeLines_(d, true);
  var date = iso_(d.ReportDate);
  where_('DPR_Activities', function (l) { return l.DPRID === d.DPRID; }).forEach(function (l) {
    var a = byKey_('Activities', l.ActivityID);
    var patch = {};
    if (num_(l.QtyAccepted) > 0 && !iso_(a.ActualStart)) patch.ActualStart = date;
    if (num_(a.TotalQty) > 0 && num_(l.CumQty) >= num_(a.TotalQty)) { patch.Status = 'Completed'; if (!iso_(a.ActualFinish)) patch.ActualFinish = date; }
    else if (num_(l.QtyAccepted) > 0 && (a.Status === 'Not started' || !a.Status)) patch.Status = 'In progress';
    update_('Activities', a, patch);
  });
  update_('DPR', d, { ProcessedStatus: 'Approved', ApprovedAt: now_(), Issues: '' });
  log_(d, 'approve', 'Submitted', 'Approved', d.ReviewComment || '');

  var doc = dprDocument_(d);
  var json = JSON.stringify(doc.content);
  var hash = sha256_(json);
  var snap = { DPRID: d.DPRID, DPRNo: d.DPRNo, Revision: num_(d.Revision), ApprovedAt: now_(), ApprovedBy: d.ReviewedBy, SHA256: hash, IntegrityOK: true };
  try {
    var jsonFile = dprFolder_(project, date, 'Daily Reports').createFile(
      Utilities.newBlob(JSON.stringify(doc, null, 2), 'application/json', d.DPRNo + '_R' + num_(d.Revision) + '_' + date + '.json'));
    var pdf = buildPdf_(doc, project);
    snap.JsonFileID = jsonFile.getId();
    snap.PdfFileID = pdf.getId();
    update_('DPR', d, { PdfURL: pdf.getUrl(), SnapshotHash: hash });
    linkEvidence_(d, project, date);
  } catch (e) {
    update_('DPR', d, { SnapshotHash: hash, Issues: 'Approved, but the PDF/Drive step failed: ' + e.message + ' — use menu "Rebuild PDF".' });
    audit_('DPR', d.DPRID, 'pdf_failed', e.message);
  }
  append_('Snapshots', [snap]);
  notify_(d.PreparedBy, d, 'Your report ' + d.DPRNo + ' was approved.');
}

/**
 * The approved report. `content` (hashed) holds only what the report itself
 * recorded; registers (safety/quality/constraints) keep their own lifecycle
 * after approval and names/codes come from masters, so both sit outside the
 * hash and are printed from `context` as at approval time.
 */
function dprDocument_(d) {
  var by = function (t) { return where_(t, function (x) { return x.DPRID === d.DPRID; }).map(function (r) { return clean_(r, t); }); };
  var header = clean_(d, 'DPR');
  ['Status', 'ProcessedStatus', 'Issues', 'PdfURL', 'SnapshotHash', 'RevisionReason', 'ApprovedAt', 'ReviewComment'].forEach(function (k) { delete header[k]; });
  var content = {
    schema: 'construct-dpr-gws/1', header: header, activities: by('DPR_Activities'), manpower: by('Manpower'),
    equipment: by('EquipmentUse'), materials: by('MaterialMoves'),
    evidence: by('Evidence').map(function (e) { delete e.DriveFileID; delete e.SHA256; return e; }),
  };
  var project = byKey_('Projects', d.ProjectID) || {};
  var names = function (t, id, fields) {
    var r = byKey_(t, id) || {};
    var o = {};
    fields.forEach(function (f) { o[f] = r[f] === undefined ? '' : (r[f] instanceof Date ? iso_(r[f]) : r[f]); });
    return o;
  };
  var context = {
    project: { Code: project.Code, Name: project.Name, Client: project.Client, WorkOrderNo: project.WorkOrderNo, Currency: project.Currency },
    status: d.Status, reviewComment: d.ReviewComment, revisionReason: num_(d.Revision) ? d.RevisionReason : '',
    area: (byKey_('Areas', d.AreaID) || {}).Name || 'Whole project',
    activities: {}, contractors: {}, equipment: {}, materials: {},
    safety: by('Safety'), quality: by('Quality'), constraints: by('Constraints'),
  };
  content.activities.forEach(function (l) { context.activities[l.ActivityID] = names('Activities', l.ActivityID, ['Code', 'Name', 'Unit', 'TotalQty']); });
  content.manpower.forEach(function (m) { context.contractors[m.ContractorID] = (byKey_('Contractors', m.ContractorID) || {}).Name || ''; });
  content.equipment.forEach(function (e) { context.equipment[e.EquipmentID] = names('Equipment', e.EquipmentID, ['Code', 'Type', 'Capacity']); });
  content.materials.forEach(function (m) { context.materials[m.MaterialID] = names('Materials', m.MaterialID, ['Code', 'Description', 'Unit']); });
  return { content: content, context: context, history: where_('Approvals', function (x) { return x.DPRID === d.DPRID; }).map(function (r) { return clean_(r, 'Approvals'); }), generatedAt: new Date().toISOString() };
}

/** Plain JSON copy of a row: dates as YYYY-MM-DD, date-times as ISO, numbers as numbers. */
function clean_(row, table) {
  var types = {};
  ((SCHEMA[table] || {}).cols || []).forEach(function (c) { types[c.n] = c.t; });
  var o = {};
  for (var k in row) {
    if (k === '_row') continue;
    var v = row[k];
    if (types[k] === 'Date') v = iso_(v);
    else if (types[k] === 'DateTime') v = v instanceof Date ? v.toISOString() : String(v);
    else if (v instanceof Date) v = iso_(v);
    else if (['Decimal', 'Number', 'Percent', 'Price'].indexOf(types[k]) >= 0) v = v === '' ? '' : num_(v);
    o[k] = v;
  }
  return o;
}

function startRevision_(d) {
  var rev = num_(d.Revision) + 1;
  var copy = clean_(d, 'DPR');
  delete copy.DPRID;
  Object.assign(copy, { Revision: rev, PreviousDPRID: d.DPRID, RevisionReason: d.RevisionReason, Status: 'Draft', ProcessedStatus: 'Draft',
    ReviewedBy: '', ReviewComment: '', SubmittedAt: '', ApprovedAt: '', Issues: 'Revision ' + rev + ': ' + d.RevisionReason, PdfURL: '', SnapshotHash: '',
    PreparedBy: actor_() === 'script' ? d.PreparedBy : actor_() });
  var nd = append_('DPR', [copy])[0];
  ['DPR_Activities', 'Manpower', 'EquipmentUse', 'MaterialMoves', 'Evidence'].forEach(function (t) {
    var key = keyOf_(t);
    var children = where_(t, function (x) { return x.DPRID === d.DPRID; }).map(function (x) {
      var c = clean_(x, t);
      delete c[key];
      c.DPRID = nd.DPRID;
      if (t === 'DPR_Activities') c.QtyAccepted = '';
      return c;
    });
    append_(t, children);
  });
  return nd;
}

// ------------------------------------------------------------ recalculation
function recalcProject_(projectId) {
  var eff = {};
  effectiveDprs_(projectId).forEach(function (d) { eff[d.DPRID] = true; });
  var accepted = {};
  where_('DPR_Activities', function (l) { return eff[l.DPRID]; }).forEach(function (l) {
    accepted[l.ActivityID] = (accepted[l.ActivityID] || 0) + (l.QtyAccepted !== '' ? num_(l.QtyAccepted) : num_(l.QtyToday));
  });
  var byBoq = {};
  where_('Activities', function (a) { return a.ProjectID === projectId; }).forEach(function (a) {
    var cum = accepted[a.ActivityID] || 0;
    update_('Activities', a, { AcceptedCum: round_(cum), ProgressPct: num_(a.TotalQty) > 0 ? round_(cum * 100 / num_(a.TotalQty), 2) : '' });
    if (a.BOQID) byBoq[a.BOQID] = (byBoq[a.BOQID] || 0) + cum;
  });
  where_('BOQ', function (b) { return b.ProjectID === projectId; }).forEach(function (b) {
    var qty = b.RevisedQty !== '' ? num_(b.RevisedQty) : num_(b.TenderQty);
    var exec = byBoq[b.BOQID] || 0;
    update_('BOQ', b, { ExecutedQty: round_(exec), ContractAmount: round_(qty * num_(b.Rate), 2),
      ExecutedAmount: round_(exec * num_(b.Rate), 2), BilledAmount: round_(num_(b.BilledQty) * num_(b.Rate), 2) });
  });
  var stock = {};
  where_('MaterialMoves', function (m) { return eff[m.DPRID]; }).forEach(function (m) {
    stock[m.MaterialID] = (stock[m.MaterialID] || 0) + num_(m.Received) + num_(m.Returned) - num_(m.Issued) - num_(m.Rejected);
  });
  where_('Materials', function (m) { return m.ProjectID === projectId; }).forEach(function (m) {
    var s = round_(stock[m.MaterialID] || 0);
    update_('Materials', m, { Stock: s, BelowReorder: m.ReorderLevel !== '' && s < num_(m.ReorderLevel) });
  });
}

function applyBoqRevision_(r) {
  var b = byKey_('BOQ', r.BOQID);
  var approver = user_(r.ApprovedBy);
  if (!b || !approver || ['Admin', 'Project Manager'].indexOf(approver.Role) < 0 || !String(r.Reason || '').trim()) {
    update_('BOQRevisions', r, { Status: 'Rejected' });
    return audit_('BOQRevisions', r.RevisionID, 'rejected', 'Needs a reason and approval by Admin or Project Manager');
  }
  update_('BOQRevisions', r, { OldQty: b.RevisedQty !== '' ? num_(b.RevisedQty) : num_(b.TenderQty), OldRate: num_(b.Rate), AppliedAt: now_() });
  update_('BOQ', b, { RevisedQty: num_(r.NewQty), Rate: num_(r.NewRate) });
  audit_('BOQ', b.BOQID, 'revised', { qty: r.NewQty, rate: r.NewRate, reason: r.Reason, by: r.ApprovedBy });
}

/** Nightly: confirms approved data still matches its snapshot checksum. */
function integrityCheck() {
  return withLock_(function () {
    var bad = [];
    rows_('Snapshots').forEach(function (s) {
      var d = byKey_('DPR', s.DPRID);
      var ok = !!d && sha256_(JSON.stringify(dprDocument_(d).content)) === s.SHA256;
      update_('Snapshots', s, { IntegrityOK: ok });
      if (!ok) bad.push(s.DPRNo + ' R' + s.Revision);
    });
    if (bad.length) {
      audit_('Snapshots', '', 'integrity_failed', bad.join(', '));
      admins_().forEach(function (e) { mail_(e, 'CONSTRUCT DPR: approved data changed', 'These approved reports no longer match their snapshots: ' + bad.join(', ') + '. The approved PDF/JSON in Drive is the authoritative copy.'); });
    }
    return bad;
  });
}

// -------------------------------------------------------- logging / e-mail
function log_(d, action, from, to, comment) {
  append_('Approvals', [{ DPRID: d.DPRID, Action: action, By: action === 'submit' ? d.PreparedBy : (d.ReviewedBy || actor_()),
    At: now_(), FromStatus: from, ToStatus: to, Comment: comment || '' }]);
}

function admins_() {
  return rows_('Users').filter(function (u) { return u.Role === 'Admin' && yes_(u.Active); }).map(function (u) { return u.Email; });
}

function notifyApprovers_(d, text) {
  rows_('Users').filter(function (u) {
    return yes_(u.Active) && APPROVER_ROLES.indexOf(u.Role) >= 0 &&
      (String(u.Projects).toUpperCase() === 'ALL' || String(u.Projects).split(',').map(function (s) { return s.trim(); }).indexOf(d.ProjectID) >= 0);
  }).forEach(function (u) { notify_(u.Email, d, text); });
}

function notify_(email, d, text) {
  if (!email) return;
  mail_(email, 'CONSTRUCT DPR ' + (d.DPRNo || '') + ' — ' + iso_(d.ReportDate), text);
}

function mail_(to, subject, body) {
  if (setting_('NOTIFY_EMAIL', 'Y') !== 'Y') return;
  try { MailApp.sendEmail(to, subject, body); } catch (e) { audit_('Mail', '', 'failed', e.message); }
}
