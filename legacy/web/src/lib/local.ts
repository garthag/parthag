import { openDB, type DBSchema, type IDBPDatabase } from 'idb'
import type { DprDraft } from './types'

/**
 * On-device storage (IndexedDB) for offline work.
 *  drafts – DPR documents being edited, with their sync state
 *  outbox – queued server operations (each with an idempotency key)
 *  media  – photos / videos / voice notes waiting for upload (Blob + metadata)
 *  cache  – read-only copies of project masters for offline lookup
 *
 * Browser storage can be cleared by the user, the browser or the OS under
 * storage pressure. It is a buffer, not a backup. We request persistent
 * storage where supported to reduce the risk of eviction.
 */
export type SyncState = 'local' | 'queued' | 'syncing' | 'synced' | 'failed' | 'conflict'

export interface DraftRecord {
  id: string
  projectId: string
  draft: DprDraft
  savedAt: number
  syncState: SyncState
  error?: string
  serverVersion?: number | null
}

export interface OutboxItem {
  id: string
  kind: 'save_dpr'
  projectId: string
  dprId: string
  payload: Record<string, unknown>
  idempotencyKey: string
  attempts: number
  nextAttemptAt: number
  state: 'waiting' | 'syncing' | 'failed' | 'conflict'
  lastError?: string
  createdAt: number
}

export interface MediaItem {
  id: string // = attachments.id on the server
  projectId: string
  dprId: string | null
  activityId: string | null
  blob?: Blob // removed after successful upload
  filename: string
  mime: string
  size: number
  sha256: string
  category: 'photo' | 'video' | 'audio' | 'document' | 'drawing' | 'report'
  caption?: string
  transcript?: string
  capturedAt: string
  latitude?: number | null
  longitude?: number | null
  accuracy?: number | null
  locationSource?: 'device_gps' | 'manual' | null
  state: 'queued' | 'uploading' | 'uploaded' | 'failed'
  progress: number // 0..1
  error?: string
  attempts: number
  nextAttemptAt: number
  registered: boolean
  driveFileId?: string
  reportRevisionId?: string // when this is an approved-report PDF
  createdAt: number
}

interface Schema extends DBSchema {
  drafts: { key: string; value: DraftRecord; indexes: { byProject: string } }
  outbox: { key: string; value: OutboxItem; indexes: { byDpr: string } }
  media: { key: string; value: MediaItem; indexes: { byDpr: string; byState: string } }
  cache: { key: string; value: { key: string; value: unknown; at: number } }
}

let dbp: Promise<IDBPDatabase<Schema>> | null = null
export function db() {
  dbp ??= openDB<Schema>('construct-dpr', 1, {
    upgrade(d) {
      d.createObjectStore('drafts', { keyPath: 'id' }).createIndex('byProject', 'projectId')
      d.createObjectStore('outbox', { keyPath: 'id' }).createIndex('byDpr', 'dprId')
      const m = d.createObjectStore('media', { keyPath: 'id' })
      m.createIndex('byDpr', 'dprId')
      m.createIndex('byState', 'state')
      d.createObjectStore('cache', { keyPath: 'key' })
    },
  })
  return dbp
}

export async function requestPersistentStorage(): Promise<boolean> {
  try {
    return (await navigator.storage?.persist?.()) ?? false
  } catch {
    return false
  }
}

export async function cacheGet<T>(key: string): Promise<{ value: T; at: number } | undefined> {
  const r = await (await db()).get('cache', key)
  return r ? { value: r.value as T, at: r.at } : undefined
}
export async function cachePut(key: string, value: unknown) {
  await (await db()).put('cache', { key, value, at: Date.now() })
}

export const drafts = {
  get: async (id: string) => (await db()).get('drafts', id),
  put: async (r: DraftRecord) => (await db()).put('drafts', r),
  delete: async (id: string) => (await db()).delete('drafts', id),
  byProject: async (projectId: string) => (await db()).getAllFromIndex('drafts', 'byProject', projectId),
}

export const outbox = {
  all: async () => (await db()).getAll('outbox'),
  put: async (r: OutboxItem) => (await db()).put('outbox', r),
  delete: async (id: string) => (await db()).delete('outbox', id),
  byDpr: async (dprId: string) => (await db()).getAllFromIndex('outbox', 'byDpr', dprId),
}

export const media = {
  get: async (id: string) => (await db()).get('media', id),
  all: async () => (await db()).getAll('media'),
  put: async (r: MediaItem) => (await db()).put('media', r),
  delete: async (id: string) => (await db()).delete('media', id),
  byDpr: async (dprId: string) => (await db()).getAllFromIndex('media', 'byDpr', dprId),
}
