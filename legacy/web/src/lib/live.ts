import { useEffect, useRef, useState } from 'react'
import type { RealtimeChannel } from '@supabase/supabase-js'
import { supabase } from './supabase'
import { deviceLabel } from './platform'

/**
 * Live data across phones, PCs and browsers.
 *
 * Supabase Realtime pushes committed changes, but it does not replay what a
 * device missed while asleep, backgrounded or offline. Every subscription
 * made here therefore also "catches up": it calls onChange again when the
 * channel reconnects and when the app returns to the foreground, so screens
 * always converge on the server state.
 */
export function subscribeWithCatchUp(name: string, configure: (ch: RealtimeChannel) => RealtimeChannel, onChange: () => void): () => void {
  let subscribedOnce = false
  let hiddenAt = 0
  const ch = configure(supabase.channel(`${name}:${Math.random().toString(36).slice(2)}`))
  ch.subscribe((status) => {
    if (status === 'SUBSCRIBED') {
      if (subscribedOnce) onChange() // reconnected: fetch what we missed
      subscribedOnce = true
    }
  })
  const onVisibility = () => {
    if (document.visibilityState === 'hidden') hiddenAt = Date.now()
    else if (hiddenAt && Date.now() - hiddenAt > 5000) onChange()
  }
  const onOnline = () => onChange()
  document.addEventListener('visibilitychange', onVisibility)
  window.addEventListener('online', onOnline)
  return () => {
    document.removeEventListener('visibilitychange', onVisibility)
    window.removeEventListener('online', onOnline)
    void supabase.removeChannel(ch)
  }
}

/** Is the live (websocket) connection up? Drives the "Live" badge. */
export function useLiveConnection(enabled: boolean): boolean {
  const [live, setLive] = useState(false)
  useEffect(() => {
    if (!enabled) return
    const ch = supabase.channel('cdpr-live-status')
    ch.subscribe((status) => setLive(status === 'SUBSCRIBED'))
    const off = () => setLive(false)
    window.addEventListener('offline', off)
    return () => { window.removeEventListener('offline', off); void supabase.removeChannel(ch) }
  }, [enabled])
  return live
}

export function deviceId(): string {
  let id = localStorage.getItem('cdpr.device')
  if (!id) {
    id = crypto.randomUUID()
    localStorage.setItem('cdpr.device', id)
  }
  return id
}

export interface PresentUser { userId: string; name: string; device: string; since: string }

/** Who else (other people, or you on another device) has the same record open right now. */
export function usePresence(topic: string | null, me: { id: string; name: string } | null): PresentUser[] {
  const [others, setOthers] = useState<PresentUser[]>([])
  useEffect(() => {
    if (!topic || !me) return
    const myKey = `${me.id}:${deviceId()}`
    const ch = supabase.channel(`presence:${topic}`, { config: { presence: { key: myKey } } })
    ch.on('presence', { event: 'sync' }, () => {
      const state = ch.presenceState<PresentUser>()
      setOthers(Object.entries(state).filter(([k]) => k !== myKey).map(([, metas]) => metas[0]).filter(Boolean))
    })
    ch.subscribe((status) => {
      if (status === 'SUBSCRIBED') void ch.track({ userId: me.id, name: me.name, device: deviceLabel(), since: new Date().toISOString() })
    })
    return () => { void supabase.removeChannel(ch) }
  }, [topic, me?.id, me?.name]) // eslint-disable-line react-hooks/exhaustive-deps
  return others
}

/**
 * Watches one row (e.g. the DPR being edited). `isForeign(version)` decides
 * whether a change came from elsewhere; it runs after a short delay so this
 * device's own save can record its new version first.
 */
export function useRowWatch(table: string, id: string | null, isForeign: (row: Record<string, unknown>) => Promise<boolean>, onForeign: (row: Record<string, unknown>) => void) {
  const cb = useRef({ isForeign, onForeign })
  cb.current = { isForeign, onForeign }
  useEffect(() => {
    if (!id) return
    const ch = supabase.channel(`row:${table}:${id}:${Math.random().toString(36).slice(2)}`)
      .on('postgres_changes', { event: 'UPDATE', schema: 'public', table, filter: `id=eq.${id}` }, (payload) => {
        const row = payload.new as Record<string, unknown>
        setTimeout(() => { void cb.current.isForeign(row).then((f) => { if (f) cb.current.onForeign(row) }) }, 1500)
      })
      .subscribe()
    return () => { void supabase.removeChannel(ch) }
  }, [table, id])
}
