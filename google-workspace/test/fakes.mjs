// In-memory fakes of the Google Apps Script services used by Code.gs.
// Behaviour follows the documented Apps Script APIs closely enough to run the
// workflow end to end; they are not a full emulation.
import { createHash, randomUUID } from 'node:crypto'

class Range {
  constructor(sheet, r, c, nr, nc) { Object.assign(this, { sheet, r, c, nr, nc }) }
  getValues() {
    const out = []
    for (let i = 0; i < this.nr; i++) {
      const row = []
      for (let j = 0; j < this.nc; j++) row.push(this.sheet.cell(this.r + i, this.c + j))
      out.push(row)
    }
    return out
  }
  getValue() { return this.sheet.cell(this.r, this.c) }
  setValues(v) {
    if (v.length !== this.nr || v.some((row) => row.length !== this.nc)) throw new Error(`setValues: dimensions ${v.length}x${v[0]?.length} != ${this.nr}x${this.nc}`)
    v.forEach((row, i) => row.forEach((x, j) => this.sheet.set(this.r + i, this.c + j, x)))
    return this
  }
  setValue(x) { this.sheet.set(this.r, this.c, x); return this }
  clearContent() { for (let i = 0; i < this.nr; i++) for (let j = 0; j < this.nc; j++) this.sheet.set(this.r + i, this.c + j, ''); return this }
  getRow() { return this.r }
  setFontWeight() { return this } setBackground() { return this } setFontColor() { return this } setNumberFormat() { return this }
  setDataValidation(rule) { this.sheet.validations.push({ col: this.c, rule }); return this }
}

class Sheet {
  constructor(name, makeDate) { this.makeDate = makeDate; this.name = name; this.data = []; this.validations = []; this.protections = []; this.frozen = 0 }
  getName() { return this.name }
  cell(r, c) { const v = this.data[r - 1]?.[c - 1]; return v === undefined ? '' : v }
  set(r, c, v) {
    while (this.data.length < r) this.data.push([])
    const row = this.data[r - 1]
    while (row.length < c) row.push('')
    // Sheets turns ISO date strings typed into date cells into Date objects; mimic for yyyy-mm-dd.
    row[c - 1] = typeof v === 'string' && /^\d{4}-\d{2}-\d{2}$/.test(v) ? this.makeDate(v + 'T00:00:00+05:30') : v
  }
  getLastRow() { for (let i = this.data.length; i > 0; i--) if (this.data[i - 1].some((v) => v !== '' && v !== undefined)) return i; return 0 }
  getLastColumn() { return Math.max(0, ...this.data.map((r) => { for (let j = r.length; j > 0; j--) if (r[j - 1] !== '') return j; return 0 })) }
  getMaxRows() { return 1000 }
  getRange(r, c, nr = 1, nc = 1) { if (r < 1 || c < 1 || nr < 1 || nc < 1) throw new Error(`bad range ${r},${c},${nr},${nc}`); return new Range(this, r, c, nr, nc) }
  getActiveRange() { return this.getRange(this.activeRow ?? 2, 1) }
  setFrozenRows(n) { this.frozen = n; return this }
  getProtections() { return this.protections }
  protect() {
    const p = { editors: ['someone@example.com'], desc: '', setDescription(d) { this.desc = d; return this }, getEditors() { return this.editors },
      removeEditors(list) { this.editors = this.editors.filter((e) => !list.includes(e)); return this }, canDomainEdit() { return false }, setDomainEdit() { return this } }
    this.protections.push(p)
    return p
  }
}

// DateCtor must be the script realm's Date (Apps Script has a single realm,
// and the script uses `instanceof Date`).
export function createGoogle({ me = 'owner@example.com', DateCtor = Date } = {}) {
  const makeDate = (s) => new DateCtor(s)
  const sheets = new Map([['Sheet1', new Sheet('Sheet1', makeDate)]])
  const files = new Map()
  const sent = []
  const triggers = []
  const docs = []
  let user = me
  let n = 0
  const newId = () => `id${String(++n).padStart(28, '0')}`

  const makeFile = (name, blob, parent, mime) => {
    const id = newId()
    const f = {
      id, name, blob, parent, mime, trashed: false,
      getId: () => id, getName: () => f.name, getUrl: () => `https://drive.google.com/file/d/${id}/view`,
      getBlob: () => f.blob, setTrashed: (t) => { f.trashed = t },
      getParents: () => iter(f.parent ? [f.parent] : []),
      moveTo: (folder) => { f.parent = folder; return f },
      getAs: (m) => blobOf(Buffer.from(`PDF of ${f.name}\n` + (f.text ?? '')), m, f.name),
    }
    files.set(id, f)
    return f
  }
  const makeFolder = (name, parent) => {
    const id = newId()
    const folder = {
      id, name, parent, isFolder: true,
      getId: () => id, getName: () => name, getUrl: () => `https://drive.google.com/drive/folders/${id}`,
      createFolder: (nm) => makeFolder(nm, folder),
      getFoldersByName: (nm) => iter([...files.values()].filter((x) => x.isFolder && x.parent === folder && x.getName() === nm)),
      getFilesByName: (nm) => iter([...files.values()].filter((x) => !x.isFolder && !x.trashed && x.parent === folder && x.getName() === nm)),
      createFile: (b) => makeFile(b.getName(), b, folder, b.getContentType()),
      getParents: () => iter(parent ? [parent] : []),
    }
    files.set(id, folder)
    return folder
  }
  const root = makeFolder('My Drive', null)
  const ssFile = makeFile('CONSTRUCT DPR data', null, root, 'sheet')

  const blobOf = (bytes, type, name) => {
    const b = { bytes: Buffer.from(bytes), type, name,
      getBytes: () => [...b.bytes].map((x) => (x > 127 ? x - 256 : x)), getContentType: () => b.type,
      getName: () => b.name, setName: (nm) => { b.name = nm; return b }, getDataAsString: () => b.bytes.toString('utf8') }
    return b
  }

  const ss = {
    getId: () => ssFile.getId(),
    getSheetByName: (nm) => sheets.get(nm) ?? null,
    insertSheet: (nm) => { const s = new Sheet(nm, makeDate); sheets.set(nm, s); return s },
    getSheets: () => [...sheets.values()],
    deleteSheet: (s) => sheets.delete(s.getName()),
  }
  const active = { sheet: null }

  const iter = (arr) => { let i = 0; return { hasNext: () => i < arr.length, next: () => arr[i++] } }
  const pad = (x) => String(x).padStart(2, '0')

  const g = {
    SpreadsheetApp: {
      getActive: () => ss,
      getActiveSheet: () => active.sheet,
      newDataValidation: () => { const r = { list: null, requireValueInList(l) { r.list = l; return r }, setAllowInvalid() { return r }, build() { return r } }; return r },
      ProtectionType: { SHEET: 'SHEET' },
      getUi: () => { throw new Error('No UI in tests') },
    },
    DriveApp: {
      getFileById: (id) => { const f = files.get(id); if (!f) throw new Error('No file ' + id); return f },
      getFolderById: (id) => { const f = files.get(id); if (!f?.isFolder) throw new Error('No folder ' + id); return f },
      getRootFolder: () => root,
      createShortcut: (targetId) => makeFile('shortcut→' + targetId, null, root, 'shortcut'),
    },
    DocumentApp: {
      ParagraphHeading: { TITLE: 'T', SUBTITLE: 'S', HEADING2: 'H2' },
      create: (title) => {
        const text = []
        const textObj = () => { const x = { setFontSize: () => x, setItalic: () => x, setBold: () => x }; return x }
        const para = (t) => { text.push(t); const p = { setHeading: () => p, editAsText: textObj }; return p }
        const body = {
          setMarginTop: () => body, setMarginBottom: () => body, setMarginLeft: () => body, setMarginRight: () => body,
          appendParagraph: para,
          appendTable: (cells) => { cells.forEach((r) => text.push(r.join(' | '))); return { getRow: () => ({ editAsText: textObj }), editAsText: textObj } },
          appendImage: () => { text.push('[image]'); const img = { w: 1000, h: 750, getWidth: () => img.w, getHeight: () => img.h, setWidth: (v) => { img.w = v; return img }, setHeight: (v) => { img.h = v; return img } }; return img },
        }
        const f = makeFile(title, null, root, 'doc')
        docs.push({ title, text })
        return { getBody: () => body, saveAndClose: () => { f.text = text.join('\n') }, getId: () => f.getId() }
      },
    },
    Utilities: {
      getUuid: () => randomUUID(),
      DigestAlgorithm: { SHA_256: 'sha256' },
      Charset: { UTF_8: 'utf8' },
      computeDigest: (_alg, data) => [...createHash('sha256').update(Array.isArray(data) ? Buffer.from(data.map((x) => x & 0xff)) : Buffer.from(String(data), 'utf8')).digest()].map((x) => (x > 127 ? x - 256 : x)),
      formatDate: (d, tz, fmt) => {
        const p = Object.fromEntries(new Intl.DateTimeFormat('en-CA', { timeZone: tz, year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', hour12: false }).formatToParts(d).map((x) => [x.type, x.value]))
        return fmt.replace('yyyy', p.year).replace('MM', p.month).replace('dd', p.day).replace('HH', pad(p.hour === '24' ? 0 : p.hour)).replace('mm', p.minute)
      },
      newBlob: (data, type, name) => blobOf(Buffer.from(String(data), 'utf8'), type, name),
    },
    Session: {
      getScriptTimeZone: () => 'Asia/Kolkata',
      getActiveUser: () => ({ getEmail: () => user }),
      getEffectiveUser: () => ({ getEmail: () => me }),
    },
    LockService: { getScriptLock: () => ({ tryLock: () => true, releaseLock: () => {} }) },
    MailApp: { sendEmail: (to, subject, body) => sent.push({ to, subject, body }) },
    ScriptApp: {
      getProjectTriggers: () => [...triggers], // the real API returns a copy
      deleteTrigger: (t) => triggers.splice(triggers.indexOf(t), 1),
      newTrigger: (fn) => { const t = { fn, getHandlerFunction: () => fn }; const b = { timeBased: () => b, everyMinutes: (m) => { t.every = m; return b }, atHour: () => b, everyDays: () => b, create: () => { triggers.push(t); return t } }; return b },
    },
    Logger: { log: () => {} },
  }
  return { globals: g, sheets, files, sent, triggers, docs, root, setUser: (u) => { user = u }, setActiveSheet: (nm, row) => { active.sheet = sheets.get(nm); active.sheet.activeRow = row },
    addAppFile: (path, bytes) => {
      // AppSheet stores images in folders next to the spreadsheet, e.g. "Evidence_Images/x.jpg".
      const parts = path.split('/')
      let folder = root
      for (const p of parts.slice(0, -1)) {
        const it = folder.getFoldersByName(p)
        folder = it.hasNext() ? it.next() : folder.createFolder(p)
      }
      return makeFile(parts.at(-1), blobOf(Buffer.from(bytes), 'image/jpeg', parts.at(-1)), folder, 'image/jpeg')
    } }
}
