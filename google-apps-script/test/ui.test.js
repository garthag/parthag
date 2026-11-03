/*
 * End-to-end UI test: real Index.html + App.html in Chromium, real Code.gs behind google.script.run
 * (fake Gmail/Drive/Sheets).  PDFJS_DIR=/path/to/pdfjs-dist/build node test/ui.test.js
 */
'use strict';
const assert = require('node:assert/strict');
const fs = require('fs');
const path = require('path');
const { chromium } = require('playwright');
const { start } = require('./devserver');

(async () => {
  const port = 8791;
  const { server, env } = start(port);
  const browser = await chromium.launch();
  const page = await browser.newPage({ viewport: { width: 1500, height: 920 } });
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  page.on('console', (m) => m.type() === 'error' && !/cdnjs|ERR_|Failed to load resource/.test(m.text()) && errors.push(m.text()));
  const pdfjs = process.env.PDFJS_DIR;
  await page.route('https://cdnjs.cloudflare.com/**', (route) => {
    const f = route.request().url().split('/').pop();
    if (pdfjs && fs.existsSync(path.join(pdfjs, f))) route.fulfill({ path: path.join(pdfjs, f), contentType: 'text/javascript', headers: { 'access-control-allow-origin': '*' } });
    else route.abort();
  });
  const shot = (n) => process.env.SHOTS && page.screenshot({ path: path.join(process.env.SHOTS, n) });

  await page.goto(`http://127.0.0.1:${port}/`);
  await page.waitForFunction(() => document.querySelector('#account').textContent.includes('@'));
  assert.equal(await page.textContent('#account'), 'parthag@mckkutty.com');

  // ---- search
  await page.fill('[name=senders]', 'mck kutty');
  await page.fill('[name=keywords]', 'invoice');
  await page.fill('[name=date_from]', '2024-01-01');
  await page.fill('[name=date_to]', '2025-12-25');
  await page.click('#goBtn');
  await page.waitForSelector('#tbl tbody tr');
  const stats = (await page.textContent('#stats')).replace(/\s+/g, ' ');
  console.log('stats:', stats);
  const names = await page.$$eval('#tbl tbody .fn', (els) => els.map((e) => e.textContent));
  assert.ok(names.some((n) => n.includes('MCK_Invoice_1004.pdf')), 'Jan 2024 invoice found');
  assert.ok(!names.some((n) => n.includes('amazon')), 'other sender filtered');
  assert.ok(!names.some((n) => n.includes('logo.png')), 'inline logo hidden');
  assert.ok(names.some((n) => n.includes('IMG_2207.pdf')), 'Gmail matched the word inside the scanned file');
  assert.match(stats, /duplicate files removed/);
  const n = names.length;
  assert.ok(n >= 24 && n <= 26, `expected ~25 rows, got ${n}`);

  // ---- preview (pdf.js) + text (Drive OCR) + details + tables
  await page.locator('#tbl tbody tr').nth(1).click();
  if (pdfjs) { await page.waitForSelector('#pvBody canvas', { timeout: 15000 }); console.log('pdf.js preview: rendered'); }
  await page.click('#pvTabs [data-m=tables]');
  await page.waitForSelector('#pvBody .dt');
  assert.equal(await page.textContent('#pvBody .dt tr td'), 'Item');
  await shot('gas-search.png');
  await page.click('#pvTabs [data-m=fields]');
  await page.waitForSelector('#fieldsForm');
  assert.equal(await page.inputValue('#fieldsForm [name=gstin]'), '32ABCDE1234F1Z5');
  await page.fill('#fieldsForm [name=doc_no]', 'CORRECTED-1');
  await page.press('#fieldsForm [name=doc_no]', 'Tab');
  await page.waitForFunction(() => document.querySelector('#toast').textContent === 'Saved');
  await page.click('#pvTabs [data-m=text]');
  await page.waitForSelector('#pvPre mark');
  await page.click('#pvClose');

  // ---- select range, read text (OCR), save to Drive twice (dedupe), zip, export sheet
  await page.locator('#tbl tbody tr input[type=checkbox]').nth(0).click();
  await page.locator('#tbl tbody tr').nth(5).click({ modifiers: ['Shift'] });
  assert.match(await page.textContent('#selInfo'), /^6 selected/);
  await page.click('#readBtn');
  await page.waitForFunction(() => document.querySelector('#toast').textContent.startsWith('Read '), null, { timeout: 30000 });
  console.log('read:', await page.textContent('#toast'));
  assert.match(await page.textContent('#tbl tbody tr:first-child .amt'), /₹/);
  await page.click('#saveBtn');
  await page.waitForFunction(() => /Saved \d+ file/.test(document.querySelector('#dlReport').textContent));
  console.log('save #1:', (await page.textContent('#dlReport')).trim().slice(0, 80));
  await page.click('#saveBtn');
  await page.waitForFunction(() => /skipped/.test(document.querySelector('#dlReport').textContent));
  console.log('save #2:', (await page.textContent('#dlReport')).replace(/\s+/g, ' ').trim().slice(0, 120));
  const [dl] = await Promise.all([page.waitForEvent('download'), page.click('#zipBtn')]);
  console.log('zip download:', dl.suggestedFilename());
  await page.click('.menu summary');
  const popup = page.waitForEvent('popup').catch(() => null);
  await page.click('[data-export=sheet]');
  await page.waitForFunction(() => /Google Sheet created/.test(document.querySelector('#dlReport').textContent));
  await popup;
  console.log('export:', (await page.textContent('#dlReport')).trim().slice(0, 60));

  // ---- share: Enter key must not send; email + Drive link
  await page.click('#shareBtn');
  await page.fill('#shareForm [name=to]', 'ca@firm.in');
  await page.press('#shareForm [name=to]', 'Enter');
  await page.waitForTimeout(300);
  assert.equal(env.store.sent.length, 0, 'Enter key did not send');
  await page.click('#shareForm [value=email]');
  await page.waitForFunction(() => /Sent/.test(document.querySelector('#shareMsg').textContent));
  assert.equal(env.store.sent.length, 1);
  await page.click('#shareForm [value=drive]');
  await page.waitForFunction(() => /Shared/.test(document.querySelector('#shareMsg').textContent));
  assert.equal(env.store.perms[0].emailAddress, 'ca@firm.in');
  await page.click('#shareForm [value=cancel]');

  // ---- table view: group + edit amount
  await page.click('#tabs [data-view=table]');
  await page.selectOption('#groupBy', 'month');
  const cell = page.locator('#sheet tbody tr').first().locator('td[data-f=amount]');
  await cell.click(); await page.keyboard.press('Control+A'); await page.keyboard.type('99999'); await page.keyboard.press('Enter');
  await page.waitForTimeout(300);
  console.log('table total:', await page.textContent('#sheetTotal'));
  await shot('gas-table.png');

  // ---- risky file label
  await page.click('#tabs [data-view=search]');
  await page.fill('[name=senders]', '');
  await page.fill('[name=keywords]', 'overdue');
  for (const t of ['pdf']) await page.uncheck(`[name=t][value=${t}]`);
  await page.click('#goBtn');
  await page.waitForSelector('#tbl tbody .risky');
  console.log('risky label: shown');

  // ---- data view
  await page.click('#tabs [data-view=data]');
  await page.waitForFunction(() => /documents read/.test(document.querySelector('#kpis').textContent));
  await shot('gas-data.png');

  assert.deepEqual(errors, []);
  console.log('UI TEST OK');
  await browser.close();
  server.close();
})().catch((e) => { console.error(e); process.exit(1); });
