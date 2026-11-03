/* Runs Code.gs + SmartFields.gs against fake Gmail/Drive/Sheets.  node --test test/ */
'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { makeEnv, makeMessage } = require('./mocks');

const PDF = (t) => Buffer.from(`%PDF-1.4\n/Font /F1\n(${t})\n%%EOF`);
const SCAN = Buffer.from('%PDF-1.4\n/Image /XObject\n%%EOF');      // image-only PDF (no fonts)

function mailbox() {
  const msgs = [];
  let n = 1;
  for (let y = 2023; y <= 2026; y++) {
    for (let m = 1; m <= 12; m++) {
      const d = new Date(Date.UTC(y, m - 1, 5, 6));
      if (d < new Date('2023-10-01') || d > new Date('2026-03-31')) continue;
      const no = 1000 + n;
      msgs.push(makeMessage(`m${n++}`, { from: 'MCK Kutty Traders <accounts@mckkutty.in>', subject: `Invoice #${no}`, date: d,
        body: `Dear customer, invoice ${no} attached.`, attachments: [
          { name: `MCK_Invoice_${no}.pdf`, mime: 'application/pdf', data: PDF(`TAX INVOICE #${no}`) },
          { name: 'logo.png', mime: 'image/png', data: Buffer.from('PNG'), inline: true }] }));
    }
  }
  msgs.push(makeMessage('dupe', { from: 'MCK Kutty Traders <accounts@mckkutty.in>', subject: 'Reminder: Invoice #1006', date: '2024-03-15T06:00:00Z',
    body: 'Reminder', attachments: [{ name: 'MCK_Invoice_1006.pdf', mime: 'application/pdf', data: PDF('TAX INVOICE #1006') }] }));
  msgs.push(makeMessage('scan', { from: 'MCK Kutty Traders <accounts@mckkutty.in>', subject: 'Scanned copy', date: '2025-09-15T06:00:00Z',
    body: 'see attached', attachments: [{ name: 'IMG_2207.pdf', mime: 'application/pdf', data: SCAN }] }));
  msgs.push(makeMessage('other', { from: 'Amazon <auto@amazon.in>', subject: 'Your invoice', date: '2025-01-10T06:00:00Z',
    body: 'Order invoice', attachments: [{ name: 'amz.pdf', mime: 'application/pdf', data: PDF('amazon') }] }));
  msgs.push(makeMessage('xl', { from: 'MCK Kutty Traders <accounts@mckkutty.in>', subject: 'Statement', date: '2025-04-01T06:00:00Z',
    body: 'statement', attachments: [{ name: 'stmt.xlsx', mime: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet', data: Buffer.from('PK') }] }));
  return msgs;
}

const DOC_TEXTS = {
  'IMG_2207.pdf': 'MCK KUTTY TRADERS\nTAX INVOICE\nInvoice No: MK-5521   Date: 15/09/2025\nItem   Qty   Amount\nRice bags   4   2,400.00\nSugar   2   1,100.00\nGrand Total   Rs. 18,750.00\nGSTIN 32ABCDE1234F1Z5',
};

const range = (a, b) => ({ fromTs: new Date(a + 'T00:00:00').getTime(), toTs: new Date(b + 'T23:59:59.999').getTime() });

function runSearch(ctx, req) {
  const plan = ctx.planSearch(req);
  const ids = new Set();
  plan.queries.forEach((q) => { let tok = null; do { const r = ctx.listIds(q, tok); r.ids.forEach((i) => ids.add(i)); tok = r.next; } while (tok); });
  return { plan, items: ctx.getMeta([...ids]) };
}

test('query matrix: senders x date windows, Gmail syntax', () => {
  const { ctx } = makeEnv();
  const p = ctx.planSearch({ senders: ['mck kutty', 'MCK KUTTY', 'acme'], keywords: ['invoice'], types: ['pdf', 'excel'],
                             exclude: ['reminder'], ...range('2024-01-01', '2025-12-25'), windowMonths: 3 });
  assert.equal(p.cells, 8 * 2);                                        // duplicate sender removed
  const q = p.queries[0];
  assert.match(q, /has:attachment/);
  assert.match(q, /\{"mck kutty" filename:"mck kutty"\}|\{from:"mck kutty" from:mckkutty\}/);
  assert.match(q, /\{invoice filename:invoice\}/);
  assert.match(q, /filename:pdf filename:xls filename:xlsx/);
  assert.match(q, /-reminder/);
  assert.match(q, /after:\d{10} before:\d{10}/);
  const phrase = ctx.planSearch({ keywords: ['price', 'list'], matchMode: 'phrase' }).queries[0];
  assert.match(phrase, /\{"price list" filename:"price list"\}/);
  const all = ctx.planSearch({ keywords: ['price', 'list'], matchMode: 'all' }).queries[0];
  assert.match(all, /\{price filename:price\} \{list filename:list\}/);
});

test('date windows cover the whole range with overlap', () => {
  const { ctx } = makeEnv();
  const r = range('2024-01-01', '2025-12-25');
  const w = ctx.dateWindows_(r.fromTs, r.toTs, 3);
  assert.equal(w.length, 8);
  assert.ok(w[0][0] <= r.fromTs / 1000 && w[w.length - 1][1] >= r.toTs / 1000);
  for (let i = 1; i < w.length; i++) assert.ok(w[i][0] < w[i - 1][1]);
});

test('search finds attachments, skips inline logos, quick fields from subject', () => {
  const { ctx } = makeEnv({ messages: mailbox() });
  const { items } = runSearch(ctx, { senders: ['mck kutty'], keywords: ['invoice'], types: ['pdf'], ...range('2024-01-01', '2025-12-25') });
  const names = items.filter((i) => !i.inline).map((i) => i.filename);
  assert.ok(names.includes('MCK_Invoice_1004.pdf'));
  assert.ok(!names.includes('amz.pdf'));
  assert.ok(items.some((i) => i.inline && i.filename === 'logo.png'));       // reported, flagged inline
  const it = items.find((i) => i.filename === 'MCK_Invoice_1010.pdf');
  assert.equal(it.fromEmail, 'accounts@mckkutty.in');
  assert.equal(it.fromName, 'MCK Kutty Traders');
  assert.equal(it.fields.doc_type, 'invoice');
  assert.equal(it.fields.doc_no, '1010');
  assert.equal(it.key, `${it.msgId}:${it.partId}`);
});

test('preview returns the exact bytes; saveToDrive organises and never stores duplicates', () => {
  const { ctx, store } = makeEnv({ messages: mailbox() });
  const { items } = runSearch(ctx, { senders: ['mck kutty'], types: ['pdf'], ...range('2024-03-01', '2024-03-31') });
  const files = items.filter((i) => !i.inline);
  const a = ctx.getAttachment(files[0].msgId, files[0].partId);
  assert.equal(Buffer.from(a.b64, 'base64').toString(), PDF('TAX INVOICE #1006').toString());
  const rep = ctx.saveToDrive(files, 'sender/year-month', 'original');
  assert.equal(rep.saved.length, 1, 'reminder carries the identical PDF -> saved once');
  assert.equal(rep.skipped.length, 1);
  const saved = store.files[rep.saved[0].id];
  const parent = store.files[saved.parents[0]];
  assert.equal(parent.name, '2024-03');
  assert.equal(store.files[parent.parents[0]].name, 'MCK Kutty Traders');
  assert.ok(saved.appProperties.sha256.length === 64);
  assert.equal(ctx.saveToDrive(files, 'sender/year-month').saved.length, 0);        // second run: all skipped
});

test('readText: Drive OCR for scanned PDF -> fields + tables, stored and reused', () => {
  const { ctx, store } = makeEnv({ messages: mailbox(), docTexts: DOC_TEXTS });
  const it = ctx.getMeta(['scan'])[0];
  const d = ctx.readText(it);
  assert.equal(d.source, 'ocr');
  assert.equal(d.fields.doc_no, 'MK-5521');
  assert.equal(d.fields.amount, 18750);
  assert.equal(d.fields.currency, 'INR');
  assert.equal(d.fields.doc_date, '2025-09-15');
  assert.equal(d.fields.gstin, '32ABCDE1234F1Z5');
  const rows = JSON.parse(JSON.stringify(d.tables.flat()));
  assert.ok(rows.some((r) => r.join('|') === 'Rice bags|4|2,400.00'), JSON.stringify(rows));
  assert.equal(Object.values(store.files).filter((f) => f.name.startsWith('MAA-temp')).length, 0, 'temp OCR files deleted');
  const stored = ctx.getStored([it.key]);
  assert.equal(stored[it.key].fields.amount, 18750);
  assert.equal(stored[it.key].text_source, 'ocr');
});

test('user corrections are kept even when the file is read again', () => {
  const { ctx } = makeEnv({ messages: mailbox(), docTexts: DOC_TEXTS });
  const it = ctx.getMeta(['scan'])[0];
  ctx.readText(it);
  const f = ctx.saveField(it, 'amount', '1,23,456.50');
  assert.equal(f.amount, 123456.5);
  const again = ctx.readText(it, true);
  assert.equal(again.fields.amount, 123456.5);
  assert.equal(again.fields.doc_no, 'MK-5521');
  assert.throws(() => ctx.saveField(it, 'evil', 'x'));
});

test('xlsx attachments become tables via Sheets conversion', () => {
  const rows = [['Date', 'Desc', 'Amount'], ['01/04/2025', 'Opening', '1,000.00'], ['02/04/2025', 'Invoice 1005', '27,900.00']];
  const { ctx } = makeEnv({ messages: mailbox(), sheetsFromXlsx: { 'stmt.xlsx': rows } });
  const d = ctx.readText(ctx.getMeta(['xl'])[0]);
  assert.equal(d.source, 'office');
  assert.deepEqual(d.tables[0], rows);
});

test('zip, export sheet, share by email and by Drive link', () => {
  const { ctx, store } = makeEnv({ messages: mailbox() });
  const { items } = runSearch(ctx, { senders: ['mck kutty'], types: ['pdf'], ...range('2024-03-01', '2024-04-30') });
  const files = items.filter((i) => !i.inline);
  const z = ctx.makeZip(files, 'sender/year-month');
  assert.equal(z.files, 2);
  assert.equal(z.skipped, 1);
  assert.ok(z.b64 && z.name.endsWith('.zip'));
  const ex = ctx.exportSheet(files.map((f) => ({ ...f, fields: { ...f.fields, amount: 100 } })));
  const ss = Object.values(store.spreadsheets).find((s) => s.getUrl() === ex.url);
  assert.deepEqual(ss.sheets.map((s) => s.name), ['Attachments', 'By month', 'By sender']);
  assert.equal(ss.sheets[0].rows[0][0], 'Email date');
  assert.match(String(ss.sheets[0].rows[files.length + 1][10]), /^=SUBTOTAL\(9,K2:K/);
  const sent = ctx.shareEmail(files.slice(0, 1), 'ca@firm.in; bad', 'Invoices', 'hi');
  assert.deepEqual(Array.from(sent.sentTo), ['ca@firm.in']);
  assert.equal(store.sent[0].attachments.length, 1);
  assert.throws(() => ctx.shareEmail(files, 'nobody', 's', 'm'), /valid email/);
  const sh = ctx.shareDrive(files.slice(0, 2), 'ca@firm.in', 'see');
  assert.equal(store.perms[0].emailAddress, 'ca@firm.in');
  assert.equal(store.perms[0].role, 'reader');
  assert.match(sh.link, /drive\/folders\//);
});

test('risky files flagged; erase moves the data sheet to the Bin', () => {
  const { ctx, store } = makeEnv({ messages: mailbox(), docTexts: DOC_TEXTS });
  assert.ok(ctx.isRisky_('invoice.pdf.exe') && ctx.isRisky_('a.XLSM') && !ctx.isRisky_('a.pdf'));
  ctx.readText(ctx.getMeta(['scan'])[0]);
  assert.equal(ctx.getUser().store.rows, 1);
  ctx.eraseData();
  assert.equal(ctx.getUser().store.exists, false);
  assert.ok(Object.values(store.spreadsheets).some((s) => s.trashed));
});

test('smart fields: common Indian and international formats', () => {
  const { ctx } = makeEnv();
  const cases = [
    ['TAX INVOICE\nInvoice No: INV/24-25/0012   Invoice Date: 15/03/2025\nGrand Total   Rs. 12,390.00',
     { doc_type: 'invoice', doc_no: 'INV/24-25/0012', doc_date: '2025-03-15', amount: 12390, currency: 'INR' }],
    ['Payment received. Total paid $1,234.50 on Nov 3, 2025. Receipt #R-88812',
     { doc_type: 'receipt', doc_no: 'R-88812', doc_date: '2025-11-03', amount: 1234.5, currency: 'USD' }],
    ['Net Pay: 45,000.00\nSalary slip for March 2025', { doc_type: 'payslip', amount: 45000 }],
  ];
  for (const [text, want] of cases) {
    const f = ctx.extractFields(text);
    for (const k of Object.keys(want)) assert.equal(f[k], want[k], `${k} in ${JSON.stringify(f)}`);
  }
  const t = ctx.findTables('Item   Qty   Amount\nCement   10   4,500.00\nSteel   5   6,000.00\nSub Total   10,500.00\nThanks');
  assert.deepEqual(JSON.parse(JSON.stringify(t[0][3])), ['Sub Total', '', '10,500.00']);
});

test('Gmail data as byte arrays (Apps Script advanced-service form) works too', () => {
  const { ctx, b64url } = makeEnv({ messages: mailbox() });
  const Buf = Buffer;
  const orig = ctx.Gmail.Users.Messages.Attachments.get;
  ctx.Gmail.Users.Messages.Attachments.get = (u, id, att) => {
    const s = orig(u, id, att).data.replace(/-/g, '+').replace(/_/g, '/');
    return { data: Array.from(Buf.from(s, 'base64')).map((b) => (b > 127 ? b - 256 : b)) };
  };
  const it = ctx.getMeta(['scan'])[0];
  const a = ctx.getAttachment(it.msgId, it.partId);
  assert.equal(Buf.from(a.b64, 'base64').toString('latin1'), '%PDF-1.4\n/Image /XObject\n%%EOF');
});

test('doGet: uses the Index file, or the built-in screen when there is none (2-file setup)', () => {
  const withIndex = makeEnv({ htmlFiles: { Index: '<p>from file</p>' } });
  assert.equal(withIndex.ctx.doGet().content, '<p>from file</p>');
  const none = makeEnv();
  if (process.env.EASY) {
    assert.match(none.ctx.doGet().content, /Mail Attachment Agent/);
    assert.match(none.ctx.doGet().content, /google\.script\.run/);
  } else {
    assert.throws(() => none.ctx.doGet(), /name it exactly Index/);
  }
});
