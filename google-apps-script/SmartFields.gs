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
