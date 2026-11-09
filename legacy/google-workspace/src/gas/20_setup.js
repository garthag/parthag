/**
 * One-click setup: creates every sheet with headers, formats and dropdowns,
 * the Drive root folder, default settings, the first Admin user (you) and
 * the automatic triggers. Safe to run again: existing sheets and data are kept.
 */
function setup() {
  withLock_(function () {
    var ss = ss_();
    Object.keys(SCHEMA).forEach(function (name) { ensureSheet_(ss, name); });
    var blank = ss.getSheetByName('Sheet1');
    if (blank && ss.getSheets().length > 1 && blank.getLastRow() === 0) ss.deleteSheet(blank);

    defaults_();
    var me = Session.getEffectiveUser().getEmail();
    if (me && !user_(me)) append_('Users', [{ Email: me, Name: me.split('@')[0], Role: 'Admin', Projects: 'ALL', Active: true }]);
    ensureRootFolder_();
    protectScriptSheets_(ss);
    installTriggers();
    audit_('Settings', '', 'setup', 'Spreadsheet structure verified');
  });
  return 'CONSTRUCT DPR is set up. Next: Extensions → AppSheet → Create an app (see docs/GOOGLE_WORKSPACE_EDITION.md).';
}

function ensureSheet_(ss, name) {
  var def = SCHEMA[name];
  var sh = ss.getSheetByName(name) || ss.insertSheet(name);
  var want = def.cols.map(function (c) { return c.n; });
  var have = sh.getLastColumn() ? sh.getRange(1, 1, 1, sh.getLastColumn()).getValues()[0].map(String) : [];
  // Add any missing columns at the end (never reorder or delete: AppSheet keys on names).
  var missing = want.filter(function (h) { return have.indexOf(h) < 0; });
  if (!have.length || have.every(function (h) { return h === ''; })) have = [];
  if (missing.length) {
    sh.getRange(1, have.length + 1, 1, missing.length).setValues([missing]);
    have = have.concat(missing);
  }
  sh.setFrozenRows(1);
  sh.getRange(1, 1, 1, have.length).setFontWeight('bold').setBackground('#0f3d5e').setFontColor('#ffffff');
  def.cols.forEach(function (c) {
    var col = have.indexOf(c.n) + 1;
    var body = sh.getRange(2, col, Math.max(sh.getMaxRows() - 1, 1), 1);
    if (c.t === 'Date') body.setNumberFormat('yyyy-mm-dd');
    else if (c.t === 'DateTime') body.setNumberFormat('yyyy-mm-dd hh:mm');
    if (c.enum) body.setDataValidation(SpreadsheetApp.newDataValidation().requireValueInList(c.enum, true).setAllowInvalid(false).build());
    if (c.by === 'script') sh.getRange(1, col).setBackground('#5b6b7a');
  });
  delete cache_[name];
  return sh;
}

function defaults_() {
  var d = {
    WORK_DAYS: ['1,2,3,4,5,6', 'Working weekdays, 0 = Sunday'],
    DATE_ORDER: ['DMY', 'How typed dates are read: DMY (India/UK) or MDY'],
    NOTIFY_EMAIL: ['Y', 'Send e-mails on submit / approve / reject (Y/N)'],
    PROCESS_EVERY_MINUTES: ['5', 'How often the automatic processor runs (1, 5, 10, 15 or 30)'],
    APPSHEET_FOLDER_ID: ['', 'Optional: Drive folder where AppSheet stores images, if not next to this spreadsheet'],
  };
  Object.keys(d).forEach(function (k) { if (setting_(k, null) === null) setSetting_(k, d[k][0], d[k][1]); });
}

/** Approval history, snapshots, baselines and audit can only be edited by the owner. */
function protectScriptSheets_(ss) {
  ['Approvals', 'Snapshots', 'Baselines', 'Audit', 'Settings', 'Report_Daily', 'Report_SCurve'].forEach(function (name) {
    var sh = ss.getSheetByName(name);
    if (!sh) return;
    var existing = sh.getProtections(SpreadsheetApp.ProtectionType.SHEET);
    if (existing.length) return;
    var p = sh.protect().setDescription('Managed by CONSTRUCT DPR script');
    p.removeEditors(p.getEditors());
    if (p.canDomainEdit()) p.setDomainEdit(false);
  });
}

function installTriggers() {
  ScriptApp.getProjectTriggers().forEach(function (t) {
    if (['processQueue', 'nightly'].indexOf(t.getHandlerFunction()) >= 0) ScriptApp.deleteTrigger(t);
  });
  var every = Number(setting_('PROCESS_EVERY_MINUTES', '5'));
  ScriptApp.newTrigger('processQueue').timeBased().everyMinutes([1, 5, 10, 15, 30].indexOf(every) >= 0 ? every : 5).create();
  ScriptApp.newTrigger('nightly').timeBased().atHour(1).everyDays(1).create();
}
