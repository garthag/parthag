import { useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { useProject } from '../lib/project'
import { useAuth } from '../lib/auth'
import { useLoad, useRealtime, must } from '../lib/hooks'
import { supabase, friendlyError } from '../lib/supabase'
import { drafts } from '../lib/local'
import { today, type DprHeader } from '../lib/types'
import { Alert, Card, DprStatusBadge, Empty, PageHeader, Stat } from '../components/ui'

export function Home() {
  const { project, projects, loading, reloadProjects, setProjectId, can } = useProject()
  const { profile } = useAuth()
  const nav = useNavigate()
  const [busy, setBusy] = useState(false)
  const [err, setErr] = useState<string | null>(null)

  const loadDemo = async () => {
    setBusy(true)
    setErr(null)
    try {
      const id = await must(supabase.rpc('load_demo_project'))
      await reloadProjects()
      setProjectId(id as string)
    } catch (e) {
      setErr(friendlyError(e))
    } finally {
      setBusy(false)
    }
  }

  const summary = useLoad(async () => {
    if (!project) return null
    const t = today()
    const [todayDprs, pending, recent, local] = await Promise.all([
      must(supabase.from('dpr_headers').select('id,dpr_no,status,revision,shift').eq('project_id', project.id).eq('report_date', t)),
      must(supabase.from('dpr_headers').select('id', { count: 'exact', head: false }).eq('project_id', project.id).in('status', ['submitted', 'under_review'])),
      must(supabase.from('dpr_headers').select('id,dpr_no,report_date,status,revision,shift').eq('project_id', project.id).order('report_date', { ascending: false }).limit(5)),
      drafts.byProject(project.id),
    ])
    const openItems = await Promise.all(['safety_observations', 'quality_ncrs', 'constraints', 'action_items'].map((t) =>
      supabase.from(t).select('id', { count: 'exact', head: true }).eq('project_id', project.id).neq('status', 'closed')))
    return { todayDprs: todayDprs as DprHeader[], pending: (pending as unknown[]).length, recent: recent as DprHeader[], local, open: openItems.map((r) => r.count ?? 0) }
  }, [project?.id])
  useRealtime('dpr_headers', project?.id, () => void summary.reload())

  if (loading) return null
  if (!projects.length) {
    return (
      <div className="flex flex-col gap-4">
        <PageHeader title={`Welcome${profile?.full_name ? `, ${profile.full_name}` : ''}`} subtitle="You are not a member of any project yet." />
        {err && <Alert>{err}</Alert>}
        <Card title="Get started">
          <div className="flex flex-col gap-3 sm:flex-row">
            <button className="btn btn-primary" disabled={busy} onClick={() => void loadDemo()}>{busy ? 'Creating demo…' : 'Load demo project'}</button>
            <Link to="/projects" className="btn btn-secondary">Create organisation & project</Link>
          </div>
          <p className="mt-3 text-sm text-slate-600 dark:text-slate-400">
            The demo creates a sample blast-furnace project (you as administrator) with activities, BOQ, baseline and a week of approved DPRs.
            If someone else set up your project, ask them to add your e-mail as a member.
          </p>
        </Card>
      </div>
    )
  }
  if (!project) return <Empty title="Choose a project to start"><Link to="/projects" className="btn btn-primary mt-2">Projects</Link></Empty>

  const s = summary.data
  return (
    <div className="flex flex-col gap-4">
      <PageHeader title={project.name} subtitle={`${project.code} · ${new Date().toLocaleDateString('en-IN', { weekday: 'long', day: 'numeric', month: 'long', year: 'numeric' })}`} />
      {summary.error && <Alert tone="amber">{summary.error}</Alert>}
      <div className="grid grid-cols-1 gap-3 sm:grid-cols-3">
        {can('dpr', 'create') && <button className="btn btn-primary min-h-16 text-lg" onClick={() => nav('/dpr/new')}>＋ Today's DPR</button>}
        <Link className="btn btn-secondary min-h-16" to="/approvals">Approvals {s?.pending ? `(${s.pending})` : ''}</Link>
        <Link className="btn btn-secondary min-h-16" to="/dashboards">Dashboards</Link>
      </div>
      <div className="grid grid-cols-2 gap-3 md:grid-cols-4">
        <Stat label="Pending approval" value={s?.pending ?? '—'} tone={s?.pending ? 'warn' : undefined} />
        <Stat label="Open safety obs." value={s?.open[0] ?? '—'} tone={s?.open[0] ? 'warn' : 'good'} />
        <Stat label="Open NCRs" value={s?.open[1] ?? '—'} tone={s?.open[1] ? 'bad' : 'good'} />
        <Stat label="Open constraints" value={s?.open[2] ?? '—'} sub={`${s?.open[3] ?? 0} action items open`} />
      </div>
      <Card title="Today">
        {s?.todayDprs.length ? (
          <ul className="flex flex-col gap-2">
            {s.todayDprs.map((d) => (
              <li key={d.id}><Link to={`/dpr/${d.id}`} className="flex items-center justify-between gap-2 rounded-lg p-2 hover:bg-slate-100 dark:hover:bg-slate-800">
                <span className="font-semibold">{d.dpr_no} · {d.shift} shift</span><DprStatusBadge status={d.status} revision={d.revision} /></Link></li>
            ))}
          </ul>
        ) : <p className="text-sm text-slate-600 dark:text-slate-400">No DPR on the server for today yet.</p>}
        {!!s?.local.filter((l) => l.syncState !== 'synced').length && (
          <Alert tone="amber">
            {s.local.filter((l) => l.syncState !== 'synced').length} report(s) on this device are not yet synchronised. <Link to="/dpr?tab=device" className="underline font-semibold">Review</Link>
          </Alert>
        )}
      </Card>
      <Card title="Recent reports" actions={<Link className="btn btn-ghost" to="/dpr">All reports</Link>}>
        <ul className="flex flex-col gap-1">
          {s?.recent.map((d) => (
            <li key={d.id}><Link to={`/dpr/${d.id}`} className="flex items-center justify-between gap-2 rounded-lg p-2 hover:bg-slate-100 dark:hover:bg-slate-800">
              <span><span className="font-semibold">{d.dpr_no}</span> <span className="text-sm text-slate-500">{d.report_date} · {d.shift}</span></span>
              <DprStatusBadge status={d.status} revision={d.revision} /></Link></li>
          ))}
        </ul>
      </Card>
    </div>
  )
}
