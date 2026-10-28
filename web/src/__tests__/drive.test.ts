import { describe, expect, it } from 'vitest'
import {
  GoogleDrive, ensureFolderPath, createStandardTree, folderPathFor, safeFileName, validateUpload, type FolderStore,
} from '../../../supabase/functions/drive/core'

/** In-memory imitation of the Drive v3 endpoints the service uses. */
function fakeDrive() {
  const files = new Map<string, { id: string; name: string; mimeType: string; parents: string[]; appProperties: Record<string, string>; size?: number; trashed?: boolean }>()
  const sessions = new Map<string, { meta: { name: string; parents: string[]; appProperties: Record<string, string>; mimeType: string }; total: number; received: number }>()
  let n = 0
  let failNextChunk = false
  const http = async (input: string | URL | Request, init: RequestInit = {}): Promise<Response> => {
    const url = new URL(String(input))
    const method = init.method ?? 'GET'
    if (url.pathname === '/drive/v3/files' && method === 'GET') {
      const q = url.searchParams.get('q')!
      const props = [...q.matchAll(/key='(\w+)' and value='([^']*)'/g)].map((m) => [m[1], m[2]])
      const wantFolder = q.includes("mimeType = 'application/vnd.google-apps.folder'")
      const found = [...files.values()].filter((f) => !f.trashed && props.every(([k, v]) => f.appProperties[k] === v) &&
        (!wantFolder || f.mimeType.endsWith('folder')))
      return Response.json({ files: found.map((f) => ({ id: f.id, name: f.name, parents: f.parents, size: f.size != null ? String(f.size) : undefined })) })
    }
    if (url.pathname === '/drive/v3/files' && method === 'POST') {
      const b = JSON.parse(String(init.body))
      const id = `f${++n}`
      files.set(id, { id, name: b.name, mimeType: b.mimeType, parents: b.parents, appProperties: b.appProperties ?? {} })
      return Response.json({ id, name: b.name, parents: b.parents })
    }
    if (url.pathname.startsWith('/drive/v3/files/') && method === 'PATCH') {
      files.get(url.pathname.split('/').pop()!)!.trashed = true
      return Response.json({})
    }
    if (url.pathname === '/upload/drive/v3/files' && method === 'POST') {
      const b = JSON.parse(String(init.body))
      const sid = `https://upload.example/session/${++n}`
      sessions.set(sid, { meta: b, total: Number(new Headers(init.headers).get('X-Upload-Content-Length')), received: 0 })
      return new Response(null, { status: 200, headers: { Location: sid } })
    }
    if (url.pathname.startsWith('/session/') && method === 'PUT') {
      const s = sessions.get(String(input))
      if (!s) return new Response('gone', { status: 404 })
      const range = new Headers(init.headers).get('Content-Range')!
      const m = /bytes (\d+)-(\d+)\/(\d+)/.exec(range)
      if (m) {
        if (failNextChunk) { failNextChunk = false; throw new TypeError('fetch failed') } // network drop
        if (Number(m[1]) !== s.received) return new Response(null, { status: 308, headers: s.received ? { Range: `bytes=0-${s.received - 1}` } : {} })
        s.received = Number(m[2]) + 1
      }
      if (s.received >= s.total) {
        const id = `f${++n}`
        files.set(id, { id, name: s.meta.name, mimeType: s.meta.mimeType, parents: s.meta.parents, appProperties: s.meta.appProperties, size: s.total })
        return Response.json({ id, parents: s.meta.parents, size: String(s.total) })
      }
      return new Response(null, { status: 308, headers: s.received ? { Range: `bytes=0-${s.received - 1}` } : {} })
    }
    return new Response('unexpected ' + method + ' ' + url, { status: 500 })
  }
  return { http: http as typeof fetch, files, failNext: () => { failNextChunk = true } }
}

function memoryStore(): FolderStore & { rows: Map<string, string> } {
  const rows = new Map<string, string>()
  return {
    rows,
    async getFolder(p, path) { return rows.get(`${p}|${path}`) ?? null },
    async registerFolder(p, path, id) { const k = `${p}|${path}`; if (!rows.has(k)) rows.set(k, id); return rows.get(k)! },
  }
}

const token = { token: async () => 'test-token' }
const project = { id: 'p1', code: 'BF2', name: 'Blast Furnace', orgId: 'o1', orgName: 'Demo Org' }

describe('Drive folder management', () => {
  it('creates the standard tree once and is idempotent on retry', async () => {
    const fake = fakeDrive(), store = memoryStore()
    const drive = new GoogleDrive(token, {}, fake.http)
    await createStandardTree(drive, store, project, '2026', '10')
    const count = fake.files.size
    await createStandardTree(drive, store, project, '2026', '10')
    expect(fake.files.size).toBe(count)
    expect(store.rows.has('p1|04_DPR/2026/10/Photographs')).toBe(true)
    expect([...fake.files.values()].find((f) => f.name === 'BF2 - Blast Furnace')).toBeTruthy()
  })

  it('recovers when Drive succeeded but the database write was lost', async () => {
    const fake = fakeDrive(), store = memoryStore()
    const drive = new GoogleDrive(token, {}, fake.http)
    await ensureFolderPath(drive, store, project, '01_Contracts')
    store.rows.delete('p1|01_Contracts') // simulate lost DB registration
    const before = fake.files.size
    const id = await ensureFolderPath(drive, store, project, '01_Contracts')
    expect(fake.files.size).toBe(before) // found by app properties, not duplicated
    expect(store.rows.get('p1|01_Contracts')).toBe(id)
  })

  it('trashes the loser of a concurrent folder creation', async () => {
    const fake = fakeDrive(), store = memoryStore()
    const drive = new GoogleDrive(token, {}, fake.http)
    await ensureFolderPath(drive, store, project, '')
    store.rows.set('p1|09_Safety', 'winner-from-other-device')
    // Simulate: our getFolder missed it (stale read) — register then returns the other id.
    const racing: FolderStore = { getFolder: async (p, path) => (path === '09_Safety' ? null : store.getFolder(p, path)), registerFolder: store.registerFolder }
    const id = await ensureFolderPath(drive, racing, project, '09_Safety')
    expect(id).toBe('winner-from-other-device')
    expect([...fake.files.values()].filter((f) => f.name === '09_Safety' && !f.trashed)).toHaveLength(0)
  })
})

describe('resumable upload', () => {
  it('uploads in chunks and resumes after a dropped connection', async () => {
    const fake = fakeDrive()
    const drive = new GoogleDrive(token, {}, fake.http)
    const total = 10
    const session = await drive.startResumable({ name: 'x.jpg', parent: 'root', mimeType: 'image/jpeg', size: total, appProperties: { cdprAttachmentId: 'a1' } })
    expect(await drive.putChunk(session, new Uint8Array(4), 0, total)).toEqual({ kind: 'partial', next: 4 })
    fake.failNext()
    await expect(drive.putChunk(session, new Uint8Array(4), 4, total)).rejects.toThrow()
    expect(await drive.queryOffset(session, total)).toEqual({ kind: 'partial', next: 4 }) // ask where to resume
    expect((await drive.putChunk(session, new Uint8Array(4), 4, total)).kind).toBe('partial')
    const done = await drive.putChunk(session, new Uint8Array(2), 8, total)
    expect(done.kind).toBe('done')
    // Reconciliation: the finished file is discoverable by attachment id (no duplicate re-upload)
    expect((await drive.findByAppProperties({ cdprAttachmentId: 'a1' }))?.size).toBe('10')
  })
  it('reports an expired session so the client restarts', async () => {
    const drive = new GoogleDrive(token, {}, fakeDrive().http)
    expect(await drive.putChunk('https://upload.example/session/999', new Uint8Array(1), 0, 1)).toEqual({ kind: 'expired' })
  })
})

describe('upload rules', () => {
  it('validates type and size per category', () => {
    expect(validateUpload('photo', 'image/jpeg', 1000)).toBeNull()
    expect(validateUpload('photo', 'application/x-msdownload', 1000)).toMatch(/not allowed/)
    expect(validateUpload('video', 'video/mp4', 600 * 1024 ** 2)).toMatch(/limit/)
    expect(validateUpload('audio', 'audio/webm;codecs=opus', 5000)).toBeNull()
  })
  it('builds folder paths and safe names', () => {
    expect(folderPathFor('photo', '2026-10-28')).toBe('04_DPR/2026/10/Photographs')
    expect(folderPathFor('report', '2026-10-28')).toBe('04_DPR/2026/10/Approved PDFs')
    expect(folderPathFor('drawing', null)).toBe('02_Drawings')
    expect(safeFileName(['BF2-DPR-0001', 'photo', 'abcd1234'], '../../etc/pass wd?.JPG')).toBe('BF2-DPR-0001_photo_abcd1234_.._.._etc_pass wd_.jpg')
  })
})
