/**
 * Table access on top of SpreadsheetApp: rows as objects keyed by header.
 * Reads are cached per execution and invalidated on write.
 */
var cache_ = {};

function ss_() {
  return SpreadsheetApp.getActive();
}

function sheet_(name) {
  var sh = ss_().getSheetByName(name);
  if (!sh) throw new Error('Sheet "' + name + '" is missing. Run CONSTRUCT DPR → Setup.');
  return sh;
}

function headers_(name) {
  var sh = sheet_(name);
  return sh.getRange(1, 1, 1, sh.getLastColumn()).getValues()[0].map(String);
}

/** All rows of a table as objects; each object carries its sheet row number in _row. */
function rows_(name) {
  if (cache_[name]) return cache_[name];
  var sh = sheet_(name);
  var last = sh.getLastRow();
  var hdr = headers_(name);
  var out = [];
  if (last >= 2) {
    var values = sh.getRange(2, 1, last - 1, hdr.length).getValues();
    for (var i = 0; i < values.length; i++) {
      if (values[i].every(function (v) { return v === '' || v === null; })) continue;
      var o = { _row: i + 2 };
      for (var j = 0; j < hdr.length; j++) o[hdr[j]] = values[i][j];
      out.push(o);
    }
  }
  cache_[name] = out;
  return out;
}

function where_(name, pred) {
  return rows_(name).filter(pred);
}

function byKey_(name, id) {
  var k = keyOf_(name);
  var r = rows_(name).filter(function (x) { return String(x[k]) === String(id); });
  return r[0] || null;
}

/** Appends rows (objects); missing columns are left blank. Returns the objects. */
function append_(name, objs) {
  if (!objs.length) return objs;
  var sh = sheet_(name);
  var hdr = headers_(name);
  var k = keyOf_(name);
  var values = objs.map(function (o) {
    if (k && !o[k]) o[k] = uuid_();
    return hdr.map(function (h) { return o[h] === undefined || o[h] === null ? '' : o[h]; });
  });
  sh.getRange(sh.getLastRow() + 1, 1, values.length, hdr.length).setValues(values);
  delete cache_[name];
  return objs;
}

/** Writes the given fields of an existing row (object from rows_()). */
function update_(name, row, patch) {
  var sh = sheet_(name);
  var hdr = headers_(name);
  var changed = false;
  for (var f in patch) {
    var col = hdr.indexOf(f);
    if (col < 0) throw new Error('Unknown column ' + name + '.' + f);
    var v = patch[f] === undefined || patch[f] === null ? '' : patch[f];
    if (!same_(row[f], v)) {
      sh.getRange(row._row, col + 1).setValue(v);
      row[f] = v;
      changed = true;
    }
  }
  if (changed) delete cache_[name];
  return changed;
}

function same_(a, b) {
  if (a instanceof Date || b instanceof Date) return iso_(a) === iso_(b);
  return String(a === null || a === undefined ? '' : a) === String(b);
}

// ---------------------------------------------------------------- values
function num_(v) {
  if (v === '' || v === null || v === undefined) return 0;
  var n = typeof v === 'number' ? v : Number(String(v).replace(/,/g, ''));
  return isFinite(n) ? n : 0;
}

function round_(v, d) {
  var f = Math.pow(10, d === undefined ? 4 : d);
  return Math.round((v + Number.EPSILON) * f) / f;
}

function yes_(v) {
  return v === true || /^(y|yes|true|1)$/i.test(String(v));
}

/**
 * Normalises a date cell to YYYY-MM-DD. Accepts Date objects, ISO strings and
 * d/m/y or m/d/y strings (Settings DATE_ORDER, default DMY as used in India).
 */
function iso_(v) {
  if (v === '' || v === null || v === undefined) return '';
  if (v instanceof Date) return Utilities.formatDate(v, tz_(), 'yyyy-MM-dd');
  var s = String(v).trim();
  var m = /^(\d{4})-(\d{2})-(\d{2})/.exec(s);
  if (m) return m[1] + '-' + m[2] + '-' + m[3];
  m = /^(\d{1,2})[\/.-](\d{1,2})[\/.-](\d{4})/.exec(s);
  if (m) {
    var dmy = setting_('DATE_ORDER', 'DMY') !== 'MDY';
    var d = dmy ? m[1] : m[2], mo = dmy ? m[2] : m[1];
    return m[3] + '-' + ('0' + mo).slice(-2) + '-' + ('0' + d).slice(-2);
  }
  return s;
}

function tz_() {
  return Session.getScriptTimeZone() || 'Asia/Kolkata';
}

function now_() {
  return new Date();
}

function today_() {
  return Utilities.formatDate(new Date(), tz_(), 'yyyy-MM-dd');
}

function uuid_() {
  return Utilities.getUuid().replace(/-/g, '').slice(0, 12);
}

function sha256_(text) {
  var bytes = Utilities.computeDigest(Utilities.DigestAlgorithm.SHA_256, text, Utilities.Charset.UTF_8);
  return bytes.map(function (b) { return ('0' + (b & 0xff).toString(16)).slice(-2); }).join('');
}

// --------------------------------------------------------------- settings
function setting_(key, fallback) {
  var r = rows_('Settings').filter(function (x) { return x.Key === key; })[0];
  return r && r.Value !== '' ? String(r.Value) : fallback;
}

function setSetting_(key, value, notes) {
  var r = rows_('Settings').filter(function (x) { return x.Key === key; })[0];
  if (r) update_('Settings', r, { Value: value });
  else append_('Settings', [{ Key: key, Value: value, Notes: notes || '' }]);
}

// ------------------------------------------------------------ audit / log
function actor_() {
  try {
    return Session.getActiveUser().getEmail() || Session.getEffectiveUser().getEmail() || 'script';
  } catch (e) {
    return 'script';
  }
}

function audit_(table, rowId, action, details) {
  append_('Audit', [{ At: now_(), User: actor_(), Table: table, RowID: rowId, Action: action,
    Details: typeof details === 'string' ? details : JSON.stringify(details) }]);
}

function withLock_(fn) {
  var lock = LockService.getScriptLock();
  if (!lock.tryLock(30000)) throw new Error('Another update is running; try again in a minute.');
  try {
    cache_ = {};
    return fn();
  } finally {
    lock.releaseLock();
  }
}

function user_(email) {
  return rows_('Users').filter(function (u) { return String(u.Email).toLowerCase() === String(email || '').toLowerCase() && yes_(u.Active); })[0] || null;
}
