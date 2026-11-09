import 'fake-indexeddb/auto'
import { beforeEach, describe, expect, it, vi } from 'vitest'

/**
 * Test E/J (client side): offline entry, reconnection, retry after a lost
 * response, and conflict handling — against a simulated server that
 * implements the same idempotency contract as save_dpr().
 */
const server = {
  online: false,
  loseNextResponse: false,
  rows: new Map<string, { version: number; payload: Record<string, unknown> }>(),
  keys: new Map<string, unknown>(),
  calls: 0,
}

vi.mock('../lib/supabase', () => ({
  supabase: {
    auth: {
      getSession: async () => ({ data: { session: { access_token: 't' } } }),
      getUser: async () => ({ data: { user: { id: 'u1' } } }),
    },
    rpc: async (_fn: string, { p }: { p: Record<string, unknown> }) => {
      server.calls++
      if (!server.online) return { data: null, error: { message: 'TypeError: Failed to fetch' } }
      const key = p.idempotency_key as string
      if (server.keys.has(key)) return { data: { ...(server.keys.get(key) as object), replayed: true }, error: null }
      const id = p.id as string
      const row = server.rows.get(id)
      if (row && p.expected_version != null && p.expected_version !== row.version) {
        return { data: null, error: { message: `CONFLICT: DPR changed (your version ${p.expected_version}, server version ${row.version})` } }
      }
      const version = (row?.version ?? 0) + 1
      server.rows.set(id, { version, payload: p })
      const result = { id, version, dpr_no: 'PA-DPR-0001', status: 'draft', revision: 0 }
      server.keys.set(key, result)
      if (server.loseNextResponse) { server.loseNextResponse = false; return { data: null, error: { message: 'Failed to fetch' } } }
      return { data: result, error: null }
    },
    from: () => ({ select: () => ({ eq: () => ({ single: async () => ({ data: { version: [...server.rows.values()][0]?.version, status: 'draft' }, error: null }) }) }) }),
  },
  friendlyError: (e: { message?: string }) => e?.message ?? String(e),
  isConflict: (e: { message?: string }) => /CONFLICT/.test(e?.message ?? ''),
  isNetworkError: (e: { message?: string }) => /Failed to fetch/.test(e?.message ?? ''),
}))
vi.mock('../lib/driveClient', () => ({ driveEnabled: false, uploadBlob: vi.fn(), DriveError: class extends Error {} }))

const { sync } = await import('../lib/sync')
const { drafts, outbox } = await import('../lib/local')

const draft = (over: Record<string, unknown> = {}) => ({
  id: 'dpr-1', project_id: 'p1', report_date: '2026-10-28', shift: 'day', activities: [
    { key: 'k1', activity_id: 'a1', qty_today: 12 }], manpower: [], equipment: [], materials: [], safety: [],
  inspections: [], ncrs: [], constraints: [], actions: [], ...over,
})

describe('offline queue', () => {
  beforeEach(() => { server.online = false; server.calls = 0 })

  it('keeps work on the device while offline and syncs once on reconnection', async () => {
    await sync.queueSave(draft())
    await sync.run()
    expect((await drafts.get('dpr-1'))?.syncState).toBe('queued')
    expect(await outbox.all()).toHaveLength(1)
    expect(server.rows.size).toBe(0)

    server.online = true
    ;(await outbox.all()).forEach((i) => void outbox.put({ ...i, nextAttemptAt: 0 }))
    await new Promise((r) => setTimeout(r, 10))
    await sync.run()
    const rec = await drafts.get('dpr-1')
    expect(rec?.syncState).toBe('synced')
    expect(rec?.serverVersion).toBe(1)
    expect(await outbox.all()).toHaveLength(0)
    expect(server.rows.size).toBe(1)
  })

  it('a lost response is retried with the same key and does not duplicate', async () => {
    server.online = true
    server.loseNextResponse = true
    await sync.queueSave(draft({ weather: 'Rain' }))
    await sync.run() // server applies v2 but the reply is lost
    expect(server.rows.get('dpr-1')?.version).toBe(2)
    ;(await outbox.all()).forEach((i) => void outbox.put({ ...i, nextAttemptAt: 0 }))
    await new Promise((r) => setTimeout(r, 10))
    await sync.run() // replay → same result, no new version
    expect(server.rows.get('dpr-1')?.version).toBe(2)
    expect((await drafts.get('dpr-1'))?.serverVersion).toBe(2)
  })

  it('stops on a conflict and lets the user resolve it explicitly', async () => {
    server.online = true
    server.rows.set('dpr-1', { version: 7, payload: {} }) // someone else saved meanwhile
    await sync.queueSave(draft({ weather: 'Clear' }))
    await sync.run()
    expect((await drafts.get('dpr-1'))?.syncState).toBe('conflict')
    expect(sync.status.conflicts).toBe(1)
    await sync.resolveKeepMine('dpr-1')
    await new Promise((r) => setTimeout(r, 20))
    await sync.run()
    expect((await drafts.get('dpr-1'))?.syncState).toBe('synced')
    expect(server.rows.get('dpr-1')?.version).toBe(8)
  })
})
