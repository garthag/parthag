import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react'
import { supabase } from './supabase'
import { cacheGet, cachePut } from './local'
import { useAuth } from './auth'
import type { Action, Activity, Module, Project } from './types'

/** Read-only project masters used for lookups; cached on the device for offline use. */
export interface Masters {
  activities: Activity[]
  areas: Array<{ id: string; code: string; name: string; kind: string; parent_id: string | null }>
  wbs: Array<{ id: string; code: string; name: string; parent_id: string | null }>
  contractors: Array<{ id: string; name: string; kind: string }>
  equipment: Array<{ id: string; code: string; type: string; rated_capacity: string | null; certification_expiry: string | null; owner_contractor_id: string | null }>
  materials: Array<{ id: string; code: string; description: string; unit: string; grade: string | null }>
  boq: Array<{ id: string; item_no: string; description: string; unit: string }>
  members: Array<{ id: string; user_id: string; role: string; active: boolean; profiles: { full_name: string; email: string | null } | null }>
  calendar: { work_days: number[]; holidays: string[]; hours_per_day: number; shifts: Array<{ code: string; name: string; start: string; end: string }> } | null
}

const EMPTY: Masters = { activities: [], areas: [], wbs: [], contractors: [], equipment: [], materials: [], boq: [], members: [], calendar: null }

interface ProjectCtx {
  projects: Project[]
  project: Project | null
  setProjectId: (id: string | null) => void
  reloadProjects: () => Promise<void>
  masters: Masters
  reloadMasters: () => Promise<void>
  mastersFrom: 'server' | 'device' | null
  mastersAt: number | null
  can: (module: Module, action: Action) => boolean
  loading: boolean
}

const Ctx = createContext<ProjectCtx>(null!)
export const useProject = () => useContext(Ctx)

export function ProjectProvider({ children }: { children: ReactNode }) {
  const { user } = useAuth()
  const [projects, setProjects] = useState<Project[]>([])
  const [projectId, setProjectIdState] = useState<string | null>(() => localStorage.getItem('cdpr.project'))
  const [masters, setMasters] = useState<Masters>(EMPTY)
  const [mastersFrom, setMastersFrom] = useState<'server' | 'device' | null>(null)
  const [mastersAt, setMastersAt] = useState<number | null>(null)
  const [perms, setPerms] = useState<Set<string>>(new Set())
  const [loading, setLoading] = useState(true)

  const setProjectId = (id: string | null) => {
    setProjectIdState(id)
    if (id) localStorage.setItem('cdpr.project', id)
    else localStorage.removeItem('cdpr.project')
  }

  const reloadProjects = useCallback(async () => {
    if (!user) return
    const { data, error } = await supabase.from('projects').select('*').order('name')
    if (error) {
      const cached = await cacheGet<Project[]>(`projects:${user.id}`)
      if (cached) setProjects(cached.value)
    } else {
      setProjects(data as Project[])
      await cachePut(`projects:${user.id}`, data)
    }
    setLoading(false)
  }, [user])

  const reloadMasters = useCallback(async () => {
    if (!projectId) return
    const key = `masters:${projectId}`
    try {
      const q = (t: string, cols = '*', order = 'code') => supabase.from(t).select(cols).eq('project_id', projectId).order(order)
      const [acts, areas, wbs, eq, mat, boq, members, cal, permRows, proj] = await Promise.all([
        q('activities'), q('areas'), q('wbs_nodes', '*', 'sort_order'), q('equipment'), q('materials'), q('boq_items', 'id,item_no,description,unit', 'item_no'),
        supabase.from('project_members').select('id,user_id,role,active,profiles(full_name,email)').eq('project_id', projectId),
        supabase.from('project_calendars').select('*').eq('project_id', projectId).eq('is_default', true).maybeSingle(),
        supabase.rpc('my_permissions', { p_project: projectId }),
        supabase.from('projects').select('organization_id').eq('id', projectId).single(),
      ])
      const firstErr = [acts, areas, wbs, eq, mat, boq, permRows].find((r) => r.error)?.error
      if (firstErr) throw firstErr
      const contractors = proj.data
        ? (await supabase.from('contractors').select('id,name,kind').eq('organization_id', proj.data.organization_id).order('name')).data ?? []
        : []
      const m: Masters = {
        activities: acts.data as unknown as Activity[], areas: areas.data as never, wbs: wbs.data as never, equipment: eq.data as never,
        materials: mat.data as never, boq: boq.data as never, members: (members.data ?? []) as never, contractors: contractors as never,
        calendar: cal.data as never,
      }
      const p = new Set((permRows.data as Array<{ module: string; action: string }>).map((r) => `${r.module}.${r.action}`))
      setMasters(m)
      setPerms(p)
      setMastersFrom('server')
      setMastersAt(Date.now())
      await cachePut(key, { m, p: [...p] })
    } catch {
      const cached = await cacheGet<{ m: Masters; p: string[] }>(key)
      if (cached) {
        setMasters(cached.value.m)
        setPerms(new Set(cached.value.p))
        setMastersFrom('device')
        setMastersAt(cached.at)
      }
    }
  }, [projectId])

  useEffect(() => { void reloadProjects() }, [reloadProjects])
  useEffect(() => { setMasters(EMPTY); setPerms(new Set()); void reloadMasters() }, [reloadMasters])

  // Live updates: committed changes to activities / BOQ refresh the masters for everyone on the project.
  useEffect(() => {
    if (!projectId) return
    const ch = supabase.channel(`masters:${projectId}`)
      .on('postgres_changes', { event: '*', schema: 'public', table: 'activities', filter: `project_id=eq.${projectId}` }, () => void reloadMasters())
      .on('postgres_changes', { event: '*', schema: 'public', table: 'boq_items', filter: `project_id=eq.${projectId}` }, () => void reloadMasters())
      .subscribe()
    return () => { void supabase.removeChannel(ch) }
  }, [projectId, reloadMasters])

  const project = useMemo(() => projects.find((p) => p.id === projectId) ?? null, [projects, projectId])
  const can = useCallback((m: Module, a: Action) => perms.has(`${m}.${a}`), [perms])

  return (
    <Ctx.Provider value={{ projects, project, setProjectId, reloadProjects, masters, reloadMasters, mastersFrom, mastersAt, can, loading }}>
      {children}
    </Ctx.Provider>
  )
}
