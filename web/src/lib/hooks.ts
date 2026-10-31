import { useCallback, useEffect, useRef, useState, useSyncExternalStore } from 'react'
import { friendlyError } from './supabase'
import { sync } from './sync'
import { subscribeWithCatchUp } from './live'

export function useSyncStatus() {
  return useSyncExternalStore((cb) => sync.subscribe(cb), () => sync.status)
}

/** Simple data loader with error/loading state and a reload function. */
export function useLoad<T>(fn: () => Promise<T>, deps: unknown[]) {
  const [data, setData] = useState<T | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)
  // eslint-disable-next-line react-hooks/exhaustive-deps
  const load = useCallback(fn, deps)
  const reload = useCallback(async () => {
    setLoading(true)
    try {
      setData(await load())
      setError(null)
    } catch (e) {
      setError(friendlyError(e))
    } finally {
      setLoading(false)
    }
  }, [load])
  useEffect(() => { void reload() }, [reload])
  return { data, error, loading, reload, setData }
}

/**
 * Re-runs `onChange` whenever a committed row changes in `table` for the
 * project (Supabase Realtime, RLS-filtered), and again after reconnects or
 * when the app returns to the foreground, so missed events are caught up.
 */
export function useRealtime(table: string, projectId: string | null | undefined, onChange: () => void) {
  const cb = useRef(onChange)
  cb.current = onChange
  useEffect(() => {
    if (!projectId) return
    return subscribeWithCatchUp(`rt:${table}:${projectId}`,
      (ch) => ch.on('postgres_changes', { event: '*', schema: 'public', table, filter: `project_id=eq.${projectId}` }, () => cb.current()),
      () => cb.current())
  }, [table, projectId])
}

export async function must<T>(p: PromiseLike<{ data: T; error: unknown }>): Promise<T> {
  const { data, error } = await p
  if (error) throw error
  return data
}

export function useTheme() {
  const [theme, setTheme] = useState<'light' | 'dark' | 'system'>(() => (localStorage.getItem('cdpr.theme') as never) ?? 'system')
  useEffect(() => {
    const apply = () => {
      const dark = theme === 'dark' || (theme === 'system' && matchMedia('(prefers-color-scheme: dark)').matches)
      document.documentElement.classList.toggle('dark', dark)
    }
    apply()
    localStorage.setItem('cdpr.theme', theme)
    const mq = matchMedia('(prefers-color-scheme: dark)')
    mq.addEventListener('change', apply)
    return () => mq.removeEventListener('change', apply)
  }, [theme])
  return [theme, setTheme] as const
}

export function useFavorites(projectId: string | undefined) {
  const key = `cdpr.fav.${projectId}`
  const [fav, setFav] = useState<string[]>(() => JSON.parse(localStorage.getItem(key) ?? '[]'))
  useEffect(() => setFav(JSON.parse(localStorage.getItem(key) ?? '[]')), [key])
  const toggle = (id: string) => {
    const next = fav.includes(id) ? fav.filter((x) => x !== id) : [...fav, id]
    setFav(next)
    localStorage.setItem(key, JSON.stringify(next))
  }
  return { fav, toggle }
}
