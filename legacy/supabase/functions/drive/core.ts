/**
 * Google Drive integration core — runtime-agnostic (Deno edge function in
 * production, Node/Vitest in tests). All I/O is injected.
 *
 * Responsibilities
 *  - Build the standard project folder tree idempotently
 *  - Start / continue / finish resumable uploads in chunks
 *  - Reconcile after interruptions (find a file by its attachment id)
 *  - Stream downloads for authorised users
 */

export const PROJECT_TREE = [
  '01_Contracts', '02_Drawings', '03_Baseline_and_Schedules', '04_DPR', '05_Manpower', '06_Equipment',
  '07_Materials', '08_QA_QC', '09_Safety', '10_Inspection_and_Test_Records', '11_Fabrication_and_Dispatch',
  '12_Billing_and_BOQ', '13_Commissioning', '14_Client_Correspondence', '15_Archive',
]
export const DPR_SUBFOLDERS = ['Daily Reports', 'Photographs', 'Videos', 'Voice Notes', 'Approved PDFs']

const CATEGORY_FOLDER: Record<string, string> = {
  photo: 'Photographs', video: 'Videos', audio: 'Voice Notes', report: 'Approved PDFs', document: 'Daily Reports', drawing: 'Daily Reports',
}

/** Logical folder for an attachment, e.g. 04_DPR/2026/10/Photographs. Non-DPR documents go to their module folder. */
export function folderPathFor(category: string, reportDate: string | null, moduleFolder?: string | null): string {
  if (moduleFolder && PROJECT_TREE.includes(moduleFolder)) return moduleFolder
  if (category === 'drawing' && !reportDate) return '02_Drawings'
  const d = reportDate ?? new Date().toISOString().slice(0, 10)
  return `04_DPR/${d.slice(0, 4)}/${d.slice(5, 7)}/${CATEGORY_FOLDER[category] ?? 'Daily Reports'}`
}

/** Drive-safe, human-readable file name. */
export function safeFileName(parts: Array<string | null | undefined>, original: string): string {
  const clean = (s: string) => s.normalize('NFKD').replace(/[^\w.\- ]+/g, '_').replace(/\s+/g, ' ').trim()
  const ext = /\.[A-Za-z0-9]{1,8}$/.exec(original)?.[0] ?? ''
  const base = clean(original.slice(0, original.length - ext.length)).slice(0, 80) || 'file'
  return [...parts.filter(Boolean).map((p) => clean(p!)), base].join('_') + ext.toLowerCase()
}

export const MAX_BYTES: Record<string, number> = {
  photo: 25 * 1024 ** 2, video: 500 * 1024 ** 2, audio: 50 * 1024 ** 2,
  document: 100 * 1024 ** 2, drawing: 200 * 1024 ** 2, report: 50 * 1024 ** 2,
}
const MIME_OK: Record<string, RegExp> = {
  photo: /^image\/(jpeg|png|webp|heic|heif)$/,
  video: /^video\/(mp4|webm|quicktime)$/,
  audio: /^audio\/(webm|ogg|mpeg|mp4|wav|x-m4a|aac)$/,
  report: /^application\/pdf$/,
  document: /^(application\/pdf|application\/vnd\.openxmlformats-officedocument\..+|application\/vnd\.ms-excel|text\/csv|image\/(jpeg|png))$/,
  drawing: /^(application\/pdf|image\/vnd\.dwg|application\/acad|application\/dxf|application\/octet-stream|image\/(jpeg|png))$/,
}
export function validateUpload(category: string, mime: string, size: number): string | null {
  const m = mime.split(';')[0].trim()
  if (!MIME_OK[category]) return `Unknown category ${category}`
  if (!MIME_OK[category].test(m)) return `File type ${m} is not allowed for ${category}`
  if (size <= 0) return 'Empty file'
  if (size > MAX_BYTES[category]) return `File is larger than the ${Math.round(MAX_BYTES[category] / 1024 ** 2)} MB limit for ${category}`
  return null
}

// Chunks must be multiples of 256 KiB (Google resumable upload rule) except the last one.
export const CHUNK_SIZE = 4 * 1024 * 1024

export interface TokenSource { token(): Promise<string> }

export interface DriveConfig {
  rootFolderId?: string | null // folder inside My Drive or a Shared Drive
  sharedDriveId?: string | null
}

export interface DriveFile { id: string; name?: string; webViewLink?: string; parents?: string[]; size?: string; md5Checksum?: string }

type Fetch = typeof fetch
const API = 'https://www.googleapis.com/drive/v3'
const UPLOAD = 'https://www.googleapis.com/upload/drive/v3'
const FOLDER_MIME = 'application/vnd.google-apps.folder'

export class GoogleDrive {
  private auth: TokenSource
  private cfg: DriveConfig
  private http: Fetch
  constructor(auth: TokenSource, cfg: DriveConfig, http: Fetch = fetch) {
    this.auth = auth
    this.cfg = cfg
    this.http = http
  }

  private async req(url: string, init: RequestInit = {}): Promise<Response> {
    const headers = new Headers(init.headers)
    headers.set('Authorization', `Bearer ${await this.auth.token()}`)
    for (let attempt = 0; ; attempt++) {
      const res = await this.http(url, { ...init, headers })
      // Retry rate-limit / transient errors with exponential backoff (body-less requests only).
      if ((res.status === 429 || res.status >= 500) && attempt < 3 && !init.body) {
        await new Promise((r) => setTimeout(r, 2 ** attempt * 500))
        continue
      }
      return res
    }
  }

  private allDrives(q: URLSearchParams) {
    q.set('supportsAllDrives', 'true')
    if (this.cfg.sharedDriveId) {
      q.set('includeItemsFromAllDrives', 'true')
      q.set('corpora', 'drive')
      q.set('driveId', this.cfg.sharedDriveId)
    }
    return q
  }

  get root(): string {
    return this.cfg.rootFolderId || this.cfg.sharedDriveId || 'root'
  }

  async findByAppProperties(props: Record<string, string>, opts: { folder?: boolean; parent?: string } = {}): Promise<DriveFile | null> {
    const clauses = Object.entries(props).map(([k, v]) => `appProperties has { key='${k}' and value='${v.replace(/'/g, "\\'")}' }`)
    clauses.push('trashed = false')
    if (opts.folder) clauses.push(`mimeType = '${FOLDER_MIME}'`)
    if (opts.parent) clauses.push(`'${opts.parent}' in parents`)
    const q = this.allDrives(new URLSearchParams({ q: clauses.join(' and '), fields: 'files(id,name,webViewLink,parents,size,md5Checksum)', pageSize: '10' }))
    const res = await this.req(`${API}/files?${q}`)
    if (!res.ok) throw new Error(`Drive search failed: ${res.status} ${await res.text()}`)
    const { files } = (await res.json()) as { files: DriveFile[] }
    return files[0] ?? null
  }

  async createFolder(name: string, parent: string, appProperties: Record<string, string>): Promise<DriveFile> {
    const res = await this.req(`${API}/files?supportsAllDrives=true&fields=id,name,webViewLink,parents`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ name, mimeType: FOLDER_MIME, parents: [parent], appProperties }),
    })
    if (!res.ok) throw new Error(`Drive folder creation failed: ${res.status} ${await res.text()}`)
    return (await res.json()) as DriveFile
  }

  async trash(fileId: string): Promise<void> {
    await this.req(`${API}/files/${fileId}?supportsAllDrives=true`, {
      method: 'PATCH', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ trashed: true }),
    })
  }

  async startResumable(meta: { name: string; parent: string; mimeType: string; size: number; description?: string; appProperties: Record<string, string> }): Promise<string> {
    const res = await this.req(`${UPLOAD}/files?uploadType=resumable&supportsAllDrives=true&fields=id,name,webViewLink,parents,size,md5Checksum`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json; charset=UTF-8',
        'X-Upload-Content-Type': meta.mimeType,
        'X-Upload-Content-Length': String(meta.size),
      },
      body: JSON.stringify({ name: meta.name, parents: [meta.parent], mimeType: meta.mimeType, description: meta.description, appProperties: meta.appProperties }),
    })
    const loc = res.headers.get('Location')
    if (!res.ok || !loc) throw new Error(`Could not start Drive upload: ${res.status} ${await res.text()}`)
    return loc
  }

  /** Sends one chunk. Returns the next byte offset, or the finished file. 'expired' means start over. */
  async putChunk(session: string, chunk: Uint8Array, offset: number, total: number):
    Promise<{ kind: 'partial'; next: number } | { kind: 'done'; file: DriveFile } | { kind: 'expired' }> {
    const end = offset + chunk.byteLength - 1
    const res = await this.http(session, {
      method: 'PUT',
      headers: { 'Content-Range': `bytes ${offset}-${end}/${total}`, 'Content-Length': String(chunk.byteLength) },
      body: chunk as unknown as BodyInit,
    })
    return this.interpret(res)
  }

  /** Ask Google how many bytes it already has (after a dropped connection). */
  async queryOffset(session: string, total: number) {
    const res = await this.http(session, { method: 'PUT', headers: { 'Content-Range': `bytes */${total}`, 'Content-Length': '0' } })
    return this.interpret(res)
  }

  private async interpret(res: Response) {
    if (res.status === 308) {
      const range = res.headers.get('Range')
      const next = range ? Number(range.split('-')[1]) + 1 : 0
      return { kind: 'partial' as const, next }
    }
    if (res.status === 200 || res.status === 201) return { kind: 'done' as const, file: (await res.json()) as DriveFile }
    if (res.status === 404 || res.status === 410) return { kind: 'expired' as const }
    throw new Error(`Drive upload error ${res.status}: ${await res.text()}`)
  }

  async download(fileId: string): Promise<Response> {
    return this.req(`${API}/files/${encodeURIComponent(fileId)}?alt=media&supportsAllDrives=true`)
  }

  async share(fileId: string, email: string, role: 'reader' | 'commenter' | 'writer'): Promise<void> {
    const res = await this.req(`${API}/files/${fileId}/permissions?supportsAllDrives=true&sendNotificationEmail=false`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ type: 'user', role, emailAddress: email }),
    })
    if (!res.ok) throw new Error(`Sharing failed: ${res.status} ${await res.text()}`)
  }
}

/** Database operations the service needs (implemented with the service-role client). */
export interface FolderStore {
  getFolder(projectId: string, path: string): Promise<string | null>
  /** Inserts if absent and returns the canonical folder id (first writer wins). */
  registerFolder(projectId: string, path: string, driveId: string, parent: string | null): Promise<string>
}

/**
 * Ensures every folder along `path` exists exactly once. Safe under retries
 * and concurrent requests: Drive is searched by app properties before
 * creating, and the database unique key decides the winner — a losing
 * duplicate is moved to the trash.
 */
export async function ensureFolderPath(
  drive: GoogleDrive, store: FolderStore,
  project: { id: string; code: string; name: string; orgId: string; orgName: string },
  path: string,
): Promise<string> {
  let parent = await store.getFolder(project.id, '')
  if (!parent) {
    const orgProps = { cdprOrgId: project.orgId }
    const org = (await drive.findByAppProperties(orgProps, { folder: true }))
      ?? (await drive.createFolder(project.orgName, drive.root, orgProps))
    parent = await ensureOne(drive, store, project.id, '', `${project.code} - ${project.name}`, org.id)
  }
  if (!path) return parent
  let current = ''
  for (const seg of path.split('/')) {
    current = current ? `${current}/${seg}` : seg
    const known = await store.getFolder(project.id, current)
    parent = known ?? (await ensureOne(drive, store, project.id, current, seg, parent))
  }
  return parent
}

async function ensureOne(drive: GoogleDrive, store: FolderStore, projectId: string, path: string, name: string, parent: string) {
  const props = { cdprProjectId: projectId, cdprPath: path || '/' }
  const existing = await drive.findByAppProperties(props, { folder: true })
  const mine = existing ?? (await drive.createFolder(name, parent, props))
  const winner = await store.registerFolder(projectId, path, mine.id, parent)
  if (winner !== mine.id && !existing) await drive.trash(mine.id)
  return winner
}

export async function createStandardTree(drive: GoogleDrive, store: FolderStore,
  project: { id: string; code: string; name: string; orgId: string; orgName: string }, year: string, month: string) {
  for (const f of PROJECT_TREE) await ensureFolderPath(drive, store, project, f)
  for (const s of DPR_SUBFOLDERS) await ensureFolderPath(drive, store, project, `04_DPR/${year}/${month}/${s}`)
}
