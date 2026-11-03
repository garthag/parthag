/**
 * Mail Attachment Agent - Google Apps Script edition.
 *
 * Runs inside your own Google account: no passwords, no App Passwords, nothing installed.
 *   Gmail (read-only)  -> search + attachment details + file bytes
 *   Drive              -> save files, OCR (scanned PDFs / photos -> text), ZIP, share links
 *   Sheets             -> the app's small database (fields, text, your corrections) + exports
 *
 * Functions ending in "_" are private: google.script.run cannot call them.
 */

var APP_NAME = 'Mail Attachment Agent';
var STORE_PROP = 'MAA_STORE_ID';
var ROOT_PROP = 'MAA_ROOT_FOLDER_ID';
var MAX_PREVIEW = 15 * 1024 * 1024;   // bytes sent to the browser for preview
var MAX_ZIP = 20 * 1024 * 1024;       // ZIP returned directly; bigger ones go to Drive
var MAX_MAIL = 24 * 1024 * 1024;      // Gmail sending limit (25 MB incl. encoding)
var STORE_COLS = ['key', 'msgId', 'partId', 'filename', 'from', 'date', 'subject', 'doc_type', 'doc_no',
                  'doc_date', 'amount', 'currency', 'gstin', 'text_source', 'user_fields', 'sha256',
                  'drive_id', 'updated', 'text'];
var FIELD_NAMES = ['doc_type', 'doc_no', 'doc_date', 'amount', 'currency', 'gstin'];

// ------------------------------------------------------------------ web app
function doGet() {
  var page;
  try {
    page = HtmlService.createTemplateFromFile('Index').evaluate();
  } catch (e) {
    // The 2-file setup has the whole screen built into this script (EMBEDDED_PAGE), so no Index file is needed.
    if (typeof EMBEDDED_PAGE === 'undefined') {
      throw new Error('The screen file is missing: in the editor click + then HTML and name it exactly Index (capital I).');
    }
    page = HtmlService.createHtmlOutput(EMBEDDED_PAGE);
  }
  return page
    .setTitle(APP_NAME)
    .addMetaTag('viewport', 'width=device-width, initial-scale=1')
    .setXFrameOptionsMode(HtmlService.XFrameOptionsMode.DEFAULT);   // cannot be framed by other sites
}

function include(name) {
  return HtmlService.createHtmlOutputFromFile(name).getContent();
}

function getUser() {
  var store = storeInfo_();
  return { email: Session.getActiveUser().getEmail() || Session.getEffectiveUser().getEmail(),
           timeZone: Session.getScriptTimeZone(), store: store };
}

// ------------------------------------------------------- search: query matrix
/**
 * Build the query matrix: date windows x senders. Each cell is one Gmail search the browser runs in
 * parallel. Dates are sent as epoch seconds (exact, no time-zone surprises).
 * req: {senders[], keywords[], matchMode, exclude[], recipients[], fromTs, toTs (ms, inclusive end of day),
 *       types[], sizeMinMb, sizeMaxMb, anywhere, windowMonths}
 */
function planSearch(req) {
  req = req || {};
  var senders = uniq_(req.senders || []);
  var windows = dateWindows_(req.fromTs, req.toTs, req.windowMonths || 3);
  var common = commonQuery_(req);
  var queries = [];
  windows.forEach(function (w) {
    (senders.length ? senders : [null]).forEach(function (s) {
      var q = common.slice();
      if (s) q.push(senderQuery_(s));
      if (w[0]) q.push('after:' + w[0]);
      if (w[1]) q.push('before:' + w[1]);
      queries.push(q.join(' '));
    });
  });
  return { queries: queries, cells: queries.length };
}

function commonQuery_(req) {
  var q = ['has:attachment'];
  var kws = (req.keywords || []).filter(String);
  if (kws.length) {
    if (req.matchMode === 'phrase') {
      var p = kws.join(' ');
      q.push('{' + gq_(p) + ' filename:' + gq_(p) + '}');
    } else if (req.matchMode === 'all') {
      kws.forEach(function (k) { q.push('{' + gq_(k) + ' filename:' + gq_(k) + '}'); });
    } else {
      q.push('{' + kws.map(function (k) { return gq_(k) + ' filename:' + gq_(k); }).join(' ') + '}');
    }
  }
  (req.exclude || []).filter(String).forEach(function (e) { q.push('-' + gq_(e)); });
  var to = (req.recipients || []).filter(String);
  if (to.length) q.push('{' + to.map(function (t) { return 'to:' + gq_(t) + ' cc:' + gq_(t); }).join(' ') + '}');
  var types = normaliseTypes_(req.types || []);
  if (types.length) q.push('{' + types.map(function (t) { return 'filename:' + t; }).join(' ') + '}');
  if (req.sizeMinMb) q.push('larger:' + Math.max(1, Math.round(req.sizeMinMb * 1024)) + 'K');
  if (req.sizeMaxMb) q.push('smaller:' + Math.max(1, Math.round(req.sizeMaxMb * 1024)) + 'K');
  if (req.anywhere) q.push('in:anywhere');
  return q;
}

function senderQuery_(s) {
  var compact = s.toLowerCase().replace(/[^0-9a-z@.]+/g, '');
  var alts = ['from:' + gq_(s)];
  if (compact && compact !== s.toLowerCase()) alts.push('from:' + compact);   // "mck kutty" -> mckkutty@...
  return alts.length > 1 ? '{' + alts.join(' ') + '}' : alts[0];
}

function gq_(t) {
  t = String(t).replace(/"/g, '').trim();
  return /\s/.test(t) ? '"' + t + '"' : t;
}

var TYPE_GROUPS = {
  image: ['jpg', 'jpeg', 'png', 'gif', 'webp', 'heic', 'tif', 'tiff', 'bmp'],
  excel: ['xls', 'xlsx', 'xlsm', 'csv', 'ods'], word: ['doc', 'docx', 'odt', 'rtf'],
  archive: ['zip', 'rar', '7z'], pdf: ['pdf']
};

function normaliseTypes_(types) {
  var out = [];
  types.forEach(function (t) {
    t = String(t).toLowerCase().replace(/^[.*]+/, '').trim();
    if (!t || t === 'any') return;
    (TYPE_GROUPS[t] || [t]).forEach(function (e) { if (out.indexOf(e) < 0) out.push(e); });
  });
  return out;
}

/** Split [from, to] into windows of N months, 1 day overlap each side (results are merged as a set). */
function dateWindows_(fromMs, toMs, months) {
  if (!fromMs && !toMs) return [[null, null]];
  var DAY = 86400;
  var start = fromMs ? Math.floor(fromMs / 1000) : null;
  var end = toMs ? Math.floor(toMs / 1000) + 1 : null;      // toMs = last millisecond of the end day
  if (!start || !end || !months) return [[start ? start - DAY : null, end ? end + DAY : null]];
  if (end < start) { var t = start; start = end; end = t; }
  var out = [];
  var cur = new Date(start * 1000);
  while (cur.getTime() / 1000 < end) {
    var next = new Date(cur.getTime());
    next.setMonth(next.getMonth() + months);
    var a = Math.floor(cur.getTime() / 1000), b = Math.min(Math.floor(next.getTime() / 1000), end);
    out.push([a - DAY, b + DAY]);
    cur = next;
  }
  return out;
}

/** One matrix cell, one page: returns message ids (Gmail's own search index - very fast). */
function listIds(query, pageToken) {
  var ids = [], token = pageToken || null, t0 = Date.now();
  do {
    var opt = { q: query, maxResults: 500 };
    if (token) opt.pageToken = token;
    var res = Gmail.Users.Messages.list('me', opt);
    (res.messages || []).forEach(function (m) { ids.push(m.id); });
    token = res.nextPageToken || null;
  } while (token && ids.length < 2000 && Date.now() - t0 < 20000);
  return { ids: ids, next: token };
}

// ------------------------------------------------------------ attachment list
/** Details of a batch of messages: headers + every attachment part (no file bytes downloaded). */
function getMeta(ids) {
  var out = [];
  (ids || []).forEach(function (id) {
    var m;
    try {
      m = Gmail.Users.Messages.get('me', id, { format: 'full' });
    } catch (e) {
      return;                                   // deleted meanwhile
    }
    var h = headers_(m.payload);
    var from = parseAddress_(h.from || '');
    var date = new Date(Number(m.internalDate));
    var snippet = (m.snippet || '').replace(/&#39;/g, "'").replace(/&quot;/g, '"').replace(/&amp;/g, '&');
    var body = bodyText_(m.payload).slice(0, 2000);
    walkParts_(m.payload, function (p) {
      var name = p.filename || '';
      if (!name || !p.body || !(p.body.attachmentId || p.body.data)) return;
      var ph = headers_(p);
      var disp = (ph['content-disposition'] || '').toLowerCase();
      var inline = disp.indexOf('inline') === 0 || (!!ph['content-id'] && /^image\//.test(p.mimeType || '') &&
                                                    disp.indexOf('attachment') !== 0);
      var item = {
        key: m.id + ':' + p.partId, msgId: m.id, threadId: m.threadId, partId: p.partId,
        filename: name, mime: (p.mimeType || 'application/octet-stream').toLowerCase(),
        ext: name.indexOf('.') >= 0 ? name.split('.').pop().toLowerCase() : '',
        size: p.body.size || 0, inline: inline,
        fromName: from.name || from.email, fromEmail: from.email, to: [h.to, h.cc].filter(String).join(', '),
        subject: h.subject || '', date: date.toISOString(), messageId: (h['message-id'] || '').trim(),
        snippet: (body || snippet).slice(0, 1500),      // Gmail's snippet is just the start of the body
        labels: m.labelIds || []
      };
      item.fields = quickFields(item.subject, item.snippet, name);
      item.risky = isRisky_(name);
      out.push(item);
    });
  });
  return out;
}

function headers_(part) {
  var h = {};
  ((part && part.headers) || []).forEach(function (x) { h[x.name.toLowerCase()] = x.value; });
  return h;
}

function walkParts_(part, fn) {
  if (!part) return;
  fn(part);
  (part.parts || []).forEach(function (p) { walkParts_(p, fn); });
}

function bodyText_(payload) {
  var plain = '', html = '';
  walkParts_(payload, function (p) {
    if (p.filename || !p.body || !p.body.data) return;
    if (p.mimeType === 'text/plain' && !plain) plain = b64urlText_(p.body.data);
    if (p.mimeType === 'text/html' && !html) html = b64urlText_(p.body.data);
  });
  var t = plain || html.replace(/<(script|style)[^>]*>[\s\S]*?<\/\1>/gi, ' ').replace(/<[^>]+>/g, ' ');
  return t.replace(/&nbsp;/g, ' ').replace(/\s+/g, ' ').trim();
}

/** Gmail data may arrive as a web-safe base64 string or (Apps Script advanced service) as a byte array. */
function b64urlBytes_(data) {
  if (data == null) return [];
  if (typeof data === 'string') return Utilities.base64DecodeWebSafe(data);
  return Array.prototype.slice.call(data);
}

function b64urlText_(data) {
  try { return Utilities.newBlob(b64urlBytes_(data)).getDataAsString('UTF-8'); } catch (e) { return ''; }
}

function parseAddress_(s) {
  var m = /^\s*"?([^"<]*?)"?\s*<([^>]+)>/.exec(s);
  if (m) return { name: m[1].trim(), email: m[2].trim().toLowerCase() };
  return { name: '', email: s.trim().toLowerCase() };
}

var RISKY = ['exe', 'com', 'scr', 'pif', 'bat', 'cmd', 'ps1', 'vbs', 'vbe', 'js', 'jse', 'wsf', 'hta', 'msi', 'jar',
             'lnk', 'reg', 'dll', 'iso', 'img', 'docm', 'xlsm', 'xlsb', 'pptm', 'dotm', 'chm', 'one', 'svg'];

function isRisky_(name) {
  var e = String(name).toLowerCase().split('.').pop();
  return name.indexOf('.') >= 0 && RISKY.indexOf(e) >= 0;
}

// --------------------------------------------------------------- file bytes
function attachmentBlob_(msgId, partId) {
  var m = Gmail.Users.Messages.get('me', msgId, { format: 'full' });
  var part = null;
  walkParts_(m.payload, function (p) { if (p.partId === partId) part = p; });
  if (!part) throw new Error('Attachment not found (the email may have been deleted)');
  var data = part.body.data;
  if (!data && part.body.attachmentId) {
    data = Gmail.Users.Messages.Attachments.get('me', msgId, part.body.attachmentId).data;
  }
  return Utilities.newBlob(b64urlBytes_(data), part.mimeType || 'application/octet-stream', part.filename);
}

/** For the preview pane. Returns standard base64. */
function getAttachment(msgId, partId) {
  var blob = attachmentBlob_(msgId, partId);
  var bytes = blob.getBytes();
  if (bytes.length > MAX_PREVIEW) {
    return { tooBig: true, size: bytes.length, name: blob.getName(), mime: blob.getContentType() };
  }
  return { b64: Utilities.base64Encode(bytes), size: bytes.length, name: blob.getName(), mime: blob.getContentType() };
}

function sha256_(bytes) {
  return Utilities.computeDigest(Utilities.DigestAlgorithm.SHA_256, bytes).map(function (b) {
    return ('0' + (b & 0xff).toString(16)).slice(-2);
  }).join('');
}

// ------------------------------------------------- text reader / OCR (Drive)
var OCR_TYPES = ['pdf', 'png', 'jpg', 'jpeg', 'gif', 'bmp', 'tif', 'tiff', 'webp', 'heic'];
var CONVERT = { docx: 'application/vnd.google-apps.document', doc: 'application/vnd.google-apps.document',
                odt: 'application/vnd.google-apps.document', rtf: 'application/vnd.google-apps.document',
                xlsx: 'application/vnd.google-apps.spreadsheet', xls: 'application/vnd.google-apps.spreadsheet',
                ods: 'application/vnd.google-apps.spreadsheet' };

/**
 * Read the text of an attachment. PDFs & images go through Google Drive's OCR (works for scanned
 * documents too); Word/Excel are converted; text files are read directly. Result is stored.
 */
function readText(item, force) {
  var store = openStore_();
  var row = findRow_(store, item.key);
  if (row && row.values.text_source && !force) {
    return docResult_(row.values, null);
  }
  var blob = attachmentBlob_(item.msgId, item.partId);
  var ext = (item.ext || '').toLowerCase();
  var text = '', source = 'none', tables = [];
  if (OCR_TYPES.indexOf(ext) >= 0 || /^image\//.test(blob.getContentType()) || blob.getContentType() === 'application/pdf') {
    text = driveConvertText_(blob, 'application/vnd.google-apps.document', true);
    source = text ? (ext === 'pdf' ? 'pdf' : 'ocr') : 'none';
    if (ext === 'pdf' && text && looksScanned_(blob)) source = 'ocr';
  } else if (CONVERT[ext] === 'application/vnd.google-apps.document') {
    text = driveConvertText_(blob, CONVERT[ext], false);
    source = text ? 'office' : 'none';
  } else if (CONVERT[ext]) {
    var sheet = driveConvertSheet_(blob);
    tables = sheet.tables;
    text = sheet.text;
    source = text ? 'office' : 'none';
  } else if (/^text\//.test(blob.getContentType()) || ['txt', 'csv', 'tsv', 'json', 'xml', 'md', 'eml', 'html', 'htm'].indexOf(ext) >= 0) {
    text = blob.getDataAsString();
    if (ext === 'html' || ext === 'htm') text = text.replace(/<(script|style)[^>]*>[\s\S]*?<\/\1>/gi, ' ').replace(/<[^>]+>/g, ' ');
    source = 'plain';
    if (ext === 'csv' || ext === 'tsv') tables = [Utilities.parseCsv(text, ext === 'tsv' ? '\t' : ',').slice(0, 2000)];
  }
  text = (text || '').slice(0, 45000);                          // Sheets cell limit is 50,000
  var fields = mergeFields(item.fields || {}, extractFields(text, item.subject, item.filename));
  var values = upsertRow_(store, item, { text: text, text_source: source, sha256: sha256_(blob.getBytes()) }, fields);
  return docResult_(values, tables);
}

function looksScanned_(blob) {
  // a PDF whose bytes contain no text operators is image-only
  var s = blob.getDataAsString('ISO-8859-1');
  return !/\/Font\b/.test(s);
}

function driveConvertText_(blob, targetMime, ocr) {
  var opts = ocr ? { ocrLanguage: 'en' } : {};
  var f = Drive.Files.create({ name: 'MAA-temp-' + Date.now(), mimeType: targetMime }, blob, opts);
  try {
    return DocumentApp.openById(f.id).getBody().getText();
  } finally {
    try { Drive.Files.remove(f.id); } catch (e) { /* temp file - ignore */ }
  }
}

function driveConvertSheet_(blob) {
  var f = Drive.Files.create({ name: 'MAA-temp-' + Date.now(), mimeType: 'application/vnd.google-apps.spreadsheet' }, blob);
  try {
    var ss = SpreadsheetApp.openById(f.id), tables = [], text = [];
    ss.getSheets().slice(0, 10).forEach(function (sh) {
      var v = sh.getDataRange().getDisplayValues().slice(0, 2000);
      tables.push(v);
      text.push('[' + sh.getName() + ']\n' + v.map(function (r) { return r.join('\t'); }).join('\n'));
    });
    return { tables: tables, text: text.join('\n\n') };
  } finally {
    try { Drive.Files.remove(f.id); } catch (e) { /* ignore */ }
  }
}

function docResult_(values, tables) {
  return { text: values.text || '', source: values.text_source || '', fields: fieldsOf_(values),
           tables: tables && tables.length ? tables : findTables(values.text || '') };
}

// ------------------------------------------------------ the store (a Sheet)
/** The app's database: one private Google Sheet in your Drive. */
function openStore_() {
  var props = PropertiesService.getUserProperties();
  var id = props.getProperty(STORE_PROP);
  var ss = null;
  if (id) { try { ss = SpreadsheetApp.openById(id); } catch (e) { ss = null; } }
  if (!ss) {
    ss = SpreadsheetApp.create(APP_NAME + ' - data (do not edit columns)');
    var sh = ss.getSheets()[0];
    sh.setName('Documents');
    sh.getRange(1, 1, 1, STORE_COLS.length).setValues([STORE_COLS]).setFontWeight('bold');
    sh.setFrozenRows(1);
    props.setProperty(STORE_PROP, ss.getId());
  }
  return ss.getSheetByName('Documents');
}

function storeInfo_() {
  var id = PropertiesService.getUserProperties().getProperty(STORE_PROP);
  if (!id) return { exists: false, rows: 0 };
  try {
    var sh = SpreadsheetApp.openById(id).getSheetByName('Documents');
    return { exists: true, rows: Math.max(0, sh.getLastRow() - 1), url: SpreadsheetApp.openById(id).getUrl() };
  } catch (e) {
    return { exists: false, rows: 0 };
  }
}

function readAll_(sh) {
  var n = sh.getLastRow() - 1;
  if (n <= 0) return [];
  return sh.getRange(2, 1, n, STORE_COLS.length).getValues();
}

function rowObj_(arr) {
  var o = {};
  STORE_COLS.forEach(function (c, i) { o[c] = arr[i]; });
  return o;
}

function findRow_(sh, key) {
  var n = sh.getLastRow() - 1;
  if (n <= 0) return null;
  var f = sh.getRange(2, 1, n, 1).createTextFinder(key).matchEntireCell(true).findNext();
  if (!f) return null;
  var r = f.getRow();
  return { row: r, values: rowObj_(sh.getRange(r, 1, 1, STORE_COLS.length).getValues()[0]) };
}

function upsertRow_(sh, item, extra, fields) {
  var lock = LockService.getUserLock();
  lock.waitLock(20000);
  try {
    var existing = findRow_(sh, item.key);
    var v = existing ? existing.values : {};
    var user = {};
    var userStr = (extra && extra.user_fields) || v.user_fields;
    try { user = userStr ? JSON.parse(userStr) : {}; } catch (e) { user = {}; }
    var f = mergeFields(fieldsOf_(v), fields || {});
    Object.keys(user).forEach(function (k) { f[k] = user[k]; });        // your corrections always win
    var out = {
      key: item.key, msgId: item.msgId, partId: item.partId, filename: item.filename,
      from: item.fromName || item.from || v.from || '', date: item.date || v.date || '',
      subject: item.subject || v.subject || '', user_fields: JSON.stringify(user),
      text_source: v.text_source || '', sha256: v.sha256 || '', drive_id: v.drive_id || '', text: v.text || '',
      updated: new Date().toISOString()
    };
    FIELD_NAMES.forEach(function (k) { out[k] = f[k] == null ? '' : f[k]; });
    Object.keys(extra || {}).forEach(function (k) { out[k] = extra[k]; });
    var arr = STORE_COLS.map(function (c) { return out[c] == null ? '' : out[c]; });
    if (existing) sh.getRange(existing.row, 1, 1, arr.length).setValues([arr]);
    else sh.appendRow(arr);
    return out;
  } finally {
    lock.releaseLock();
  }
}

function fieldsOf_(v) {
  var f = {};
  FIELD_NAMES.forEach(function (k) {
    var x = v[k];
    f[k] = (x === '' || x == null) ? null : (k === 'amount' ? Number(x) : String(x));
  });
  if (!f.doc_type) f.doc_type = null;
  return f;
}

/** Stored fields / read state for a list of keys (one sheet read). */
function getStored(keys) {
  var id = PropertiesService.getUserProperties().getProperty(STORE_PROP);
  if (!id) return {};
  var want = {};
  (keys || []).forEach(function (k) { want[k] = true; });
  var out = {};
  readAll_(openStore_()).forEach(function (r) {
    var o = rowObj_(r);
    if (want[o.key]) out[o.key] = { fields: fieldsOf_(o), text_source: o.text_source, drive_id: o.drive_id,
                                    hasText: !!o.text, text: String(o.text || '').slice(0, 20000) };
  });
  return out;
}

/** Save one corrected field (Details tab / Table view). Corrections are never overwritten later. */
function saveField(item, field, value) {
  if (FIELD_NAMES.indexOf(field) < 0) throw new Error('Unknown field');
  if (field === 'amount') value = value === '' || value == null ? null : toNumber(String(value));
  else value = value == null || String(value).trim() === '' ? null : String(value).trim();
  var sh = openStore_();
  var row = findRow_(sh, item.key);
  var user = {};
  try { user = row && row.values.user_fields ? JSON.parse(row.values.user_fields) : {}; } catch (e) { user = {}; }
  user[field] = value;
  return fieldsOf_(upsertRow_(sh, item, { user_fields: JSON.stringify(user) }, item.fields || {}));
}

// ------------------------------------------------------------- save / zip
function rootFolder_() {
  var props = PropertiesService.getUserProperties();
  var id = props.getProperty(ROOT_PROP);
  if (id) {
    try { var f = Drive.Files.get(id, { fields: 'id,trashed' }); if (!f.trashed) return id; } catch (e) { /* recreate */ }
  }
  var created = Drive.Files.create({ name: 'Mail Attachments', mimeType: 'application/vnd.google-apps.folder' });
  props.setProperty(ROOT_PROP, created.id);
  return created.id;
}

function childFolder_(parentId, name, cache) {
  var key = parentId + '/' + name;
  if (cache[key]) return cache[key];
  var q = "'" + parentId + "' in parents and mimeType='application/vnd.google-apps.folder' and trashed=false and name='" +
          name.replace(/\\/g, '\\\\').replace(/'/g, "\\'") + "'";
  var res = Drive.Files.list({ q: q, fields: 'files(id)', pageSize: 1 });
  var id = res.files && res.files.length ? res.files[0].id
    : Drive.Files.create({ name: name, mimeType: 'application/vnd.google-apps.folder', parents: [parentId] }).id;
  cache[key] = id;
  return id;
}

function safeName_(s, fallback) {
  s = String(s || '').replace(/[<>:"\/\\|?*\x00-\x1f]+/g, '_').replace(/\s+/g, ' ').trim().replace(/^\.+/, '');
  return (s || fallback || 'unknown').slice(0, 120);
}

function folderPath_(item, layout) {
  var d = new Date(item.date || Date.now());
  var y = String(d.getFullYear()), m = ('0' + (d.getMonth() + 1)).slice(-2);
  var sender = safeName_(item.fromName || item.fromEmail, 'unknown');
  return { 'sender/year-month': [sender, y + '-' + m], 'sender/year': [sender, y], 'year/month': [y, m],
           sender: [sender], flat: [] }[layout] || [sender, y + '-' + m];
}

/**
 * Save attachments to Google Drive in "Mail Attachments/<sender>/<year-month>".
 * Identical files (same SHA-256) are never saved twice.
 */
function saveToDrive(items, layout, naming, targetFolderId) {
  var root = targetFolderId || rootFolder_();
  var cache = {}, report = { saved: [], skipped: [], failed: [], folderUrl: 'https://drive.google.com/drive/folders/' + root };
  var store = openStore_();
  (items || []).forEach(function (it) {
    try {
      var blob = attachmentBlob_(it.msgId, it.partId);
      var hash = sha256_(blob.getBytes());
      var dup = Drive.Files.list({ q: "appProperties has { key='sha256' and value='" + hash + "' } and trashed=false",
                                   fields: 'files(id,name,webViewLink)', pageSize: 1 }).files || [];
      if (dup.length && !targetFolderId) {
        report.skipped.push({ filename: it.filename, link: dup[0].webViewLink, reason: 'identical file already saved' });
        return;
      }
      var parent = root;
      folderPath_(it, layout).forEach(function (seg) { parent = childFolder_(parent, seg, cache); });
      var d = new Date(it.date || Date.now());
      var name = naming === 'date_original'
        ? Utilities.formatDate(d, Session.getScriptTimeZone(), 'yyyy-MM-dd') + '_' + it.filename : it.filename;
      blob.setName(safeName_(name, 'attachment'));
      var f = Drive.Files.create({ name: blob.getName(), parents: [parent], appProperties: { sha256: hash, source: 'MAA' },
                                   description: 'From: ' + (it.fromEmail || '') + '\nSubject: ' + (it.subject || '') +
                                                '\nEmail date: ' + (it.date || '') },
                                 blob, { fields: 'id,webViewLink' });
      upsertRow_(store, it, { sha256: hash, drive_id: f.id }, it.fields || {});
      report.saved.push({ filename: it.filename, link: f.webViewLink, id: f.id });
    } catch (e) {
      report.failed.push({ filename: it.filename, error: String(e.message || e) });
    }
  });
  return report;
}

/** ZIP of the selected files. Small ZIPs come straight to the browser; big ones are saved in Drive. */
function makeZip(items, layout) {
  var blobs = [], names = {}, seen = {}, total = 0, skipped = 0;
  (items || []).forEach(function (it) {
    var b = attachmentBlob_(it.msgId, it.partId);
    var h = sha256_(b.getBytes());
    if (seen[h]) { skipped++; return; }
    seen[h] = true;
    var path = folderPath_(it, layout || 'flat').concat([safeName_(it.filename, 'attachment')]).join('/');
    var base = path, n = 2;
    while (names[path.toLowerCase()]) { path = base.replace(/(\.[^.\/]+)?$/, ' (' + (n++) + ')$1'); }
    names[path.toLowerCase()] = true;
    b.setName(path);
    total += b.getBytes().length;
    blobs.push(b);
  });
  var name = 'mail-attachments-' + Utilities.formatDate(new Date(), Session.getScriptTimeZone(), 'yyyyMMdd-HHmm') + '.zip';
  var zip = Utilities.zip(blobs, name);
  var bytes = zip.getBytes();
  if (bytes.length <= MAX_ZIP) return { name: name, b64: Utilities.base64Encode(bytes), files: blobs.length, skipped: skipped };
  var f = Drive.Files.create({ name: name, parents: [rootFolder_()] }, zip, { fields: 'id,webViewLink' });
  return { name: name, link: f.webViewLink, files: blobs.length, skipped: skipped, inDrive: true };
}

// ------------------------------------------------------------ export / share
var EXPORT_HEAD = ['Email date', 'From', 'From email', 'Subject', 'File', 'Type', 'Size (KB)', 'Document', 'Number',
                   'Document date', 'Amount', 'Currency', 'GSTIN', 'Text'];

/** Create a Google Sheet with the list + totals by month and by sender. Returns its URL. */
function exportSheet(rows, title) {
  var ss = SpreadsheetApp.create(title || ('Mail attachments ' + Utilities.formatDate(new Date(), Session.getScriptTimeZone(), 'yyyy-MM-dd HH:mm')));
  var sh = ss.getSheets()[0];
  sh.setName('Attachments');
  var data = (rows || []).map(function (r) {
    var f = r.fields || {};
    return [r.date ? new Date(r.date) : '', r.fromName || '', r.fromEmail || '', r.subject || '', r.filename || '',
            r.ext || '', Math.round((r.size || 0) / 102.4) / 10, DOC_LABELS[f.doc_type || 'other'] || '', f.doc_no || '',
            f.doc_date || '', f.amount == null ? '' : Number(f.amount), f.currency || '', f.gstin || '', r.text_source || ''];
  });
  sh.getRange(1, 1, 1, EXPORT_HEAD.length).setValues([EXPORT_HEAD]).setFontWeight('bold')
    .setBackground('#2563eb').setFontColor('#ffffff');
  if (data.length) {
    sh.getRange(2, 1, data.length, EXPORT_HEAD.length).setValues(data);
    sh.getRange(2, 1, data.length, 1).setNumberFormat('yyyy-mm-dd hh:mm');
    sh.getRange(2, 11, data.length + 1, 1).setNumberFormat('#,##0.00');
    var tr = data.length + 2;
    sh.getRange(tr, 1).setValue('TOTAL').setFontWeight('bold');
    sh.getRange(tr, 5).setValue(data.length + ' files').setFontWeight('bold');
    sh.getRange(tr, 11).setFormula('=SUBTOTAL(9,K2:K' + (data.length + 1) + ')').setFontWeight('bold');
    sh.getRange(1, 1, data.length + 1, EXPORT_HEAD.length).createFilter();
  }
  sh.setFrozenRows(1);
  sh.autoResizeColumns(1, EXPORT_HEAD.length);
  [['By month', 'Month', function (r) { var f = r.fields || {}; return String(f.doc_date || r.date || 'unknown').slice(0, 7); }],
   ['By sender', 'Sender', function (r) { return r.fromName || r.fromEmail || 'unknown'; }]].forEach(function (g) {
    var groups = {};
    (rows || []).forEach(function (r) {
      var k = g[2](r);
      groups[k] = groups[k] || [0, 0];
      groups[k][0]++;
      groups[k][1] += Number((r.fields || {}).amount) || 0;
    });
    var s2 = ss.insertSheet(g[0]);
    var vals = [[g[1], 'Files', 'Total amount']].concat(Object.keys(groups).sort().map(function (k) {
      return [k, groups[k][0], Math.round(groups[k][1] * 100) / 100];
    }));
    s2.getRange(1, 1, vals.length, 3).setValues(vals);
    s2.getRange(1, 1, 1, 3).setFontWeight('bold');
    s2.getRange(2, 3, Math.max(1, vals.length - 1), 1).setNumberFormat('#,##0.00');
    s2.setFrozenRows(1);
    s2.autoResizeColumns(1, 3);
  });
  return { url: ss.getUrl(), name: ss.getName() };
}

/** Tables found inside one document -> a new Google Sheet (one tab per table). */
function tablesToSheet(name, tables) {
  var ss = SpreadsheetApp.create('Tables - ' + (name || 'document'));
  var first = ss.getSheets()[0];
  (tables || []).slice(0, 20).forEach(function (t, i) {
    var sh = i === 0 ? first : ss.insertSheet();
    sh.setName('Table ' + (i + 1));
    var w = Math.max.apply(null, t.map(function (r) { return r.length; }));
    var vals = t.map(function (r) {
      var row = r.slice();
      while (row.length < w) row.push('');
      return row.map(function (c) { return NUMERIC_CELL.test(String(c).trim()) ? toNumber(String(c)) : c; });
    });
    sh.getRange(1, 1, vals.length, w).setValues(vals);
    sh.getRange(1, 1, 1, w).setFontWeight('bold');
  });
  return { url: ss.getUrl() };
}

/** Email the selected files from your own Gmail (MailApp - send-only permission). */
function shareEmail(items, to, subject, message) {
  var rcpt = String(to || '').split(/[,;\s]+/).filter(function (a) { return /^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(a); });
  if (!rcpt.length) throw new Error('Enter at least one valid email address');
  if (MailApp.getRemainingDailyQuota() < rcpt.length) throw new Error('Daily email quota reached - try tomorrow or use Drive link');
  var blobs = [], total = 0;
  (items || []).forEach(function (it) { var b = attachmentBlob_(it.msgId, it.partId); total += b.getBytes().length; blobs.push(b); });
  if (total * 4 / 3 > MAX_MAIL) throw new Error('Too big for one email (' + (total / 1048576).toFixed(1) + ' MB). Use "Share Drive link" instead.');
  MailApp.sendEmail({ to: rcpt.join(','), subject: subject || (blobs.length + ' attachments'),
                      body: message || 'Please find the attached files.', attachments: blobs });
  return { sentTo: rcpt, files: blobs.length };
}

/** Put the files in a new Drive folder and give the recipients view access (Google sends them the link). */
function shareDrive(items, to, message) {
  var rcpt = String(to || '').split(/[,;\s]+/).filter(function (a) { return /^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(a); });
  if (!rcpt.length) throw new Error('Enter at least one valid email address');
  var name = 'Shared ' + Utilities.formatDate(new Date(), Session.getScriptTimeZone(), 'yyyy-MM-dd HH:mm');
  var folder = Drive.Files.create({ name: name, mimeType: 'application/vnd.google-apps.folder', parents: [rootFolder_()] });
  folder.webViewLink = 'https://drive.google.com/drive/folders/' + folder.id;
  var rep = saveToDrive(items, 'flat', 'original', folder.id);
  rcpt.forEach(function (email) {
    Drive.Permissions.create({ type: 'user', role: 'reader', emailAddress: email }, folder.id,
                             { sendNotificationEmail: true, emailMessage: message || 'Files shared with you.' });
  });
  return { link: folder.webViewLink, files: rep.saved.length, failed: rep.failed, sharedWith: rcpt };
}

// ------------------------------------------------------------------ people
/*
 * Every email address in your mailbox (From / To / Cc), without duplicates.
 * The browser lists conversations page by page, reads their headers in parallel batches,
 * merges everything, and saves the list in the "People" tab of the data sheet.
 * Later updates only read mail newer than the last scan.
 */
var CONTACT_COLS = ['email', 'name', 'emails', 'last', 'domain'];
var CONTACTS_SYNC_PROP = 'MAA_CONTACTS_SYNC';

/** One page of conversation ids (up to 2000). sinceMs: only conversations with mail after that time. */
function contactsListThreads(sinceMs, pageToken) {
  var q = '-in:chats' + (sinceMs ? ' after:' + Math.floor(sinceMs / 1000 - 86400) : '');
  var ids = [], token = pageToken || null, t0 = Date.now();
  do {
    var opt = { q: q, maxResults: 500 };
    if (token) opt.pageToken = token;
    var res = Gmail.Users.Threads.list('me', opt);
    (res.threads || []).forEach(function (t) { ids.push(t.id); });
    token = res.nextPageToken || null;
  } while (token && ids.length < 2000 && Date.now() - t0 < 20000);
  return { ids: ids, next: token };
}

/** Addresses found in a batch of conversations: [[email, name, count, lastMs], ...] */
function contactsFromThreads(threadIds, sinceMs) {
  var map = {}, newest = 0, messages = 0;
  (threadIds || []).forEach(function (tid) {
    var t;
    try {
      t = Gmail.Users.Threads.get('me', tid, { format: 'metadata', metadataHeaders: ['From', 'To', 'Cc', 'Bcc'] });
    } catch (e) { return; }
    (t.messages || []).forEach(function (m) {
      var when = Number(m.internalDate) || 0;
      if (sinceMs && when <= sinceMs) return;              // already counted in the last scan
      messages++;
      if (when > newest) newest = when;
      ((m.payload && m.payload.headers) || []).forEach(function (h) {
        if (!/^(from|to|cc|bcc)$/i.test(h.name)) return;
        parseAddressList(h.value).forEach(function (a) {
          var c = map[a.email] || (map[a.email] = [a.email, '', 0, 0]);
          c[2]++;
          if (when >= c[3]) { c[3] = when; if (a.name) c[1] = a.name; }   // newest name wins
          else if (!c[1] && a.name) c[1] = a.name;
        });
      });
    });
  });
  return { contacts: Object.keys(map).map(function (k) { return map[k]; }), newest: newest, messages: messages };
}

/** '"Kutty, MCK" <a@b.in>, c@d.com, Name <e@f.org>' -> [{name, email}] (lower-case, de-duplicated) */
function parseAddressList(value) {
  var out = [], seen = {}, s = String(value || ''), m;
  var rx = /(?:"((?:[^"\\]|\\.)*)"|([^,<>"]*?))\s*<\s*([^<>\s]+@[^<>\s]+)\s*>|([A-Za-z0-9._%+'\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,})/g;
  while ((m = rx.exec(s))) {
    var email = (m[3] || m[4] || '').trim().toLowerCase().replace(/^mailto:/, '').replace(/[.,;]+$/, '');
    if (!/^[^@\s]+@[^@\s]+\.[a-z]{2,}$/.test(email) || seen[email]) continue;
    seen[email] = 1;
    var name = (m[1] != null ? m[1] : (m[2] || '')).replace(/\\"/g, '"').replace(/^[\s,'"]+|[\s,'"]+$/g, '');
    if (name.toLowerCase() === email) name = '';
    out.push({ email: email, name: name });
  }
  return out;
}

function contactsSheet_() {
  var ss = openStore_().getParent();
  var sh = ss.getSheetByName('People');
  if (!sh) {
    sh = ss.insertSheet('People');
    sh.getRange(1, 1, 1, CONTACT_COLS.length).setValues([CONTACT_COLS]).setFontWeight('bold');
    sh.setFrozenRows(1);
  }
  return sh;
}

/** Save the merged list (replaces the People tab). rows: [[email, name, count, lastMs], ...] */
function saveContacts(rows, newestMs) {
  var sh = contactsSheet_();
  var n = sh.getLastRow() - 1;
  if (n > 0) sh.getRange(2, 1, n, CONTACT_COLS.length).clearContent();
  var vals = (rows || []).map(function (r) {
    return [r[0], r[1] || '', Number(r[2]) || 0, r[3] ? new Date(Number(r[3])) : '', String(r[0]).split('@')[1] || ''];
  });
  if (vals.length) sh.getRange(2, 1, vals.length, CONTACT_COLS.length).setValues(vals);
  var props = PropertiesService.getUserProperties();
  if (newestMs) props.setProperty(CONTACTS_SYNC_PROP, String(newestMs));
  props.setProperty(CONTACTS_SYNC_PROP + '_AT', String(Date.now()));
  return { saved: vals.length };
}

/** The saved list, for suggestions and the People tab. */
function getContacts() {
  var props = PropertiesService.getUserProperties();
  if (!props.getProperty(STORE_PROP)) return { rows: [], syncedTo: 0, updatedAt: 0, me: Session.getActiveUser().getEmail() };
  var sh = openStore_().getParent().getSheetByName('People');
  var rows = [];
  if (sh && sh.getLastRow() > 1) {
    rows = sh.getRange(2, 1, sh.getLastRow() - 1, 4).getValues().map(function (r) {
      var d = r[3] instanceof Date ? r[3].getTime() : (r[3] ? Date.parse(r[3]) || 0 : 0);
      return [String(r[0]), String(r[1] || ''), Number(r[2]) || 0, d];
    });
  }
  return { rows: rows, syncedTo: Number(props.getProperty(CONTACTS_SYNC_PROP) || 0),
           updatedAt: Number(props.getProperty(CONTACTS_SYNC_PROP + '_AT') || 0),
           me: Session.getActiveUser().getEmail() };
}

/** People list -> a new Google Sheet (for sharing / printing). */
function exportContacts(rows, title) {
  var ss = SpreadsheetApp.create(title || ('Email addresses ' + Utilities.formatDate(new Date(), Session.getScriptTimeZone(), 'yyyy-MM-dd')));
  var sh = ss.getSheets()[0];
  sh.setName('People');
  var vals = [['Name', 'Email', 'Domain', 'Emails', 'Last email']].concat((rows || []).map(function (r) {
    return [r[1] || '', r[0], String(r[0]).split('@')[1] || '', Number(r[2]) || 0, r[3] ? new Date(Number(r[3])) : ''];
  }));
  sh.getRange(1, 1, vals.length, 5).setValues(vals);
  sh.getRange(1, 1, 1, 5).setFontWeight('bold');
  sh.setFrozenRows(1);
  sh.autoResizeColumns(1, 5);
  return { url: ss.getUrl() };
}

// ------------------------------------------------------------------ privacy
/** Delete everything this app created for you: the data sheet (moved to Bin) and its settings. */
function eraseData() {
  var props = PropertiesService.getUserProperties();
  var id = props.getProperty(STORE_PROP);
  if (id) { try { Drive.Files.update({ trashed: true }, id); } catch (e) { /* already gone */ } }
  props.deleteProperty(STORE_PROP);
  props.deleteProperty(CONTACTS_SYNC_PROP);
  props.deleteProperty(CONTACTS_SYNC_PROP + '_AT');
  return { ok: true, note: 'The data sheet was moved to the Drive Bin. Saved attachments in "Mail Attachments" were kept.' };
}

function uniq_(a) {
  var seen = {}, out = [];
  a.forEach(function (x) { x = String(x || '').trim(); if (x && !seen[x.toLowerCase()]) { seen[x.toLowerCase()] = 1; out.push(x); } });
  return out;
}
