/*
 * Local preview of the Apps Script web app, for development and UI tests.
 *   node test/devserver.js [port]
 * Serves Index.html (with include() expanded) and answers google.script.run calls by running
 * the real Code.gs against the in-memory fake Gmail / Drive / Sheets from mocks.js.
 */
'use strict';
const http = require('http');
const fs = require('fs');
const path = require('path');
const { makeEnv, makeMessage } = require('./mocks');

const ROOT = path.join(__dirname, '..');
const PDF = (lines) => {        // tiny valid PDF with a text layer (so pdf.js can render it)
  const esc = (s) => s.replace(/[\\()]/g, (c) => '\\' + c);
  const content = 'BT /F1 14 Tf 60 780 Td 18 TL ' + lines.map((l) => `(${esc(l)}) '`).join(' ') + ' ET';
  const objs = ['<< /Type /Catalog /Pages 2 0 R >>', '<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
    '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>',
    `<< /Length ${content.length} >>\nstream\n${content}\nendstream`, '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>'];
  let out = '%PDF-1.4\n'; const offs = [];
  objs.forEach((o, i) => { offs.push(out.length); out += `${i + 1} 0 obj\n${o}\nendobj\n`; });
  const x = out.length;
  out += `xref\n0 ${objs.length + 1}\n0000000000 65535 f \n` + offs.map((o) => String(o).padStart(10, '0') + ' 00000 n \n').join('');
  out += `trailer\n<< /Size ${objs.length + 1} /Root 1 0 R >>\nstartxref\n${x}\n%%EOF\n`;
  return Buffer.from(out, 'latin1');
};

function demoMailbox() {
  const msgs = [];
  let n = 1001;
  for (let d = new Date(Date.UTC(2023, 9, 5, 6)); d < new Date('2026-04-01'); d = new Date(Date.UTC(d.getUTCFullYear(), d.getUTCMonth() + 1, 3 + (n % 6), 6))) {
    const q1 = 2 + (n * 7) % 30, q2 = 1 + (n * 3) % 15, amt = q1 * 450 + q2 * 1200;
    const f = (v) => v.toLocaleString('en-IN', { minimumFractionDigits: 2 });
    const lines = [`MCK KUTTY TRADERS - TAX INVOICE #${n}`, `Date: ${d.toISOString().slice(0, 10)}`, 'GSTIN 32ABCDE1234F1Z5', '',
      'Item              Qty      Rate        Amount', `Cement bags       ${q1}       450.00      ${f(q1 * 450)}`,
      `Steel rods        ${q2}       1,200.00    ${f(q2 * 1200)}`, `Grand Total                           INR ${f(amt)}`];
    msgs.push(makeMessage(`m${n}`, { from: 'MCK Kutty Traders <accounts@mckkutty.in>', subject: `Invoice #${n} for ${d.toLocaleString('en', { month: 'long', year: 'numeric' })}`,
      date: d, body: `Dear customer, please find attached invoice ${n}. Regards, MCK Kutty`,
      attachments: [{ name: `MCK_Invoice_${n}.pdf`, mime: 'application/pdf', data: PDF(lines), text: lines.join('\n') },
                    { name: 'logo.png', mime: 'image/png', data: Buffer.from('89504e470d0a1a0a', 'hex'), inline: true }] }));
    if (n % 7 === 0) {
      msgs.push(makeMessage(`r${n}`, { from: 'MCK Kutty Traders <accounts@mckkutty.in>', subject: `Reminder: payment due for invoice ${n}`, date: new Date(d.getTime() + 9 * 864e5),
        body: 'Gentle reminder - invoice attached again.', attachments: [{ name: `MCK_Invoice_${n}.pdf`, mime: 'application/pdf', data: PDF(lines), text: lines.join('\n') }] }));
    }
    n++;
  }
  const scanLines = ['MCK KUTTY TRADERS', 'TAX INVOICE', 'Invoice No: MK-5521   Date: 15/09/2025', 'Grand Total   Rs. 18,750.00'];
  msgs.push(makeMessage('scan', { from: 'MCK Kutty Traders <accounts@mckkutty.in>', subject: 'Scanned copy', date: '2025-09-15T06:00:00Z',
    body: 'Scanned copy attached. TAX INVOICE inside.', attachments: [{ name: 'IMG_2207.pdf', mime: 'application/pdf', data: Buffer.from('%PDF-1.4\n/XObject\n%%EOF'), text: scanLines.join('\n') }] }));
  msgs.push(makeMessage('amz', { from: 'Amazon <auto@amazon.in>', subject: 'Your invoice', date: '2025-01-10T06:00:00Z', body: 'Order invoice',
    attachments: [{ name: 'amazon_invoice.pdf', mime: 'application/pdf', data: PDF(['Amazon invoice', 'Total INR 1,299.00']) }] }));
  msgs.push(makeMessage('exe', { from: 'Unknown <x@spam.example>', subject: 'Invoice overdue', date: '2025-05-02T06:00:00Z', body: 'open the invoice',
    attachments: [{ name: 'invoice.pdf.exe', mime: 'application/octet-stream', data: Buffer.from('MZ') }] }));
  // everyday conversations (no attachments) - for the People list
  const team = [['Parthag', 'parthag@mckkutty.com'], ['Accounts', 'accounts@mckkutty.com'], ['Sales Desk', 'sales@mckkutty.com'],
                ['Anil Kumar', 'anil.kumar@mckkutty.com'], ['Office', 'office@mckkutty.com'], ['Ravi Kumar', 'ravi@gmail.com'],
                ['CA Firm', 'audit@cafirm.in']];
  team.forEach(([name, email], i) => {
    msgs.push(makeMessage(`c${i}`, { from: `${name} <${email}>`, to: 'Parthag <parthag@mckkutty.com>', cc: 'ACCOUNTS@MCKKUTTY.COM, "Kutty, MCK" <md@mckkutty.com>',
      subject: `Re: weekly update ${i}`, date: new Date(Date.UTC(2025, 6, 1 + i, 6)), body: 'noted, thanks', threadId: `TC${i % 3}` }));
  });
  return msgs;
}

function start(port = 8790) {
  const messages = demoMailbox();
  // the fake "Drive OCR" returns the text that each demo file really contains
  const docTexts = Object.fromEntries(messages.flatMap((m) => m._texts || []));
  const env = makeEnv({ messages, docTexts });
  const page = () => fs.readFileSync(path.join(ROOT, 'Index.html'), 'utf8')
    .replace(/<\?!= include\('(\w+)'\); \?>/g, (_, f) => fs.readFileSync(path.join(ROOT, f + '.html'), 'utf8'))
    .replace('<head>', `<head><script>
      // stand-in for Apps Script's google.script.run (calls the real Code.gs on the dev server)
      window.google = { script: { get run() {
        let ok = () => {}, bad = () => {};
        const h = { withSuccessHandler(f) { ok = f; return p; }, withFailureHandler(f) { bad = f; return p; } };
        const p = new Proxy(h, { get(t, name) { return t[name] || ((...args) => fetch('/rpc/' + name, { method: 'POST', body: JSON.stringify(args) })
          .then((r) => r.json()).then((j) => (j.error ? bad({ message: j.error }) : ok(j.result)))); } });
        return p; } } };
    </script>`);
  const server = http.createServer((req, res) => {
    if (req.method === 'GET' && (req.url === '/' || req.url.startsWith('/?'))) {
      res.writeHead(200, { 'content-type': 'text/html; charset=utf-8' }); res.end(page()); return;
    }
    const m = /^\/rpc\/(\w+)$/.exec(req.url);
    if (req.method === 'POST' && m) {
      let body = '';
      req.on('data', (c) => { body += c; });
      req.on('end', () => {
        const fn = m[1];
        let out;
        try {
          if (fn.endsWith('_') || typeof env.ctx[fn] !== 'function') throw new Error(`${fn} is not callable`);
          out = { result: JSON.parse(JSON.stringify(env.ctx[fn](...JSON.parse(body)) ?? null)) };
        } catch (e) { out = { error: String(e.message || e) }; }
        res.writeHead(200, { 'content-type': 'application/json' }); res.end(JSON.stringify(out));
      });
      return;
    }
    res.writeHead(404); res.end();
  });
  server.listen(port, '127.0.0.1');
  return { server, env };
}

module.exports = { start, demoMailbox };

if (require.main === module) {
  const port = Number(process.argv[2] || 8790);
  start(port);
  console.log(`Dev preview at http://127.0.0.1:${port}`);
}
