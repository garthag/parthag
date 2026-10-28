import { createClient, type SupabaseClient } from '@supabase/supabase-js'

/**
 * The anon key is designed to be public: it only identifies the project.
 * All access control is enforced by PostgreSQL Row Level Security and the
 * permission checks inside the database functions. NEVER put the
 * service-role key in any VITE_ variable — it would ship in the bundle.
 */
const url = import.meta.env.VITE_SUPABASE_URL as string | undefined
const anonKey = import.meta.env.VITE_SUPABASE_ANON_KEY as string | undefined

export const isConfigured = Boolean(url && anonKey)

export const supabase: SupabaseClient = createClient(url || 'http://localhost:54321', anonKey || 'missing-anon-key', {
  auth: {
    persistSession: true, // Supabase stores a refresh token in localStorage; Drive tokens are memory-only (see drive.ts)
    autoRefreshToken: true,
    detectSessionInUrl: true,
  },
  realtime: { params: { eventsPerSecond: 5 } },
})

/** Turns PostgREST / database errors into messages a site engineer can act on. */
export function friendlyError(e: unknown): string {
  if (!e) return 'Unknown error'
  if (typeof e === 'string') return e
  const err = e as { message?: string; code?: string; details?: string; hint?: string }
  const msg = err.message ?? String(e)
  if (/Failed to fetch|NetworkError|Load failed/i.test(msg)) return 'No connection to the server. Your work is kept on this device and will sync when you are back online.'
  if (err.code === '42501' || /row-level security/i.test(msg)) return 'You do not have permission for this action in this project.'
  if (err.code === '23505') return 'A record with the same number or code already exists.'
  if (err.code === '23503') return 'This record is linked to other records and cannot be removed.'
  if (err.code === '23514') return `A value is outside the allowed range (${err.details ?? msg}).`
  if (/JWT expired/i.test(msg)) return 'Your session expired. Please sign in again.'
  return msg
}

export const isConflict = (e: unknown) => /CONFLICT/.test((e as { message?: string })?.message ?? '')
export const isNetworkError = (e: unknown) => /Failed to fetch|NetworkError|Load failed|fetch failed|timeout/i.test((e as { message?: string })?.message ?? String(e))
