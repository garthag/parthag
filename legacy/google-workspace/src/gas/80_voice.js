/**
 * Voice / typed statements → proposed structured entry (QuickEntry).
 * The user dictates with the phone keyboard's microphone in AppSheet; the
 * script proposes activity, quantity, workers and equipment; nothing is added
 * to the report until the user reviews it and sets Status = "Confirmed".
 */
function proposeQuickEntry_(q) {
  var acts = where_('Activities', function (a) { return a.ProjectID === q.ProjectID && a.Status !== 'Cancelled'; })
    .map(function (a) { return { id: a.ActivityID, code: a.Code, name: a.Name, unit: a.Unit, location: a.Location }; });
  var p = CDPR_ENGINE.parseVoiceReport(String(q.Statement || ''), acts);
  update_('QuickEntry', q, {
    Status: 'Proposed',
    ProposedActivityID: p.activity ? p.activity.value.id : '',
    ProposedQty: p.quantity ? p.quantity.value : '',
    ProposedUnit: p.unit ? p.unit.value : '',
    ProposedWorkers: p.workers ? p.workers.value : '',
    ProposedEquipment: p.equipment.map(function (e) { return e.count + ' × ' + e.type; }).join('; '),
    Uncertain: p.uncertain.join(', '),
  });
}

function applyQuickEntry_(q) {
  var d = byKey_('DPR', q.DPRID);
  if (!d || ['Draft', 'Rejected'].indexOf(d.ProcessedStatus || 'Draft') < 0) {
    return update_('QuickEntry', q, { Status: 'Proposed', Uncertain: 'Report is no longer editable' });
  }
  if (!q.ProposedActivityID || q.ProposedQty === '') return update_('QuickEntry', q, { Status: 'Proposed', Uncertain: 'Choose the activity and quantity, then confirm again' });
  var existing = where_('DPR_Activities', function (l) { return l.DPRID === q.DPRID && l.ActivityID === q.ProposedActivityID; })[0];
  if (existing) update_('DPR_Activities', existing, { QtyToday: num_(existing.QtyToday) + num_(q.ProposedQty) });
  else append_('DPR_Activities', [{ DPRID: q.DPRID, ProjectID: q.ProjectID, ActivityID: q.ProposedActivityID, QtyToday: num_(q.ProposedQty), Remarks: 'From voice entry' }]);
  if (num_(q.ProposedWorkers) > 0) {
    append_('Manpower', [{ DPRID: q.DPRID, ProjectID: q.ProjectID, Trade: q.ProposedTrade || 'General', Skilled: num_(q.ProposedWorkers), HoursPerPerson: 8 }]);
  }
  String(q.ProposedEquipment || '').split(';').map(function (s) { return s.trim(); }).filter(String).forEach(function (s) {
    var m = /^(\d+)\s*×\s*(.+)$/.exec(s);
    if (!m) return;
    var eq = where_('Equipment', function (e) { return e.ProjectID === q.ProjectID && String(e.Type).toLowerCase().indexOf(m[2].toLowerCase()) >= 0; })[0];
    if (!eq) return audit_('QuickEntry', q.EntryID, 'equipment_not_in_register', m[2]);
    for (var i = 0; i < Number(m[1]); i++) append_('EquipmentUse', [{ DPRID: q.DPRID, ProjectID: q.ProjectID, EquipmentID: eq.EquipmentID }]);
  });
  update_('QuickEntry', q, { Status: 'Applied' });
}
