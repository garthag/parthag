import { supabase } from './supabase'

/** Browser side of the `drive` edge function. Holds no Google credentials. */
const base = `${import.meta.env.VITE_SUPABASE_URL ?? ''}/functions/v1/drive`
export const driveEnabled = (import.meta.env.VITE_DRIVE_ENABLED ?? 'true') !== 'false'

export class DriveError extends Error {
  status: number
  constructor(message: string, status: number) {
    super(message)
    this.status = status
  }
  /** 4xx (except 408/409/429) will not succeed by retrying. */
  get permanent() { return this.status >= 400 && this.status < 500 && ![408, 409, 429].includes(this.status) }
}

async function authHeaders(): Promise<Record<string, string>> {
  const { data } = await supabase.auth.getSession()
  const token = data.session?.access_token
  if (!token) throw new DriveError('Not signed in', 401)
  return { Authorization: `Bearer ${token}`, apikey: import.meta.env.VITE_SUPABASE_ANON_KEY ?? '' }
}

async function call<T>(init: RequestInit & { query?: Record<string, string> }): Promise<T> {
  const url = init.query ? `${base}?${new URLSearchParams(init.query)}` : base
  const res = await fetch(url, { ...init, headers: { ...(await authHeaders()), ...(init.headers ?? {}) } })
  const body = await res.json().catch(() => ({ error: `Drive service error ${res.status}` }))
  if (!res.ok) throw new DriveError(body.error ?? `Drive service error ${res.status}`, res.status)
  return body as T
}

type StartResult = { done: true; driveFileId: string } | { offset: number; chunkSize: number }
type ChunkResult = { done: true; driveFileId: string } | { offset: number } | { restart: true }

/** Uploads a Blob that already has an `attachments` row, resuming where the server left off. */
export async function uploadBlob(attachmentId: string, blob: Blob, onProgress?: (fraction: number) => void): Promise<string> {
  for (let restarts = 0; restarts < 3; restarts++) {
    const s = await call<StartResult>({ method: 'POST', body: JSON.stringify({ action: 'start', attachmentId }), headers: { 'Content-Type': 'application/json' } })
    if ('done' in s) { onProgress?.(1); return s.driveFileId }
    let offset = s.offset
    const size = s.chunkSize
    let restart = false
    while (offset < blob.size) {
      onProgress?.(offset / blob.size)
      const part = blob.slice(offset, Math.min(offset + size, blob.size))
      const r = await call<ChunkResult>({ method: 'PUT', query: { attachmentId, offset: String(offset) }, body: part, headers: { 'Content-Type': 'application/octet-stream' } })
      if ('done' in r) { onProgress?.(1); return r.driveFileId }
      if ('restart' in r) { restart = true; break }
      offset = r.offset
    }
    if (!restart) {
      // All bytes sent but no completion seen (e.g. response lost) — ask again.
      const again = await call<StartResult>({ method: 'POST', body: JSON.stringify({ action: 'start', attachmentId }), headers: { 'Content-Type': 'application/json' } })
      if ('done' in again) return again.driveFileId
    }
  }
  throw new DriveError('Upload could not be completed after several restarts', 500)
}

const objectUrls = new Map<string, string>()
/** Authorised download through the edge function (RLS decides). Returns an object URL. */
export async function fileObjectUrl(attachmentId: string): Promise<string> {
  const hit = objectUrls.get(attachmentId)
  if (hit) return hit
  const res = await fetch(`${base}?${new URLSearchParams({ attachmentId })}`, { headers: await authHeaders() })
  if (!res.ok) throw new DriveError((await res.json().catch(() => ({}))).error ?? `Download failed (${res.status})`, res.status)
  const url = URL.createObjectURL(await res.blob())
  objectUrls.set(attachmentId, url)
  return url
}

export const createFolderTree = (projectId: string) =>
  call<{ rootFolderId: string }>({ method: 'POST', body: JSON.stringify({ action: 'tree', projectId }), headers: { 'Content-Type': 'application/json' } })

export const shareProjectFolder = (projectId: string, email: string, role: 'reader' | 'commenter' | 'writer') =>
  call<{ shared: boolean }>({ method: 'POST', body: JSON.stringify({ action: 'share', projectId, email, role }), headers: { 'Content-Type': 'application/json' } })
