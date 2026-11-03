// ===== Mail Attachment Agent - paste ALL of this into Code.gs =====
// The screen is built in at the bottom (EMBEDDED_PAGE), so no separate HTML file is needed.

/**
 * Smart fields: document type, number, date, amount, currency and GSTIN from text.
 * Plus a table finder for line items. Pure JavaScript, no services - also unit-tested with Node.
 */

var DOC_LABELS = { invoice: 'Invoice', receipt: 'Receipt', statement: 'Statement', payslip: 'Payslip',
                   quotation: 'Quotation', order: 'Order', credit_note: 'Credit note', other: 'Other' };

var DOC_TYPES = [
  ['credit_note', [/credit\s*note/g]],
  ['payslip', [/pay\s*slip/g, /salary\s*slip/g, /payslip/g]],
  ['quotation', [/quotation/g, /\bquote\b/g, /estimate/g, /pro[\s-]?forma/g]],
  ['order', [/purchase\s*order/g, /order\s*confirmation/g, /\bp\.?o\.?\s*(?:no|number|#)/g]],
  ['receipt', [/receipt/g, /payment\s*(?:received|confirmation|successful)/g]],
  ['statement', [/statement/g, /account\s*summary/g, /e-?statement/g]],
  ['invoice', [/tax\s*invoice/g, /invoice/g, /\bbill\b/g, /\binv\b/g]]
];
var GSTIN_RX = /\b\d{2}[A-Z]{5}\d{4}[A-Z][1-9A-Z]Z[0-9A-Z]\b/;
var MONEY_RX = /(₹|rs\.?|inr|\$|usd|us\$|€|eur|£|gbp|aed|sgd)?\s*(\d{1,3}(?:,\d{2,3})+(?:\.\d{1,2})?|\d+(?:\.\d{1,2})?)\s*(inr|usd|eur|gbp|\/-)?/gi;
var AMOUNT_LABELS = [
  [/grand\s*total/gi, 10], [/total\s*amount\s*(?:payable|due)?/gi, 9], [/amount\s*payable/gi, 9],
  [/net\s*(?:payable|amount|pay)/gi, 9], [/balance\s*due/gi, 8], [/amount\s*due/gi, 8], [/invoice\s*total/gi, 8],
  [/total\s*\(?(?:inr|rs|₹)?\)?/gi, 6], [/\bamount\b/gi, 4], [/\bpaid\b/gi, 3], [/\bsum\b/gi, 2]
];
var NO_LABEL_RX = /(?:invoice|inv|bill|receipt|order|quotation|quote|credit\s*note|document|doc|ref(?:erence)?)\s*(?:no\.?|number|num|#|id)?\s*[:#.\-]?\s*((?=[A-Z0-9\-\/_.]*\d)[A-Z0-9][A-Z0-9\-\/_.]{1,30})/gi;
var HASH_NO_RX = /#\s*([A-Z0-9][A-Z0-9\-\/_]{1,30})/gi;
var MONTHS = { jan: 1, feb: 2, mar: 3, apr: 4, may: 5, jun: 6, jul: 7, aug: 8, sep: 9, oct: 10, nov: 11, dec: 12 };
var NUMERIC_CELL = /^[-+(]?[₹$€£]?\s?\d[\d,]*(\.\d+)?%?\)?$/;

function toNumber(s) {
  var n = parseFloat(String(s).replace(/[,₹$€£\s]/g, '').replace(/^\((.*)\)$/, '-$1'));
  return isNaN(n) ? null : n;
}

function mkDate_(y, m, d) {
  y = +y; m = +m; d = +d;
  if (!(m >= 1 && m <= 12 && d >= 1 && d <= 31 && y >= 1990 && y <= 2100)) return null;
  var dt = new Date(Date.UTC(y, m - 1, d));
  if (dt.getUTCMonth() !== m - 1) return null;
  return y + '-' + ('0' + m).slice(-2) + '-' + ('0' + d).slice(-2);
}

function parseDates_(text) {
  var out = [], m;
  var pats = [
    [/\b(\d{4})[-\/.](\d{1,2})[-\/.](\d{1,2})\b/g, function (a) { return mkDate_(a[1], a[2], a[3]); }],
    [/\b(\d{1,2})[-\/.](\d{1,2})[-\/.](\d{4})\b/g, function (a) { return mkDate_(a[3], a[2], a[1]) || mkDate_(a[3], a[1], a[2]); }],
    [/\b(\d{1,2})[-\s.]*([A-Za-z]{3,9})[-\s.,]*(\d{4})\b/g, function (a) { var mo = MONTHS[a[2].slice(0, 3).toLowerCase()]; return mo ? mkDate_(a[3], mo, a[1]) : null; }],
    [/\b([A-Za-z]{3,9})[\s.]+(\d{1,2})(?:st|nd|rd|th)?,?\s+(\d{4})\b/g, function (a) { var mo = MONTHS[a[1].slice(0, 3).toLowerCase()]; return mo ? mkDate_(a[3], mo, a[2]) : null; }]
  ];
  pats.forEach(function (p) {
    p[0].lastIndex = 0;
    while ((m = p[0].exec(text))) { var d = p[1](m); if (d) out.push([m.index, d]); }
  });
  return out.sort(function (a, b) { return a[0] - b[0]; });
}

function count_(rx, s) { rx.lastIndex = 0; var n = 0; while (rx.exec(s)) n++; return n; }

function detectType(text) {
  var low = text.toLowerCase(), head = low.slice(0, 400), body = low.slice(0, 20000);
  var best = 'other', bestScore = 0;
  DOC_TYPES.forEach(function (t) {
    var s = 0;
    t[1].forEach(function (rx) { s += 3 * count_(rx, head) + count_(rx, body); });
    if (s > bestScore) { best = t[0]; bestScore = s; }
  });
  return best;
}

function detectNumber(text) {
  var src = String(text || '').slice(0, 20000), rxs = [NO_LABEL_RX, HASH_NO_RX];
  for (var i = 0; i < rxs.length; i++) {
    var rx = rxs[i], m;
    rx.lastIndex = 0;
    while ((m = rx.exec(src))) {
      var no = m[1].replace(/^[._\-\/]+|[._\-\/]+$/g, '');
      if (/\d/.test(no) && !/^\d{1,2}$/.test(no) && !GSTIN_RX.test(no) && !/^\d{1,2}[-\/.]\d{1,2}[-\/.]\d{2,4}$/.test(no)) return no;
    }
  }
  return null;
}

function currency_(tok) {
  if (!tok) return null;
  var t = tok.toLowerCase().replace(/[.\s]/g, '');
  return { '₹': 'INR', rs: 'INR', inr: 'INR', '/-': 'INR', $: 'USD', usd: 'USD', us$: 'USD', '€': 'EUR', eur: 'EUR',
           '£': 'GBP', gbp: 'GBP', aed: 'AED', sgd: 'SGD' }[t] || null;
}

function detectAmount(text) {
  var low = String(text || '').slice(0, 40000), best = null;
  AMOUNT_LABELS.forEach(function (l) {
    var rx = l[0], m;
    rx.lastIndex = 0;
    while ((m = rx.exec(low))) {
      var win = low.slice(m.index + m[0].length, m.index + m[0].length + 60), mm;
      MONEY_RX.lastIndex = 0;
      while ((mm = MONEY_RX.exec(win))) {
        if (!mm[0].trim()) { MONEY_RX.lastIndex++; continue; }
        var v = toNumber(mm[2]);
        if (!v) continue;
        if (/^(19|20)\d\d$/.test(mm[2]) && !mm[1]) continue;     // a year, not money
        var score = l[1] * 1000 - mm.index;
        if (!best || score > best[0] || (score === best[0] && v > best[1])) best = [score, v, currency_(mm[1] || mm[3])];
        break;
      }
    }
  });
  if (best) return [best[1], best[2]];
  var tagged = [], t;
  MONEY_RX.lastIndex = 0;
  while ((t = MONEY_RX.exec(low))) {
    if (!t[0].trim()) { MONEY_RX.lastIndex++; continue; }
    if (t[1] || (t[3] && t[3] !== '')) { var n = toNumber(t[2]); if (n) tagged.push([n, currency_(t[1] || t[3])]); }
  }
  if (tagged.length) return tagged.sort(function (a, b) { return b[0] - a[0]; })[0];
  return [null, null];
}

function detectDate(text) {
  var low = String(text || '').slice(0, 20000), rx = /(?:invoice|bill|document|doc|receipt|statement|order)?\s*dated?\s*[:\-]?\s*/gi, m;
  while ((m = rx.exec(low))) {
    if (!m[0].length) { rx.lastIndex++; continue; }
    var near = parseDates_(low.slice(m.index + m[0].length, m.index + m[0].length + 40));
    if (near.length && near[0][0] < 6) return near[0][1];
  }
  var all = parseDates_(low.slice(0, 3000));
  return all.length ? all[0][1] : null;
}

/** Fields from a document's text (subject / file name as hints). */
function extractFields(text, subject, filename) {
  text = String(text || '');
  var name = String(filename || '').replace(/\.[^.]+$/, '').replace(/[_\-.]+/g, ' ');
  var head = (subject || '') + '\n' + name + '\n';
  var f = { doc_type: detectType(head + text) };
  f.doc_no = detectNumber(text) || detectNumber(head);
  f.doc_date = detectDate(text);
  var a = detectAmount(text);
  if (a[0] == null) a = detectAmount(head);
  f.amount = a[0];
  f.currency = a[1];
  var g = GSTIN_RX.exec(text.toUpperCase());
  f.gstin = g ? g[0] : null;
  return f;
}

/** Cheap first guess before the file itself has been read. */
function quickFields(subject, snippet, filename) {
  return extractFields(snippet, subject, filename);
}

function mergeFields(base, better) {
  var out = {};
  Object.keys(base || {}).forEach(function (k) { out[k] = base[k]; });
  Object.keys(better || {}).forEach(function (k) {
    var v = better[k];
    if ((v !== null && v !== '' && v !== 'other' && v !== undefined) || !(k in out) || out[k] == null) out[k] = v;
  });
  return out;
}

// ------------------------------------------------------------------- tables
function cellsStrict_(line) {
  return line.trim().split(/\t|\s*\|\s*|\s{2,}/).filter(function (c) { return c.trim(); }).map(function (c) { return c.trim(); });
}

function cellsLoose_(line) {
  var toks = line.trim().split(/\s+/), nums = [];
  while (toks.length && NUMERIC_CELL.test(toks[toks.length - 1])) nums.unshift(toks.pop());
  if (!nums.length || !toks.length) return [];
  return [toks.join(' ')].concat(nums);
}

/** Column-aligned blocks of text (invoice line items, statements) -> tables. */
function findTables(text) {
  function run(split) {
    var tables = [], block = [];
    function flush() {
      if (block.length >= 2) {
        var w = Math.max.apply(null, block.map(function (r) { return r.length; }));
        var hasNum = block.some(function (r) { return r.some(function (c) { return NUMERIC_CELL.test(c); }); });
        if (w >= 2 && hasNum) {
          tables.push(block.map(function (r) {
            var pad = []; for (var i = r.length; i < w; i++) pad.push('');
            return r.slice(0, 1).concat(pad, r.slice(1));          // short rows: numbers right-aligned
          }));
        }
      }
      block = [];
    }
    String(text || '').split(/\r?\n/).forEach(function (line) {
      if (!line.trim()) return;
      var c = split(line);
      if (c.length >= 2) {
        if (block.length && Math.abs(c.length - block[block.length - 1].length) > 2) flush();
        block.push(c);
      } else flush();
    });
    flush();
    return tables;
  }
  var t = run(cellsStrict_);
  return (t.length ? t : run(cellsLoose_)).slice(0, 20);
}


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


// ---- the app screen (generated from Index.html + Styles.html + App.html) ----
var EMBEDDED_PAGE = "<!DOCTYPE html>\n<html>\n<head>\n  <base target=\"_top\">\n  <meta charset=\"utf-8\">\n  <meta name=\"referrer\" content=\"no-referrer\">\n  <style>\n:root {\n  --bg: #f4f6f9; --card: #fff; --ink: #1c2430; --muted: #6b7686; --line: #e3e7ee;\n  --accent: #2563eb; --accent-ink: #fff; --sel: #e8f0ff; --ok: #15803d; --warn: #b45309; --err: #b91c1c;\n  --hex: #2563eb; --hexfill: rgba(37, 99, 235, .22); --grid: #cfd6e1;\n}\n@media (prefers-color-scheme: dark) {\n  :root {\n    --bg: #0f141b; --card: #171d26; --ink: #e6ebf2; --muted: #93a0b2; --line: #273141;\n    --accent: #5b8cff; --sel: #1d2a44; --ok: #4ade80; --warn: #fbbf24; --err: #f87171;\n    --hex: #7aa2ff; --hexfill: rgba(122, 162, 255, .25); --grid: #3a475b;\n  }\n}\n* { box-sizing: border-box; }\nbody { margin: 0; background: var(--bg); color: var(--ink); font: 14px/1.45 system-ui, -apple-system, \"Segoe UI\", Roboto, sans-serif; }\n.hidden { display: none !important; }\n.muted { color: var(--muted); } .small { font-size: 12px; }\n.err { color: var(--err); min-height: 1em; margin: 6px 0 0; white-space: pre-line; }\n.top { display: flex; justify-content: space-between; align-items: center; padding: 10px 18px; border-bottom: 1px solid var(--line); background: var(--card); }\n.brand { display: flex; gap: 8px; align-items: center; font-weight: 650; color: var(--accent); }\n.account { display: flex; gap: 10px; align-items: center; color: var(--muted); }\n.card { background: var(--card); border: 1px solid var(--line); border-radius: 10px; padding: 14px; }\n.login { max-width: 460px; margin: 40px auto; }\nlabel { display: block; margin: 8px 0; font-size: 13px; color: var(--muted); }\ninput, select { display: block; width: 100%; margin-top: 4px; padding: 7px 9px; border: 1px solid var(--line); border-radius: 7px; background: var(--bg); color: var(--ink); font: inherit; }\ninput[type=checkbox], input[type=radio] { display: inline; width: auto; margin: 0 6px 0 0; }\ninput[type=range] { padding: 0; }\n.row { display: flex; gap: 8px; } .row > label { flex: 1; min-width: 0; } .narrow { max-width: 90px; }\n.check { color: var(--ink); }\n.hint { font-size: 12px; color: var(--muted); background: var(--bg); padding: 8px; border-radius: 7px; }\n.btn { border: 1px solid var(--line); background: var(--card); color: var(--ink); padding: 7px 12px; border-radius: 7px; cursor: pointer; font: inherit; white-space: nowrap; }\n.btn:hover { border-color: var(--accent); }\n.btn.primary { background: var(--accent); color: var(--accent-ink); border-color: var(--accent); }\n.btn.ghost { background: transparent; }\n.btn:disabled { opacity: .45; cursor: default; }\n.wide { width: 100%; margin-top: 10px; padding: 10px; font-weight: 600; }\n\n.layout { display: grid; grid-template-columns: 300px minmax(0, 1fr); gap: 14px; padding: 14px; height: calc(100vh - 50px); }\n.layout.with-preview { grid-template-columns: 280px minmax(0, 1fr) minmax(340px, 40%); }\n.side { overflow: auto; } .side h3 { margin: 0 0 6px; }\n.chips { display: flex; flex-wrap: wrap; gap: 5px; margin: 2px 0 6px; }\n.chips button { border: 1px solid var(--line); background: var(--bg); color: var(--ink); border-radius: 99px; padding: 2px 9px; font-size: 12px; cursor: pointer; }\n.chips button:hover { border-color: var(--accent); }\n.types { border: 1px solid var(--line); border-radius: 7px; margin: 8px 0; padding: 4px 10px 8px; }\n.types label { display: inline-block; margin: 4px 10px 4px 0; color: var(--ink); }\ndetails { margin: 8px 0; } summary { cursor: pointer; color: var(--accent); }\n.history { margin-top: 12px; font-size: 12px; }\n.history a { display: block; color: var(--muted); text-decoration: none; padding: 3px 0; border-top: 1px dashed var(--line); cursor: pointer; }\n.history a:hover { color: var(--accent); }\n\n.results { display: flex; flex-direction: column; min-height: 0; padding: 0; }\n.stats { padding: 12px 14px; border-bottom: 1px solid var(--line); }\n.stats b { color: var(--ink); }\n.toolbar { display: flex; gap: 6px; align-items: center; padding: 8px 14px; border-bottom: 1px solid var(--line); flex-wrap: wrap; }\n.toolbar input { width: 220px; margin: 0; }\n.tablewrap { overflow: auto; flex: 1; }\ntable { width: 100%; border-collapse: collapse; }\nth, td { padding: 6px 8px; border-bottom: 1px solid var(--line); text-align: left; vertical-align: middle; }\nth { position: sticky; top: 0; background: var(--card); font-size: 12px; color: var(--muted); font-weight: 600; cursor: pointer; user-select: none; z-index: 1; }\nth.c, td.c { width: 32px; text-align: center; } .r { text-align: right; }\ntr { cursor: pointer; } tbody tr:hover { background: var(--bg); }\ntr.sel { background: var(--sel); } tr.active { outline: 2px solid var(--accent); outline-offset: -2px; }\n.fn { font-weight: 550; overflow-wrap: anywhere; }\ntd.nw { white-space: nowrap; }\n.with-preview .cf { display: none; } .sub { font-size: 12px; color: var(--muted); }\n.badge { display: inline-block; font-size: 11px; padding: 0 6px; border-radius: 99px; background: var(--bg); color: var(--muted); border: 1px solid var(--line); margin-left: 4px; }\n.ext { font-size: 10px; font-weight: 700; text-transform: uppercase; padding: 1px 5px; border-radius: 4px; background: var(--accent); color: var(--accent-ink); margin-right: 6px; }\n.ext.x-pdf { background: #dc2626; } .ext.x-xlsx, .ext.x-xls, .ext.x-csv { background: #16a34a; } .ext.x-docx, .ext.x-doc { background: #2563eb; } .ext.x-jpg, .ext.x-png, .ext.x-jpeg { background: #9333ea; }\n.hexcell { display: flex; align-items: center; gap: 6px; font-variant-numeric: tabular-nums; }\n.empty { padding: 30px; text-align: center; color: var(--muted); }\n\n.dlbar { display: flex; gap: 10px; align-items: flex-end; padding: 10px 14px; border-top: 1px solid var(--line); flex-wrap: wrap; }\n.dlbar label { margin: 0; } .dlbar label:first-child { flex: 1; min-width: 200px; }\n.dlbtns { display: flex; gap: 6px; }\n.destrow { display: flex; gap: 6px; margin-top: 4px; } .destrow input { margin: 0; flex: 1; min-width: 0; }\n.saved { display: flex; gap: 6px; margin: 10px 0 14px; } .saved .btn.primary { flex: 1; }\n.report .btn { margin-left: 8px; padding: 2px 9px; font-size: 12px; }\n.report { padding: 8px 14px 12px; font-size: 13px; border-top: 1px solid var(--line); max-height: 140px; overflow: auto; }\n.report .ok { color: var(--ok); } .report .warn { color: var(--warn); } .report .bad { color: var(--err); }\n\n.preview { display: flex; flex-direction: column; min-height: 0; padding: 0; }\n.pvhead { display: flex; justify-content: space-between; gap: 8px; padding: 10px 12px; border-bottom: 1px solid var(--line); }\n.pvhead b { word-break: break-all; }\n.pvtools { display: flex; gap: 4px; align-items: flex-start; }\n.pvtools .btn { padding: 4px 9px; }\n.pvhex { display: flex; gap: 14px; align-items: center; padding: 8px 12px; border-bottom: 1px solid var(--line); font-size: 12px; }\n.pvhex table { width: auto; flex: none; } .pvhex table td { border: 0; padding: 1px 6px; white-space: nowrap; }\n.pvsnip { flex: 1; min-width: 0; }\n.pvtabs { display: flex; gap: 4px; padding: 6px 12px 0; border-bottom: 1px solid var(--line); }\n.pvtabs button { border: 0; background: none; color: var(--muted); padding: 6px 10px; cursor: pointer; font: inherit; border-bottom: 2px solid transparent; }\n.pvtabs button.on { color: var(--accent); border-bottom-color: var(--accent); }\n.pvbody { flex: 1; min-height: 0; overflow: auto; background: var(--bg); }\n.pvbody iframe { width: 100%; height: 100%; border: 0; background: #fff; }\n.pvbody img { max-width: 100%; display: block; margin: 10px auto; }\n.pvbody pre { white-space: pre-wrap; padding: 12px; margin: 0; font-size: 12px; }\n.pvbody .none { padding: 30px; text-align: center; color: var(--muted); }\n\n.busy { position: fixed; inset: 0; background: rgba(0,0,0,.25); display: flex; gap: 12px; align-items: center; justify-content: center; color: #fff; font-weight: 600; z-index: 10; }\n.spinner { width: 26px; height: 26px; border: 3px solid rgba(255,255,255,.4); border-top-color: #fff; border-radius: 50%; animation: spin .8s linear infinite; }\n@keyframes spin { to { transform: rotate(360deg); } }\n\n@media (max-width: 900px) {\n  .layout, .layout.with-preview { grid-template-columns: 1fr; height: auto; }\n  .preview { position: fixed; inset: 8px; z-index: 5; }\n  .tablewrap { max-height: 60vh; }\n  .hide-sm { display: none; }\n}\n\n/* ===== v2: views, index, table, reader, share ===== */\n.tabs { display: flex; gap: 2px; background: var(--bg); border-radius: 9px; padding: 3px; }\n.tabs button { border: 0; background: none; color: var(--muted); padding: 6px 14px; border-radius: 7px; cursor: pointer; font: inherit; }\n.tabs button.on { background: var(--card); color: var(--accent); font-weight: 600; box-shadow: 0 1px 2px rgba(0,0,0,.08); }\n.pill { font-size: 11px; background: var(--accent); color: var(--accent-ink); border-radius: 99px; padding: 0 6px; margin-left: 3px; }\n.pill:empty { display: none; }\n.jobchip { font-size: 12px; background: var(--sel); color: var(--accent); padding: 3px 9px; border-radius: 99px; }\n.seg { display: flex; border: 1px solid var(--line); border-radius: 8px; overflow: hidden; margin-bottom: 4px; }\n.seg label { flex: 1; margin: 0; }\n.seg input { display: none; }\n.seg span { display: block; text-align: center; padding: 7px 4px; cursor: pointer; color: var(--muted); font-size: 13px; }\n.seg small { display: block; font-size: 10px; }\n.seg input:checked + span { background: var(--accent); color: var(--accent-ink); font-weight: 600; }\n.inrow { display: flex; gap: 6px; margin-top: 4px; } .inrow input { margin: 0; flex: 1; min-width: 0; } .inrow select { margin: 0; width: auto; }\n.btnrow { align-items: center; margin-top: 10px; } .btnrow > .btn { flex: none; } .btnrow > .btn.wide { margin-top: 0; width: auto; flex: 1; }\n.view { min-height: 0; }\n.grow { flex: 1; }\n.spacer { flex: 1; }\n.pad { padding: 12px 16px; }\n.wrap { flex-wrap: wrap; }\n.inl { display: inline-flex; align-items: center; gap: 6px; margin: 0; } .inl select { margin: 0; width: auto; }\n.amt { font-variant-numeric: tabular-nums; white-space: nowrap; }\n.doc { display: inline-block; white-space: nowrap; margin: 0 6px 0 0 !important; font-size: 10px; font-weight: 700; text-transform: uppercase; letter-spacing: .02em; padding: 1px 5px; border-radius: 4px; border: 1px solid var(--line); color: var(--muted); margin-left: 6px; }\n.doc.d-invoice { color: #b45309; border-color: #f2c48d; } .doc.d-receipt { color: var(--ok); border-color: #9ad6ae; }\n.src { font-size: 10px; padding: 0 5px; border-radius: 4px; background: var(--bg); color: var(--muted); }\n.src.s-ocr { background: #fef3c7; color: #92400e; }\n@media (prefers-color-scheme: dark) { .src.s-ocr { background: #3b2f12; color: #fbbf24; } }\n.menu { position: relative; } .menu summary { list-style: none; } .menu summary::-webkit-details-marker { display: none; }\n.menubody { position: absolute; right: 0; bottom: 110%; background: var(--card); border: 1px solid var(--line); border-radius: 8px; box-shadow: 0 8px 24px rgba(0,0,0,.15); padding: 4px; min-width: 240px; z-index: 6; }\n.menubody button { display: block; width: 100%; text-align: left; border: 0; background: none; color: var(--ink); padding: 7px 10px; border-radius: 6px; cursor: pointer; font: inherit; }\n.menubody button:hover { background: var(--bg); } .menubody small { color: var(--muted); }\n.need-sel-s[aria-disabled=true] { opacity: .45; pointer-events: none; }\n.dlbar .dlbtns { flex-wrap: wrap; }\n\n.sheet td[contenteditable] { cursor: text; } .sheet td[contenteditable]:hover { background: var(--bg); }\n.sheet td[contenteditable]:focus { outline: 2px solid var(--accent); outline-offset: -2px; background: var(--card); }\n.sheet td.edited { box-shadow: inset 3px 0 0 var(--ok); }\n.sheet tfoot td { position: sticky; bottom: 0; background: var(--card); font-weight: 650; border-top: 2px solid var(--line); }\n.sheet td { font-size: 13px; } .sheet .sub { max-width: 260px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }\n.groups { padding: 8px 14px; border-bottom: 1px solid var(--line); max-height: 200px; overflow: auto; }\n.groups table td, .groups table th { padding: 3px 10px; font-size: 13px; position: static; }\n\n.kpis { display: grid; grid-template-columns: repeat(auto-fill, minmax(150px, 1fr)); gap: 10px; margin: 12px 0 16px; }\n.kpi { background: var(--bg); border-radius: 9px; padding: 10px 12px; }\n.kpi b { display: block; font-size: 20px; font-variant-numeric: tabular-nums; } .kpi span { font-size: 12px; color: var(--muted); }\n.btnrow2 { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; margin: 6px 0 12px; }\n.btn.danger { color: var(--err); }\n.jobbox { background: var(--bg); border-radius: 9px; padding: 10px 12px; margin-bottom: 12px; }\n.bar { height: 8px; background: var(--line); border-radius: 99px; overflow: hidden; margin-bottom: 6px; }\n.bar i { display: block; height: 100%; width: 0; background: var(--accent); transition: width .3s; }\n.bar.indet i { width: 30% !important; animation: indet 1.2s ease-in-out infinite; }\n@keyframes indet { from { margin-left: -30%; } to { margin-left: 100%; } }\n.idxdetails { display: grid; grid-template-columns: repeat(auto-fit, minmax(240px, 1fr)); gap: 16px; font-size: 13px; }\n.idxdetails table td, .idxdetails table th { padding: 3px 8px; position: static; }\n\n.pvtitle { min-width: 0; }\n.pvtext { display: flex; flex-direction: column; height: 100%; }\n.pvbar { display: flex; gap: 6px; align-items: center; padding: 8px 10px; border-bottom: 1px solid var(--line); background: var(--card); flex-wrap: wrap; }\n.pvbar input { margin: 0; width: 160px; }\n.pvbar .btn { padding: 4px 9px; font-size: 12px; }\n.pvtext pre { flex: 1; overflow: auto; }\nmark { background: #fde047; color: #111; border-radius: 2px; }\n.fieldsform { padding: 12px; display: grid; grid-template-columns: 120px 1fr; gap: 6px 10px; align-items: center; }\n.fieldsform label { margin: 0; } .fieldsform input, .fieldsform select { margin: 0; }\n.fieldsform .full { grid-column: 1 / -1; }\n.dt { margin: 10px; width: calc(100% - 20px); background: var(--card); font-size: 12px; }\n.dt td { border: 1px solid var(--line); padding: 3px 6px; }\n.dt tr:first-child td { font-weight: 600; background: var(--bg); }\n.tblhead { display: flex; gap: 6px; align-items: center; padding: 10px 10px 0; font-size: 13px; }\n\n.dlg { border: 1px solid var(--line); border-radius: 12px; background: var(--card); color: var(--ink); width: min(520px, 92vw); padding: 18px; }\n.dlg::backdrop { background: rgba(0,0,0,.35); }\n.dlg h3 { margin: 0 0 8px; }\ntextarea { display: block; width: 100%; margin-top: 4px; padding: 7px 9px; border: 1px solid var(--line); border-radius: 7px; background: var(--bg); color: var(--ink); font: inherit; resize: vertical; }\n.dlgbtns { display: flex; gap: 6px; flex-wrap: wrap; margin-top: 10px; }\n.toast { position: fixed; left: 50%; bottom: 22px; transform: translateX(-50%); background: #111827; color: #fff; padding: 10px 16px; border-radius: 9px; z-index: 20; max-width: 90vw; box-shadow: 0 6px 20px rgba(0,0,0,.25); }\n@media (max-width: 900px) { .tabs button { padding: 6px 8px; } }\n.detect { font-size: 13px; padding: 8px 10px; border-radius: 7px; background: var(--sel); margin: 4px 0 8px; }\n.detect.bad { background: #fde8e8; color: var(--err); }\n@media (prefers-color-scheme: dark) { .detect.bad { background: #3b1a1a; } }\n.risky { font-size: 10px; font-weight: 700; color: #fff; background: var(--err); border-radius: 4px; padding: 1px 5px; white-space: nowrap; }\n/* ---- Apps Script edition ---- */\n.pdfpages { padding: 10px; display: flex; flex-direction: column; gap: 10px; align-items: center; }\n.pdfpages canvas { max-width: 100%; box-shadow: 0 1px 4px rgba(0,0,0,.2); background: #fff; }\n.progress { position: fixed; left: 50%; top: 64px; transform: translateX(-50%); background: var(--card); border: 1px solid var(--line); border-radius: 10px; padding: 10px 14px; z-index: 15; box-shadow: 0 6px 20px rgba(0,0,0,.15); min-width: 320px; }\n.progress .row { align-items: center; gap: 10px; }\na { color: var(--accent); }\na.btn { text-decoration: none; display: inline-block; }\n.linklist a { display: block; padding: 2px 0; word-break: break-all; }\n.suggest { position: fixed; z-index: 30; background: var(--card); border: 1px solid var(--line); border-radius: 9px; box-shadow: 0 10px 28px rgba(0,0,0,.18); max-height: 340px; overflow: auto; min-width: 280px; }\n.suggest .sg { display: flex; gap: 10px; align-items: baseline; padding: 7px 12px; cursor: pointer; border-bottom: 1px solid var(--line); }\n.suggest .sg:last-child { border-bottom: 0; }\n.suggest .sg.on, .suggest .sg:hover { background: var(--sel); }\n.suggest .sg .nm { font-weight: 600; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; max-width: 32%; flex: none; }\n.suggest .sg .em { color: var(--muted); flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }\n.suggest .sg .ct { font-size: 12px; color: var(--muted); font-variant-numeric: tabular-nums; }\n.suggest .sg.all { color: var(--accent); font-weight: 600; }\n.suggest .head { padding: 6px 12px; font-size: 12px; color: var(--muted); background: var(--bg); position: sticky; top: 0; }\n.suggest mark, #peopleTbl mark { background: #fde047; color: #111; border-radius: 2px; }\n.pad0 { padding: 8px 14px 0; }\n.chips .on { background: var(--accent); color: var(--accent-ink); border-color: var(--accent); }\n#peopleFilter { flex: 1; min-width: 200px; }\n.me { font-size: 11px; color: var(--accent); margin-left: 4px; }\n</style>\n\n</head>\n<body>\n<header class=\"top\">\n  <div class=\"brand\"><svg viewBox=\"0 0 24 24\" width=\"22\" height=\"22\" aria-hidden=\"true\"><path d=\"M12 2l8.66 5v10L12 22l-8.66-5V7z\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\"/><path d=\"M7 9l5 3.5L17 9\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\"/></svg>\n    <span class=\"hide-sm\">Mail Attachment Agent</span></div>\n  <nav id=\"tabs\" class=\"tabs\">\n    <button data-view=\"search\" class=\"on\">Search</button>\n    <button data-view=\"table\">Table</button>\n    <button data-view=\"people\">People</button>\n    <button data-view=\"data\">Data</button>\n  </nav>\n  <div class=\"account\"><span id=\"account\">…</span></div>\n</header>\n\n<main id=\"app\" class=\"layout\">\n  <form id=\"searchForm\" class=\"card side\" autocomplete=\"off\">\n    <p class=\"muted small\">Searches your Gmail directly – fast, and it also looks inside PDFs.</p>\n    <label>From <span class=\"muted\">(name, email or domain)</span>\n      <input name=\"senders\" placeholder=\"type: parthag, mckkutty.com …\" autocomplete=\"off\" data-suggest=\"1\"></label>\n    <p id=\"peopleHint\" class=\"muted small hidden\"><a href=\"#\" id=\"peopleHintBtn\">Scan my mail once</a> to get address suggestions while typing.</p>\n    <label>Keywords <span class=\"muted\">(subject, body, file name, inside files)</span>\n      <span class=\"inrow\"><input name=\"keywords\" placeholder=\"invoice, bill\">\n      <select name=\"match_mode\" title=\"How keywords combine\"><option value=\"any\">any</option><option value=\"all\">all</option><option value=\"phrase\">exact phrase</option></select></span></label>\n    <div class=\"row\">\n      <label>From date<input type=\"date\" name=\"date_from\"></label>\n      <label>To date<input type=\"date\" name=\"date_to\"></label>\n    </div>\n    <div class=\"chips\" id=\"quickDates\">\n      <button type=\"button\" data-q=\"fy\">This FY</button>\n      <button type=\"button\" data-q=\"lfy\">Last FY</button>\n      <button type=\"button\" data-q=\"m\">This month</button>\n      <button type=\"button\" data-q=\"y\">This year</button>\n      <button type=\"button\" data-q=\"ly\">Last year</button>\n      <button type=\"button\" data-q=\"90\">90 days</button>\n      <button type=\"button\" data-q=\"clr\">Any time</button>\n    </div>\n    <fieldset class=\"types\">\n      <legend>File types</legend>\n      <label><input type=\"checkbox\" name=\"t\" value=\"pdf\" checked> PDF</label>\n      <label><input type=\"checkbox\" name=\"t\" value=\"image\"> Images</label>\n      <label><input type=\"checkbox\" name=\"t\" value=\"excel\"> Excel/CSV</label>\n      <label><input type=\"checkbox\" name=\"t\" value=\"word\"> Word</label>\n      <label><input type=\"checkbox\" name=\"t\" value=\"archive\"> ZIP</label>\n      <input name=\"custom_types\" placeholder=\"other: xml, txt\">\n    </fieldset>\n    <fieldset class=\"types\">\n      <legend>Document type <span class=\"muted\">(auto-detected)</span></legend>\n      <label><input type=\"checkbox\" name=\"dt\" value=\"invoice\"> Invoice</label>\n      <label><input type=\"checkbox\" name=\"dt\" value=\"receipt\"> Receipt</label>\n      <label><input type=\"checkbox\" name=\"dt\" value=\"statement\"> Statement</label>\n      <label><input type=\"checkbox\" name=\"dt\" value=\"payslip\"> Payslip</label>\n      <label><input type=\"checkbox\" name=\"dt\" value=\"quotation\"> Quotation</label>\n      <label><input type=\"checkbox\" name=\"dt\" value=\"order\"> Order</label>\n      <label><input type=\"checkbox\" name=\"dt\" value=\"credit_note\"> Credit note</label>\n      <label><input type=\"checkbox\" name=\"dt\" value=\"other\"> Other</label>\n    </fieldset>\n    <details id=\"moreFilters\">\n      <summary>More filters</summary>\n      <label>To / Cc<input name=\"recipients\" placeholder=\"accounts@mckkutty.com\" autocomplete=\"off\" data-suggest=\"1\"></label>\n      <label>Exclude words<input name=\"exclude\" placeholder=\"reminder, cancelled\"></label>\n      <label>File name pattern<input name=\"filename_pattern\" placeholder=\"INV*2025*.pdf  or  part of name\"></label>\n      <div class=\"row\">\n        <label>Size min (MB)<input name=\"size_min_mb\" inputmode=\"decimal\" placeholder=\"0\"></label>\n        <label>Size max (MB)<input name=\"size_max_mb\" inputmode=\"decimal\" placeholder=\"∞\"></label>\n      </div>\n      <div class=\"row\">\n        <label>Amount min<input name=\"amount_min\" inputmode=\"decimal\" placeholder=\"e.g. 10000\"></label>\n        <label>Amount max<input name=\"amount_max\" inputmode=\"decimal\"></label>\n      </div>\n      <p class=\"muted small\">Exact amounts are known after a file is read (Read text).</p>\n      <label>Document text\n        <select name=\"text_state\"><option value=\"any\">any</option><option value=\"has_text\">read</option><option value=\"scanned\">scanned (OCR)</option><option value=\"not_analyzed\">not read yet</option></select></label>\n      <label class=\"check\"><input type=\"checkbox\" name=\"fuzzy\" checked> Typo tolerant (kuty ≈ kutty)</label>\n      <label class=\"check\"><input type=\"checkbox\" name=\"hide_duplicates\" checked> Hide duplicate files</label>\n      <label class=\"check\"><input type=\"checkbox\" name=\"include_inline\"> Include inline images / logos</label>\n      <label class=\"check\"><input type=\"checkbox\" name=\"anywhere\"> Also search Spam &amp; Bin</label>\n      <label>Minimum match score <output id=\"msOut\">0%</output>\n        <input type=\"range\" name=\"min_score\" min=\"0\" max=\"1\" step=\"0.05\" value=\"0\"></label>\n      <label>Date-matrix window\n        <select name=\"window_months\"><option value=\"1\">1 month</option><option value=\"3\" selected>3 months</option><option value=\"6\">6 months</option><option value=\"12\">12 months</option></select></label>\n    </details>\n    <div class=\"row btnrow\">\n      <button class=\"btn primary wide\" type=\"submit\" id=\"goBtn\">Search</button>\n      <button class=\"btn ghost\" type=\"button\" id=\"resetBtn\">Reset</button>\n    </div>\n    <p id=\"searchErr\" class=\"err\"></p>\n    <div id=\"history\" class=\"history\"></div>\n  </form>\n\n  <!-- ======== SEARCH VIEW ======== -->\n  <section class=\"card results view\" id=\"viewSearch\">\n    <div id=\"stats\" class=\"stats muted\">Example: From <b>mck kutty</b>, keyword <b>invoice</b>, 2024-01-01 → 2025-12-25, PDF → <b>Search</b>.</div>\n    <div class=\"toolbar\">\n      <input id=\"filter\" placeholder=\"Filter results…\" type=\"search\">\n      <button class=\"btn ghost\" id=\"selAll\">Select all</button>\n      <button class=\"btn ghost\" id=\"selNone\">None</button>\n      <button class=\"btn ghost\" id=\"selInv\">Invert</button>\n      <span id=\"selInfo\" class=\"muted\">0 selected</span>\n    </div>\n    <div class=\"tablewrap\">\n      <table id=\"tbl\" class=\"grid\">\n        <thead><tr>\n          <th class=\"c\"><input type=\"checkbox\" id=\"chkAll\" aria-label=\"select all\"></th>\n          <th data-sort=\"score\" title=\"Hexagon match\">Match</th>\n          <th data-sort=\"filename\">File</th>\n          <th data-sort=\"fromName\" class=\"cf\">From</th>\n          <th data-sort=\"amount\" class=\"r cf\">Amount</th>\n          <th data-sort=\"date\">Date ▾</th>\n          <th data-sort=\"size\" class=\"r\">Size</th>\n          <th></th>\n        </tr></thead>\n        <tbody></tbody>\n      </table>\n      <div id=\"empty\" class=\"empty hidden\">No attachments matched. Try fewer keywords or a wider date range.</div>\n    </div>\n    <div class=\"dlbar\">\n      <label>Drive folders<select id=\"layout\">\n        <option value=\"sender/year-month\">sender / year-month</option><option value=\"sender/year\">sender / year</option>\n        <option value=\"year/month\">year / month</option><option value=\"sender\">sender</option><option value=\"flat\">one folder</option></select></label>\n      <label>File names<select id=\"naming\"><option value=\"original\">original</option><option value=\"date_original\">date + original</option></select></label>\n      <div class=\"dlbtns\">\n        <button class=\"btn primary need-sel\" id=\"saveBtn\" disabled>Save to Drive</button>\n        <button class=\"btn need-sel\" id=\"zipBtn\" disabled>Download ZIP</button>\n        <button class=\"btn need-sel\" id=\"shareBtn\" disabled>Share…</button>\n        <details class=\"menu\"><summary class=\"btn need-sel-s\">Export ▾</summary>\n          <div class=\"menubody\">\n            <button data-export=\"sheet\">Google Sheet <small>list + monthly totals</small></button>\n            <button data-export=\"csv\">CSV file (.csv)</button>\n            <button data-export=\"txt\">All text (.txt) <small>reads / scans files</small></button>\n          </div></details>\n        <button class=\"btn need-sel\" id=\"readBtn\" disabled title=\"Read text (OCR for scans) and detect number / date / amount\">Read text</button>\n      </div>\n    </div>\n    <div id=\"dlReport\" class=\"report hidden\"></div>\n  </section>\n\n  <!-- ======== TABLE VIEW ======== -->\n  <section class=\"card results view hidden\" id=\"viewTable\">\n    <div class=\"toolbar\">\n      <b>Document table</b><span class=\"muted\" id=\"tblInfo\">Run a search first.</span><span class=\"spacer\"></span>\n      <label class=\"inl\">Group by <select id=\"groupBy\"><option value=\"\">—</option><option value=\"month\">month</option><option value=\"sender\">sender</option><option value=\"doc_type\">document type</option></select></label>\n      <button class=\"btn\" id=\"tblRead\">Read all rows</button>\n      <button class=\"btn\" id=\"tblSheet\">Google Sheet</button>\n      <button class=\"btn\" id=\"tblCsv\">CSV</button>\n    </div>\n    <div id=\"groupSummary\" class=\"groups hidden\"></div>\n    <div class=\"tablewrap\">\n      <table class=\"grid sheet\" id=\"sheet\">\n        <thead><tr>\n          <th data-ssort=\"date\">Email date</th><th data-ssort=\"fromName\">From</th><th data-ssort=\"filename\">File</th>\n          <th data-ssort=\"doc_type\">Document</th><th data-ssort=\"doc_no\">Number</th><th data-ssort=\"doc_date\">Doc date</th>\n          <th data-ssort=\"amount\" class=\"r\">Amount</th><th data-ssort=\"currency\">Cur</th><th data-ssort=\"gstin\">GSTIN</th><th>Text</th>\n        </tr></thead>\n        <tbody></tbody>\n        <tfoot><tr><td colspan=\"6\" id=\"sheetCount\"></td><td class=\"r\" id=\"sheetTotal\"></td><td colspan=\"3\"></td></tr></tfoot>\n      </table>\n    </div>\n    <p class=\"muted small pad\">Click a Number, Date, Amount, Currency or GSTIN cell to correct it – corrections are saved and never overwritten.</p>\n  </section>\n\n  <!-- ======== PEOPLE VIEW ======== -->\n  <section class=\"card results view hidden\" id=\"viewPeople\">\n    <div class=\"stats\" id=\"peopleStats\">Every email address in your mailbox (From, To, Cc), each listed once.</div>\n    <div class=\"toolbar\">\n      <input id=\"peopleFilter\" type=\"search\" placeholder=\"Type a name, address or domain – e.g. parthag or mckkutty.com\" autocomplete=\"off\">\n      <button class=\"btn primary\" id=\"peopleScan\">Scan my mail</button>\n      <button class=\"btn ghost\" id=\"peopleFull\" title=\"Read the whole mailbox again\">Full rescan</button>\n    </div>\n    <div class=\"chips pad0\" id=\"domainChips\"></div>\n    <div class=\"tablewrap\">\n      <table class=\"grid\" id=\"peopleTbl\">\n        <thead><tr><th data-psort=\"name\">Name</th><th data-psort=\"email\">Email</th><th data-psort=\"count\" class=\"r\">Emails</th><th data-psort=\"last\">Last email</th><th></th></tr></thead>\n        <tbody></tbody>\n      </table>\n      <div id=\"peopleEmpty\" class=\"empty hidden\"></div>\n    </div>\n    <div class=\"dlbar\">\n      <span class=\"muted\" id=\"peopleCount\"></span><span class=\"spacer\"></span>\n      <button class=\"btn\" id=\"peopleCopy\">Copy addresses</button>\n      <button class=\"btn\" id=\"peopleCsv\">CSV</button>\n      <button class=\"btn\" id=\"peopleSheet\">Google Sheet</button>\n    </div>\n  </section>\n\n  <!-- ======== DATA VIEW ======== -->\n  <section class=\"card results view hidden\" id=\"viewData\">\n    <div class=\"pad\">\n      <h3>Your data</h3>\n      <p class=\"muted\">Everything stays in your own Google account. The app keeps a small Google Sheet with the details it\n        detected (document type, number, amount, text it read, your corrections). Files you save go to the Drive folder\n        <b>Mail Attachments</b>.</p>\n      <div class=\"kpis\" id=\"kpis\"></div>\n      <div class=\"btnrow2\">\n        <a class=\"btn\" id=\"openStore\" target=\"_blank\" rel=\"noopener\">Open data sheet</a>\n        <a class=\"btn\" id=\"openDrive\" target=\"_blank\" rel=\"noopener\" href=\"https://drive.google.com/drive/search?q=%22Mail%20Attachments%22\">Open \"Mail Attachments\" in Drive</a>\n        <span class=\"spacer\"></span>\n        <button class=\"btn ghost danger\" id=\"eraseBtn\">Erase app data</button>\n      </div>\n      <h3>Security</h3>\n      <ul class=\"muted small\">\n        <li>Runs as you, inside Google. No password is typed or stored anywhere.</li>\n        <li>Gmail access is <b>read-only</b>. The app cannot delete, move or change your mail.</li>\n        <li>Drive access is limited to files this app creates. It cannot see your other Drive files.</li>\n        <li>Sending uses Google's send-only permission, and only when you press Share.</li>\n        <li>Remove access any time: myaccount.google.com → Security → Third-party apps.</li>\n      </ul>\n    </div>\n  </section>\n\n  <!-- ======== PREVIEW / READER ======== -->\n  <aside id=\"preview\" class=\"card preview hidden\">\n    <div class=\"pvhead\">\n      <div class=\"pvtitle\"><b id=\"pvName\"></b><div class=\"muted small\" id=\"pvMeta\"></div></div>\n      <div class=\"pvtools\">\n        <button class=\"btn ghost\" id=\"pvPrev\" title=\"Previous (↑)\">‹</button>\n        <button class=\"btn ghost\" id=\"pvNext\" title=\"Next (↓)\">›</button>\n        <button class=\"btn ghost\" id=\"pvSel\" title=\"Select (space)\">Select</button>\n        <button class=\"btn ghost\" id=\"pvClose\" title=\"Close (Esc)\">✕</button>\n      </div>\n    </div>\n    <div id=\"pvHex\" class=\"pvhex\"></div>\n    <div class=\"pvtabs\" id=\"pvTabs\">\n      <button data-m=\"doc\" class=\"on\">Document</button><button data-m=\"text\">Text</button>\n      <button data-m=\"fields\">Details</button><button data-m=\"tables\">Tables</button>\n    </div>\n    <div id=\"pvBody\" class=\"pvbody\"></div>\n  </aside>\n</main>\n\n<dialog id=\"shareDlg\" class=\"dlg\">\n  <form method=\"dialog\" id=\"shareForm\">\n    <button value=\"noop\" disabled hidden aria-hidden=\"true\"></button>\n    <h3>Share <span id=\"shareCount\"></span></h3>\n    <label>To <span class=\"muted\">(comma separated)</span><input name=\"to\" placeholder=\"accountant@firm.in\" autocomplete=\"off\" data-suggest=\"1\"></label>\n    <label>Subject<input name=\"subject\"></label>\n    <label>Message<textarea name=\"message\" rows=\"4\"></textarea></label>\n    <p class=\"muted small\" id=\"shareNote\"></p>\n    <div class=\"dlgbtns\">\n      <button class=\"btn primary\" value=\"email\">Email the files</button>\n      <button class=\"btn\" value=\"drive\" title=\"Puts the files in a Drive folder and shares it with them\">Share Drive link</button>\n      <button class=\"btn\" value=\"copy\">Copy list</button>\n      <span class=\"spacer\"></span>\n      <button class=\"btn ghost\" value=\"cancel\">Close</button>\n    </div>\n    <p id=\"shareMsg\" class=\"small\"></p>\n  </form>\n</dialog>\n\n<div id=\"progress\" class=\"progress hidden\">\n  <div class=\"bar\"><i id=\"progBar\"></i></div>\n  <div class=\"row\"><span id=\"progText\" class=\"muted small\"></span><span class=\"spacer\"></span><button class=\"btn ghost\" id=\"progCancel\">Stop</button></div>\n</div>\n<div id=\"suggest\" class=\"suggest hidden\" role=\"listbox\"></div>\n<div id=\"busy\" class=\"busy hidden\"><div class=\"spinner\"></div><span id=\"busyMsg\">Working…</span></div>\n<div id=\"toast\" class=\"toast hidden\"></div>\n<script src=\"https://cdnjs.cloudflare.com/ajax/libs/pdf.js/3.11.174/pdf.min.js\"></script>\n<script>\n\"use strict\";\n/* ============================================================ basics */\nconst $ = (s, r = document) => r.querySelector(s);\nconst $$ = (s, r = document) => [...r.querySelectorAll(s)];\nconst esc = (s) => String(s ?? \"\").replace(/[&<>\"']/g, (c) => ({ \"&\": \"&amp;\", \"<\": \"&lt;\", \">\": \"&gt;\", '\"': \"&quot;\", \"'\": \"&#39;\" }[c]));\nconst FACETS = [\"sender\", \"subject\", \"body\", \"filename\", \"filetype\", \"date\"];\nconst FACET_LABEL = { sender: \"Sender\", subject: \"Subject\", body: \"Body/text\", filename: \"File name\", filetype: \"Type\", date: \"Date\" };\nconst DOC = { invoice: \"Invoice\", receipt: \"Receipt\", statement: \"Statement\", payslip: \"Payslip\", quotation: \"Quotation\", order: \"Order\", credit_note: \"Credit note\", other: \"Other\" };\nconst TYPE_GROUPS = { image: [\"jpg\", \"jpeg\", \"png\", \"gif\", \"webp\", \"heic\", \"tif\", \"tiff\", \"bmp\"], excel: [\"xls\", \"xlsx\", \"xlsm\", \"csv\", \"ods\"], word: [\"doc\", \"docx\", \"odt\", \"rtf\"], archive: [\"zip\", \"rar\", \"7z\"], pdf: [\"pdf\"] };\nconst MIME_EXT = { \"application/pdf\": \"pdf\", \"image/jpeg\": \"jpg\", \"image/png\": \"png\" };\n\nlet rows = [], view = [], selected = new Set(), lastClick = -1, activeId = null, lastReq = null;\nlet sortKey = \"date\", sortDir = -1, sheetSort = \"date\", sheetDir = -1, pvMode = \"doc\", stopFlag = false;\nconst docCache = new Map(), fileCache = new Map();\n\nfunction store(k, v) { try { if (v === undefined) return JSON.parse(localStorage.getItem(k)); localStorage.setItem(k, JSON.stringify(v)); } catch { return null; } }\n\n/** google.script.run as a Promise */\nfunction run(fn, ...args) {\n  return new Promise((resolve, reject) => {\n    google.script.run.withSuccessHandler(resolve).withFailureHandler((e) => reject(new Error((e && e.message) || String(e))))[fn](...args);\n  });\n}\n/** run `worker` over items with at most n in flight (Apps Script handles parallel calls well up to ~10) */\nasync function pool(items, n, worker, onEach) {\n  let i = 0, done = 0;\n  const errors = [];\n  async function next() {\n    while (i < items.length && !stopFlag) {\n      const k = i++;\n      try { await worker(items[k], k); } catch (e) { errors.push(e); }\n      done++; onEach?.(done, items.length);\n    }\n  }\n  await Promise.all(Array.from({ length: Math.min(n, items.length) }, next));\n  return errors;\n}\nconst chunk = (a, n) => Array.from({ length: Math.ceil(a.length / n) }, (_, i) => a.slice(i * n, i * n + n));\n\nfunction busy(on, msg) { $(\"#busy\").classList.toggle(\"hidden\", !on); if (msg) $(\"#busyMsg\").textContent = msg; }\nfunction progress(text, done, total) {\n  const p = $(\"#progress\");\n  if (text == null) { p.classList.add(\"hidden\"); return; }\n  p.classList.remove(\"hidden\"); $(\"#progText\").textContent = text;\n  const pct = total ? Math.round((100 * done) / total) : null;\n  $(\"#progBar\").parentElement.classList.toggle(\"indet\", pct == null);\n  $(\"#progBar\").style.width = (pct ?? 30) + \"%\";\n}\n$(\"#progCancel\").addEventListener(\"click\", () => { stopFlag = true; });\nlet toastT;\nfunction toast(msg, ms = 4000) { const t = $(\"#toast\"); t.textContent = msg; t.classList.remove(\"hidden\"); clearTimeout(toastT); toastT = setTimeout(() => t.classList.add(\"hidden\"), ms); }\nfunction size(n) { if (n < 1024) return n + \" B\"; if (n < 1048576) return (n / 1024).toFixed(1) + \" KB\"; return (n / 1048576).toFixed(1) + \" MB\"; }\nfunction fmtDate(iso) { return iso ? iso.slice(0, 10) : \"—\"; }\nfunction money(v, cur) { if (v == null || v === \"\") return \"\"; const s = Number(v).toLocaleString(\"en-IN\", { minimumFractionDigits: 2, maximumFractionDigits: 2 }); return (cur && cur !== \"INR\" ? cur + \" \" : cur === \"INR\" ? \"₹\" : \"\") + s; }\nconst byId = (id) => rows.find((x) => x.key === id);\nfunction report(html) { const r = $(\"#dlReport\"); r.innerHTML = html; r.classList.remove(\"hidden\"); }\nfunction downloadBlob(blob, name) {\n  const a = Object.assign(document.createElement(\"a\"), { href: URL.createObjectURL(blob), download: name });\n  document.body.append(a); a.click(); a.remove(); setTimeout(() => URL.revokeObjectURL(a.href), 10000);\n}\nfunction b64ToBytes(b64) { const bin = atob(b64); const u = new Uint8Array(bin.length); for (let i = 0; i < bin.length; i++) u[i] = bin.charCodeAt(i); return u; }\n\n/* ============================================================ hexagon scoring */\nconst MATCH = 0.6, FUZZY = 0.84;\nconst tokens = (s) => (String(s).toLowerCase().match(/[0-9a-z]+/g) || []);\nfunction withInitials(toks) { const out = new Set(toks); let run = \"\"; for (const t of [...toks, \"\"]) { if (t.length === 1) run += t; else { if (run.length > 1) out.add(run); run = \"\"; } } return out; }\nfunction similarity(a, b) {          // 1 - Levenshtein / max length\n  const m = a.length, n = b.length; if (!m || !n) return 0;\n  let prev = Array.from({ length: n + 1 }, (_, j) => j);\n  for (let i = 1; i <= m; i++) { const cur = [i]; for (let j = 1; j <= n; j++) cur[j] = Math.min(prev[j] + 1, cur[j - 1] + 1, prev[j - 1] + (a[i - 1] === b[j - 1] ? 0 : 1)); prev = cur; }\n  return 1 - prev[n] / Math.max(m, n);\n}\nfunction termScore(term, text, fuzzy = true) {\n  if (!term || !text) return 0;\n  const t = term.toLowerCase().trim(), x = String(text).toLowerCase();\n  if (x.includes(t)) return 1;\n  const tc = t.replace(/[^0-9a-z]/g, \"\");\n  if (tc.length >= 3 && x.slice(0, 30000).replace(/[^0-9a-z]/g, \"\").includes(tc)) return 1;\n  const want = tokens(term), have = withInitials(tokens(x.slice(0, 30000)));\n  if (!want.length || !have.size) return 0;\n  let got = 0;\n  for (const w of want) {\n    if (have.has(w)) { got++; continue; }\n    if (!fuzzy || w.length < 4) continue;\n    let best = 0;\n    for (const h of have) if (Math.abs(h.length - w.length) <= 2 && (h[0] === w[0] || h.slice(-2) === w.slice(-2))) best = Math.max(best, similarity(w, h));\n    if (best >= FUZZY) got += best;\n  }\n  return got / want.length;\n}\nconst bestOf = (terms, text, fz) => Math.max(0, ...terms.map((t) => termScore(t, text, fz)));\nfunction normTypes(types) { const out = []; types.forEach((t) => { t = t.toLowerCase().replace(/^[.*]+/, \"\"); (TYPE_GROUPS[t] || [t]).forEach((e) => { if (e && !out.includes(e)) out.push(e); }); }); return out; }\nconst extOf = (a) => a.ext || MIME_EXT[a.mime] || \"\";\n\nfunction scoreItem(a, q, types) {\n  const f = Object.fromEntries(FACETS.map((k) => [k, null]));\n  let fz = q.fuzzy, kws = q.keywords;\n  if (kws.length && q.match_mode === \"phrase\") { kws = [kws.join(\" \")]; fz = false; }\n  if (q.senders.length) f.sender = bestOf(q.senders, `${a.fromName} ${a.fromEmail}`, fz);\n  let kwOk = true;\n  if (kws.length) {\n    const texts = { subject: a.subject, body: a.snippet + \"\\n\" + (a.text || \"\"), filename: a.filename };\n    const per = kws.map((k) => Object.fromEntries(Object.entries(texts).map(([n, t]) => [n, termScore(k, t, fz)])));\n    for (const n of Object.keys(texts)) f[n] = Math.max(...per.map((p) => p[n]));\n    // Gmail also matches words *inside* attachments: trust its hit when our own text check can't see it\n    if (!a.text && a.gmailHit) f.body = Math.max(f.body, MATCH);\n    kwOk = q.match_mode === \"all\" ? per.every((p) => Math.max(...Object.values(p)) >= MATCH || (!a.text && a.gmailHit))\n                                  : Math.max(f.subject, f.body, f.filename) >= MATCH;\n  }\n  if (types.length) f.filetype = types.includes(extOf(a)) ? 1 : 0;\n  if (q.fromTs || q.toTs) { const t = Date.parse(a.date); f.date = (!q.fromTs || t >= q.fromTs) && (!q.toTs || t <= q.toTs) ? 1 : 0; }\n  let ok = kwOk;\n  if (f.date !== null && f.date < 1) ok = false;\n  if (f.filetype !== null && f.filetype < 1) ok = false;\n  if (f.sender !== null && f.sender < MATCH) ok = false;\n  if (q.exclude.some((e) => termScore(e, `${a.subject} ${a.filename}`, false) >= 1)) ok = false;\n  const active = Object.entries(f).filter(([k, v]) => v !== null && ![\"subject\", \"body\", \"filename\"].includes(k)).map(([, v]) => v);\n  if (kws.length) { const kw = [f.subject, f.body, f.filename].sort((x, y) => y - x); active.push(Math.min(1, kw[0] + 0.1 * kw[1] + 0.05 * kw[2])); }\n  return { score: active.length ? active.reduce((s, v) => s + v, 0) / active.length : 1, facets: f, ok };\n}\nfunction wildcard(pat) { return new RegExp(\"^\" + pat.replace(/[.+^${}()|[\\]\\\\]/g, \"\\\\$&\").replace(/\\*/g, \".*\").replace(/\\?/g, \".\") + \"$\", \"i\"); }\nfunction passesFilters(a, q) {\n  if (q.recipients.length && bestOf(q.recipients, a.to, q.fuzzy) < MATCH) return false;\n  const pat = q.filename_pattern.trim();\n  if (pat && (/[*?]/.test(pat) ? !wildcard(pat).test(a.filename) : !a.filename.toLowerCase().includes(pat.toLowerCase()))) return false;\n  const amt = a.fields.amount;\n  if (q.amount_min !== null && (amt == null || amt < q.amount_min)) return false;\n  if (q.amount_max !== null && (amt == null || amt > q.amount_max)) return false;\n  if (q.doc_types.length && !q.doc_types.includes(a.fields.doc_type || \"other\")) return false;\n  if (q.text_state === \"has_text\" && !a.text_source) return false;\n  if (q.text_state === \"scanned\" && a.text_source !== \"ocr\") return false;\n  if (q.text_state === \"not_analyzed\" && a.text_source) return false;\n  if (!q.include_inline && a.inline) return false;\n  return true;\n}\nfunction hexSVG(f, px = 30) {\n  const c = px / 2, R = px / 2 - 2;\n  const pt = (i, r) => { const a = -Math.PI / 2 + (i * Math.PI) / 3; return [c + r * Math.cos(a), c + r * Math.sin(a)]; };\n  const poly = (r) => FACETS.map((_, i) => pt(i, r).map((v) => v.toFixed(1)).join(\",\")).join(\" \");\n  const vals = FACETS.map((k, i) => pt(i, R * (f[k] == null ? 0.15 : Math.max(0.08, f[k]))).map((v) => v.toFixed(1)).join(\",\")).join(\" \");\n  const tip = FACETS.map((k) => `${FACET_LABEL[k]}: ${f[k] == null ? \"not used\" : Math.round(f[k] * 100) + \"%\"}`).join(\"\\n\");\n  return `<svg width=\"${px}\" height=\"${px}\" viewBox=\"0 0 ${px} ${px}\" role=\"img\" aria-label=\"match hexagon\"><title>${esc(tip)}</title>\n    <polygon points=\"${poly(R)}\" fill=\"none\" stroke=\"var(--grid)\" stroke-width=\"1\"/><polygon points=\"${poly(R / 2)}\" fill=\"none\" stroke=\"var(--grid)\" stroke-width=\".6\"/>\n    <polygon points=\"${vals}\" fill=\"var(--hexfill)\" stroke=\"var(--hex)\" stroke-width=\"1.4\" stroke-linejoin=\"round\"/></svg>`;\n}\n\n/* ============================================================ form */\nconst list = (s) => String(s || \"\").split(/[,;\\n]/).map((x) => x.trim()).filter(Boolean);\nconst num = (s) => { const t = String(s || \"\").replace(/,/g, \"\").trim(); return t === \"\" || isNaN(+t) ? null : +t; };\nfunction iso(d) { return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, \"0\")}-${String(d.getDate()).padStart(2, \"0\")}`; }\nconst TEXT_FIELDS = [\"senders\", \"recipients\", \"keywords\", \"exclude\", \"date_from\", \"date_to\", \"filename_pattern\", \"size_min_mb\", \"size_max_mb\", \"amount_min\", \"amount_max\"];\nfunction readForm() {\n  const f = $(\"#searchForm\"), raw = {};\n  for (const k of TEXT_FIELDS) raw[k] = f[k].value.trim();\n  const types = $$(\"[name=t]:checked\", f).map((x) => x.value).concat(f.custom_types.value.split(/[,\\s]+/).filter(Boolean));\n  Object.assign(raw, { types, doc_types: $$(\"[name=dt]:checked\", f).map((x) => x.value), match_mode: f.match_mode.value, text_state: f.text_state.value,\n    fuzzy: f.fuzzy.checked, hide_duplicates: f.hide_duplicates.checked, include_inline: f.include_inline.checked, anywhere: f.anywhere.checked,\n    min_score: +f.min_score.value, window_months: +f.window_months.value });\n  return raw;\n}\nfunction toQuery(raw) {\n  return { ...raw, senders: list(raw.senders), keywords: list(raw.keywords), exclude: list(raw.exclude), recipients: list(raw.recipients),\n    fromTs: raw.date_from ? new Date(raw.date_from + \"T00:00:00\").getTime() : null,\n    toTs: raw.date_to ? new Date(raw.date_to + \"T23:59:59.999\").getTime() : null,\n    amount_min: num(raw.amount_min), amount_max: num(raw.amount_max), size_min: num(raw.size_min_mb), size_max: num(raw.size_max_mb) };\n}\nfunction fillForm(q) {\n  const f = $(\"#searchForm\");\n  for (const k of TEXT_FIELDS) f[k].value = q[k] || \"\";\n  const groups = $$(\"[name=t]\", f).map((x) => x.value);\n  $$(\"[name=t]\", f).forEach((x) => (x.checked = (q.types || []).includes(x.value)));\n  $$(\"[name=dt]\", f).forEach((x) => (x.checked = (q.doc_types || []).includes(x.value)));\n  f.custom_types.value = (q.types || []).filter((t) => !groups.includes(t)).join(\", \");\n  f.match_mode.value = q.match_mode || \"any\"; f.text_state.value = q.text_state || \"any\";\n  f.fuzzy.checked = q.fuzzy !== false; f.hide_duplicates.checked = q.hide_duplicates !== false;\n  f.include_inline.checked = !!q.include_inline; f.anywhere.checked = !!q.anywhere;\n}\n$(\"#quickDates\").addEventListener(\"click\", (e) => {\n  const q = e.target.dataset.q; if (!q) return;\n  const t = new Date(), y = t.getFullYear(), fy = t.getMonth() >= 3 ? y : y - 1;\n  const set = (a, b) => { $(\"[name=date_from]\").value = a; $(\"[name=date_to]\").value = b; };\n  ({ fy: () => set(`${fy}-04-01`, `${fy + 1}-03-31`), lfy: () => set(`${fy - 1}-04-01`, `${fy}-03-31`), m: () => set(iso(new Date(y, t.getMonth(), 1)), iso(t)),\n     y: () => set(`${y}-01-01`, `${y}-12-31`), ly: () => set(`${y - 1}-01-01`, `${y - 1}-12-31`), 90: () => set(iso(new Date(Date.now() - 90 * 864e5)), iso(t)), clr: () => set(\"\", \"\") })[q]();\n});\n$(\"[name=min_score]\").addEventListener(\"input\", (e) => { $(\"#msOut\").textContent = Math.round(e.target.value * 100) + \"%\"; });\n$(\"#resetBtn\").addEventListener(\"click\", () => fillForm({ types: [\"pdf\"] }));\nconst qLabel = (q) => [q.senders, q.keywords, q.doc_types?.length && q.doc_types.join(\"/\"), q.date_from && `${q.date_from}→${q.date_to || \"…\"}`].filter(Boolean).join(\" · \") || \"search\";\nfunction renderHistory() {\n  const h = store(\"history\") || [];\n  $(\"#history\").innerHTML = h.length ? \"<div class='muted'>Recent searches</div>\" + h.map((q, i) => `<a data-i=\"${i}\">${esc(qLabel(q))}</a>`).join(\"\") : \"\";\n}\n$(\"#history\").addEventListener(\"click\", (e) => { const i = e.target.dataset.i; if (i != null) { fillForm(store(\"history\")[i]); $(\"#searchForm\").requestSubmit(); } });\n\n/* ============================================================ search */\n$(\"#searchForm\").addEventListener(\"submit\", async (e) => {\n  e.preventDefault(); $(\"#searchErr\").textContent = \"\";\n  const raw = readForm(), q = toQuery(raw);\n  if (!q.senders.length && !q.keywords.length && !q.fromTs && !q.toTs && !q.recipients.length && !q.filename_pattern) {\n    $(\"#searchErr\").textContent = \"Give at least a sender, a keyword or a date range\"; return;\n  }\n  stopFlag = false; const t0 = performance.now();\n  try {\n    progress(\"Planning search…\");\n    const plan = await run(\"planSearch\", { senders: q.senders, keywords: q.keywords, matchMode: q.match_mode, exclude: q.exclude, recipients: q.recipients,\n      fromTs: q.fromTs, toTs: q.toTs, types: q.types, sizeMinMb: q.size_min, sizeMaxMb: q.size_max, anywhere: q.anywhere, windowMonths: q.window_months });\n    // 1. matrix cells in parallel -> a Set of message ids (no duplicates)\n    const ids = new Set();\n    await pool(plan.queries, 6, async (query) => {\n      let tok = null;\n      do { const r = await run(\"listIds\", query, tok); r.ids.forEach((i) => ids.add(i)); tok = r.next; } while (tok && ids.size < 5000 && !stopFlag);\n    }, (d, n) => progress(`Searching Gmail · ${d}/${n} matrix cells · ${ids.size} emails`, d, n));\n    // 2. attachment details in parallel batches\n    const items = [];\n    await pool(chunk([...ids], 15), 8, async (c) => { items.push(...(await run(\"getMeta\", c))); },\n      (d, n) => progress(`Reading ${ids.size} emails · ${items.length} attachments`, d, n));\n    // 3. what we already know (read text, amounts, your corrections)\n    progress(\"Merging saved details…\");\n    const known = items.length ? await run(\"getStored\", items.map((i) => i.key)) : {};\n    items.forEach((it) => { const k = known[it.key]; it.gmailHit = true; if (k) { it.fields = k.fields; it.text_source = k.text_source; it.text = k.text; it.drive_id = k.drive_id; } });\n    // 4. same email under two ids (forwarded copies share Message-ID) -> keep one\n    const seen = new Set(); let dupMsgs = 0;\n    const uniq = items.filter((it) => { const k = (it.messageId || it.msgId) + \"|\" + it.partId; if (seen.has(k)) { dupMsgs++; return false; } seen.add(k); return true; });\n    // 5. hexagon + exact filters\n    const types = normTypes(q.types);\n    let matched = [];\n    for (const it of uniq) {\n      if (!passesFilters(it, q)) continue;\n      const s = scoreItem(it, q, types);\n      if (s.ok && s.score >= q.min_score) matched.push(Object.assign(it, { score: s.score, facets: s.facets, duplicates: 0 }));\n    }\n    // 6. same file sent again -> one row with \"+n dup\"\n    matched.sort((a, b) => Date.parse(a.date) - Date.parse(b.date));\n    let dupFiles = 0;\n    if (q.hide_duplicates) {\n      const keep = new Map();\n      for (const a of matched) { const k = a.filename.toLowerCase() + \"|\" + Math.floor(a.size / 64); if (keep.has(k)) { keep.get(k).duplicates++; dupFiles++; } else keep.set(k, a); }\n      matched = [...keep.values()];\n    }\n    matched.sort((a, b) => Date.parse(b.date) - Date.parse(a.date));\n    rows = matched; selected.clear(); lastClick = -1; docCache.clear(); lastReq = q;\n    store(\"lastQuery\", raw);\n    store(\"history\", [raw, ...(store(\"history\") || []).filter((x) => qLabel(x) !== qLabel(raw))].slice(0, 8)); renderHistory();\n    const secs = ((performance.now() - t0) / 1000).toFixed(1), total = rows.reduce((s, r) => s + (r.fields.amount || 0), 0);\n    $(\"#stats\").innerHTML = `<b>${rows.length}</b> attachment${rows.length === 1 ? \"\" : \"s\"} · <b>${size(rows.reduce((s, r) => s + r.size, 0))}</b>` +\n      (total ? ` · total <b>${money(total)}</b>` : \"\") + ` · <b>${secs} s</b> <span class=\"badge\">${plan.cells} matrix cells</span>` +\n      `<span class=\"badge\">${ids.size} emails</span>` + (dupMsgs ? `<span class=\"badge\">${dupMsgs} duplicate emails removed</span>` : \"\") +\n      (dupFiles ? `<span class=\"badge\">${dupFiles} duplicate files removed</span>` : \"\") + (stopFlag ? `<span class=\"badge\">stopped early</span>` : \"\");\n    $(\"#dlReport\").classList.add(\"hidden\");\n    showView(\"search\"); render(); renderSheet();\n  } catch (er) { $(\"#searchErr\").textContent = er.message; }\n  finally { progress(null); }\n});\n\n/* ============================================================ results table */\nfunction sortVal(r, k) { return k === \"amount\" ? (r.fields.amount ?? -1) : (r[k] ?? \"\"); }\nfunction render() {\n  const f = $(\"#filter\").value.trim().toLowerCase();\n  view = rows.filter((r) => !f || `${r.filename} ${r.fromName} ${r.fromEmail} ${r.subject} ${r.date} ${r.fields.doc_no || \"\"} ${r.fields.amount || \"\"}`.toLowerCase().includes(f));\n  view.sort((a, b) => { const x = sortVal(a, sortKey), y = sortVal(b, sortKey); return (x < y ? -1 : x > y ? 1 : 0) * sortDir; });\n  $(\"#tbl tbody\").innerHTML = view.map((r, i) => {\n    const d = r.fields.doc_type && r.fields.doc_type !== \"other\" ? `<span class=\"doc d-${esc(r.fields.doc_type)}\">${esc(DOC[r.fields.doc_type] || r.fields.doc_type)}${r.fields.doc_no ? \" \" + esc(r.fields.doc_no) : \"\"}</span>` : \"\";\n    return `<tr data-i=\"${i}\" class=\"${selected.has(r.key) ? \"sel\" : \"\"} ${r.key === activeId ? \"active\" : \"\"}\">\n      <td class=\"c\"><input type=\"checkbox\" ${selected.has(r.key) ? \"checked\" : \"\"} aria-label=\"select\"></td>\n      <td><div class=\"hexcell\">${hexSVG(r.facets)}<span>${Math.round(r.score * 100)}%</span></div></td>\n      <td><div class=\"fn\"><span class=\"ext x-${esc(r.ext)}\">${esc(r.ext || \"file\")}</span>${esc(r.filename)}${r.duplicates ? `<span class=\"badge\" title=\"identical copies hidden\">+${r.duplicates} dup</span>` : \"\"}${r.text_source === \"ocr\" ? ` <span class=\"src s-ocr\">OCR</span>` : \"\"}${r.risky ? ` <span class=\"risky\" title=\"Can run programs/macros – only open if you trust the sender\">⚠ risky file</span>` : \"\"}</div>\n          <div class=\"sub\">${d}${esc(r.subject)}</div></td>\n      <td class=\"cf\"><div>${esc(r.fromName)}</div><div class=\"sub hide-sm\">${esc(r.fromEmail)}</div></td>\n      <td class=\"r cf amt\">${money(r.fields.amount, r.fields.currency)}</td>\n      <td class=\"nw\">${fmtDate(r.date)}</td><td class=\"r nw\">${size(r.size)}</td>\n      <td><button class=\"btn ghost pv\" title=\"Preview\">👁</button></td></tr>`;\n  }).join(\"\");\n  $(\"#empty\").classList.toggle(\"hidden\", !!view.length || !rows.length);\n  $$(\"#tbl th[data-sort]\").forEach((th) => { th.textContent = th.textContent.replace(/ [▴▾]$/, \"\") + (th.dataset.sort === sortKey ? (sortDir < 0 ? \" ▾\" : \" ▴\") : \"\"); });\n  updateSel();\n}\nfunction updateSel() {\n  const sel = rows.filter((r) => selected.has(r.key));\n  const amt = sel.reduce((a, r) => a + (r.fields.amount || 0), 0);\n  $(\"#selInfo\").textContent = `${selected.size} selected${selected.size ? \" · \" + size(sel.reduce((a, r) => a + r.size, 0)) : \"\"}${amt ? \" · \" + money(amt) : \"\"}`;\n  $$(\".need-sel\").forEach((b) => (b.disabled = !selected.size));\n  $$(\".need-sel-s\").forEach((b) => b.setAttribute(\"aria-disabled\", String(!selected.size)));\n  $(\"#chkAll\").checked = view.length > 0 && view.every((r) => selected.has(r.key));\n  $(\"#pvSel\").textContent = activeId && selected.has(activeId) ? \"Selected ✓\" : \"Select\";\n}\nfunction toggle(id, on) { (on ?? !selected.has(id)) ? selected.add(id) : selected.delete(id); }\n$(\"#tbl tbody\").addEventListener(\"click\", (e) => {\n  const tr = e.target.closest(\"tr\"); if (!tr) return;\n  const i = +tr.dataset.i, r = view[i];\n  if (e.target.closest(\".pv\")) { openPreview(r.key); return; }\n  if (e.shiftKey && lastClick >= 0) { const [a, b] = [Math.min(lastClick, i), Math.max(lastClick, i)]; const on = !selected.has(r.key); for (let k = a; k <= b; k++) toggle(view[k].key, on); }\n  else if (e.target.type === \"checkbox\" || e.ctrlKey || e.metaKey) toggle(r.key);\n  else { openPreview(r.key); return; }\n  lastClick = i; render();\n});\n$(\"#tbl thead\").addEventListener(\"click\", (e) => {\n  const th = e.target.closest(\"th[data-sort]\"); if (!th) return;\n  sortDir = th.dataset.sort === sortKey ? -sortDir : ([\"date\", \"score\", \"amount\", \"size\"].includes(th.dataset.sort) ? -1 : 1);\n  sortKey = th.dataset.sort; render();\n});\n$(\"#chkAll\").addEventListener(\"click\", (e) => { e.stopPropagation(); const on = e.target.checked; view.forEach((r) => toggle(r.key, on)); render(); });\n$(\"#selAll\").addEventListener(\"click\", () => { view.forEach((r) => selected.add(r.key)); render(); });\n$(\"#selNone\").addEventListener(\"click\", () => { selected.clear(); render(); });\n$(\"#selInv\").addEventListener(\"click\", () => { view.forEach((r) => toggle(r.key)); render(); });\n$(\"#filter\").addEventListener(\"input\", render);\nconst selItems = () => rows.filter((r) => selected.has(r.key));\nconst slim = (r) => ({ key: r.key, msgId: r.msgId, partId: r.partId, filename: r.filename, ext: r.ext, mime: r.mime, fromName: r.fromName, fromEmail: r.fromEmail, subject: r.subject, date: r.date, size: r.size, fields: r.fields, text_source: r.text_source });\n\n/* ============================================================ preview / reader */\nasync function getFile(r) {\n  if (fileCache.has(r.key)) return fileCache.get(r.key);\n  const f = await run(\"getAttachment\", r.msgId, r.partId);\n  if (fileCache.size > 30) fileCache.delete(fileCache.keys().next().value);\n  fileCache.set(r.key, f);\n  return f;\n}\nasync function getDoc(r, force) {\n  if (!force && docCache.has(r.key)) return docCache.get(r.key);\n  const d = await run(\"readText\", slim(r), !!force);\n  docCache.set(r.key, d);\n  Object.assign(r, { fields: d.fields, text_source: d.source, text: d.text });\n  return d;\n}\nasync function openPreview(id) {\n  activeId = id;\n  const r = byId(id); if (!r) return;\n  $(\"#preview\").classList.remove(\"hidden\"); $(\"#app\").classList.add(\"with-preview\");\n  $(\"#pvName\").textContent = r.filename;\n  $(\"#pvMeta\").textContent = `${r.fromName} · ${fmtDate(r.date)} · ${size(r.size)}`;\n  $(\"#pvHex\").innerHTML = hexSVG(r.facets, 86) + \"<table>\" + FACETS.map((k) => `<tr><td class=\"muted\">${FACET_LABEL[k]}</td><td>${r.facets[k] == null ? \"—\" : Math.round(r.facets[k] * 100) + \"%\"}</td></tr>`).join(\"\") + \"</table>\" +\n    `<div class=\"sub pvsnip\">${esc(r.subject)}<br>${esc(r.snippet.slice(0, 180))}</div>`;\n  $$(\"#pvTabs button\").forEach((b) => b.classList.toggle(\"on\", b.dataset.m === pvMode));\n  render();\n  const body = $(\"#pvBody\");\n  body.innerHTML = `<div class='none'>${pvMode === \"doc\" ? \"Loading…\" : r.text_source ? \"Loading…\" : \"Reading the file with Google (scans are OCR'd)…\"}</div>`;\n  try {\n    if (pvMode === \"doc\") return await showDocument(r);\n    const d = await getDoc(r);\n    if (activeId !== id) return;\n    ({ text: showText, fields: showFields, tables: showTables })[pvMode](r, d);\n    render();\n  } catch (er) { if (activeId === id) body.innerHTML = `<div class='none'>${esc(er.message)}</div>`; }\n}\nasync function showDocument(r) {\n  const f = await getFile(r);\n  if (activeId !== r.key) return;\n  const body = $(\"#pvBody\");\n  if (f.tooBig) { body.innerHTML = `<div class=\"none\">File is ${size(f.size)} – too big to preview here.<br>Use <b>Save to Drive</b> and open it there.</div>`; return; }\n  const ext = extOf(r), mime = (f.mime || r.mime || \"\").toLowerCase();\n  if (ext === \"pdf\" || mime === \"application/pdf\") return renderPdf(b64ToBytes(f.b64), body, r.key);\n  if (/^image\\/(png|jpe?g|gif|webp|bmp)$/.test(mime)) { body.innerHTML = `<img alt=\"\" src=\"data:${mime};base64,${f.b64}\">`; return; }\n  if (/^text\\//.test(mime) || [\"txt\", \"csv\", \"json\", \"xml\", \"log\", \"eml\", \"html\", \"htm\", \"md\"].includes(ext)) {\n    body.innerHTML = `<pre>${esc(new TextDecoder().decode(b64ToBytes(f.b64)).slice(0, 400000))}</pre>`; return;   // HTML shown as text, never rendered\n  }\n  body.innerHTML = `<div class=\"none\">No picture preview for .${esc(ext || \"?\")} files.<br>Open the <b>Text</b> or <b>Tables</b> tab, or Save to Drive.</div>`;\n}\nlet pdfWorkerReady = null;\nasync function pdfReady() {\n  if (!window.pdfjsLib) throw new Error(\"PDF viewer could not load – use the Text tab or Save to Drive\");\n  if (!pdfWorkerReady) {\n    pdfWorkerReady = (async () => {\n      // Apps Script pages run in a sandbox: load the worker as a same-origin blob\n      try {\n        const src = await (await fetch(\"https://cdnjs.cloudflare.com/ajax/libs/pdf.js/3.11.174/pdf.worker.min.js\")).text();\n        pdfjsLib.GlobalWorkerOptions.workerSrc = URL.createObjectURL(new Blob([src], { type: \"text/javascript\" }));\n      } catch { pdfjsLib.GlobalWorkerOptions.workerSrc = \"https://cdnjs.cloudflare.com/ajax/libs/pdf.js/3.11.174/pdf.worker.min.js\"; }\n    })();\n  }\n  return pdfWorkerReady;\n}\nasync function renderPdf(bytes, body, key) {\n  await pdfReady();\n  const pdf = await pdfjsLib.getDocument({ data: bytes, isEvalSupported: false }).promise;\n  if (activeId !== key) return;\n  body.innerHTML = `<div class=\"pdfpages\"></div>${pdf.numPages > 10 ? `<div class=\"none small\">Showing 10 of ${pdf.numPages} pages</div>` : \"\"}`;\n  const wrap = body.querySelector(\".pdfpages\"), width = Math.max(320, body.clientWidth - 30);\n  for (let p = 1; p <= Math.min(pdf.numPages, 10); p++) {\n    if (activeId !== key) return;\n    const page = await pdf.getPage(p), vp0 = page.getViewport({ scale: 1 });\n    const vp = page.getViewport({ scale: (width / vp0.width) * (window.devicePixelRatio || 1) });\n    const c = document.createElement(\"canvas\"); c.width = vp.width; c.height = vp.height; c.style.width = width + \"px\";\n    wrap.append(c);\n    await page.render({ canvasContext: c.getContext(\"2d\"), viewport: vp }).promise;\n  }\n}\nfunction highlight(text, q) {\n  const safe = esc(text), words = (q || \"\").split(/[,\\s]+/).filter((w) => w.length > 1).map((w) => w.replace(/[.*+?^${}()|[\\]\\\\]/g, \"\\\\$&\"));\n  return words.length ? safe.replace(new RegExp(`(${words.join(\"|\")})`, \"gi\"), \"<mark>$1</mark>\") : safe;\n}\nfunction showText(r, d) {\n  const src = { pdf: \"PDF text\", ocr: \"scanned – Google OCR\", office: \"Office document\", plain: \"text file\", none: \"no text found\" }[d.source] || d.source || \"\";\n  const q = $(\"[name=keywords]\").value;\n  $(\"#pvBody\").innerHTML = `<div class=\"pvtext\"><div class=\"pvbar\"><span class=\"src ${d.source === \"ocr\" ? \"s-ocr\" : \"\"}\">${esc(src)}</span>\n      <input id=\"pvFind\" type=\"search\" placeholder=\"Find in text…\" value=\"${esc(q)}\"><span class=\"spacer\"></span>\n      <button class=\"btn\" id=\"pvCopy\">Copy</button><button class=\"btn\" id=\"pvTxt\">Save .txt</button><button class=\"btn\" id=\"pvRe\">Re-scan</button></div>\n    <pre id=\"pvPre\">${d.text ? highlight(d.text, q) : \"(no text)\"}</pre></div>`;\n  $(\"#pvFind\").addEventListener(\"input\", (e) => { $(\"#pvPre\").innerHTML = highlight(d.text || \"\", e.target.value); $(\"#pvPre mark\")?.scrollIntoView({ block: \"center\" }); });\n  $(\"#pvCopy\").addEventListener(\"click\", async () => { try { await navigator.clipboard.writeText(d.text || \"\"); toast(\"Text copied\"); } catch { toast(\"Select the text and press Ctrl+C\"); } });\n  $(\"#pvTxt\").addEventListener(\"click\", () => downloadBlob(new Blob([d.text || \"\"], { type: \"text/plain\" }), r.filename.replace(/\\.[^.]+$/, \"\") + \".txt\"));\n  $(\"#pvRe\").addEventListener(\"click\", async () => { $(\"#pvPre\").textContent = \"Reading again…\"; try { const nd = await getDoc(r, true); if (activeId === r.key) showText(r, nd); render(); } catch (er) { $(\"#pvPre\").textContent = er.message; } });\n}\nfunction showFields(r, d) {\n  const f = d.fields || {};\n  $(\"#pvBody\").innerHTML = `<form class=\"fieldsform\" id=\"fieldsForm\">\n    <label>Document</label><select name=\"doc_type\">${Object.entries(DOC).map(([k, v]) => `<option value=\"${k}\" ${f.doc_type === k ? \"selected\" : \"\"}>${v}</option>`).join(\"\")}</select>\n    <label>Number</label><input name=\"doc_no\" value=\"${esc(f.doc_no || \"\")}\">\n    <label>Document date</label><input name=\"doc_date\" type=\"date\" value=\"${esc(f.doc_date || \"\")}\">\n    <label>Amount</label><input name=\"amount\" inputmode=\"decimal\" value=\"${f.amount ?? \"\"}\">\n    <label>Currency</label><input name=\"currency\" value=\"${esc(f.currency || \"\")}\" placeholder=\"INR\">\n    <label>GSTIN</label><input name=\"gstin\" value=\"${esc(f.gstin || \"\")}\">\n    <div class=\"full muted small\">Detected automatically. Corrections are saved instantly and kept.</div><div class=\"full\"><hr></div>\n    <label>From</label><div>${esc(r.fromName)} &lt;${esc(r.fromEmail)}&gt;</div><label>To</label><div class=\"small\">${esc(r.to || \"—\")}</div>\n    <label>Subject</label><div>${esc(r.subject)}</div><label>Email date</label><div>${esc(r.date.replace(\"T\", \" \").slice(0, 16))}</div>\n    <label>Open in Gmail</label><div><a href=\"https://mail.google.com/mail/u/0/#all/${esc(r.threadId)}\" target=\"_blank\" rel=\"noopener\">open email ↗</a></div></form>`;\n  $(\"#fieldsForm\").addEventListener(\"change\", async (e) => {\n    const el = e.target; if (!el.name) return;\n    try { r.fields = await run(\"saveField\", slim(r), el.name, el.value); d.fields = r.fields; toast(\"Saved\"); render(); } catch (er) { toast(er.message); }\n  });\n}\nfunction tablesCsv(tables) { return \"﻿\" + tables.map((t) => t.map((row) => row.map((c) => /[\",\\n]/.test(c) ? `\"${String(c).replace(/\"/g, '\"\"')}\"` : c).join(\",\")).join(\"\\n\")).join(\"\\n\\n\"); }\nfunction showTables(r, d) {\n  const t = d.tables || [];\n  if (!t.length) { $(\"#pvBody\").innerHTML = `<div class=\"none\">No tables found in this file.</div>`; return; }\n  $(\"#pvBody\").innerHTML = `<div class=\"tblhead\"><b>${t.length} table${t.length > 1 ? \"s\" : \"\"}</b><span class=\"spacer\"></span><button class=\"btn\" data-tx=\"sheet\">All → Google Sheet</button><button class=\"btn\" data-tx=\"csv\">All → CSV</button></div>` +\n    t.map((tb, i) => `<div class=\"tblhead\"><span class=\"muted\">Table ${i + 1} · ${tb.length} rows</span><span class=\"spacer\"></span><button class=\"btn\" data-tcopy=\"${i}\">Copy</button></div>\n      <table class=\"dt\">${tb.slice(0, 300).map((row) => `<tr>${row.map((c) => `<td>${esc(c)}</td>`).join(\"\")}</tr>`).join(\"\")}</table>`).join(\"\");\n  $(\"#pvBody\").onclick = async (e) => {\n    const b = e.target.closest(\"button\"); if (!b) return;\n    if (b.dataset.tcopy != null) { try { await navigator.clipboard.writeText(t[+b.dataset.tcopy].map((row) => row.join(\"\\t\")).join(\"\\n\")); toast(\"Copied – paste into Sheets / Excel\"); } catch { toast(\"Copy not allowed here\"); } return; }\n    if (b.dataset.tx === \"csv\") downloadBlob(new Blob([tablesCsv(t)], { type: \"text/csv\" }), r.filename.replace(/\\.[^.]+$/, \"\") + \"-tables.csv\");\n    if (b.dataset.tx === \"sheet\") { busy(true, \"Creating Google Sheet…\"); try { const res = await run(\"tablesToSheet\", r.filename, t); report(`<div class=\"ok\">✔ Tables sheet: <a href=\"${esc(res.url)}\" target=\"_blank\" rel=\"noopener\">open ↗</a></div>`); window.open(res.url, \"_blank\", \"noopener\"); } catch (er) { toast(er.message); } finally { busy(false); } }\n  };\n}\nfunction closePreview(silent) { activeId = null; $(\"#preview\").classList.add(\"hidden\"); $(\"#app\").classList.remove(\"with-preview\"); $(\"#pvBody\").innerHTML = \"\"; if (!silent) render(); }\nfunction step(d) { const i = view.findIndex((r) => r.key === activeId); const n = view[i + d]; if (n) openPreview(n.key); }\n$(\"#pvTabs\").addEventListener(\"click\", (e) => { const m = e.target.dataset.m; if (!m || !activeId) return; pvMode = m; store(\"pvMode\", m); openPreview(activeId); });\n$(\"#pvClose\").addEventListener(\"click\", () => closePreview());\n$(\"#pvPrev\").addEventListener(\"click\", () => step(-1));\n$(\"#pvNext\").addEventListener(\"click\", () => step(1));\n$(\"#pvSel\").addEventListener(\"click\", () => { if (activeId) { toggle(activeId); render(); } });\ndocument.addEventListener(\"keydown\", (e) => {\n  if (!activeId || [\"INPUT\", \"SELECT\", \"TEXTAREA\"].includes(document.activeElement.tagName) || document.activeElement.isContentEditable) return;\n  if (e.key === \"Escape\") closePreview();\n  else if ([\"ArrowDown\", \"ArrowRight\", \"j\"].includes(e.key)) { e.preventDefault(); step(1); }\n  else if ([\"ArrowUp\", \"ArrowLeft\", \"k\"].includes(e.key)) { e.preventDefault(); step(-1); }\n  else if (e.key === \" \") { e.preventDefault(); toggle(activeId); render(); }\n});\n\n/* ============================================================ save / zip / export / read */\n$(\"#saveBtn\").addEventListener(\"click\", async () => {\n  const items = selItems().map(slim); stopFlag = false;\n  const rep = { saved: [], skipped: [], failed: [], folderUrl: \"\" };\n  await pool(chunk(items, 5), 4, async (c) => { const r = await run(\"saveToDrive\", c, $(\"#layout\").value, $(\"#naming\").value); rep.saved.push(...r.saved); rep.skipped.push(...r.skipped); rep.failed.push(...r.failed); rep.folderUrl = r.folderUrl; },\n    (d, n) => progress(`Saving to Drive · ${rep.saved.length + rep.skipped.length}/${items.length}`, d, n));\n  progress(null);\n  store(\"layout\", $(\"#layout\").value); store(\"naming\", $(\"#naming\").value);\n  report(`<div class=\"ok\">✔ Saved ${rep.saved.length} file(s) to Drive → <a href=\"${esc(rep.folderUrl)}\" target=\"_blank\" rel=\"noopener\">open \"Mail Attachments\" ↗</a></div>` +\n    (rep.skipped.length ? `<div class=\"warn\">↷ ${rep.skipped.length} skipped – identical file already in Drive</div>` : \"\") +\n    rep.failed.map((f) => `<div class=\"bad\">✖ ${esc(f.filename)}: ${esc(f.error)}</div>`).join(\"\"));\n});\n$(\"#zipBtn\").addEventListener(\"click\", async () => {\n  const items = selItems();\n  if (items.reduce((s, r) => s + r.size, 0) > 60 * 1048576 || items.length > 200) { toast(\"Too many files for one ZIP – select fewer (max ~60 MB) or use Save to Drive\", 6000); return; }\n  busy(true, `Packing ${items.length} files…`);\n  try {\n    const z = await run(\"makeZip\", items.map(slim), $(\"#layout\").value);\n    if (z.b64) { downloadBlob(new Blob([b64ToBytes(z.b64)], { type: \"application/zip\" }), z.name); report(`<div class=\"ok\">✔ ZIP downloaded: ${z.files} files${z.skipped ? `, ${z.skipped} duplicate skipped` : \"\"}. If nothing downloaded, your browser blocked it – use Save to Drive.</div>`); }\n    else report(`<div class=\"ok\">✔ ZIP is large, so it was saved to Drive: <a href=\"${esc(z.link)}\" target=\"_blank\" rel=\"noopener\">${esc(z.name)} ↗</a> (download it from there)</div>`);\n  } catch (er) { report(`<div class=\"bad\">✖ ${esc(er.message)}</div>`); }\n  finally { busy(false); }\n});\nasync function readMany(list) {\n  const todo = list.filter((r) => !r.text_source); stopFlag = false;\n  let ocr = 0, failed = 0;\n  await pool(todo, 4, async (r) => { try { const d = await getDoc(r); if (d.source === \"ocr\") ocr++; } catch { failed++; } },\n    (d, n) => progress(`Reading files with Google (OCR for scans) · ${d}/${n}`, d, n));\n  progress(null); render(); renderSheet();\n  toast(`Read ${todo.length - failed} file(s)${ocr ? `, ${ocr} scanned with OCR` : \"\"}${failed ? `, ${failed} failed` : \"\"}`);\n}\n$(\"#readBtn\").addEventListener(\"click\", () => readMany(selItems()));\nfunction csvOf(list) {\n  const head = [\"Email date\", \"From\", \"From email\", \"Subject\", \"File\", \"Type\", \"Size (KB)\", \"Document\", \"Number\", \"Document date\", \"Amount\", \"Currency\", \"GSTIN\", \"Text\"];\n  const q = (v) => { v = v == null ? \"\" : String(v); return /[\",\\n]/.test(v) ? `\"${v.replace(/\"/g, '\"\"')}\"` : v; };\n  return \"﻿\" + [head.join(\",\")].concat(list.map((r) => [r.date.replace(\"T\", \" \").slice(0, 16), r.fromName, r.fromEmail, r.subject, r.filename, r.ext, (r.size / 1024).toFixed(1),\n    DOC[r.fields.doc_type] || \"\", r.fields.doc_no, r.fields.doc_date, r.fields.amount, r.fields.currency, r.fields.gstin, r.text_source].map(q).join(\",\"))).join(\"\\n\");\n}\nasync function doExport(fmt, list) {\n  $$(\".menu\").forEach((m) => (m.open = false));\n  const stamp = new Date().toISOString().slice(0, 16).replace(/[-:]/g, \"\").replace(\"T\", \"-\");\n  if (fmt === \"csv\") { downloadBlob(new Blob([csvOf(list)], { type: \"text/csv\" }), `attachments-${stamp}.csv`); return; }\n  if (fmt === \"txt\") {\n    await readMany(list);\n    const parts = list.map((r) => `${\"=\".repeat(70)}\\nFILE    : ${r.filename}\\nFROM    : ${r.fromName} <${r.fromEmail}>\\nDATE    : ${r.date.slice(0, 16).replace(\"T\", \" \")}\\nSUBJECT : ${r.subject}\\n` +\n      (r.fields.amount != null ? `DETECTED: ${DOC[r.fields.doc_type] || \"\"} ${r.fields.doc_no || \"\"} ${r.fields.doc_date || \"\"} ${money(r.fields.amount, r.fields.currency)}\\n` : \"\") +\n      `${\"-\".repeat(70)}\\n${(r.text || \"\").trim() || \"(no text)\"}\\n`);\n    downloadBlob(new Blob([parts.join(\"\\n\")], { type: \"text/plain\" }), `attachments-text-${stamp}.txt`); return;\n  }\n  busy(true, \"Creating Google Sheet…\");\n  try { const res = await run(\"exportSheet\", list.map(slim)); report(`<div class=\"ok\">✔ Google Sheet created: <a href=\"${esc(res.url)}\" target=\"_blank\" rel=\"noopener\">${esc(res.name)} ↗</a></div>`); window.open(res.url, \"_blank\", \"noopener\"); }\n  catch (er) { report(`<div class=\"bad\">✖ ${esc(er.message)}</div>`); }\n  finally { busy(false); }\n}\n$$(\"[data-export]\").forEach((b) => b.addEventListener(\"click\", () => doExport(b.dataset.export, selItems())));\ndocument.addEventListener(\"click\", (e) => { $$(\".menu[open]\").forEach((m) => { if (!m.contains(e.target)) m.open = false; }); });\n\n/* ============================================================ table view */\nconst SHEET_EDIT = [\"doc_no\", \"doc_date\", \"amount\", \"currency\", \"gstin\"];\nconst tableRows = () => (view.length || !rows.length ? view : rows);\nfunction sheetVal(r, k) { return [\"date\", \"fromName\", \"filename\"].includes(k) ? (r[k] ?? \"\") : (r.fields[k] ?? (k === \"amount\" ? -Infinity : \"\")); }\nfunction renderSheet() {\n  const listR = [...tableRows()];\n  $(\"#tblInfo\").textContent = listR.length ? `${listR.length} rows from the current results` : \"Run a search first.\";\n  listR.sort((a, b) => { const x = sheetVal(a, sheetSort), y = sheetVal(b, sheetSort); return (x < y ? -1 : x > y ? 1 : 0) * sheetDir; });\n  $(\"#sheet tbody\").innerHTML = listR.map((r) => `<tr data-id=\"${esc(r.key)}\"><td class=\"nw\">${fmtDate(r.date)}</td><td><div class=\"sub\">${esc(r.fromName)}</div></td><td><div class=\"sub\" title=\"${esc(r.filename)}\">${esc(r.filename)}</div></td>\n    <td><select data-f=\"doc_type\">${Object.entries(DOC).map(([k, v]) => `<option value=\"${k}\" ${r.fields.doc_type === k ? \"selected\" : \"\"}>${v}</option>`).join(\"\")}</select></td>\n    ${SHEET_EDIT.map((k) => `<td contenteditable=\"plaintext-only\" data-f=\"${k}\" class=\"${k === \"amount\" ? \"r amt\" : \"\"}\">${esc(k === \"amount\" ? (r.fields.amount != null ? Number(r.fields.amount).toFixed(2) : \"\") : r.fields[k] || \"\")}</td>`).join(\"\")}\n    <td>${r.text_source ? `<span class=\"src ${r.text_source === \"ocr\" ? \"s-ocr\" : \"\"}\">${esc(r.text_source)}</span>` : `<span class=\"muted small\">not read</span>`}</td></tr>`).join(\"\");\n  const total = listR.reduce((a, r) => a + (r.fields.amount || 0), 0);\n  $(\"#sheetCount\").textContent = `${listR.length} documents · ${listR.filter((r) => r.fields.amount != null).length} with amount`;\n  $(\"#sheetTotal\").textContent = money(total);\n  const g = $(\"#groupBy\").value, box = $(\"#groupSummary\");\n  if (!g || !listR.length) { box.classList.add(\"hidden\"); return; }\n  const groups = new Map();\n  for (const r of listR) {\n    const k = g === \"month\" ? ((r.fields.doc_date || r.date || \"\").slice(0, 7) || \"unknown\") : g === \"sender\" ? (r.fromName || r.fromEmail) : (DOC[r.fields.doc_type] || \"Other\");\n    const v = groups.get(k) || [0, 0]; v[0]++; v[1] += r.fields.amount || 0; groups.set(k, v);\n  }\n  const ent = [...groups.entries()].sort((a, b) => (g === \"month\" ? (a[0] < b[0] ? 1 : -1) : b[1][1] - a[1][1]));\n  box.innerHTML = `<table><tr><th>${esc($(\"#groupBy\").selectedOptions[0].text)}</th><th class=\"r\">Files</th><th class=\"r\">Total</th></tr>` + ent.map(([k, [n, s]]) => `<tr><td>${esc(k)}</td><td class=\"r\">${n}</td><td class=\"r amt\">${money(s)}</td></tr>`).join(\"\") + \"</table>\";\n  box.classList.remove(\"hidden\");\n}\n$(\"#groupBy\").addEventListener(\"change\", renderSheet);\n$(\"#sheet thead\").addEventListener(\"click\", (e) => { const th = e.target.closest(\"th[data-ssort]\"); if (!th) return; sheetDir = th.dataset.ssort === sheetSort ? -sheetDir : ([\"date\", \"amount\", \"doc_date\"].includes(th.dataset.ssort) ? -1 : 1); sheetSort = th.dataset.ssort; renderSheet(); });\nasync function saveCell(el) {\n  const r = byId(el.closest(\"tr\").dataset.id), field = el.dataset.f;\n  const value = el.tagName === \"SELECT\" ? el.value : el.textContent.trim();\n  const old = field === \"amount\" ? (r.fields.amount != null ? Number(r.fields.amount).toFixed(2) : \"\") : (r.fields[field] || \"\");\n  if (value === old) return;\n  try { r.fields = await run(\"saveField\", slim(r), field, value); el.classList.add(\"edited\"); render(); $(\"#sheetTotal\").textContent = money(tableRows().reduce((a, x) => a + (x.fields.amount || 0), 0)); }\n  catch (er) { toast(er.message); el.textContent = old; }\n}\n$(\"#sheet tbody\").addEventListener(\"focusout\", (e) => { if (e.target.dataset?.f) saveCell(e.target); });\n$(\"#sheet tbody\").addEventListener(\"change\", (e) => { if (e.target.tagName === \"SELECT\") saveCell(e.target); });\n$(\"#sheet tbody\").addEventListener(\"keydown\", (e) => { if (e.key === \"Enter\" && e.target.isContentEditable) { e.preventDefault(); e.target.blur(); } });\n$(\"#sheet tbody\").addEventListener(\"dblclick\", (e) => { const tr = e.target.closest(\"tr\"); if (tr && !e.target.isContentEditable) { showView(\"search\"); openPreview(tr.dataset.id); } });\n$(\"#tblRead\").addEventListener(\"click\", () => readMany(tableRows()));\n$(\"#tblSheet\").addEventListener(\"click\", () => tableRows().length && doExport(\"sheet\", tableRows()));\n$(\"#tblCsv\").addEventListener(\"click\", () => tableRows().length && doExport(\"csv\", tableRows()));\n\n/* ============================================================ share */\n$(\"#shareBtn\").addEventListener(\"click\", () => {\n  const sel = selItems(), f = $(\"#shareForm\"), bytes = sel.reduce((a, r) => a + r.size, 0);\n  $(\"#shareCount\").textContent = `${sel.length} file${sel.length > 1 ? \"s\" : \"\"}`;\n  $(\"#shareNote\").textContent = `${size(bytes)} total` + (bytes > 18 * 1048576 ? \" – too large for email, use Share Drive link.\" : \"\");\n  if (!f.subject.value) f.subject.value = sel.length === 1 ? sel[0].filename : `${sel.length} attachments`;\n  $(\"#shareMsg\").textContent = \"\"; $(\"#shareDlg\").showModal();\n});\nfunction shareText(list) {\n  let total = 0;\n  const lines = list.map((r) => { const a = r.fields.amount; total += a || 0; return [fmtDate(r.date), r.filename, r.fromName, r.fields.doc_no && \"#\" + r.fields.doc_no, a != null && money(a, r.fields.currency)].filter(Boolean).join(\" | \"); });\n  if (total) lines.push(`Total: ${money(total)} (${list.length} files)`);\n  return lines.join(\"\\n\");\n}\n$(\"#shareForm\").addEventListener(\"submit\", async (e) => {\n  const action = e.submitter?.value; if (!action || action === \"cancel\") return;\n  e.preventDefault();\n  const f = $(\"#shareForm\"), items = selItems(), msg = $(\"#shareMsg\");\n  try {\n    if (action === \"copy\") { try { await navigator.clipboard.writeText(shareText(items)); msg.textContent = \"✔ List copied – paste it in WhatsApp, email or a document.\"; } catch { msg.textContent = \"Copy not allowed here.\"; } return; }\n    msg.textContent = action === \"email\" ? \"Sending…\" : \"Creating shared folder…\";\n    if (action === \"email\") { const r = await run(\"shareEmail\", items.map(slim), f.to.value, f.subject.value, f.message.value); msg.innerHTML = `✔ Sent ${r.files} file(s) to ${esc(r.sentTo.join(\", \"))}`; }\n    else { const r = await run(\"shareDrive\", items.map(slim), f.to.value, f.message.value); msg.innerHTML = `✔ Shared ${r.files} file(s) with ${esc(r.sharedWith.join(\", \"))} – <a href=\"${esc(r.link)}\" target=\"_blank\" rel=\"noopener\">open folder ↗</a>`; }\n  } catch (er) { msg.innerHTML = `<span class=\"err\">${esc(er.message)}</span>`; }\n});\n\n/* ============================================================ views / data */\nfunction showView(v) {\n  $$(\"#tabs button\").forEach((b) => b.classList.toggle(\"on\", b.dataset.view === v));\n  $(\"#viewSearch\").classList.toggle(\"hidden\", v !== \"search\");\n  $(\"#viewTable\").classList.toggle(\"hidden\", v !== \"table\");\n  $(\"#viewData\").classList.toggle(\"hidden\", v !== \"data\");\n  $(\"#viewPeople\").classList.toggle(\"hidden\", v !== \"people\");\n  hideSuggest();\n  if (v !== \"search\") closePreview(true);\n  if (v === \"table\") renderSheet();\n  if (v === \"data\") refreshData();\n  if (v === \"people\") { renderPeople(); setTimeout(() => $(\"#peopleFilter\").focus(), 0); }\n}\n$(\"#tabs\").addEventListener(\"click\", (e) => { const v = e.target.closest(\"button\")?.dataset.view; if (v) showView(v); });\nasync function refreshData() {\n  try {\n    const u = await run(\"getUser\");\n    $(\"#account\").textContent = u.email;\n    const s = u.store, k = (v, l) => `<div class=\"kpi\"><b>${v}</b><span>${l}</span></div>`;\n    $(\"#kpis\").innerHTML = k(s.rows || 0, \"documents read / corrected\") + k(contacts.rows.length.toLocaleString(), \"email addresses found\") + k(rows.length, \"in current results\") + k(esc(u.timeZone), \"time zone\");\n    const a = $(\"#openStore\"); if (s.url) { a.href = s.url; a.classList.remove(\"hidden\"); } else a.classList.add(\"hidden\");\n  } catch (er) { toast(er.message); }\n}\n$(\"#eraseBtn\").addEventListener(\"click\", async () => {\n  if (!confirm(\"Erase this app's data?\\n\\nThe data sheet (detected fields, text read from files, your corrections) is moved to the Drive Bin.\\nYour email and files saved in \\\"Mail Attachments\\\" are NOT touched.\")) return;\n  try { const r = await run(\"eraseData\"); try { localStorage.clear(); } catch { /* ignore */ } toast(r.note, 6000); refreshData(); } catch (er) { toast(er.message); }\n});\n\n/* ============================================================ people (all addresses) */\n// contacts.rows: [[email, name, count, lastMs], ...] - one row per address, no duplicates\nlet contacts = { rows: [], syncedTo: 0, updatedAt: 0, me: \"\" };\nlet peopleSort = \"count\", peopleDir = -1, domainFilter = \"\";\nfunction contactsFromCache() { const c = store(\"contacts\"); if (c && Array.isArray(c.rows)) contacts = c; }\nfunction contactsToCache() { try { localStorage.setItem(\"contacts\", JSON.stringify(contacts)); } catch { /* too big for browser storage - fine, the sheet has it */ } }\nconst domainOf = (e) => e.split(\"@\")[1] || \"\";\n\n/** Rank addresses for what was typed: exact > whole domain > starts with > word starts with > contains. */\nfunction matchContacts(q) {\n  q = q.trim().toLowerCase();\n  if (q.length < 2) return [];\n  const bare = q.replace(/^@/, \"\");\n  const out = [];\n  for (const r of contacts.rows) {\n    const e = r[0], n = (r[1] || \"\").toLowerCase(), local = e.split(\"@\")[0];\n    let s = -1;\n    if (e === bare) s = 0;\n    else if (domainOf(e) === bare || domainOf(e).endsWith(\".\" + bare)) s = 1;\n    else if (e.startsWith(bare) || local.startsWith(bare)) s = 2;\n    else if (n.split(/[\\s.,'-]+/).some((w) => w.startsWith(bare)) || n.startsWith(bare)) s = 3;\n    else if (e.includes(bare) || n.includes(bare)) s = 4;\n    if (s >= 0) out.push([s, r]);\n  }\n  out.sort((a, b) => a[0] - b[0] || b[1][2] - a[1][2] || a[1][0].localeCompare(b[1][0]));\n  return out.map((x) => x[1]);\n}\nfunction hl(text, q) {\n  const t = esc(text), b = q.trim().replace(/^@/, \"\");\n  if (!b) return t;\n  return t.replace(new RegExp(\"(\" + b.replace(/[.*+?^${}()|[\\]\\\\]/g, \"\\\\$&\") + \")\", \"ig\"), \"<mark>$1</mark>\");\n}\n\n// ---- suggestions while typing (From, To/Cc, Share \"To\")\nlet sgInput = null, sgItems = [], sgOn = -1;\nconst sgBox = $(\"#suggest\");\nfunction currentToken(inp) { const v = inp.value, i = v.lastIndexOf(\",\"); return v.slice(i + 1); }\nfunction showSuggest(inp) {\n  const q = currentToken(inp).trim();\n  const list = matchContacts(q);\n  if (!list.length) { hideSuggest(); return; }\n  sgInput = inp; sgOn = 0;\n  const bare = q.toLowerCase().replace(/^@/, \"\");\n  const sameDomain = list.filter((r) => domainOf(r[0]) === bare);\n  sgItems = [];\n  if (sameDomain.length > 1) sgItems.push({ all: true, value: bare, label: `All ${sameDomain.length} addresses @${bare}` });\n  list.slice(0, 60).forEach((r) => sgItems.push({ value: r[0], r }));\n  sgBox.innerHTML = `<div class=\"head\">${list.length} address${list.length > 1 ? \"es\" : \"\"} match “${esc(q)}”</div>` + sgItems.map((it, i) => it.all\n    ? `<div class=\"sg all ${i === sgOn ? \"on\" : \"\"}\" data-i=\"${i}\" role=\"option\">★ ${esc(it.label)}</div>`\n    : `<div class=\"sg ${i === sgOn ? \"on\" : \"\"}\" data-i=\"${i}\" role=\"option\"><span class=\"nm\">${hl(it.r[1] || \"\", q) || \"—\"}</span><span class=\"em\">${hl(it.r[0], q)}${it.r[0] === contacts.me ? '<span class=\"me\">(you)</span>' : \"\"}</span><span class=\"ct\">${it.r[2]} · ${it.r[3] ? fmtDate(new Date(it.r[3]).toISOString()) : \"\"}</span></div>`).join(\"\");\n  const rc = inp.getBoundingClientRect();\n  Object.assign(sgBox.style, { left: rc.left + \"px\", top: rc.bottom + 4 + \"px\", width: Math.max(rc.width, 460) + \"px\" });\n  if (rc.left + Math.max(rc.width, 460) > window.innerWidth - 8) sgBox.style.left = Math.max(8, window.innerWidth - Math.max(rc.width, 460) - 8) + \"px\";\n  sgBox.classList.remove(\"hidden\");\n}\nfunction hideSuggest() { sgBox.classList.add(\"hidden\"); sgInput = null; sgItems = []; sgOn = -1; }\nfunction pickSuggest(i) {\n  const it = sgItems[i], inp = sgInput; if (!it || !inp) return;\n  const v = inp.value, cut = v.lastIndexOf(\",\");\n  const before = cut >= 0 ? v.slice(0, cut + 1) + \" \" : \"\";\n  const parts = (before + it.value).split(\",\").map((x) => x.trim()).filter(Boolean);\n  inp.value = [...new Set(parts.map((x) => x.toLowerCase()))].map((l) => parts.find((x) => x.toLowerCase() === l)).join(\", \") + \", \";\n  hideSuggest(); inp.focus();\n}\n$$(\"[data-suggest]\").forEach((inp) => {\n  inp.addEventListener(\"input\", () => showSuggest(inp));\n  inp.addEventListener(\"focus\", () => { if (currentToken(inp).trim().length >= 2) showSuggest(inp); });\n  inp.addEventListener(\"blur\", () => setTimeout(() => { if (sgInput === inp) hideSuggest(); inp.value = inp.value.replace(/[,\\s]+$/, \"\"); }, 180));\n  inp.addEventListener(\"keydown\", (e) => {\n    if (sgBox.classList.contains(\"hidden\") || sgInput !== inp) return;\n    if (e.key === \"ArrowDown\" || e.key === \"ArrowUp\") {\n      e.preventDefault(); sgOn = (sgOn + (e.key === \"ArrowDown\" ? 1 : -1) + sgItems.length) % sgItems.length;\n      $$(\".sg\", sgBox).forEach((el, i) => el.classList.toggle(\"on\", i === sgOn));\n      $$(\".sg\", sgBox)[sgOn]?.scrollIntoView({ block: \"nearest\" });\n    } else if (e.key === \"Enter\" || e.key === \"Tab\") { if (sgOn >= 0) { e.preventDefault(); pickSuggest(sgOn); } }\n    else if (e.key === \"Escape\") hideSuggest();\n  });\n});\nsgBox.addEventListener(\"mousedown\", (e) => { const el = e.target.closest(\".sg\"); if (el) { e.preventDefault(); pickSuggest(+el.dataset.i); } });\nwindow.addEventListener(\"resize\", hideSuggest);\n$(\".side\").addEventListener(\"scroll\", hideSuggest);\n\n// ---- scanning the mailbox\nlet scanning = false;\nasync function scanPeople(full) {\n  if (scanning) return;\n  scanning = true; stopFlag = false;\n  const since = full ? 0 : (contacts.syncedTo || 0);\n  try {\n    const ids = [];\n    let tok = null;\n    do {\n      const r = await run(\"contactsListThreads\", since, tok);\n      ids.push(...r.ids); tok = r.next;\n      progress(`Finding conversations… ${ids.length.toLocaleString()}`);\n    } while (tok && !stopFlag);\n    if (stopFlag) { toast(\"Stopped – nothing was changed\"); return; }\n    const map = new Map(full ? [] : contacts.rows.map((r) => [r[0], r.slice()]));\n    let newest = since, msgs = 0;\n    const errors = await pool(chunk(ids, 50), 8, async (c) => {\n      const r = await run(\"contactsFromThreads\", c, since);\n      msgs += r.messages; newest = Math.max(newest, r.newest);\n      for (const [email, name, count, last] of r.contacts) {\n        const cur = map.get(email);\n        if (!cur) { map.set(email, [email, name, count, last]); continue; }\n        cur[2] += count;\n        if (last >= cur[3]) { cur[3] = last; if (name) cur[1] = name; } else if (!cur[1] && name) cur[1] = name;\n      }\n    }, (d, n) => progress(`Reading addresses · ${d}/${n} batches · ${map.size.toLocaleString()} unique addresses`, d, n));\n    if (stopFlag) { toast(\"Stopped – nothing was changed (run it again to finish)\"); return; }\n    if (errors.length) toast(`${errors.length} batch(es) failed – run “Scan my mail” again to fill the gaps`, 6000);\n    const rows = [...map.values()].sort((a, b) => b[2] - a[2]);\n    progress(\"Saving the list…\");\n    await run(\"saveContacts\", rows, errors.length ? since : (newest || since));\n    contacts = { rows, syncedTo: errors.length ? since : (newest || since), updatedAt: Date.now(), me: contacts.me };\n    contactsToCache(); renderPeople(); updatePeopleHint();\n    toast(`${rows.length.toLocaleString()} unique addresses${msgs ? ` · ${msgs.toLocaleString()} new emails read` : \" · nothing new\"}`);\n  } catch (er) { toast(er.message, 6000); }\n  finally { scanning = false; progress(null); }\n}\n$(\"#peopleScan\").addEventListener(\"click\", () => scanPeople(false));\n$(\"#peopleFull\").addEventListener(\"click\", () => scanPeople(true));\n$(\"#peopleHintBtn\").addEventListener(\"click\", (e) => { e.preventDefault(); showView(\"people\"); scanPeople(false); });\nfunction updatePeopleHint() { $(\"#peopleHint\").classList.toggle(\"hidden\", contacts.rows.length > 0); }\n\n// ---- People view\nfunction peopleList() {\n  const q = $(\"#peopleFilter\").value.trim();\n  let list = q.length >= 1 ? (q.length >= 2 ? matchContacts(q) : contacts.rows.filter((r) => r[0].startsWith(q.toLowerCase()))) : contacts.rows.slice();\n  if (domainFilter) list = list.filter((r) => domainOf(r[0]) === domainFilter);\n  if (!q || peopleSort !== \"count\" || peopleDir !== -1) {\n    const k = { email: 0, name: 1, count: 2, last: 3 }[peopleSort];\n    list = list.slice().sort((a, b) => { const x = k === 1 ? (a[1] || \"~\").toLowerCase() : a[k], y = k === 1 ? (b[1] || \"~\").toLowerCase() : b[k]; return (x < y ? -1 : x > y ? 1 : 0) * peopleDir; });\n  }\n  return list;\n}\nfunction renderPeople() {\n  const all = contacts.rows, q = $(\"#peopleFilter\").value.trim();\n  $(\"#peopleStats\").innerHTML = all.length\n    ? `<b>${all.length.toLocaleString()}</b> unique email addresses · <b>${new Set(all.map((r) => domainOf(r[0]))).size.toLocaleString()}</b> domains · updated ${contacts.updatedAt ? fmtDate(new Date(contacts.updatedAt).toISOString()) : \"—\"}`\n    : \"Every email address in your mailbox (From, To, Cc), each listed once. Press <b>Scan my mail</b> – the first scan takes a few minutes, later ones only read new mail.\";\n  $(\"#peopleScan\").textContent = all.length ? \"Update\" : \"Scan my mail\";\n  const doms = new Map(); all.forEach((r) => doms.set(domainOf(r[0]), (doms.get(domainOf(r[0])) || 0) + 1));\n  $(\"#domainChips\").innerHTML = all.length ? [...doms.entries()].sort((a, b) => b[1] - a[1]).slice(0, 14).map(([d, n]) =>\n    `<button type=\"button\" data-dom=\"${esc(d)}\" class=\"${d === domainFilter ? \"on\" : \"\"}\">${esc(d)} · ${n}</button>`).join(\"\") + (domainFilter ? `<button type=\"button\" data-dom=\"\">✕ all domains</button>` : \"\") : \"\";\n  const list = peopleList();\n  $(\"#peopleTbl tbody\").innerHTML = list.slice(0, 1500).map((r) => `<tr data-e=\"${esc(r[0])}\">\n      <td>${hl(r[1] || \"\", q) || '<span class=\"muted\">—</span>'}</td>\n      <td>${hl(r[0], q)}${r[0] === contacts.me ? '<span class=\"me\">(you)</span>' : \"\"}</td>\n      <td class=\"r amt\">${r[2].toLocaleString()}</td>\n      <td class=\"nw\">${r[3] ? fmtDate(new Date(r[3]).toISOString()) : \"\"}</td>\n      <td><button class=\"btn ghost\" data-find=\"${esc(r[0])}\" title=\"Find attachments from this address\">Attachments →</button></td></tr>`).join(\"\");\n  $(\"#peopleEmpty\").classList.toggle(\"hidden\", !!list.length || !all.length);\n  $(\"#peopleEmpty\").textContent = `No address matches “${q}”.`;\n  $(\"#peopleCount\").textContent = all.length ? `${list.length.toLocaleString()} shown${list.length > 1500 ? \" (first 1,500 in the table – Copy / CSV / Sheet include all)\" : \"\"}` : \"\";\n  $$(\"#peopleTbl th[data-psort]\").forEach((th) => { th.textContent = th.textContent.replace(/ [▴▾]$/, \"\") + (th.dataset.psort === peopleSort ? (peopleDir < 0 ? \" ▾\" : \" ▴\") : \"\"); });\n}\n$(\"#peopleFilter\").addEventListener(\"input\", renderPeople);\n$(\"#domainChips\").addEventListener(\"click\", (e) => { const d = e.target.dataset?.dom; if (d === undefined) return; domainFilter = d === domainFilter ? \"\" : d; renderPeople(); });\n$(\"#peopleTbl thead\").addEventListener(\"click\", (e) => { const th = e.target.closest(\"th[data-psort]\"); if (!th) return; peopleDir = th.dataset.psort === peopleSort ? -peopleDir : ([\"count\", \"last\"].includes(th.dataset.psort) ? -1 : 1); peopleSort = th.dataset.psort; renderPeople(); });\n$(\"#peopleTbl tbody\").addEventListener(\"click\", (e) => {\n  const em = e.target.dataset?.find; if (!em) return;\n  fillForm({ ...readForm(), senders: em, keywords: \"\" }); showView(\"search\"); $(\"#searchForm\").requestSubmit();\n});\n$(\"#peopleCopy\").addEventListener(\"click\", async () => {\n  const text = peopleList().map((r) => r[0]).join(\", \");\n  try { await navigator.clipboard.writeText(text); toast(`${peopleList().length} addresses copied`); } catch { toast(\"Copy not allowed here – use CSV\"); }\n});\n$(\"#peopleCsv\").addEventListener(\"click\", () => {\n  const q = (v) => (/[\",\\n]/.test(v) ? `\"${String(v).replace(/\"/g, '\"\"')}\"` : v);\n  const csv = \"﻿Name,Email,Domain,Emails,Last email\\n\" + peopleList().map((r) => [r[1] || \"\", r[0], domainOf(r[0]), r[2], r[3] ? new Date(r[3]).toISOString().slice(0, 10) : \"\"].map(q).join(\",\")).join(\"\\n\");\n  downloadBlob(new Blob([csv], { type: \"text/csv\" }), \"email-addresses.csv\");\n});\n$(\"#peopleSheet\").addEventListener(\"click\", async () => {\n  busy(true, \"Creating Google Sheet…\");\n  try { const r = await run(\"exportContacts\", peopleList()); window.open(r.url, \"_blank\", \"noopener\"); toast(\"Google Sheet created\"); }\n  catch (er) { toast(er.message); } finally { busy(false); }\n});\nasync function loadContacts() {\n  contactsFromCache(); updatePeopleHint();\n  try {\n    const c = await run(\"getContacts\");\n    if (c.rows.length || !contacts.rows.length) { contacts = c; contactsToCache(); }\n    contacts.me = c.me;\n    updatePeopleHint(); if (!$(\"#viewPeople\").classList.contains(\"hidden\")) renderPeople();\n    // keep the list fresh: quietly read new mail if the last update is older than 6 hours\n    if (contacts.syncedTo && Date.now() - (contacts.updatedAt || 0) > 6 * 3600e3) scanPeople(false);\n  } catch { /* offline / first run - suggestions just stay empty */ }\n}\n\n/* ============================================================ start */\n(function init() {\n  pvMode = store(\"pvMode\") || \"doc\";\n  const last = store(\"lastQuery\"); if (last) fillForm(last);\n  if (store(\"layout\")) $(\"#layout\").value = store(\"layout\");\n  if (store(\"naming\")) $(\"#naming\").value = store(\"naming\");\n  renderHistory();\n  loadContacts();\n  run(\"getUser\").then((u) => { $(\"#account\").textContent = u.email; }).catch((e) => { $(\"#account\").textContent = \"\"; toast(e.message, 8000); });\n})();\n</script>\n\n</body>\n</html>\n";
