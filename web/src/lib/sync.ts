import { drafts, media, outbox, type MediaItem, type OutboxItem } from './local'
import { isConflict, isNetworkError, supabase, friendlyError } from './supabase'
import { DriveError, driveEnabled, uploadBlob } from './driveClient'
import type { DprDraft } from './types'
import { uid } from './types'

/**
 * Synchronisation engine for the offline queue.
 *
 * - Every queued save carries an idempotency key; the server stores the first
 *   result per key, so a retry after a lost response never duplicates a DPR.
 * - The DPR id is generated on the device, and the database allows only one
 *   original DPR per project/date/shift/area.
 * - Saves send the last server version seen; a mismatch is a CONFLICT that
 *   stops the queue for that DPR until the user resolves it explicitly.
 * - Network failures retry with exponential backoff (2 s … 5 min).
 * - Database writes and Drive uploads are separate steps with separate status.
 */
export interface SyncStatus {
  online: boolean
  syncing: boolean
  lastSyncAt: number | null
  pendingSaves: number
  failedSaves: number
  conflicts: number
  pendingUploads: number
  failedUploads: number
  lastError: string | null
}

const backoff = (attempts: number) => Math.min(2000 * 2 ** attempts, 5 * 60_000)

/** Converts the editable document into the save_dpr payload (strips UI keys, blanks → null). */
export function toPayload(d: DprDraft): Record<string, unknown> {
  const clean = (o: object, i: number) => {
    const r: Record<string, unknown> = { sort_order: i + 1 }
    for (const [k, v] of Object.entries(o)) if (k !== 'key') r[k] = v === '' ? null : v
    return r
  }
  const { activities, manpower, equipment, materials, safety, inspections, ncrs, constraints, actions,
    dpr_no: _no, status: _st, revision: _rev, expected_version: _ev, seq: _seq, ...header } = d
  void _no; void _st; void _rev; void _ev; void _seq
  const h: Record<string, unknown> = {}
  for (const [k, v] of Object.entries(header)) h[k] = v === '' ? null : v
  return {
    ...h,
    activities: activities.map(clean), manpower: manpower.map(clean), equipment: equipment.map(clean),
    materials: materials.map(clean), safety: safety.map(clean), inspections: inspections.map(clean),
    ncrs: ncrs.map(clean), constraints: constraints.map(clean), actions: actions.map(clean),
  }
}

class SyncEngine {
  private listeners = new Set<() => void>()
  private current: Promise<void> | null = null
  private again = false
  private timer: ReturnType<typeof setInterval> | null = null
  status: SyncStatus = {
    online: typeof navigator === 'undefined' ? true : (navigator.onLine ?? true), syncing: false, lastSyncAt: null,
    pendingSaves: 0, failedSaves: 0, conflicts: 0, pendingUploads: 0, failedUploads: 0, lastError: null,
  }
  uploadProgress = new Map<string, number>()

  start() {
    if (this.timer) return
    window.addEventListener('online', () => { this.status.online = true; this.emit(); void this.run() })
    window.addEventListener('offline', () => { this.status.online = false; this.emit() })
    // Apps are paused in the background (Android) or minimised (Windows): sync as soon as they return.
    document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'visible') void this.run() })
    window.addEventListener('focus', () => void this.run())
    this.timer = setInterval(() => void this.run(), 30_000)
    void this.refreshCounts().then(() => this.run())
  }

  subscribe(fn: () => void) { this.listeners.add(fn); return () => { this.listeners.delete(fn) } }
  private emit() { this.status = { ...this.status }; this.listeners.forEach((f) => f()) }

  /** Saves on the device only (autosave). */
  async saveLocal(draft: DprDraft) {
    const prev = await drafts.get(draft.id)
    await drafts.put({
      id: draft.id, projectId: draft.project_id, draft, savedAt: Date.now(),
      syncState: prev?.syncState === 'conflict' ? 'conflict' : 'local', serverVersion: prev?.serverVersion ?? draft.expected_version ?? null,
      error: prev?.error,
    })
  }

  /** Saves on the device and queues a server save. */
  async queueSave(draft: DprDraft) {
    await this.saveLocal(draft)
    const rec = (await drafts.get(draft.id))!
    const payload = toPayload(draft)
    const existing = (await outbox.byDpr(draft.id)).find((i) => i.state !== 'syncing')
    const item: OutboxItem = existing
      ? { ...existing, payload, idempotencyKey: uid(), attempts: 0, nextAttemptAt: 0, state: existing.state === 'conflict' ? 'conflict' : 'waiting', lastError: undefined }
      : { id: uid(), kind: 'save_dpr', projectId: draft.project_id, dprId: draft.id, payload, idempotencyKey: uid(), attempts: 0, nextAttemptAt: 0, state: 'waiting', createdAt: Date.now() }
    await outbox.put(item)
    await drafts.put({ ...rec, syncState: item.state === 'conflict' ? 'conflict' : 'queued' })
    await this.refreshCounts()
    void this.run()
  }

  /** Conflict resolution: keep my version (overwrite) — adopts the server's current version number. */
  async resolveKeepMine(dprId: string) {
    const { data, error } = await supabase.from('dpr_headers').select('version, status').eq('id', dprId).single()
    if (error) throw error
    if (!['draft', 'rejected'].includes(data.status)) throw new Error(`The server copy is ${data.status}; it can no longer be overwritten.`)
    const rec = await drafts.get(dprId)
    if (!rec) return
    await drafts.put({ ...rec, serverVersion: data.version, syncState: 'queued', error: undefined })
    for (const i of await outbox.byDpr(dprId)) await outbox.put({ ...i, state: 'waiting', attempts: 0, nextAttemptAt: 0, lastError: undefined })
    await this.refreshCounts()
    void this.run()
  }

  /** Conflict resolution: discard my local copy and use the server's. */
  async resolveUseServer(dprId: string) {
    for (const i of await outbox.byDpr(dprId)) await outbox.delete(i.id)
    await drafts.delete(dprId)
    await this.refreshCounts()
  }

  async queueMedia(item: Omit<MediaItem, 'state' | 'progress' | 'attempts' | 'nextAttemptAt' | 'registered' | 'createdAt'>) {
    await media.put({ ...item, state: 'queued', progress: 0, attempts: 0, nextAttemptAt: 0, registered: false, createdAt: Date.now() })
    await this.refreshCounts()
    void this.run()
  }

  async retryMedia(id: string) {
    const m = await media.get(id)
    if (m) await media.put({ ...m, state: 'queued', attempts: 0, nextAttemptAt: 0, error: undefined })
    void this.run()
  }

  async refreshCounts() {
    const items = await outbox.all()
    const files = await media.all()
    this.status.pendingSaves = items.filter((i) => i.state === 'waiting' || i.state === 'syncing').length
    this.status.failedSaves = items.filter((i) => i.state === 'failed').length
    this.status.conflicts = items.filter((i) => i.state === 'conflict').length
    this.status.pendingUploads = files.filter((m) => m.state === 'queued' || m.state === 'uploading').length
    this.status.failedUploads = files.filter((m) => m.state === 'failed').length
    this.emit()
  }

  /** Runs a sync pass. Concurrent callers share the in-flight pass; work queued meanwhile triggers one more pass. */
  run(): Promise<void> {
    if (this.current) { this.again = true; return this.current }
    this.current = (async () => {
      do {
        this.again = false
        await this.pass()
      } while (this.again && this.status.online)
    })().finally(() => { this.current = null })
    return this.current
  }

  private async pass() {
    if (!this.status.online) return
    const { data } = await supabase.auth.getSession()
    if (!data.session) return
    this.status.syncing = true
    this.emit()
    try {
      await this.processOutbox()
      if (driveEnabled) await this.processMedia()
      this.status.lastSyncAt = Date.now()
      this.status.lastError = null
    } catch (e) {
      this.status.lastError = friendlyError(e)
    } finally {
      this.status.syncing = false
      await this.refreshCounts()
    }
  }

  private async processOutbox() {
    const items = (await outbox.all()).sort((a, b) => a.createdAt - b.createdAt)
    for (const item of items) {
      if (item.state === 'conflict' || item.state === 'failed' || item.nextAttemptAt > Date.now()) continue
      const rec = await drafts.get(item.dprId)
      await outbox.put({ ...item, state: 'syncing' })
      if (rec) await drafts.put({ ...rec, syncState: 'syncing' })
      this.emit()
      const { data, error } = await supabase.rpc('save_dpr', {
        p: { ...item.payload, idempotency_key: item.idempotencyKey, expected_version: rec?.serverVersion ?? null },
      })
      const latest = await drafts.get(item.dprId)
      if (!error) {
        await outbox.delete(item.id)
        if (latest) {
          const stillQueued = (await outbox.byDpr(item.dprId)).length > 0
          await drafts.put({
            ...latest, serverVersion: data.version, syncState: stillQueued ? 'queued' : 'synced', error: undefined,
            draft: { ...latest.draft, dpr_no: data.dpr_no, status: data.status, revision: data.revision, expected_version: data.version },
          })
        }
        continue
      }
      if (isNetworkError(error)) {
        await outbox.put({ ...item, state: 'waiting', attempts: item.attempts + 1, nextAttemptAt: Date.now() + backoff(item.attempts), lastError: friendlyError(error) })
        if (latest) await drafts.put({ ...latest, syncState: 'queued' })
        throw error // stop this round; we are effectively offline
      }
      const conflict = isConflict(error)
      await outbox.put({ ...item, state: conflict ? 'conflict' : 'failed', lastError: friendlyError(error) })
      if (latest) await drafts.put({ ...latest, syncState: conflict ? 'conflict' : 'failed', error: friendlyError(error) })
    }
  }

  private async processMedia() {
    const { data: auth } = await supabase.auth.getUser()
    const userId = auth.user?.id
    for (const m of await media.all()) {
      if (!(m.state === 'queued' || m.state === 'uploading') || !m.blob || m.nextAttemptAt > Date.now()) continue
      // The DPR row must exist on the server before files can reference it.
      if (m.dprId && (await outbox.byDpr(m.dprId)).length > 0) continue
      if (m.dprId) {
        const d = await drafts.get(m.dprId)
        if (d && d.syncState !== 'synced' && d.serverVersion == null) continue
      }
      try {
        await media.put({ ...m, state: 'uploading', error: undefined })
        this.emit()
        if (!m.registered) {
          const { error } = await supabase.from('attachments').upsert({
            id: m.id, project_id: m.projectId, dpr_id: m.dprId, activity_id: m.activityId, original_filename: m.filename,
            mime_type: m.mime, size_bytes: m.size, sha256: m.sha256, category: m.category, caption: m.caption ?? null,
            transcript: m.transcript ?? null, captured_at: m.capturedAt, latitude: m.latitude ?? null, longitude: m.longitude ?? null,
            location_accuracy_m: m.accuracy ?? null, location_source: m.locationSource ?? null, upload_status: 'pending', created_by: userId,
          }, { onConflict: 'id', ignoreDuplicates: true })
          if (error) throw error
          await media.put({ ...(await media.get(m.id))!, registered: true })
        }
        const fileId = await uploadBlob(m.id, m.blob, (p) => { this.uploadProgress.set(m.id, p); this.emit() })
        if (m.reportRevisionId) {
          const { error } = await supabase.rpc('set_report_pdf', { p_report_revision: m.reportRevisionId, p_drive_file_id: fileId, p_sha256: m.sha256 })
          if (error && !/already registered/.test(error.message)) throw error
        }
        const cur = (await media.get(m.id))!
        await media.put({ ...cur, state: 'uploaded', progress: 1, driveFileId: fileId, blob: undefined })
        this.uploadProgress.delete(m.id)
      } catch (e) {
        const cur = (await media.get(m.id)) ?? m
        const permanent = e instanceof DriveError ? e.permanent : !isNetworkError(e) && !(e instanceof TypeError)
        await media.put({
          ...cur, state: permanent ? 'failed' : 'queued', attempts: cur.attempts + 1,
          nextAttemptAt: Date.now() + backoff(cur.attempts), error: friendlyError(e),
        })
        await supabase.from('attachments').update({ upload_error: friendlyError(e), upload_status: permanent ? 'failed' : 'pending' }).eq('id', m.id)
        if (!permanent && isNetworkError(e)) return
      }
    }
  }
}

export const sync = new SyncEngine()
