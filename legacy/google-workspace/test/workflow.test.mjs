// End-to-end tests of dist/Code.gs against in-memory Google service fakes.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import vm from 'node:vm'
import { createGoogle } from './fakes.mjs'

const CODE = readFileSync(new URL('../dist/Code.gs', import.meta.url), 'utf8')

function boot() {
  const ctx = vm.createContext({ console, Buffer })
  const g = createGoogle({ DateCtor: vm.runInContext('Date', ctx) })
  Object.assign(ctx, g.globals)
  vm.runInContext(CODE, ctx, { filename: 'Code.gs' })
  const fresh = () => { ctx.cache_ = {} }
  const rows = (t, pred = () => true) => { fresh(); return ctx.rows_(t).filter(pred) }
  const one = (t, pred) => rows(t, pred)[0]
  const set = (t, row, patch) => { fresh(); ctx.update_(t, ctx.byKey_(t, row[ctx.keyOf_(t)]), patch) } // like an AppSheet edit
  const add = (t, objs) => { fresh(); return ctx.append_(t, objs) }
  const run = () => ctx.processQueue()
  return { g, ctx, rows, one, set, add, run }
}

const plain = (x) => JSON.parse(JSON.stringify(x))
// Dates come from the script realm, so duck-type them; sheet dates are IST midnights.
const iso = (d) => (d && typeof d.getTime === 'function' ? new Intl.DateTimeFormat('en-CA', { timeZone: 'Asia/Kolkata' }).format(d) : String(d))

test('setup creates every sheet, settings, admin user, Drive root and triggers (idempotent)', () => {
  const { g, ctx, rows } = boot()
  ctx.setup()
  ctx.setup()
  for (const name of Object.keys(ctx.SCHEMA)) assert.ok(g.sheets.get(name), `sheet ${name}`)
  assert.equal(g.sheets.has('Sheet1'), false)
  assert.deepEqual(plain(rows('Users').map((u) => [u.Email, u.Role])), [['owner@example.com', 'Admin']])
  assert.equal(rows('Settings', (s) => s.Key === 'ROOT_FOLDER_ID').length, 1)
  assert.deepEqual(plain(g.triggers.map((t) => t.fn).sort()), ['nightly', 'processQueue'])
  assert.ok(g.sheets.get('DPR').validations.some((v) => v.rule.list?.includes('Approved')), 'status dropdown')
  assert.ok(g.sheets.get('Audit').protections.length === 1, 'audit sheet protected')
})

test('demo project: schedule, baseline, seven approved DPRs with snapshots, PDFs and recalculated totals', () => {
  const { g, ctx, rows, one } = boot()
  ctx.setup()
  ctx.loadDemo()
  const p = one('Projects', (x) => x.Code === 'DEMO-BF2')
  const dprs = rows('DPR', (d) => d.ProjectID === p.ProjectID)
  assert.equal(dprs.length, 7)
  assert.ok(dprs.every((d) => d.ProcessedStatus === 'Approved' && d.Status === 'Approved' && d.SnapshotHash.length === 64 && d.PdfURL))
  assert.deepEqual(plain(dprs.map((d) => d.DPRNo).sort()), ['01', '02', '03', '04', '05', '06', '07'].map((n) => `DEMO-BF2-DPR-00${n}`))
  assert.equal(rows('Snapshots').length, 7)
  const exc = one('Activities', (a) => a.Code === 'A1000')
  assert.equal(exc.AcceptedCum, 85 * 6 + 40)
  assert.equal(exc.ProgressPct, Math.round((550 * 100 / 1200) * 100) / 100)
  assert.match(iso(exc.ActualStart), /^\d{4}-\d{2}-\d{2}$/)
  assert.equal(exc.Status, 'In progress')
  assert.ok(iso(exc.BaselineStart) && iso(exc.EarlyStart))
  const cement = one('Materials', (m) => m.Code === 'CEM-OPC53')
  assert.equal(cement.Stock, 600 * 4 - 220 * 7)
  const boq = one('BOQ', (b) => b.ItemNo === 'S-02')
  assert.equal(boq.ExecutedQty, 9 * 4)
  assert.equal(boq.ContractAmount, 450 * 18000)
  // Last DPR's previous cumulative equals the sum of the six before it.
  const last = dprs.sort((a, b) => iso(a.ReportDate).localeCompare(iso(b.ReportDate))).at(-1)
  const line = one('DPR_Activities', (l) => l.DPRID === last.DPRID && l.ActivityID === exc.ActivityID)
  assert.equal(line.PrevCum, 85 * 5 + 40)
  assert.equal(line.CumQty, 550)
  // PDFs are in 04_DPR/YYYY/MM/Approved PDFs and contain the quantities.
  const pdfs = [...g.files.values()].filter((f) => f.name?.endsWith('.pdf'))
  assert.equal(pdfs.length, 7)
  assert.equal(pdfs[0].parent.getName(), 'Approved PDFs')
  const lastDoc = g.docs.find((d) => d.title.startsWith(last.DPRNo))
  assert.ok(lastDoc.text.some((t) => t.includes('550')), 'cumulative 550 printed')
  assert.ok(rows('Report_Daily').length >= 14)
  assert.ok(rows('Report_SCurve').some((r) => r.ActualPct !== ''))
  assert.ok(g.sent.some((m) => m.body.includes('approved')))
})

function project(t) {
  t.ctx.setup()
  t.ctx.loadDemo()
  t.add('Users', [{ Email: 'site@example.com', Name: 'Site', Role: 'Site Engineer', Projects: 'ALL', Active: true },
    { Email: 'pm@example.com', Name: 'PM', Role: 'Project Manager', Projects: 'ALL', Active: true }])
  const p = t.one('Projects', (x) => x.Code === 'DEMO-BF2')
  t.set('Projects', p, { AllowSelfApproval: false })
  const acts = Object.fromEntries(t.rows('Activities').map((a) => [a.Code, a]))
  return { p, acts }
}

function newDpr(t, p, date, lines, extra = {}) {
  const d = t.add('DPR', [{ ProjectID: p.ProjectID, Revision: 0, ReportDate: date, Shift: 'Night', Weather: 'Clear', PreparedBy: 'site@example.com', Status: 'Draft', ...extra }])[0]
  t.add('DPR_Activities', lines.map(([a, qty, more]) => ({ DPRID: d.DPRID, ProjectID: p.ProjectID, ActivityID: a.ActivityID, QtyToday: qty, ...(more || {}) })))
  return d
}

test('submit validation: overrun is returned to draft with a reason; authorised overrun passes', () => {
  const t = boot()
  const { p, acts } = project(t)
  const d = newDpr(t, p, t.ctx.today_(), [[acts.A2010, 290]])
  t.set('DPR', d, { Status: 'Submitted' })
  t.run()
  let row = t.one('DPR', (x) => x.DPRID === d.DPRID)
  assert.equal(row.Status, 'Draft')
  assert.match(row.Issues, /exceeds approved 300/)
  const line = t.one('DPR_Activities', (l) => l.DPRID === d.DPRID)
  t.set('DPR_Activities', line, { OverrunAuthorised: true, OverrunReason: 'CO-07 extra bracing' })
  t.set('DPR', row, { Status: 'Submitted' })
  t.run()
  row = t.one('DPR', (x) => x.DPRID === d.DPRID)
  assert.equal(row.ProcessedStatus, 'Submitted')
  assert.equal(row.DPRNo, 'DEMO-BF2-DPR-0008')
})

test('approval rules: approver role required, no self-approval, rejection needs a reason, accepted qty drives cumulative', () => {
  const t = boot()
  const { p, acts } = project(t)
  const d = newDpr(t, p, t.ctx.today_(), [[acts.A2010, 12]])
  t.set('DPR', d, { Status: 'Submitted' }); t.run()

  t.set('DPR', d, { Status: 'Approved', ReviewedBy: 'site@example.com' }); t.run()
  let row = t.one('DPR', (x) => x.DPRID === d.DPRID)
  assert.equal(row.Status, 'Submitted')
  assert.match(row.Issues, /approver role/)

  t.set('Users', t.one('Users', (u) => u.Email === 'site@example.com'), { Role: 'Project Manager' })
  t.set('DPR', row, { Status: 'Approved', ReviewedBy: 'site@example.com' }); t.run()
  row = t.one('DPR', (x) => x.DPRID === d.DPRID)
  assert.equal(row.Status, 'Submitted')
  assert.match(row.Issues, /cannot approve a report you prepared/)

  t.set('DPR', row, { Status: 'Rejected', ReviewedBy: 'pm@example.com' }); t.run()
  assert.equal(t.one('DPR', (x) => x.DPRID === d.DPRID).Status, 'Submitted')
  t.set('DPR', row, { Status: 'Rejected', ReviewedBy: 'pm@example.com', ReviewComment: 'Add weather details' }); t.run()
  assert.equal(t.one('DPR', (x) => x.DPRID === d.DPRID).ProcessedStatus, 'Rejected')

  t.set('DPR', row, { Status: 'Submitted' }); t.run()
  const line = t.one('DPR_Activities', (l) => l.DPRID === d.DPRID)
  t.set('DPR_Activities', line, { QtyAccepted: 10 })
  t.set('DPR', row, { Status: 'Approved', ReviewedBy: 'pm@example.com' }); t.run()
  const done = t.one('DPR_Activities', (l) => l.DPRID === d.DPRID)
  assert.equal(done.QtyToday, 12)
  assert.equal(done.QtyAccepted, 10)
  assert.equal(done.CumQty, 36 + 10)
  assert.equal(t.one('Activities', (a) => a.Code === 'A2010').AcceptedCum, 46)
  const log = t.rows('Approvals', (a) => a.DPRID === d.DPRID).map((a) => a.Action)
  assert.deepEqual(plain(log), ['submit', 'blocked', 'blocked', 'blocked', 'reject', 'submit', 'approve'])
})

test('revision: approved report stays intact, new revision replaces it in progress figures', () => {
  const t = boot()
  const { p, acts } = project(t)
  const d = newDpr(t, p, t.ctx.today_(), [[acts.A2010, 12]])
  t.set('DPR', d, { Status: 'Submitted' }); t.run()
  t.set('DPR', d, { Status: 'Approved', ReviewedBy: 'pm@example.com' }); t.run()
  const hash = t.one('DPR', (x) => x.DPRID === d.DPRID).SnapshotHash

  t.set('DPR', d, { Status: 'Revision requested' }); t.run()
  assert.match(t.one('DPR', (x) => x.DPRID === d.DPRID).Issues, /revision reason/)
  t.set('DPR', d, { Status: 'Revision requested', RevisionReason: 'Re-measured: 11 MT' }); t.run()
  const orig = t.one('DPR', (x) => x.DPRID === d.DPRID)
  assert.equal(orig.Status, 'Approved')
  const rev = t.one('DPR', (x) => x.PreviousDPRID === d.DPRID)
  assert.equal(rev.Revision, 1)
  assert.equal(rev.DPRNo, orig.DPRNo)
  assert.equal(rev.ProcessedStatus, 'Draft')
  const revLine = t.one('DPR_Activities', (l) => l.DPRID === rev.DPRID)
  t.set('DPR_Activities', revLine, { QtyToday: 11 })
  t.set('DPR', rev, { Status: 'Submitted' }); t.run()
  t.set('DPR', rev, { Status: 'Approved', ReviewedBy: 'pm@example.com' }); t.run()
  assert.equal(t.one('Activities', (a) => a.Code === 'A2010').AcceptedCum, 36 + 11)
  assert.deepEqual(plain(t.ctx.integrityCheck()), [])
  assert.equal(t.one('DPR', (x) => x.DPRID === d.DPRID).SnapshotHash, hash)
})

test('integrity check catches edits to approved data but not legitimate later changes', () => {
  const t = boot()
  project(t)
  const d = t.rows('DPR')[0]
  t.set('Activities', t.one('Activities', (a) => a.Code === 'A1000'), { Name: 'Renamed later' })
  t.add('Safety', [{ ProjectID: d.ProjectID, DPRID: d.DPRID, Category: 'Near miss', Description: 'x', Status: 'Open' }])
  assert.deepEqual(plain(t.ctx.integrityCheck()), [])
  const line = t.one('DPR_Activities', (l) => l.DPRID === d.DPRID)
  t.set('DPR_Activities', line, { QtyToday: 999 })
  assert.deepEqual(plain(t.ctx.integrityCheck()), [d.DPRNo + ' R0'])
  assert.equal(t.one('Snapshots', (s) => s.DPRID === d.DPRID).IntegrityOK, false)
  assert.ok(t.g.sent.some((m) => m.subject.includes('approved data changed')))
})

test('voice entry: proposal first, lines only after confirmation', () => {
  const t = boot()
  const { p, acts } = project(t)
  const d = newDpr(t, p, t.ctx.today_(), [])
  const q = t.add('QuickEntry', [{ DPRID: d.DPRID, ProjectID: p.ProjectID, Status: 'New',
    Statement: 'At the skip bridge, erected twelve metric tonnes of structural steel during day shift using one mobile crane and twenty-four workers.' }])[0]
  t.run()
  let row = t.one('QuickEntry', (x) => x.EntryID === q.EntryID)
  assert.equal(row.Status, 'Proposed')
  assert.equal(row.ProposedActivityID, acts.A2010.ActivityID)
  assert.equal(row.ProposedQty, 12)
  assert.equal(row.ProposedWorkers, 24)
  assert.equal(row.ProposedEquipment, '1 × mobile crane')
  assert.equal(t.rows('DPR_Activities', (l) => l.DPRID === d.DPRID).length, 0, 'nothing added before confirmation')
  t.set('QuickEntry', row, { Status: 'Confirmed', ProposedTrade: 'Erection' })
  t.run()
  assert.equal(t.one('QuickEntry', (x) => x.EntryID === q.EntryID).Status, 'Applied')
  assert.equal(t.one('DPR_Activities', (l) => l.DPRID === d.DPRID).QtyToday, 12)
  assert.equal(t.one('Manpower', (m) => m.DPRID === d.DPRID).Skilled, 24)
  assert.equal(t.rows('EquipmentUse', (e) => e.DPRID === d.DPRID).length, 1)
})

test('BOQ revisions need a PM/Admin approver and a reason; approved reports are unaffected', () => {
  const t = boot()
  project(t)
  const boq = t.one('BOQ', (b) => b.ItemNo === 'S-02')
  const hashes = t.rows('Snapshots').map((s) => s.SHA256)
  t.add('BOQRevisions', [{ BOQID: boq.BOQID, NewQty: 500, NewRate: 1, Reason: 'x', Status: 'Approved', ApprovedBy: 'site@example.com' }])
  t.run()
  assert.equal(t.one('BOQ', (b) => b.ItemNo === 'S-02').Rate, 18000)
  t.add('BOQRevisions', [{ BOQID: boq.BOQID, NewQty: 480, NewRate: 18250.75, Reason: 'CO-01 platform steel', Status: 'Approved', ApprovedBy: 'pm@example.com' }])
  t.run()
  const b2 = t.one('BOQ', (b) => b.ItemNo === 'S-02')
  assert.equal(b2.RevisedQty, 480)
  assert.equal(b2.ContractAmount, 8760360)
  assert.equal(t.one('BOQRevisions', (r) => r.Reason === 'CO-01 platform steel').OldQty, 450)
  assert.deepEqual(plain(t.rows('Snapshots').map((s) => s.SHA256)), plain(hashes))
  assert.deepEqual(plain(t.ctx.integrityCheck()), [])
})

test('duplicate report for the same date/shift/area is refused; DMY text dates are understood', () => {
  const t = boot()
  const { p, acts } = project(t)
  const first = t.rows('DPR').sort((a, b) => iso(a.ReportDate).localeCompare(iso(b.ReportDate)))[0]
  const dupe = t.add('DPR', [{ ProjectID: p.ProjectID, Revision: 0, ReportDate: first.ReportDate, Shift: 'Day', AreaID: first.AreaID, PreparedBy: 'site@example.com', Status: 'Submitted', WorkCompleted: 'x' }])[0]
  t.run()
  assert.match(t.one('DPR', (x) => x.DPRID === dupe.DPRID).Issues, /already exists/)
  const [y, m, d] = t.ctx.today_().split('-')
  const typed = newDpr(t, p, `${d}/${m}/${y}`, [[acts.A1000, 5]])
  t.set('DPR', typed, { Status: 'Submitted' }); t.run()
  assert.equal(t.one('DPR', (x) => x.DPRID === typed.DPRID).ProcessedStatus, 'Submitted')
})

test('photo evidence is linked into the dated Drive folder, checksummed and printed in the PDF', () => {
  const t = boot()
  const { p, acts } = project(t)
  t.g.addAppFile('Evidence_Images/abc.Photo.jpg', 'fake-jpeg-bytes')
  const d = newDpr(t, p, t.ctx.today_(), [[acts.A2010, 6]])
  t.add('Evidence', [{ DPRID: d.DPRID, ProjectID: p.ProjectID, Type: 'Photo', Photo: 'Evidence_Images/abc.Photo.jpg', Caption: 'Truss T3 lifted' }])
  t.set('DPR', d, { Status: 'Submitted' }); t.run()
  t.set('DPR', d, { Status: 'Approved', ReviewedBy: 'pm@example.com' }); t.run()
  const ev = t.one('Evidence', (e) => e.DPRID === d.DPRID)
  assert.equal(ev.SHA256.length, 64)
  const shortcut = [...t.g.files.values()].find((f) => f.name === 'shortcut→' + ev.DriveFileID)
  assert.equal(shortcut.parent.getName(), 'Photographs')
  const doc = t.g.docs.find((x) => x.title.startsWith(t.one('DPR', (x) => x.DPRID === d.DPRID).DPRNo))
  assert.ok(doc.text.includes('[image]'))
  assert.ok(doc.text.some((s) => s.includes('Truss T3 lifted')))
})

test('schedule: CPM dates, float and baseline that does not move when the forecast slips', () => {
  const t = boot()
  const { p } = project(t)
  const before = t.one('Activities', (a) => a.Code === 'M9000')
  assert.equal(before.Critical, true)
  const blFinish = iso(before.BaselineFinish)
  t.set('Activities', t.one('Activities', (a) => a.Code === 'A2010'), { Duration: 60 })
  t.ctx.cache_ = {}
  t.ctx.runSchedule_(p.ProjectID)
  const after = t.one('Activities', (a) => a.Code === 'M9000')
  assert.ok(iso(after.EarlyFinish) > blFinish, 'forecast slipped')
  assert.equal(iso(after.BaselineFinish), blFinish, 'baseline unchanged')
  assert.equal(t.rows('Baselines', (b) => b.ProjectID === p.ProjectID).length, 3)
})

test('stock: issue beyond approved stock is a warning at submit and blocks approval', () => {
  const t = boot()
  const { p, acts } = project(t)
  const mat = t.one('Materials', (m) => m.Code === 'CEM-OPC53')
  const stock = mat.Stock
  const d = newDpr(t, p, t.ctx.today_(), [[acts.A1000, 5]])
  t.add('MaterialMoves', [{ DPRID: d.DPRID, ProjectID: p.ProjectID, MaterialID: mat.MaterialID, Issued: stock + 100 }])
  t.set('DPR', d, { Status: 'Submitted' }); t.run()
  let row = t.one('DPR', (x) => x.DPRID === d.DPRID)
  assert.equal(row.ProcessedStatus, 'Submitted')
  assert.match(row.Issues, /approve earlier reports first/)
  t.set('DPR', row, { Status: 'Approved', ReviewedBy: 'pm@example.com' }); t.run()
  row = t.one('DPR', (x) => x.DPRID === d.DPRID)
  assert.equal(row.Status, 'Submitted')
  assert.match(row.Issues, /exceeds approved stock/)
  assert.equal(t.one('Materials', (m) => m.Code === 'CEM-OPC53').Stock, stock)
})
