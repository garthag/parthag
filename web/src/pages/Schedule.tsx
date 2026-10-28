import { useMemo, useState } from 'react'
import { useProject } from '../lib/project'
import { useLoad, useRealtime, must } from '../lib/hooks'
import { supabase, friendlyError } from '../lib/supabase'
import { schedule, ScheduleError, type CpmOutput, type RelType } from '../lib/schedule/cpm'
import { addDays, daysBetween, DEFAULT_CALENDAR } from '../lib/schedule/calendar'
import { download, toCsv } from '../lib/csv'
import { today } from '../lib/types'
import { Alert, Badge, Card, Modal, NumberInput, PageHeader, Select, Stat, TextInput } from '../components/ui'

interface Dep { id: string; predecessor_id: string; successor_id: string; type: RelType; lag_days: number }
interface Baseline { id: string; revision_no: number; name: string; created_at: string }

export default function Schedule() {
  const { project, masters, can, reloadMasters, reloadProjects } = useProject()
  const acts = masters.activities.filter((a) => a.status !== 'cancelled')
  const deps = useLoad(async () => must(supabase.from('activity_dependencies').select('*').eq('project_id', project!.id)) as Promise<Dep[]>, [project!.id])
  const base = useLoad(async () => {
    const list = (await must(supabase.from('baseline_revisions').select('*').eq('project_id', project!.id).order('revision_no'))) as Baseline[]
    const dates = project!.approved_baseline_id ? await must(supabase.from('baseline_activity_dates').select('activity_id,start_date,finish_date').eq('baseline_id', project!.approved_baseline_id)) : []
    return { list, dates: new Map((dates as Array<{ activity_id: string; start_date: string; finish_date: string }>).map((d) => [d.activity_id, d])) }
  }, [project!.id, project!.approved_baseline_id])
  useRealtime('activity_dependencies', project?.id, () => void deps.reload())
  const [dataDate, setDataDate] = useState(today())
  const [lookAhead, setLookAhead] = useState(0)
  const [msg, setMsg] = useState<{ tone: 'red' | 'green' | 'amber'; text: string } | null>(null)
  const [depForm, setDepForm] = useState<{ p: string; s: string; t: RelType; lag: number | null }>({ p: '', s: '', t: 'FS', lag: 0 })
  const [blOpen, setBlOpen] = useState(false)
  const [blName, setBlName] = useState('')
  const [blCurrent, setBlCurrent] = useState(false)

  const start = project!.start_date ?? acts.map((a) => a.planned_start).filter(Boolean).sort()[0] ?? today()
  const result = useMemo((): { out?: CpmOutput; error?: string } => {
    if (!deps.data || !acts.length) return {}
    try {
      return {
        out: schedule(
          acts.map((a) => ({ id: a.id, code: a.code, duration: a.original_duration, isMilestone: a.is_milestone, actualStart: a.actual_start, actualFinish: a.actual_finish, remainingDuration: a.remaining_duration, constraintType: a.constraint_type, constraintDate: a.constraint_date })),
          deps.data.map((d) => ({ predecessorId: d.predecessor_id, successorId: d.successor_id, type: d.type, lag: d.lag_days })),
          { projectStart: start, dataDate, calendar: masters.calendar ? { workDays: masters.calendar.work_days, holidays: masters.calendar.holidays } : DEFAULT_CALENDAR },
        ),
      }
    } catch (e) {
      return { error: e instanceof ScheduleError ? e.message : String(e) }
    }
  }, [acts, deps.data, dataDate, masters.calendar, start])

  const rows = useMemo(() => acts.map((a) => {
    const r = result.out?.activities.get(a.id)
    const bl = base.data?.dates.get(a.id)
    const slip = r && bl?.finish_date ? daysBetween(bl.finish_date, r.earlyFinish) : null
    return { a, r, bl, slip }
  }).sort((x, y) => (x.r?.earlyStart ?? '').localeCompare(y.r?.earlyStart ?? '') || x.a.code.localeCompare(y.a.code)), [acts, result.out, base.data])

  const visible = lookAhead ? rows.filter(({ r }) => r && r.earlyFinish >= dataDate && r.earlyStart <= addDays(dataDate, lookAhead)) : rows
  const span = rows.reduce((acc, { r, bl }) => {
    const ds = [r?.earlyStart, r?.earlyFinish, bl?.start_date, bl?.finish_date].filter(Boolean) as string[]
    return { min: [acc.min, ...ds].sort()[0], max: [acc.max, ...ds].sort().at(-1)! }
  }, { min: start, max: start })
  const total = Math.max(1, daysBetween(span.min, span.max) + 1)
  const pos = (d: string) => (daysBetween(span.min, d) / total) * 100

  const addDep = async () => {
    try {
      await must(supabase.from('activity_dependencies').insert({ project_id: project!.id, predecessor_id: depForm.p, successor_id: depForm.s, type: depForm.t, lag_days: depForm.lag ?? 0 }))
      setDepForm({ p: '', s: '', t: 'FS', lag: 0 })
      await deps.reload()
    } catch (e) { setMsg({ tone: 'red', text: friendlyError(e) }) }
  }
  const apply = async () => {
    if (!result.out || !confirm('Copy the calculated early dates into the planned dates of every activity? The approved baseline is NOT changed.')) return
    try {
      const n = await must(supabase.rpc('apply_schedule_dates', { p_project: project!.id, p_rows: acts.map((a) => ({ id: a.id, version: a.version, planned_start: result.out!.activities.get(a.id)!.earlyStart, planned_finish: result.out!.activities.get(a.id)!.earlyFinish })) }))
      setMsg({ tone: 'green', text: `Updated planned dates of ${n} activities.` })
      await reloadMasters()
    } catch (e) { setMsg({ tone: 'red', text: friendlyError(e) }) }
  }
  const createBaseline = async () => {
    try {
      await must(supabase.rpc('create_baseline', { p_project: project!.id, p_name: blName, p_notes: null, p_make_current: blCurrent }))
      setBlOpen(false)
      setMsg({ tone: 'green', text: 'Baseline frozen. Earlier baselines are kept unchanged.' })
      await base.reload()
      await reloadProjects()
    } catch (e) { setMsg({ tone: 'red', text: friendlyError(e) }) }
  }
  const setCurrent = async (id: string) => {
    try { await must(supabase.rpc('set_current_baseline', { p_project: project!.id, p_baseline: id })); await reloadProjects() } catch (e) { setMsg({ tone: 'red', text: friendlyError(e) }) }
  }

  const critical = rows.filter((x) => x.r?.critical).length
  const delayed = rows.filter((x) => (x.slip ?? 0) > 0 && x.r?.status !== 'completed').length
  const code = (id: string) => masters.activities.find((a) => a.id === id)?.code ?? '?'

  return (
    <div className="flex flex-col gap-4">
      <PageHeader title="Schedule (CPM)" subtitle="Forward/backward pass on the working calendar with FS/SS/FF/SF links, lags, constraints and progress."
        actions={<>
          <button className="btn btn-secondary" disabled={!result.out} onClick={() => download(`${project!.code}_schedule.csv`, toCsv(rows.map(({ a, r, bl, slip }) => ({ code: a.code, name: a.name, duration: a.original_duration, early_start: r?.earlyStart, early_finish: r?.earlyFinish, late_start: r?.lateStart, late_finish: r?.lateFinish, total_float: r?.totalFloat, free_float: r?.freeFloat, critical: r?.critical, baseline_start: bl?.start_date, baseline_finish: bl?.finish_date, finish_variance_days: slip }))))}>Export CSV</button>
          {can('schedule', 'edit') && <button className="btn btn-secondary" disabled={!result.out} onClick={() => void apply()}>Apply dates to plan</button>}
          {can('schedule', 'create') && <button className="btn btn-primary" onClick={() => { setBlName(`Baseline rev ${(base.data?.list.length ?? 0)}`); setBlOpen(true) }}>Freeze baseline…</button>}
        </>} />
      {msg && <Alert tone={msg.tone}>{msg.text}</Alert>}
      {result.error && <Alert>{result.error}</Alert>}
      <div className="grid grid-cols-2 gap-3 md:grid-cols-4">
        <Stat label="Forecast finish" value={result.out?.projectFinish ?? '—'} sub={project!.completion_date ? `Contract ${project!.completion_date}` : undefined}
          tone={project!.completion_date && result.out && result.out.projectFinish > project!.completion_date ? 'bad' : 'good'} />
        <Stat label="Critical activities" value={critical} />
        <Stat label="Delayed vs baseline" value={delayed} tone={delayed ? 'bad' : 'good'} />
        <Stat label="Approved baseline" value={base.data?.list.find((b) => b.id === project!.approved_baseline_id)?.name ?? 'None'} />
      </div>
      <Card>
        <div className="grid grid-cols-2 gap-3 sm:grid-cols-4 items-end">
          <TextInput label="Data date" type="date" value={dataDate} onChange={(e) => setDataDate(e.target.value)} />
          <Select label="Look-ahead" value={String(lookAhead)} onChange={(e) => setLookAhead(Number(e.target.value))}>
            <option value="0">Whole schedule</option><option value="7">Next 7 days</option><option value="14">Next 14 days</option><option value="21">Next 3 weeks</option>
          </Select>
          <div className="col-span-2 text-xs text-slate-500">Bars: <span className="inline-block w-6 h-2 rounded bg-slate-400 align-middle" /> baseline · <span className="inline-block w-6 h-2 rounded align-middle" style={{ background: 'var(--series-1)' }} /> forecast · <span className="inline-block w-6 h-2 rounded align-middle" style={{ background: 'var(--status-bad)' }} /> critical. Calendar: {masters.calendar ? `${masters.calendar.work_days.length}-day week, ${masters.calendar.holidays.length} holidays` : 'default 6-day week'}.</div>
        </div>
      </Card>
      <Card title={`Activities (${visible.length})`}>
        <div className="overflow-x-auto">
          <table className="table min-w-[900px]">
            <thead><tr><th>ID</th><th>Activity</th><th className="num">Dur</th><th>Early start</th><th>Early finish</th><th className="num">TF</th><th className="num">FF</th><th>Baseline finish</th><th className="num">Var (d)</th><th className="w-[30%]">Timeline</th></tr></thead>
            <tbody>
              {visible.map(({ a, r, bl, slip }) => (
                <tr key={a.id} className={r?.critical ? 'bg-red-50/60 dark:bg-red-950/30' : ''}>
                  <td className="font-semibold whitespace-nowrap">{a.code} {a.is_milestone && '◆'}</td>
                  <td>{a.name} {r?.status === 'completed' ? <Badge tone="green">Done</Badge> : r?.status === 'in_progress' ? <Badge tone="blue">In progress</Badge> : null} {r?.critical && <Badge tone="red">Critical</Badge>}</td>
                  <td className="num">{a.original_duration}</td><td>{r?.earlyStart}</td><td>{r?.earlyFinish}</td>
                  <td className="num">{r?.totalFloat}</td><td className="num">{r?.freeFloat}</td><td>{bl?.finish_date ?? '—'}</td>
                  <td className={`num ${(slip ?? 0) > 0 ? 'text-red-700 font-bold' : ''}`}>{slip == null ? '—' : slip > 0 ? `+${slip}` : slip}</td>
                  <td>
                    <div className="relative h-6" aria-label={`Forecast ${r?.earlyStart} to ${r?.earlyFinish}`}>
                      {bl?.start_date && <div className="absolute top-0 h-2 rounded bg-slate-400" style={{ left: `${pos(bl.start_date)}%`, width: `${Math.max(0.6, pos(addDays(bl.finish_date, 1)) - pos(bl.start_date))}%` }} />}
                      {r && <div className="absolute top-3 h-3 rounded" style={{ background: r.critical ? 'var(--status-bad)' : 'var(--series-1)', left: `${pos(r.earlyStart)}%`, width: `${Math.max(0.6, pos(addDays(r.earlyFinish, 1)) - pos(r.earlyStart))}%` }} />}
                      <div className="absolute top-0 bottom-0 w-px bg-amber-500" style={{ left: `${pos(dataDate)}%` }} title="Data date" />
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        <p className="mt-2 text-xs text-slate-500">TF = total float, FF = free float, in working days. Variance = forecast finish minus approved-baseline finish (calendar days). The baseline never changes when the forecast slips; freeze a new revision to re-baseline.</p>
      </Card>
      <Card title={`Relationships (${deps.data?.length ?? 0})`}>
        {can('schedule', 'create') && (
          <div className="grid grid-cols-2 gap-2 sm:grid-cols-5 items-end mb-3">
            <Select label="Predecessor" value={depForm.p} onChange={(e) => setDepForm({ ...depForm, p: e.target.value })}><option value="">—</option>{acts.map((a) => <option key={a.id} value={a.id}>{a.code} {a.name}</option>)}</Select>
            <Select label="Successor" value={depForm.s} onChange={(e) => setDepForm({ ...depForm, s: e.target.value })}><option value="">—</option>{acts.map((a) => <option key={a.id} value={a.id}>{a.code} {a.name}</option>)}</Select>
            <Select label="Type" value={depForm.t} onChange={(e) => setDepForm({ ...depForm, t: e.target.value as RelType })}>{['FS', 'SS', 'FF', 'SF'].map((t) => <option key={t}>{t}</option>)}</Select>
            <NumberInput label="Lag (−lead), days" value={depForm.lag} min={-365} step={1} onChange={(v) => setDepForm({ ...depForm, lag: v })} />
            <button className="btn btn-primary" disabled={!depForm.p || !depForm.s || depForm.p === depForm.s} onClick={() => void addDep()}>Add link</button>
          </div>
        )}
        <ul className="grid gap-1 sm:grid-cols-2 text-sm">
          {deps.data?.map((d) => (
            <li key={d.id} className="flex items-center justify-between rounded border border-slate-200 dark:border-slate-700 px-2">
              <span>{code(d.predecessor_id)} → {code(d.successor_id)} <b>{d.type}</b>{d.lag_days ? ` ${d.lag_days > 0 ? '+' : ''}${d.lag_days}d` : ''}</span>
              {can('schedule', 'delete') && <button className="btn btn-ghost px-2 text-red-700" onClick={() => void supabase.from('activity_dependencies').delete().eq('id', d.id).then(() => deps.reload())}>Remove</button>}
            </li>
          ))}
        </ul>
      </Card>
      <Card title="Baseline revisions">
        <ul className="flex flex-col gap-1 text-sm">
          {base.data?.list.map((b) => (
            <li key={b.id} className="flex items-center justify-between gap-2">
              <span>Rev {b.revision_no} — {b.name} · frozen {new Date(b.created_at).toLocaleDateString('en-IN')} {b.id === project!.approved_baseline_id && <Badge tone="green">Approved baseline</Badge>}</span>
              {b.id !== project!.approved_baseline_id && can('schedule', 'approve') && <button className="btn btn-ghost" onClick={() => void setCurrent(b.id)}>Make approved baseline</button>}
            </li>
          ))}
        </ul>
      </Card>
      <Modal open={blOpen} onClose={() => setBlOpen(false)} title="Freeze a baseline">
        <div className="grid gap-3">
          <p className="text-sm">Copies the current planned dates, durations, quantities and weights into a new read-only baseline revision.</p>
          <TextInput label="Name" value={blName} onChange={(e) => setBlName(e.target.value)} />
          {can('schedule', 'approve') && <label className="flex items-center gap-2"><input type="checkbox" className="h-5 w-5" checked={blCurrent} onChange={(e) => setBlCurrent(e.target.checked)} />Make this the approved baseline</label>}
          <button className="btn btn-primary" disabled={!blName.trim()} onClick={() => void createBaseline()}>Freeze baseline</button>
        </div>
      </Modal>
    </div>
  )
}
