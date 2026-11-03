/* Minimal in-memory fakes of the Google services the app uses, so Code.gs can run under Node. */
'use strict';
const crypto = require('crypto');
const fs = require('fs');
const path = require('path');
const vm = require('vm');

const b64url = (buf) => Buffer.from(buf).toString('base64').replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
const toBytes = (buf) => Array.from(Buffer.from(buf)).map((b) => (b > 127 ? b - 256 : b));   // Apps Script byte[] is signed
const fromBytes = (arr) => Buffer.from(arr.map((b) => (b < 0 ? b + 256 : b)));

class Blob {
  constructor(bytes, type, name) { this.buf = Buffer.isBuffer(bytes) ? bytes : fromBytes(bytes || []); this.type = type || 'application/octet-stream'; this.name = name || ''; }
  getBytes() { return toBytes(this.buf); }
  getContentType() { return this.type; }
  getName() { return this.name; }
  setName(n) { this.name = n; return this; }
  getDataAsString(cs) { return this.buf.toString(cs === 'ISO-8859-1' ? 'latin1' : 'utf8'); }
}

/** A message in Gmail API shape. attachments: [{name, mime, data:Buffer, inline}] */
function makeMessage(id, { from, to = 'parthag@mckkutty.com', cc, subject, date, body = '', attachments = [], messageId, threadId }) {
  const parts = [
    { partId: '0', mimeType: 'multipart/alternative', filename: '', headers: [], body: { size: 0 },
      parts: [
        { partId: '0.0', mimeType: 'text/plain', filename: '', headers: [], body: { size: body.length, data: b64url(body) } },
        { partId: '0.1', mimeType: 'text/html', filename: '', headers: [], body: { size: body.length, data: b64url(`<p>${body}</p>`) } },
      ] },
  ];
  attachments.forEach((a, i) => {
    const pid = String(i + 1);
    parts.push({
      partId: pid, mimeType: a.mime, filename: a.name,
      headers: [{ name: 'Content-Disposition', value: (a.inline ? 'inline' : 'attachment') + `; filename="${a.name}"` }]
        .concat(a.inline ? [{ name: 'Content-ID', value: '<logo>' }] : []),
      body: { size: a.data.length, attachmentId: `ATT-${id}-${pid}` },
    });
  });
  return {
    id, threadId: threadId || 'T' + id, internalDate: String(new Date(date).getTime()), labelIds: ['INBOX'],
    snippet: body.slice(0, 100),
    payload: { partId: '', mimeType: 'multipart/mixed', filename: '',
               headers: [{ name: 'From', value: from }, { name: 'To', value: to }].concat(cc ? [{ name: 'Cc', value: cc }] : []).concat([{ name: 'Subject', value: subject },
                         { name: 'Date', value: new Date(date).toUTCString() }, { name: 'Message-ID', value: messageId || `<${id}@x>` }]),
               body: { size: 0 }, parts },
    _att: Object.fromEntries(attachments.map((a, i) => [`ATT-${id}-${i + 1}`, a.data])),
    _texts: attachments.filter((a) => a.text).map((a) => [a.name, a.text]),
  };
}

/** Super-small Gmail query evaluator: from:, filename:, after:/before: epoch, {a b} = OR, words. */
function matches(msg, query) {
  const h = Object.fromEntries(msg.payload.headers.map((x) => [x.name.toLowerCase(), x.value]));
  const names = msg.payload.parts.filter((p) => p.filename).map((p) => p.filename.toLowerCase());
  const text = `${h.subject} ${msg.snippet} ${names.join(' ')}`.toLowerCase();
  const t = Number(msg.internalDate) / 1000;
  const tokens = query.match(/\{[^}]*\}|-?"[^"]*"|\S+/g) || [];
  const one = (tok) => {
    tok = tok.replace(/"/g, '');
    if (tok === 'ALL') return true;
    if (tok === 'has:attachment') return names.length > 0;
    if (tok.startsWith('from:')) { const v = tok.slice(5).toLowerCase(); return h.from.toLowerCase().includes(v) || h.from.toLowerCase().replace(/[^0-9a-z@.]/g, '').includes(v); }
    if (tok.startsWith('to:') || tok.startsWith('cc:')) return (h.to || '').toLowerCase().includes(tok.slice(3).toLowerCase());
    if (tok.startsWith('filename:')) { const v = tok.slice(9).toLowerCase(); return names.some((n) => n.includes(v)); }
    if (tok.startsWith('after:')) return t >= Number(tok.slice(6));
    if (tok.startsWith('before:')) return t < Number(tok.slice(7));
    if (tok.startsWith('larger:') || tok.startsWith('smaller:') || tok === 'in:anywhere') return true;
    if (tok.startsWith('-')) return !text.includes(tok.slice(1).toLowerCase());
    return text.includes(tok.toLowerCase());
  };
  return tokens.every((tok) => {
    if (tok.startsWith('{')) return (tok.slice(1, -1).match(/"[^"]*"|\S+/g) || []).some(one);
    return one(tok);
  });
}

class Range {
  constructor(sheet, r, c, nr = 1, nc = 1) { Object.assign(this, { sheet, r, c, nr, nc }); }
  getValues() { const out = []; for (let i = 0; i < this.nr; i++) { const row = this.sheet.rows[this.r - 1 + i] || []; out.push(Array.from({ length: this.nc }, (_, j) => (row[this.c - 1 + j] ?? ''))); } return out; }
  getDisplayValues() { return this.getValues().map((r) => r.map(String)); }
  setValues(v) { v.forEach((row, i) => { const R = (this.sheet.rows[this.r - 1 + i] ||= []); row.forEach((x, j) => { R[this.c - 1 + j] = x; }); }); return this; }
  setValue(x) { return this.setValues([[x]]); }
  setFormula(f) { return this.setValue(f); }
  clearContent() { for (let i = 0; i < this.nr; i++) { const R = this.sheet.rows[this.r - 1 + i]; if (R) for (let j = 0; j < this.nc; j++) R[this.c - 1 + j] = ''; } this.sheet.rows = this.sheet.rows.filter((r) => r.some((x) => x !== '' && x != null)); return this; }
  getRow() { return this.r; }
  createTextFinder(t) {
    const self = this; let exact = false;
    return { matchEntireCell(b) { exact = b; return this; },
             findNext() { for (let i = 0; i < self.nr; i++) { const v = String((self.sheet.rows[self.r - 1 + i] || [])[self.c - 1] ?? ''); if (exact ? v === t : v.includes(t)) return new Range(self.sheet, self.r + i, self.c); } return null; } };
  }
  createFilter() { return this; }
}
['setFontWeight', 'setBackground', 'setFontColor', 'setNumberFormat'].forEach((m) => { Range.prototype[m] = function () { return this; }; });

class Sheet {
  constructor(name, parent) { this.name = name; this.rows = []; this.parent = parent; }
  getParent() { return this.parent; }
  getName() { return this.name; } setName(n) { this.name = n; return this; }
  getLastRow() { return this.rows.length; }
  getRange(r, c, nr, nc) { return new Range(this, r, c, nr, nc); }
  getDataRange() { return new Range(this, 1, 1, this.rows.length, Math.max(0, ...this.rows.map((r) => r.length))); }
  appendRow(a) { this.rows.push(a.slice()); return this; }
  setFrozenRows() { return this; } autoResizeColumns() { return this; }
}

function makeEnv({ messages = [], docTexts = {}, sheetsFromXlsx = {}, htmlFiles = {} } = {}) {
  const page = (content) => { const o = { content, setTitle: () => o, addMetaTag: () => o, setXFrameOptionsMode: () => o, getContent: () => content }; return o; };
  const store = { props: {}, files: {}, spreadsheets: {}, sent: [], perms: [], nextId: 1 };
  const byId = Object.fromEntries(messages.map((m) => [m.id, m]));
  const newId = (p) => `${p}${store.nextId++}`;
  const env = {
    console,
    Gmail: { Users: { Messages: {
      list: (_u, opt) => { const ids = messages.filter((m) => matches(m, opt.q)).map((m) => ({ id: m.id })); const start = opt.pageToken ? Number(opt.pageToken) : 0; const page = ids.slice(start, start + opt.maxResults); return { messages: page, nextPageToken: start + opt.maxResults < ids.length ? String(start + opt.maxResults) : undefined }; },
      get: (_u, id) => { if (!byId[id]) throw new Error('Not Found'); return JSON.parse(JSON.stringify({ ...byId[id], _att: undefined })); },
      Attachments: { get: (_u, id, attId) => ({ data: b64url(byId[id]._att[attId]) }) },
    },
    Threads: {
      list: (_u, opt) => {
        const tids = [...new Set(messages.filter((m) => matches(m, opt.q.replace('-in:chats', '').trim() || 'ALL')).map((m) => m.threadId))].map((id) => ({ id }));
        const start = opt.pageToken ? Number(opt.pageToken) : 0;
        return { threads: tids.slice(start, start + opt.maxResults), nextPageToken: start + opt.maxResults < tids.length ? String(start + opt.maxResults) : undefined };
      },
      get: (_u, tid, opt) => ({ id: tid, messages: messages.filter((m) => m.threadId === tid).map((m) => ({ id: m.id, internalDate: m.internalDate,
        payload: { headers: m.payload.headers.filter((h) => !opt.metadataHeaders || opt.metadataHeaders.includes(h.name)) } })) }),
    } } },
    Utilities: {
      base64DecodeWebSafe: (s) => toBytes(Buffer.from(s.replace(/-/g, '+').replace(/_/g, '/'), 'base64')),
      base64Encode: (bytes) => fromBytes(bytes).toString('base64'),
      newBlob: (bytes, type, name) => new Blob(Array.isArray(bytes) ? bytes : Buffer.from(String(bytes)), type, name),
      computeDigest: (_alg, bytes) => toBytes(crypto.createHash('sha256').update(fromBytes(bytes)).digest()),
      DigestAlgorithm: { SHA_256: 'SHA_256' },
      formatDate: (d, _tz, f) => (f === 'yyyy-MM-dd' ? d.toISOString().slice(0, 10) : d.toISOString().replace(/[-:T]/g, '').slice(0, 12)),
      parseCsv: (t, sep = ',') => t.trim().split(/\r?\n/).map((l) => l.split(sep)),
      zip: (blobs, name) => { const AdmZipLike = Buffer.concat(blobs.map((b) => Buffer.concat([Buffer.from(`--${b.name}--`), b.buf]))); const z = new Blob(AdmZipLike, 'application/zip', name); z.entries = blobs.map((b) => b.name); return z; },
    },
    Session: { getActiveUser: () => ({ getEmail: () => 'parthag@mckkutty.com' }), getEffectiveUser: () => ({ getEmail: () => 'parthag@mckkutty.com' }), getScriptTimeZone: () => 'Asia/Kolkata' },
    PropertiesService: { getUserProperties: () => ({ getProperty: (k) => store.props[k] || null, setProperty: (k, v) => { store.props[k] = v; }, deleteProperty: (k) => { delete store.props[k]; } }) },
    LockService: { getUserLock: () => ({ waitLock() {}, releaseLock() {} }) },
    SpreadsheetApp: {
      create: (name) => { const id = newId('SS'); const sheets = []; const ss = { id, name, sheets, getId: () => id, getName: () => name, getUrl: () => `https://docs.google.com/spreadsheets/d/${id}`, getSheets: () => sheets, getSheetByName: (n) => sheets.find((s) => s.name === n) || null, insertSheet: (n) => { const s = new Sheet(n || `Sheet${sheets.length + 1}`, ss); sheets.push(s); return s; } }; sheets.push(new Sheet('Sheet1', ss)); store.spreadsheets[id] = ss; return ss; },
      openById: (id) => { if (store.spreadsheets[id] && !store.spreadsheets[id].trashed) return store.spreadsheets[id]; throw new Error('not found'); },
    },
    DocumentApp: { openById: (id) => ({ getBody: () => ({ getText: () => store.files[id].text }) }) },
    Drive: {
      Files: {
        create: (res, blob, opts) => {
          const id = newId('F'); const f = { id, ...res, blob, webViewLink: `https://drive.google.com/file/d/${id}/view` };
          if (res.mimeType === 'application/vnd.google-apps.document') f.text = docTexts[blob.getName()] ?? `TEXT OF ${blob.getName()}`;
          if (res.mimeType === 'application/vnd.google-apps.spreadsheet') {
            const ss = env.SpreadsheetApp.create(res.name); ss.sheets[0].rows = sheetsFromXlsx[blob.getName()] || [['a', '1']]; store.spreadsheets[id] = ss;
          }
          store.files[id] = f; return f;
        },
        get: (id) => { if (!store.files[id]) throw new Error('nf'); return store.files[id]; },
        list: (opt) => {
          let files = Object.values(store.files).filter((f) => !f.trashed);
          const sha = /value='([0-9a-f]+)'/.exec(opt.q);
          if (opt.q.includes('appProperties')) files = files.filter((f) => f.appProperties && sha && f.appProperties.sha256 === sha[1]);
          const par = /'([^']+)' in parents/.exec(opt.q); const nm = /name='((?:[^'\\]|\\.)*)'/.exec(opt.q);
          if (par) files = files.filter((f) => (f.parents || []).includes(par[1]) && (!nm || f.name === nm[1]) && f.mimeType === 'application/vnd.google-apps.folder');
          return { files };
        },
        remove: (id) => { delete store.files[id]; if (store.spreadsheets[id]) delete store.spreadsheets[id]; },
        update: (res, id) => { if (store.spreadsheets[id]) store.spreadsheets[id].trashed = res.trashed; if (store.files[id]) Object.assign(store.files[id], res); },
      },
      Permissions: { create: (p, fileId, opt) => { store.perms.push({ ...p, fileId, opt }); return p; } },
    },
    MailApp: { getRemainingDailyQuota: () => 100, sendEmail: (o) => { store.sent.push(o); } },
    HtmlService: {
      createTemplateFromFile: (name) => { if (!htmlFiles[name]) throw new Error(`No HTML file named ${name} was found.`); return { evaluate: () => page(htmlFiles[name]) }; },
      createHtmlOutput: (h) => page(h),
      createHtmlOutputFromFile: (name) => ({ getContent: () => htmlFiles[name] }),
      XFrameOptionsMode: { DEFAULT: 'DEFAULT' },
    },
  };
  const ctx = vm.createContext(env);
  for (const f of (process.env.EASY ? ['easy/Code.gs'] : ['SmartFields.gs', 'Code.gs'])) {
    vm.runInContext(fs.readFileSync(path.join(__dirname, '..', f), 'utf8'), ctx, { filename: f });
  }
  return { ctx, store, b64url };
}

module.exports = { makeEnv, makeMessage, matches, fromBytes };
