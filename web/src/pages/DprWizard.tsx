import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { Link, useNavigate, useParams } from 'react-router-dom'
import { useProject } from '../lib/project'
import { useAuth } from '../lib/auth'
import { supabase, friendlyError } from '../lib/supabase'
import { cacheGet, cachePut, drafts } from '../lib/local'
import { sync } from '../lib/sync'
import { useFavorites, useSyncStatus } from '../lib/hooks'
import { computeProgress, linearPlannedPct } from '../lib/calc/progress'
import { closingStock, stockWarnings } from '../lib/calc/stock'
import { formatQty } from '../lib/calc/money'
import { parseVoiceReport, type VoiceProposal } from '../lib/voice/parse'
import { docToDraft, type DprDocument } from '../lib/dprDoc'
import {
  today, uid, type Activity, type ActionLine, type ConstraintLine, type DprActivityLine, type DprDraft, type EquipmentLine,
  type InspectionLine, type ManpowerLine, type MaterialLine, type NcrLine, type SafetyLine,
} from '../lib/types'
import { Evidence } from '../components/Evidence'
import { Alert, Badge, Card, DprStatusBadge, Empty, Modal, NumberInput, Select, Spinner, TextArea, TextInput } from '../components/ui'
import { Expiry } from './ProjectSetup'
import { PERMIT_CHECKLISTS } from '../lib/permits'

const STEPS = ['Report', 'Activities', 'Manpower', 'Equipment', 'Materials', 'Safety & QA', 'Narrative', 'Review'] as const

const blankDraft = (projectId: string): DprDraft => ({
  id: uid(), project_id: projectId, report_date: today(), shift: 'day', work_start: '08:00', work_end: '17:30',
  weather: '', site_conditions: '', toolbox_talks: 0, permits_issued: 0,
  activities: [], manpower: [], equipment: [], materials: [], safety: [], inspections: [], ncrs: [], constraints: [], actions: [],
})

type Ctx = Map<string, { prev: number; planned: number | null }>
type Stock = Map<string, number>

export default function DprWizard() {
  const { id } = useParams()
  const nav = useNavigate()
  const { project, masters, can } = useProject()
  const { user } = useAuth()
  const [draft, setDraft] = useState<DprDraft | null>(null)
  const [loadErr, setLoadErr] = useState<string | null>(null)
  const [step, setStep] = useState(0)
  const [ctx, setCtx] = useState<{ map: Ctx; from: 'server' | 'device' | 'none' }>({ map: new Map(), from: 'none' })
  const [stock, setStock] = useState<Stock>(new Map())
  const [voice, setVoice] = useState<VoiceProposal | null>(null)
  const [undo, setUndo] = useState<{ label: string; restore: () => void } | null>(null)
  const [busy, setBusy] = useState<string | null>(null)
  const [msg, setMsg] = useState<{ tone: 'red' | 'green' | 'amber'; text: string } | null>(null)
  const sStatus = useSyncStatus()
  const [localState, setLocalState] = useState<string>('local')

  // ---- load --------------------------------------------------------------
  useEffect(() => {
    if (!project) return
    let alive = true
    ;(async () => {
      if (!id) return setDraft(blankDraft(project.id))
      const local = await drafts.get(id)
      if (local && local.syncState !== 'synced') return alive && setDraft(local.draft)
      const { data, error } = await supabase.rpc('dpr_document', { p_id: id })
      if (error) {
        if (local) return alive && setDraft(local.draft)
        return alive && setLoadErr(friendlyError(error))
      }
      const doc = data as DprDocument
      if (!['draft', 'rejected'].includes(doc.header.status)) return nav(`/dpr/${id}`, { replace: true })
      if (alive) setDraft(docToDraft(doc))
    })()
    return () => { alive = false }
  }, [id, project, nav])

  // ---- autosave on the device (never loses typing on a dropped network) ----
  const first = useRef(true)
  useEffect(() => {
    if (!draft) return
    if (first.current) { first.current = false; return }
    const t = setTimeout(() => void sync.saveLocal(draft).then(() => drafts.get(draft.id)).then((r) => r && setLocalState(r.syncState)), 600)
    return () => clearTimeout(t)
  }, [draft])
  useEffect(() => { if (draft) void drafts.get(draft.id).then((r) => r && setLocalState(r.syncState)) }, [draft?.id, sStatus]) // eslint-disable-line react-hooks/exhaustive-deps

  // ---- context: previous cumulative & planned %, current stock ------------
  useEffect(() => {
    if (!draft || !project) return
    const key = `ctx:${project.id}:${draft.report_date}:${draft.seq ?? 'new'}`
    ;(async () => {
      const { data, error } = await supabase.rpc('dpr_activity_context', { p_project: project.id, p_date: draft.report_date, p_seq: draft.seq ?? null })
      if (!error && data) {
        const rows = data as Array<{ activity_id: string; prev_cum_qty: number; planned_progress_pct: number | null }>
        await cachePut(key, rows)
        setCtx({ map: new Map(rows.map((r) => [r.activity_id, { prev: Number(r.prev_cum_qty), planned: r.planned_progress_pct == null ? null : Number(r.planned_progress_pct) }])), from: 'server' })
      } else {
        const c = await cacheGet<Array<{ activity_id: string; prev_cum_qty: number; planned_progress_pct: number | null }>>(key)
        if (c) setCtx({ map: new Map(c.value.map((r) => [r.activity_id, { prev: Number(r.prev_cum_qty), planned: r.planned_progress_pct }])), from: 'device' })
      }
      const st = await supabase.from('material_stock').select('material_id,stock_qty').eq('project_id', project.id)
      if (!st.error) setStock(new Map((st.data ?? []).map((r) => [r.material_id as string, Number(r.stock_qty)])))
    })()
  }, [draft?.report_date, draft?.seq, project]) // eslint-disable-line react-hooks/exhaustive-deps

  const set = useCallback(<K extends keyof DprDraft>(k: K, v: DprDraft[K]) => setDraft((d) => (d ? { ...d, [k]: v } : d)), [])
  type ListKey = 'activities' | 'manpower' | 'equipment' | 'materials' | 'safety' | 'inspections' | 'ncrs' | 'constraints' | 'actions'
  const setLine = <L extends ListKey>(list: L, key: string, patch: Partial<DprDraft[L][number]>) =>
    setDraft((d) => d && { ...d, [list]: (d[list] as Array<{ key: string }>).map((l) => (l.key === key ? { ...l, ...patch } : l)) })
  const addLine = <L extends ListKey>(list: L, line: DprDraft[L][number]) => setDraft((d) => d && { ...d, [list]: [...(d[list] as unknown[]), line] })
  const removeLine = <L extends ListKey>(list: L, key: string, label: string) => {
    const before = draft![list]
    setDraft((d) => d && { ...d, [list]: (d[list] as Array<{ key: string }>).filter((l) => l.key !== key) })
    setUndo({ label, restore: () => setDraft((d) => d && { ...d, [list]: before }) })
    setTimeout(() => setUndo(null), 8000)
  }

  const actById = useMemo(() => new Map(masters.activities.map((a) => [a.id, a])), [masters.activities])
  const progressFor = (l: DprActivityLine) => {
    const a = actById.get(l.activity_id)
    const c = ctx.map.get(l.activity_id)
    return computeProgress({
      approvedTotal: a?.total_qty ?? 0, previousCumulative: c?.prev ?? 0, today: l.qty_today ?? 0, method: l.progress_method,
      manualPct: l.manual_progress_pct, overrunAuthorized: l.overrun_authorized,
      plannedPct: c?.planned ?? linearPlannedPct(a?.planned_start ?? null, a?.planned_finish ?? null, draft!.report_date),
    })
  }

  // ---- validation ------------------------------------------------------------
  const issues = useMemo(() => {
    if (!draft) return { errors: [] as string[], warnings: [] as string[] }
    const errors: string[] = [], warnings: string[] = []
    if (!draft.report_date) errors.push('Report date is required.')
    if (draft.report_date > today()) errors.push('Report date cannot be in the future.')
    if (!draft.shift) errors.push('Shift is required.')
    if (!draft.activities.length && !draft.work_completed?.trim()) errors.push('Add at least one activity entry or describe the work completed (Narrative).')
    for (const l of draft.activities) {
      const a = actById.get(l.activity_id)
      const p = progressFor(l)
      if (l.qty_today == null && l.progress_method !== 'manual') warnings.push(`${a?.code}: today's quantity is blank (treated as 0).`)
      if (p.overrun && !l.overrun_authorized) errors.push(`${a?.code}: cumulative exceeds approved quantity — correct it or record an authorised overrun with a reason.`)
      if (l.overrun_authorized && !l.overrun_reason?.trim()) errors.push(`${a?.code}: overrun reason is required.`)
      if (p.variancePct != null && p.variancePct < -5 && !l.delay_reason?.trim()) warnings.push(`${a?.code}: behind plan by ${Math.abs(p.variancePct).toFixed(1)} % — add a delay reason.`)
      if (l.actual_finish && l.actual_start && l.actual_finish < l.actual_start) errors.push(`${a?.code}: actual finish is before actual start.`)
    }
    for (const m of draft.manpower) if (!m.trade?.trim()) errors.push('Every manpower line needs a trade/category.')
    for (const e of draft.equipment) {
      if (e.operating_hours + e.idle_hours + e.breakdown_hours > 24) errors.push(`${e.equipment_code}: hours exceed 24.`)
      if (e.breakdown_hours > 0 && !e.breakdown_reason?.trim()) errors.push(`${e.equipment_code}: breakdown reason is required.`)
      if (e.certification_expiry && e.certification_expiry < draft.report_date) warnings.push(`${e.equipment_code}: inspection/certificate expired on ${e.certification_expiry}.`)
    }
    for (const m of draft.materials) {
      const w = stockWarnings({ opening: stock.get(m.material_id) ?? 0, received: m.received_qty ?? 0, accepted: m.accepted_qty, rejected: m.rejected_qty ?? 0, issued: m.issued_qty ?? 0, consumed: m.consumed_qty ?? 0, returned: m.returned_qty ?? 0 })
      const code = masters.materials.find((x) => x.id === m.material_id)?.code
      for (const x of w) (x.includes('exceeds available stock') ? errors : warnings).push(`${code}: ${x}`)
    }
    for (const s of draft.safety) if (!s.description.trim()) errors.push('Every safety record needs a description.')
    for (const n of draft.ncrs) if (!n.description.trim()) errors.push('Every NCR/observation needs a description.')
    for (const c of draft.constraints) if (!c.description.trim()) errors.push('Every constraint needs a description.')
    for (const a of draft.actions) if (!a.description.trim()) errors.push('Every action item needs a description.')
    if (!draft.weather?.trim()) warnings.push('Weather is not recorded.')
    if (!draft.tomorrow_plan?.trim()) warnings.push("Tomorrow's plan is empty.")
    return { errors, warnings }
  }, [draft, ctx, stock, actById]) // eslint-disable-line react-hooks/exhaustive-deps

  // ---- voice → structured entry --------------------------------------------
  const convertVoice = (text: string) => setVoice(parseVoiceReport(text, masters.activities))

  // ---- save / submit --------------------------------------------------------
  const saveDraft = async () => {
    setBusy('save')
    setMsg(null)
    await sync.queueSave(draft!)
    await sync.run()
    const rec = await drafts.get(draft!.id)
    setBusy(null)
    if (rec?.syncState === 'synced') {
      setDraft((d) => d && { ...d, ...rec.draft, activities: d.activities, manpower: d.manpower })
      setMsg({ tone: 'green', text: `Saved to server as ${rec.draft.dpr_no}.` })
      if (!id) nav(`/dpr/edit/${draft!.id}`, { replace: true })
    } else if (rec?.syncState === 'conflict') setMsg({ tone: 'red', text: rec.error ?? 'Conflict with a newer server version.' })
    else if (rec?.syncState === 'failed') setMsg({ tone: 'red', text: rec.error ?? 'The server rejected the report.' })
    else setMsg({ tone: 'amber', text: 'Saved on this device. It will be sent to the server automatically when you are online.' })
  }

  const submit = async () => {
    if (issues.errors.length) return setMsg({ tone: 'red', text: 'Fix the errors listed below before submitting.' })
    setBusy('submit')
    setMsg(null)
    try {
      await sync.queueSave(draft!)
      await sync.run()
      const rec = await drafts.get(draft!.id)
      if (rec?.syncState !== 'synced') throw new Error(rec?.error ?? 'The report must reach the server before it can be submitted. Check your connection and try again.')
      const { error } = await supabase.rpc('submit_dpr', { p_id: draft!.id, p_expected_version: rec.serverVersion })
      if (error) throw error
      await drafts.delete(draft!.id) // the server copy is now the record
      nav(`/dpr/${draft!.id}`)
    } catch (e) {
      setMsg({ tone: 'red', text: friendlyError(e) })
    } finally {
      setBusy(null)
    }
  }

  if (loadErr) return <Alert>{loadErr}</Alert>
  if (!draft || !project) return <Spinner />
  if (!can('dpr', 'create') && !can('dpr', 'edit')) return <Empty title="You do not have permission to prepare DPRs in this project." />

  const stateLabel: Record<string, [Parameters<typeof Badge>[0]['tone'], string]> = {
    local: ['amber', 'Saved locally'], queued: ['amber', 'Waiting to synchronise'], syncing: ['blue', 'Synchronising'],
    synced: ['green', 'Synchronised'], failed: ['red', 'Sync failed'], conflict: ['red', 'Conflict requires review'],
  }

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div>
          <h1 className="text-2xl font-extrabold">{draft.dpr_no ?? 'New DPR'} <span className="text-base font-semibold text-slate-500">· {draft.report_date} · {draft.shift} shift</span></h1>
          <div className="flex flex-wrap gap-2 mt-1">
            {draft.status && <DprStatusBadge status={draft.status} revision={draft.revision} />}
            <Badge tone={stateLabel[localState]?.[0]}>{stateLabel[localState]?.[1] ?? localState}</Badge>
            {ctx.from === 'device' && <Badge tone="amber">Using cached quantities (offline)</Badge>}
          </div>
        </div>
        <button className="btn btn-secondary" disabled={!!busy} onClick={() => void saveDraft()}>{busy === 'save' ? 'Saving…' : 'Save draft'}</button>
      </div>
      {msg && <Alert tone={msg.tone}>{msg.text}</Alert>}

      <ol className="flex gap-1 overflow-x-auto pb-1" aria-label="Report steps">
        {STEPS.map((s, i) => (
          <li key={s}>
            <button onClick={() => setStep(i)} aria-current={step === i ? 'step' : undefined}
              className={`whitespace-nowrap rounded-full px-3 min-h-10 text-sm font-semibold border ${step === i ? 'bg-brand-700 text-white border-brand-700' : 'bg-white dark:bg-slate-900 border-slate-300 dark:border-slate-700'}`}>
              {i + 1}. {s}{countFor(draft, i)}
            </button>
          </li>
        ))}
      </ol>

      {step === 0 && <StepReport draft={draft} set={set} areas={masters.areas} shifts={masters.calendar?.shifts} preparedBy={user?.email ?? ''} />}
      {step === 1 && (
        <>
          <StepActivities draft={draft} activities={masters.activities} contractors={masters.contractors} progressFor={progressFor} ctx={ctx.map}
            setLine={(k, p) => setLine('activities', k, p)} add={(a) => addLine('activities', a)} remove={(k, label) => removeLine('activities', k, label)}
            projectId={project.id} onVoice={convertVoice} setDraft={setDraft} />
          <Evidence projectId={project.id} dprId={draft.id} activities={masters.activities} onVoiceText={convertVoice} />
        </>
      )}
      {step === 2 && <StepManpower draft={draft} contractors={masters.contractors} setLine={(k, p) => setLine('manpower', k, p)} add={() => addLine('manpower', newManpower(draft.shift))} remove={(k) => removeLine('manpower', k, 'manpower line')} />}
      {step === 3 && <StepEquipment draft={draft} equipment={masters.equipment} setLine={(k, p) => setLine('equipment', k, p)} add={(l) => addLine('equipment', l)} remove={(k) => removeLine('equipment', k, 'equipment line')} />}
      {step === 4 && <StepMaterials draft={draft} materials={masters.materials} stock={stock} setLine={(k, p) => setLine('materials', k, p)} add={(l) => addLine('materials', l)} remove={(k) => removeLine('materials', k, 'material line')} />}
      {step === 5 && (
        <>
          <StepSafetyQa draft={draft} set={set} activities={masters.activities} setLine={setLine as never} addLine={addLine as never} removeLine={removeLine as never} />
          <Evidence projectId={project.id} dprId={draft.id} activities={masters.activities} />
        </>
      )}
      {step === 6 && <StepNarrative draft={draft} set={set} />}
      {step === 7 && (
        <StepReview draft={draft} issues={issues} progressFor={progressFor} actById={actById} materials={masters.materials} stock={stock}
          onSubmit={() => void submit()} busy={busy} canSubmit={can('dpr', 'submit')} goTo={setStep} />
      )}

      <div className="sticky bottom-16 lg:bottom-2 z-20 flex gap-2 rounded-xl bg-white/90 dark:bg-slate-900/90 backdrop-blur p-2 border border-slate-200 dark:border-slate-800">
        <button className="btn btn-secondary flex-1" disabled={step === 0} onClick={() => setStep((s) => s - 1)}>← Back</button>
        {step < STEPS.length - 1
          ? <button className="btn btn-primary flex-1" onClick={() => setStep((s) => s + 1)}>Next: {STEPS[step + 1]} →</button>
          : <button className="btn btn-success flex-1" disabled={!!busy || !can('dpr', 'submit')} onClick={() => void submit()}>{busy === 'submit' ? 'Submitting…' : 'Submit for review'}</button>}
      </div>

      {undo && (
        <div role="status" className="fixed bottom-36 lg:bottom-20 left-4 right-4 sm:left-auto z-40 card p-3 flex items-center gap-3 shadow-lg">
          Removed {undo.label}. <button className="btn btn-secondary" onClick={() => { undo.restore(); setUndo(null) }}>Undo</button>
        </div>
      )}

      <VoiceConfirm proposal={voice} onClose={() => setVoice(null)} activities={masters.activities} equipment={masters.equipment}
        onConfirm={(r) => {
          setDraft((d) => {
            if (!d) return d
            const activities = d.activities.some((l) => l.activity_id === r.activityId)
              ? d.activities.map((l) => (l.activity_id === r.activityId ? { ...l, qty_today: (l.qty_today ?? 0) + r.quantity, location: r.location || l.location } : l))
              : [...d.activities, { key: uid(), activity_id: r.activityId, qty_today: r.quantity, location: r.location, progress_method: 'quantity' as const }]
            const manpower = r.workers > 0 ? [...d.manpower, { ...newManpower(r.shift), trade: r.trade, [r.workerClass]: r.workers, remarks: 'From voice entry' }] : d.manpower
            const equipment = [...d.equipment, ...r.equipment.flatMap((e) => Array.from({ length: e.count }, () => ({ ...newEquipment(), equipment_id: e.id, equipment_code: e.code, equipment_type: e.type, location: r.location })))]
            return { ...d, shift: r.shift, activities, manpower, equipment }
          })
          setVoice(null)
          setMsg({ tone: 'green', text: 'Voice entry added. Review the quantities on each step before submitting.' })
        }} />
    </div>
  )
}

function countFor(d: DprDraft, i: number) {
  const n = [0, d.activities.length, d.manpower.length, d.equipment.length, d.materials.length, d.safety.length + d.inspections.length + d.ncrs.length + d.constraints.length + d.actions.length, 0, 0][i]
  return n ? ` (${n})` : ''
}

const newManpower = (shift = 'day'): ManpowerLine => ({ key: uid(), trade: '', shift, skilled: 0, semi_skilled: 0, unskilled: 0, supervisors: 0, engineers: 0, safety_staff: 0, others: 0, absent: 0, hours_per_person: 8, overtime_hours: 0 })
const newEquipment = (): EquipmentLine => ({ key: uid(), equipment_code: '', equipment_type: '', operating_hours: 0, idle_hours: 0, breakdown_hours: 0 })

// ============================================================================
// Step 1 — report details
// ============================================================================
function StepReport({ draft, set, areas, shifts, preparedBy }: { draft: DprDraft; set: <K extends keyof DprDraft>(k: K, v: DprDraft[K]) => void; areas: Array<{ id: string; code: string; name: string }>; shifts?: Array<{ code: string; name: string }>; preparedBy: string }) {
  const locked = !!draft.dpr_no // date/shift/area define the report slot once it exists on the server
  return (
    <Card title="Step 1 — Report details">
      <div className="grid gap-3 sm:grid-cols-2">
        <TextInput label="Report date" type="date" value={draft.report_date} max={today()} onChange={(e) => set('report_date', e.target.value)} disabled={locked && draft.revision != null && draft.revision > 0} required />
        <Select label="Shift" value={draft.shift} onChange={(e) => set('shift', e.target.value)}>
          {(shifts?.length ? shifts : [{ code: 'day', name: 'Day' }, { code: 'night', name: 'Night' }]).map((s) => <option key={s.code} value={s.code}>{s.name}</option>)}
          <option value="general">General</option>
        </Select>
        <TextInput label="Work start" type="time" value={draft.work_start ?? ''} onChange={(e) => set('work_start', e.target.value)} />
        <TextInput label="Work end" type="time" value={draft.work_end ?? ''} onChange={(e) => set('work_end', e.target.value)} />
        <Select label="Site / area / unit" value={draft.area_id ?? ''} onChange={(e) => set('area_id', e.target.value || null)}>
          <option value="">Whole project</option>
          {areas.map((a) => <option key={a.id} value={a.id}>{a.code} — {a.name}</option>)}
        </Select>
        <TextInput label="Prepared by" value={preparedBy} disabled hint="Recorded automatically from your sign-in." />
        <TextInput label="Weather" list="weather-options" value={draft.weather ?? ''} onChange={(e) => set('weather', e.target.value)} placeholder="Clear, 34 °C" />
        <datalist id="weather-options">{['Clear', 'Cloudy', 'Light rain', 'Heavy rain', 'Hot (>40 °C)', 'High wind', 'Fog'].map((w) => <option key={w} value={w} />)}</datalist>
        <TextInput label="Site conditions" value={draft.site_conditions ?? ''} onChange={(e) => set('site_conditions', e.target.value)} placeholder="Normal / wet ground near grid C" />
      </div>
      <p className="mt-3 text-xs text-slate-500">The DPR number is generated by the server when the report is first saved. Only one original DPR is allowed per date, shift and area.</p>
    </Card>
  )
}

// ============================================================================
// Step 2 — activity progress
// ============================================================================
function StepActivities({ draft, activities, contractors, progressFor, ctx, setLine, add, remove, projectId, onVoice, setDraft }: {
  draft: DprDraft; activities: Activity[]; contractors: Array<{ id: string; name: string }>; progressFor: (l: DprActivityLine) => ReturnType<typeof computeProgress>
  ctx: Ctx; setLine: (k: string, p: Partial<DprActivityLine>) => void; add: (l: DprActivityLine) => void; remove: (k: string, label: string) => void
  projectId: string; onVoice: (t: string) => void; setDraft: React.Dispatch<React.SetStateAction<DprDraft | null>>
}) {
  const [q, setQ] = useState('')
  const [typed, setTyped] = useState('')
  const { fav, toggle } = useFavorites(projectId)
  const recentKey = `cdpr.recent.${projectId}`
  const recent: string[] = JSON.parse(localStorage.getItem(recentKey) ?? '[]')
  const used = new Set(draft.activities.map((l) => l.activity_id))
  const addAct = (a: Activity) => {
    add({ key: uid(), activity_id: a.id, qty_today: null, location: a.location ?? '', contractor_id: a.contractor_id, progress_method: 'quantity' })
    localStorage.setItem(recentKey, JSON.stringify([a.id, ...recent.filter((x) => x !== a.id)].slice(0, 12)))
    setQ('')
  }
  const matches = q.length >= 1
    ? activities.filter((a) => !used.has(a.id) && a.status !== 'cancelled' && `${a.code} ${a.name} ${a.location ?? ''}`.toLowerCase().includes(q.toLowerCase())).slice(0, 12)
    : [...fav, ...recent.filter((r) => !fav.includes(r))].map((x) => activities.find((a) => a.id === x)).filter((a): a is Activity => !!a && !used.has(a.id)).slice(0, 10)

  const copyLast = async () => {
    const { data } = await supabase.from('dpr_headers').select('id').eq('project_id', projectId).lt('report_date', draft.report_date).order('report_date', { ascending: false }).limit(1)
    if (!data?.length) return alert('No earlier report found.')
    const { data: lines } = await supabase.from('dpr_activity_entries').select('activity_id,location,contractor_id,responsible,next_day_target').eq('dpr_id', data[0].id)
    setDraft((d) => d && {
      ...d, activities: [...d.activities, ...(lines ?? []).filter((l) => !d.activities.some((x) => x.activity_id === l.activity_id))
        .map((l) => ({ key: uid(), activity_id: l.activity_id, qty_today: null, location: l.location ?? '', contractor_id: l.contractor_id, responsible: l.responsible ?? '', progress_method: 'quantity' as const }))],
    })
  }

  return (
    <Card title="Step 2 — Daily activity progress" actions={<button className="btn btn-secondary" onClick={() => void copyLast()}>Copy activities from last report</button>}>
      <div className="grid gap-2 mb-4">
        <label className="label" htmlFor="act-search">Add activity (search code, name or location)</label>
        <input id="act-search" className="input" type="search" placeholder="e.g. A2010 or skip bridge" value={q} onChange={(e) => setQ(e.target.value)} />
        {!!matches.length && (
          <ul className="flex flex-col gap-1 rounded-lg border border-slate-200 dark:border-slate-700 p-1 max-h-72 overflow-y-auto">
            {!q && <li className="px-2 text-xs font-semibold text-slate-500">Favourites & recent</li>}
            {matches.map((a) => (
              <li key={a.id} className="flex items-center gap-1">
                <button className="flex-1 text-left rounded p-2 min-h-11 hover:bg-slate-100 dark:hover:bg-slate-800" onClick={() => addAct(a)}>
                  <b>{a.code}</b> {a.name} <span className="text-xs text-slate-500">{a.unit} · {a.location}</span>
                </button>
                <button className="btn btn-ghost px-2" onClick={() => toggle(a.id)} aria-label={fav.includes(a.id) ? 'Remove favourite' : 'Add favourite'}>{fav.includes(a.id) ? '★' : '☆'}</button>
              </li>
            ))}
          </ul>
        )}
        <details>
          <summary className="cursor-pointer text-sm font-semibold">⌨ Type or paste a site statement instead of speaking</summary>
          <div className="mt-2 flex gap-2">
            <input className="input" value={typed} onChange={(e) => setTyped(e.target.value)} placeholder="Erected 12 MT at skip bridge with 1 mobile crane and 24 workers, day shift" aria-label="Site statement" />
            <button className="btn btn-primary" disabled={!typed.trim()} onClick={() => onVoice(typed)}>Convert</button>
          </div>
        </details>
      </div>

      {!draft.activities.length && <Empty title="No activities yet">Search above, use a favourite, copy from the last report, or record a voice note below.</Empty>}
      <div className="flex flex-col gap-3">
        {draft.activities.map((l) => {
          const a = activities.find((x) => x.id === l.activity_id)
          const p = progressFor(l)
          const prev = ctx.get(l.activity_id)?.prev ?? 0
          return (
            <div key={l.key} className="rounded-xl border border-slate-200 dark:border-slate-700 p-3">
              <div className="flex items-start justify-between gap-2">
                <div><div className="font-bold">{a?.code} — {a?.name}</div><div className="text-xs text-slate-500">Approved qty {formatQty(a?.total_qty ?? 0)} {a?.unit} · planned {a?.planned_start ?? '—'} → {a?.planned_finish ?? '—'}</div></div>
                <button className="btn btn-ghost px-2 text-red-700" onClick={() => remove(l.key, a?.code ?? 'activity')} aria-label="Remove activity">✕</button>
              </div>
              <div className="mt-2 grid grid-cols-2 gap-2 sm:grid-cols-4">
                <NumberInput label={`Today (${a?.unit ?? ''})`} value={l.qty_today} onChange={(v) => setLine(l.key, { qty_today: v })} className="col-span-2 sm:col-span-1" autoFocus={l.qty_today == null} />
                <Figure label="Previous cum." value={formatQty(prev)} />
                <Figure label="Cumulative" value={formatQty(p.cumulative)} tone={p.overrun ? 'bad' : undefined} />
                <Figure label="Balance" value={formatQty(p.balance)} tone={p.balance < 0 ? 'bad' : undefined} />
                <Figure label="Actual %" value={p.actualPct == null ? '—' : `${p.actualPct.toFixed(1)} %`} />
                <Figure label="Planned %" value={ctx.get(l.activity_id)?.planned == null ? '—' : `${ctx.get(l.activity_id)!.planned!.toFixed(1)} %`} />
                <Figure label="Variance" value={p.variancePct == null ? '—' : `${p.variancePct > 0 ? '+' : ''}${p.variancePct.toFixed(1)} %`} tone={p.variancePct != null && p.variancePct < 0 ? 'bad' : 'good'} />
                <NumberInput label="Next-day target" value={l.next_day_target ?? null} onChange={(v) => setLine(l.key, { next_day_target: v })} />
              </div>
              {p.warnings.map((w) => <p key={w} className="mt-2 text-sm text-red-700 dark:text-red-400">⚠ {w}</p>)}
              {p.overrun && (
                <div className="mt-2 grid gap-2 sm:grid-cols-[auto_1fr] items-center">
                  <label className="flex items-center gap-2 text-sm font-semibold"><input type="checkbox" className="h-5 w-5" checked={!!l.overrun_authorized} onChange={(e) => setLine(l.key, { overrun_authorized: e.target.checked })} />Authorised overrun</label>
                  <input className="input" placeholder="Reason / authorisation reference (required)" value={l.overrun_reason ?? ''} onChange={(e) => setLine(l.key, { overrun_reason: e.target.value })} aria-label="Overrun reason" />
                </div>
              )}
              <details className="mt-2">
                <summary className="cursor-pointer text-sm font-semibold text-brand-700 dark:text-brand-100">More details (location, contractor, dates, delays, method)</summary>
                <div className="mt-2 grid gap-2 sm:grid-cols-2">
                  <TextInput label="Location / grid / elevation" value={l.location ?? ''} onChange={(e) => setLine(l.key, { location: e.target.value })} />
                  <Select label="Contractor" value={l.contractor_id ?? ''} onChange={(e) => setLine(l.key, { contractor_id: e.target.value || null })}>
                    <option value="">—</option>{contractors.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
                  </Select>
                  <TextInput label="Actual start" type="date" value={l.actual_start ?? ''} onChange={(e) => setLine(l.key, { actual_start: e.target.value || null })} />
                  <TextInput label="Actual finish" type="date" value={l.actual_finish ?? ''} onChange={(e) => setLine(l.key, { actual_finish: e.target.value || null })} />
                  <Select label="Progress method" value={l.progress_method ?? 'quantity'} onChange={(e) => setLine(l.key, { progress_method: e.target.value as 'quantity' | 'manual' })}>
                    <option value="quantity">Quantity-weighted (qty ÷ approved qty)</option><option value="manual">Manually assessed %</option>
                  </Select>
                  {l.progress_method === 'manual' && <NumberInput label="Assessed progress %" value={l.manual_progress_pct ?? null} onChange={(v) => setLine(l.key, { manual_progress_pct: v })} max={100} />}
                  <NumberInput label="Inspected qty (if inspected today)" value={l.qty_inspected ?? null} onChange={(v) => setLine(l.key, { qty_inspected: v })} />
                  <TextInput label="Responsible person" value={l.responsible ?? ''} onChange={(e) => setLine(l.key, { responsible: e.target.value })} />
                  <TextInput label="Reason for delay / deviation" value={l.delay_reason ?? ''} onChange={(e) => setLine(l.key, { delay_reason: e.target.value })} />
                  <TextInput label="Constraints" value={l.constraints ?? ''} onChange={(e) => setLine(l.key, { constraints: e.target.value })} />
                  <TextArea label="Remarks" className="sm:col-span-2" value={l.remarks ?? ''} onChange={(e) => setLine(l.key, { remarks: e.target.value })} rows={2} />
                </div>
              </details>
            </div>
          )
        })}
      </div>
      <p className="mt-3 text-xs text-slate-500">Reported quantity is what the site reports today. The reviewer records the accepted quantity at approval; cumulative progress uses accepted quantities from approved reports. Inspection, certification and billing are tracked separately.</p>
    </Card>
  )
}

function Figure({ label, value, tone }: { label: string; value: ReactNode; tone?: 'good' | 'bad' }) {
  return (
    <div className="rounded-lg bg-slate-50 dark:bg-slate-800 p-2">
      <div className="text-xs text-slate-500">{label}</div>
      <div className={`font-bold tabular-nums ${tone === 'bad' ? 'text-red-700 dark:text-red-400' : tone === 'good' ? 'text-emerald-700 dark:text-emerald-400' : ''}`}>{value}</div>
    </div>
  )
}

// ============================================================================
// Step 3 — manpower
// ============================================================================
const MP_FIELDS: Array<[keyof ManpowerLine, string]> = [['skilled', 'Skilled'], ['semi_skilled', 'Semi-skilled'], ['unskilled', 'Unskilled'], ['supervisors', 'Supervisors'], ['engineers', 'Engineers'], ['safety_staff', 'Safety'], ['others', 'Other staff'], ['absent', 'Absent']]
export const headcount = (m: ManpowerLine) => (m.skilled ?? 0) + (m.semi_skilled ?? 0) + (m.unskilled ?? 0) + (m.supervisors ?? 0) + (m.engineers ?? 0) + (m.safety_staff ?? 0) + (m.others ?? 0)

function StepManpower({ draft, contractors, setLine, add, remove }: { draft: DprDraft; contractors: Array<{ id: string; name: string }>; setLine: (k: string, p: Partial<ManpowerLine>) => void; add: () => void; remove: (k: string) => void }) {
  const total = draft.manpower.reduce((s, m) => s + headcount(m), 0)
  const hours = draft.manpower.reduce((s, m) => s + headcount(m) * (m.hours_per_person ?? 0) + (m.overtime_hours ?? 0), 0)
  return (
    <Card title="Step 3 — Manpower" actions={<button className="btn btn-primary" onClick={add}>＋ Add line</button>}>
      <div className="grid grid-cols-2 gap-2 mb-3 sm:grid-cols-3">
        <Figure label="Total headcount" value={total} /><Figure label="Man-hours (incl. OT)" value={hours.toLocaleString('en-IN')} />
        <Figure label="Absent" value={draft.manpower.reduce((s, m) => s + (m.absent ?? 0), 0)} />
      </div>
      {!draft.manpower.length && <Empty title="No manpower recorded" />}
      <div className="flex flex-col gap-3">
        {draft.manpower.map((m) => (
          <div key={m.key} className="rounded-xl border border-slate-200 dark:border-slate-700 p-3 grid gap-2">
            <div className="grid gap-2 sm:grid-cols-3">
              <Select label="Contractor / agency" value={m.contractor_id ?? ''} onChange={(e) => setLine(m.key, { contractor_id: e.target.value || null })}>
                <option value="">—</option>{contractors.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
              </Select>
              <TextInput label="Trade / category" list="trades" value={m.trade} onChange={(e) => setLine(m.key, { trade: e.target.value })} required />
              <Select label="Shift" value={m.shift ?? 'day'} onChange={(e) => setLine(m.key, { shift: e.target.value })}><option value="day">Day</option><option value="night">Night</option><option value="general">General</option></Select>
            </div>
            <div className="grid grid-cols-4 gap-2 sm:grid-cols-8">
              {MP_FIELDS.map(([k, label]) => <NumberInput key={k} label={label} value={m[k] as number} step={1} onChange={(v) => setLine(m.key, { [k]: Math.max(0, Math.round(v ?? 0)) })} />)}
            </div>
            <div className="grid grid-cols-3 gap-2 items-end">
              <NumberInput label="Hours / person" value={m.hours_per_person} onChange={(v) => setLine(m.key, { hours_per_person: v ?? 0 })} max={24} />
              <NumberInput label="Overtime hours (total)" value={m.overtime_hours} onChange={(v) => setLine(m.key, { overtime_hours: v ?? 0 })} />
              <div className="text-sm"><b>{headcount(m)}</b> people · <b>{(headcount(m) * m.hours_per_person + m.overtime_hours).toLocaleString('en-IN')}</b> man-h</div>
            </div>
            <div className="flex gap-2"><input className="input" placeholder="Remarks" value={m.remarks ?? ''} onChange={(e) => setLine(m.key, { remarks: e.target.value })} aria-label="Remarks" />
              <button className="btn btn-ghost text-red-700" onClick={() => remove(m.key)} aria-label="Remove manpower line">✕</button></div>
          </div>
        ))}
      </div>
      <datalist id="trades">{['Fitters', 'Welders', 'Riggers', 'Structural', 'Civil', 'Masons', 'Carpenters', 'Bar benders', 'Electricians', 'Refractory', 'Painters', 'Helpers', 'Crane operators', 'Scaffolders'].map((t) => <option key={t} value={t} />)}</datalist>
    </Card>
  )
}

// ============================================================================
// Step 4 — equipment
// ============================================================================
function StepEquipment({ draft, equipment, setLine, add, remove }: { draft: DprDraft; equipment: Array<{ id: string; code: string; type: string; rated_capacity: string | null; certification_expiry: string | null }>; setLine: (k: string, p: Partial<EquipmentLine>) => void; add: (l: EquipmentLine) => void; remove: (k: string) => void }) {
  const [pick, setPick] = useState('')
  return (
    <Card title="Step 4 — Plant & equipment">
      <div className="flex gap-2 mb-3 items-end">
        <Select label="Add equipment from register" className="flex-1" value={pick} onChange={(e) => setPick(e.target.value)}>
          <option value="">— choose —</option>{equipment.map((e) => <option key={e.id} value={e.id}>{e.code} — {e.type} {e.rated_capacity ?? ''}</option>)}
        </Select>
        <button className="btn btn-primary" disabled={!pick} onClick={() => {
          const e = equipment.find((x) => x.id === pick)!
          add({ ...newEquipment(), equipment_id: e.id, equipment_code: e.code, equipment_type: e.type, rated_capacity: e.rated_capacity ?? '', certification_expiry: e.certification_expiry })
          setPick('')
        }}>Add</button>
        <button className="btn btn-secondary" onClick={() => add(newEquipment())}>Hired / unlisted</button>
      </div>
      {!draft.equipment.length && <Empty title="No equipment recorded" />}
      <div className="flex flex-col gap-3">
        {draft.equipment.map((e) => {
          const total = e.operating_hours + e.idle_hours + e.breakdown_hours
          return (
            <div key={e.key} className="rounded-xl border border-slate-200 dark:border-slate-700 p-3 grid gap-2">
              <div className="grid gap-2 sm:grid-cols-4">
                <TextInput label="Equipment ID" value={e.equipment_code} onChange={(x) => setLine(e.key, { equipment_code: x.target.value })} disabled={!!e.equipment_id} />
                <TextInput label="Type" value={e.equipment_type} onChange={(x) => setLine(e.key, { equipment_type: x.target.value })} disabled={!!e.equipment_id} />
                <TextInput label="Location" value={e.location ?? ''} onChange={(x) => setLine(e.key, { location: x.target.value })} />
                <TextInput label="Operator" value={e.operator ?? ''} onChange={(x) => setLine(e.key, { operator: x.target.value })} />
              </div>
              <div className="grid grid-cols-3 gap-2 sm:grid-cols-6">
                <NumberInput label="Working h" value={e.operating_hours} onChange={(v) => setLine(e.key, { operating_hours: v ?? 0 })} max={24} />
                <NumberInput label="Idle h" value={e.idle_hours} onChange={(v) => setLine(e.key, { idle_hours: v ?? 0 })} max={24} />
                <NumberInput label="Breakdown h" value={e.breakdown_hours} onChange={(v) => setLine(e.key, { breakdown_hours: v ?? 0 })} max={24} />
                <NumberInput label="Fuel / energy" value={e.fuel_consumed ?? null} onChange={(v) => setLine(e.key, { fuel_consumed: v })} />
                <TextInput label="Unit" value={e.fuel_unit ?? ''} placeholder="L / kWh" onChange={(x) => setLine(e.key, { fuel_unit: x.target.value })} />
                <Figure label="Utilisation" value={total ? `${Math.round((e.operating_hours * 100) / total)} %` : '—'} tone={total > 24 ? 'bad' : undefined} />
              </div>
              {e.breakdown_hours > 0 && <TextInput label="Breakdown reason (required)" value={e.breakdown_reason ?? ''} onChange={(x) => setLine(e.key, { breakdown_reason: x.target.value })} />}
              <div className="flex flex-wrap items-center justify-between gap-2 text-sm">
                <span>Certificate / inspection expiry: <Expiry date={e.certification_expiry ?? null} /></span>
                <button className="btn btn-ghost text-red-700" onClick={() => remove(e.key)}>Remove</button>
              </div>
            </div>
          )
        })}
      </div>
    </Card>
  )
}

// ============================================================================
// Step 5 — materials
// ============================================================================
function StepMaterials({ draft, materials, stock, setLine, add, remove }: { draft: DprDraft; materials: Array<{ id: string; code: string; description: string; unit: string }>; stock: Stock; setLine: (k: string, p: Partial<MaterialLine>) => void; add: (l: MaterialLine) => void; remove: (k: string) => void }) {
  const [pick, setPick] = useState('')
  return (
    <Card title="Step 5 — Material movement">
      <div className="flex gap-2 mb-3 items-end">
        <Select label="Material" className="flex-1" value={pick} onChange={(e) => setPick(e.target.value)}>
          <option value="">— choose —</option>{materials.filter((m) => !draft.materials.some((x) => x.material_id === m.id)).map((m) => <option key={m.id} value={m.id}>{m.code} — {m.description}</option>)}
        </Select>
        <button className="btn btn-primary" disabled={!pick} onClick={() => { add({ key: uid(), material_id: pick, received_qty: 0, rejected_qty: 0, issued_qty: 0, consumed_qty: 0, returned_qty: 0, inspection_status: 'pending' }); setPick('') }}>Add</button>
      </div>
      {!draft.materials.length && <Empty title="No material movement recorded" />}
      <div className="flex flex-col gap-3">
        {draft.materials.map((m) => {
          const mat = materials.find((x) => x.id === m.material_id)
          const opening = stock.get(m.material_id) ?? 0
          const line = { opening, received: m.received_qty ?? 0, accepted: m.accepted_qty, rejected: m.rejected_qty ?? 0, issued: m.issued_qty ?? 0, consumed: m.consumed_qty ?? 0, returned: m.returned_qty ?? 0 }
          return (
            <div key={m.key} className="rounded-xl border border-slate-200 dark:border-slate-700 p-3 grid gap-2">
              <div className="flex justify-between gap-2"><b>{mat?.code} — {mat?.description} ({mat?.unit})</b><button className="btn btn-ghost text-red-700 px-2" onClick={() => remove(m.key)} aria-label="Remove material">✕</button></div>
              <div className="grid grid-cols-3 gap-2 sm:grid-cols-8">
                <Figure label="Opening (store)" value={formatQty(opening)} />
                <NumberInput label="Received" value={m.received_qty} onChange={(v) => setLine(m.key, { received_qty: v ?? 0 })} />
                <NumberInput label="Accepted" value={m.accepted_qty ?? null} onChange={(v) => setLine(m.key, { accepted_qty: v })} />
                <NumberInput label="Rejected" value={m.rejected_qty} onChange={(v) => setLine(m.key, { rejected_qty: v ?? 0 })} />
                <NumberInput label="Issued" value={m.issued_qty} onChange={(v) => setLine(m.key, { issued_qty: v ?? 0 })} />
                <NumberInput label="Consumed" value={m.consumed_qty} onChange={(v) => setLine(m.key, { consumed_qty: v ?? 0 })} />
                <NumberInput label="Returned" value={m.returned_qty} onChange={(v) => setLine(m.key, { returned_qty: v ?? 0 })} />
                <Figure label="Closing" value={formatQty(closingStock(line))} tone={closingStock(line) < 0 ? 'bad' : undefined} />
              </div>
              {stockWarnings(line).map((w) => <p key={w} className="text-sm text-red-700 dark:text-red-400">⚠ {w}</p>)}
              <div className="grid gap-2 sm:grid-cols-3">
                <TextInput label="Challan / receipt no." value={m.challan_no ?? ''} onChange={(e) => setLine(m.key, { challan_no: e.target.value })} />
                <TextInput label="Heat / batch no." value={m.heat_batch_no ?? ''} onChange={(e) => setLine(m.key, { heat_batch_no: e.target.value })} />
                <Select label="Inspection status" value={m.inspection_status} onChange={(e) => setLine(m.key, { inspection_status: e.target.value })}>
                  {[['pending', 'Pending'], ['accepted', 'Accepted'], ['partially_accepted', 'Partially accepted'], ['rejected', 'Rejected'], ['not_required', 'Not required']].map(([k, l]) => <option key={k} value={k}>{l}</option>)}
                </Select>
                <TextInput label="Storage location" value={m.storage_location ?? ''} onChange={(e) => setLine(m.key, { storage_location: e.target.value })} />
                <TextInput label="Supplier" value={m.supplier ?? ''} onChange={(e) => setLine(m.key, { supplier: e.target.value })} />
                <TextInput label="Remarks" value={m.remarks ?? ''} onChange={(e) => setLine(m.key, { remarks: e.target.value })} />
              </div>
            </div>
          )
        })}
      </div>
      <p className="mt-3 text-xs text-slate-500">Opening stock comes from the approved store ledger and is recalculated by the server. Closing = opening + received + returned − issued − rejected. Draft and rejected reports do not move stock.</p>
    </Card>
  )
}

// ============================================================================
// Step 6 — safety, QA/QC, constraints and actions
// ============================================================================
function StepSafetyQa({ draft, set, activities, setLine, addLine, removeLine }: {
  draft: DprDraft; set: <K extends keyof DprDraft>(k: K, v: DprDraft[K]) => void; activities: Activity[]
  setLine: (list: string, key: string, patch: object) => void; addLine: (list: string, l: object) => void; removeLine: (list: string, key: string, label: string) => void
}) {
  const actSelect = (value: string | null | undefined, onChange: (v: string | null) => void) => (
    <Select label="Activity" value={value ?? ''} onChange={(e) => onChange(e.target.value || null)}>
      <option value="">—</option>{activities.map((a) => <option key={a.id} value={a.id}>{a.code} — {a.name}</option>)}
    </Select>
  )
  const statusSel = (value: string, onChange: (v: string) => void) => (
    <Select label="Status" value={value} onChange={(e) => onChange(e.target.value)}><option value="open">Open</option><option value="in_progress">In progress</option><option value="closed">Closed</option></Select>
  )
  return (
    <div className="flex flex-col gap-4">
      <Card title="Step 6 — Safety">
        <div className="grid grid-cols-2 gap-2 sm:grid-cols-3 mb-3">
          <NumberInput label="Toolbox talks held" value={draft.toolbox_talks ?? 0} step={1} onChange={(v) => set('toolbox_talks', Math.round(v ?? 0))} />
          <NumberInput label="Permits issued" value={draft.permits_issued ?? 0} step={1} onChange={(v) => set('permits_issued', Math.round(v ?? 0))} />
          <TextInput label="Toolbox topics" className="col-span-2 sm:col-span-1" value={draft.toolbox_topics ?? ''} onChange={(e) => set('toolbox_topics', e.target.value)} />
        </div>
        {draft.safety.map((s) => (
          <div key={s.key} className="rounded-xl border border-slate-200 dark:border-slate-700 p-3 grid gap-2 mb-2 sm:grid-cols-3">
            <Select label="Type" value={s.category} onChange={(e) => setLine('safety', s.key, { category: e.target.value })}>
              {[['permit', 'Permit to work'], ['lifting_plan', 'Lifting plan / inspection'], ['work_at_height', 'Work at height'], ['unsafe_condition', 'Unsafe condition'], ['unsafe_act', 'Unsafe act'], ['near_miss', 'Near miss'], ['incident', 'Incident'], ['first_aid', 'First aid case'], ['good_practice', 'Good practice'], ['other', 'Other']].map(([k, l]) => <option key={k} value={k}>{l}</option>)}
            </Select>
            {s.category === 'permit' && (
              <>
                <Select label="Permit type" value={s.permit_type ?? ''} onChange={(e) => setLine('safety', s.key, { permit_type: e.target.value || null })}>
                  <option value="">—</option>{Object.entries(PERMIT_CHECKLISTS).map(([k, v]) => <option key={k} value={k}>{v.title}</option>)}
                </Select>
                <TextInput label="Permit no." value={s.permit_no ?? ''} onChange={(e) => setLine('safety', s.key, { permit_no: e.target.value })} />
              </>
            )}
            <Select label="Severity" value={s.severity ?? ''} onChange={(e) => setLine('safety', s.key, { severity: e.target.value || null })}><option value="">—</option><option value="low">Low</option><option value="medium">Medium</option><option value="high">High</option><option value="critical">Critical</option></Select>
            <TextArea label="Description" className="sm:col-span-3" value={s.description} onChange={(e) => setLine('safety', s.key, { description: e.target.value })} rows={2} />
            {s.category === 'permit' && s.permit_type && <PermitChecklist type={s.permit_type} value={s.preventive_action ?? ''} onChange={(v) => setLine('safety', s.key, { preventive_action: v })} />}
            <TextInput label="Corrective action" value={s.corrective_action ?? ''} onChange={(e) => setLine('safety', s.key, { corrective_action: e.target.value })} />
            {s.category !== 'permit' && <TextInput label="Preventive action" value={s.preventive_action ?? ''} onChange={(e) => setLine('safety', s.key, { preventive_action: e.target.value })} />}
            <TextInput label="Responsible" value={s.responsible ?? ''} onChange={(e) => setLine('safety', s.key, { responsible: e.target.value })} />
            <TextInput label="Target closure" type="date" value={s.target_date ?? ''} onChange={(e) => setLine('safety', s.key, { target_date: e.target.value || null })} />
            {statusSel(s.status, (v) => setLine('safety', s.key, { status: v }))}
            <button className="btn btn-ghost text-red-700 self-end" onClick={() => removeLine('safety', s.key, 'safety record')}>Remove</button>
          </div>
        ))}
        <button className="btn btn-secondary" onClick={() => addLine('safety', { key: uid(), category: 'unsafe_condition', description: '', status: 'open' } satisfies SafetyLine)}>＋ Safety observation / permit / incident</button>
      </Card>

      <Card title="QA/QC — inspections, NCRs, hold points">
        {draft.inspections.map((s) => (
          <div key={s.key} className="rounded-xl border border-slate-200 dark:border-slate-700 p-3 grid gap-2 mb-2 sm:grid-cols-3">
            {actSelect(s.activity_id, (v) => setLine('inspections', s.key, { activity_id: v }))}
            <TextInput label="Inspection request no." value={s.request_no ?? ''} onChange={(e) => setLine('inspections', s.key, { request_no: e.target.value })} />
            <TextInput label="Inspection type" list="insp-types" value={s.inspection_type} onChange={(e) => setLine('inspections', s.key, { inspection_type: e.target.value })} />
            <NumberInput label="Inspected qty" value={s.inspected_qty ?? null} onChange={(v) => setLine('inspections', s.key, { inspected_qty: v })} />
            <Select label="Outcome" value={s.outcome} onChange={(e) => setLine('inspections', s.key, { outcome: e.target.value })}>
              <option value="pending">Pending</option><option value="accepted">Accepted</option><option value="accepted_with_comments">Accepted with comments</option><option value="rejected">Rejected</option>
            </Select>
            <TextInput label="Inspector" value={s.inspector ?? ''} onChange={(e) => setLine('inspections', s.key, { inspector: e.target.value })} />
            <label className="flex items-center gap-2 text-sm font-semibold"><input type="checkbox" className="h-5 w-5" checked={s.is_hold_point} onChange={(e) => setLine('inspections', s.key, { is_hold_point: e.target.checked })} />Hold point (work stops until released)</label>
            <TextInput label="Remarks" className="sm:col-span-2" value={s.remarks ?? ''} onChange={(e) => setLine('inspections', s.key, { remarks: e.target.value })} />
            <button className="btn btn-ghost text-red-700" onClick={() => removeLine('inspections', s.key, 'inspection')}>Remove</button>
          </div>
        ))}
        <datalist id="insp-types">{['Fit-up', 'Welding visual', 'NDT (UT/RT/MPI/DPT)', 'Alignment & level', 'Bolt torque', 'Concrete pour card', 'Reinforcement', 'Painting DFT', 'Refractory', 'Hydro test'].map((t) => <option key={t} value={t} />)}</datalist>
        {draft.ncrs.map((s) => (
          <div key={s.key} className="rounded-xl border border-red-200 dark:border-red-900 p-3 grid gap-2 mb-2 sm:grid-cols-3">
            <Select label="Kind" value={s.kind} onChange={(e) => setLine('ncrs', s.key, { kind: e.target.value })}><option value="ncr">NCR</option><option value="observation">Observation</option></Select>
            <TextInput label="NCR no." value={s.ncr_no ?? ''} onChange={(e) => setLine('ncrs', s.key, { ncr_no: e.target.value })} />
            {actSelect(s.activity_id, (v) => setLine('ncrs', s.key, { activity_id: v }))}
            <TextArea label="Description" className="sm:col-span-3" value={s.description} onChange={(e) => setLine('ncrs', s.key, { description: e.target.value })} rows={2} />
            <label className="flex items-center gap-2 text-sm font-semibold"><input type="checkbox" className="h-5 w-5" checked={s.rework_required} onChange={(e) => setLine('ncrs', s.key, { rework_required: e.target.checked })} />Rework required</label>
            <TextInput label="Corrective action" value={s.corrective_action ?? ''} onChange={(e) => setLine('ncrs', s.key, { corrective_action: e.target.value })} />
            <TextInput label="Responsible" value={s.responsible ?? ''} onChange={(e) => setLine('ncrs', s.key, { responsible: e.target.value })} />
            <TextInput label="Target closure" type="date" value={s.target_date ?? ''} onChange={(e) => setLine('ncrs', s.key, { target_date: e.target.value || null })} />
            {statusSel(s.status, (v) => setLine('ncrs', s.key, { status: v }))}
            <button className="btn btn-ghost text-red-700" onClick={() => removeLine('ncrs', s.key, 'NCR')}>Remove</button>
          </div>
        ))}
        <div className="flex flex-wrap gap-2">
          <button className="btn btn-secondary" onClick={() => addLine('inspections', { key: uid(), inspection_type: '', is_hold_point: false, outcome: 'pending' } satisfies InspectionLine)}>＋ Inspection</button>
          <button className="btn btn-secondary" onClick={() => addLine('ncrs', { key: uid(), kind: 'ncr', description: '', rework_required: false, status: 'open' } satisfies NcrLine)}>＋ NCR / observation</button>
        </div>
      </Card>

      <Card title="Constraints & required actions">
        {draft.constraints.map((s) => (
          <div key={s.key} className="rounded-xl border border-slate-200 dark:border-slate-700 p-3 grid gap-2 mb-2 sm:grid-cols-3">
            <Select label="Category" value={s.category} onChange={(e) => setLine('constraints', s.key, { category: e.target.value })}>
              {['material', 'equipment', 'labour', 'design', 'client', 'weather', 'permit', 'access', 'other'].map((c) => <option key={c} value={c}>{c[0].toUpperCase() + c.slice(1)}</option>)}
            </Select>
            {actSelect(s.activity_id, (v) => setLine('constraints', s.key, { activity_id: v }))}
            <TextInput label="Responsible" value={s.responsible ?? ''} onChange={(e) => setLine('constraints', s.key, { responsible: e.target.value })} />
            <TextArea label="Constraint" className="sm:col-span-2" value={s.description} onChange={(e) => setLine('constraints', s.key, { description: e.target.value })} rows={2} />
            <TextInput label="Needed by" type="date" value={s.target_date ?? ''} onChange={(e) => setLine('constraints', s.key, { target_date: e.target.value || null })} />
            <button className="btn btn-ghost text-red-700" onClick={() => removeLine('constraints', s.key, 'constraint')}>Remove</button>
          </div>
        ))}
        {draft.actions.map((s) => (
          <div key={s.key} className="rounded-xl border border-slate-200 dark:border-slate-700 p-3 grid gap-2 mb-2 sm:grid-cols-4">
            <TextInput label="Action required" className="sm:col-span-2" value={s.description} onChange={(e) => setLine('actions', s.key, { description: e.target.value })} />
            <TextInput label="Responsible" value={s.responsible ?? ''} onChange={(e) => setLine('actions', s.key, { responsible: e.target.value })} />
            <TextInput label="Target date" type="date" value={s.target_date ?? ''} onChange={(e) => setLine('actions', s.key, { target_date: e.target.value || null })} />
            <Select label="Priority" value={s.priority} onChange={(e) => setLine('actions', s.key, { priority: e.target.value })}><option value="low">Low</option><option value="normal">Normal</option><option value="high">High</option><option value="urgent">Urgent</option></Select>
            <button className="btn btn-ghost text-red-700" onClick={() => removeLine('actions', s.key, 'action')}>Remove</button>
          </div>
        ))}
        <div className="flex flex-wrap gap-2">
          <button className="btn btn-secondary" onClick={() => addLine('constraints', { key: uid(), category: 'material', description: '', status: 'open' } satisfies ConstraintLine)}>＋ Constraint</button>
          <button className="btn btn-secondary" onClick={() => addLine('actions', { key: uid(), description: '', priority: 'normal', status: 'open' } satisfies ActionLine)}>＋ Action item</button>
        </div>
      </Card>
    </div>
  )
}

function PermitChecklist({ type, value, onChange }: { type: string; value: string; onChange: (v: string) => void }) {
  const cfg = PERMIT_CHECKLISTS[type]
  if (!cfg) return null
  const checked = new Set(value.split('\n').filter((l) => l.startsWith('[x] ')).map((l) => l.slice(4)))
  const toggle = (item: string) => {
    const next = new Set(checked)
    if (next.has(item)) next.delete(item)
    else next.add(item)
    onChange(cfg.items.map((i) => `${next.has(i) ? '[x]' : '[ ]'} ${i}`).join('\n'))
  }
  return (
    <fieldset className="sm:col-span-3 rounded-lg bg-slate-50 dark:bg-slate-800 p-3">
      <legend className="font-semibold text-sm">{cfg.title} — checklist</legend>
      <div className="grid gap-1 sm:grid-cols-2">
        {cfg.items.map((i) => <label key={i} className="flex items-start gap-2 text-sm min-h-9"><input type="checkbox" className="h-5 w-5 mt-0.5" checked={checked.has(i)} onChange={() => toggle(i)} />{i}</label>)}
      </div>
      <p className="mt-2 text-xs text-slate-500">This checklist records what was verified on site. It does not certify regulatory compliance; the issuing authority's permit system governs.</p>
    </fieldset>
  )
}

// ============================================================================
// Step 7 — narrative
// ============================================================================
const NARRATIVE: Array<[keyof DprDraft, string]> = [
  ['work_completed', 'Work completed'], ['achievements', 'Major achievements'], ['work_not_completed', 'Work not completed'],
  ['delay_reasons', 'Reasons for delay'], ['weather_interruptions', 'Weather interruptions'], ['material_shortages', 'Material shortages'],
  ['equipment_constraints', 'Equipment constraints'], ['labour_constraints', 'Labour constraints'], ['design_issues', 'Design / drawing issues'],
  ['client_instructions', 'Client instructions'], ['decisions_required', 'Decisions required'], ['tomorrow_plan', "Tomorrow's planned activities"],
  ['required_manpower', 'Required manpower'], ['required_equipment', 'Required equipment'], ['required_materials', 'Required materials'], ['priority_actions', 'Priority actions'],
]
function StepNarrative({ draft, set }: { draft: DprDraft; set: <K extends keyof DprDraft>(k: K, v: DprDraft[K]) => void }) {
  return (
    <Card title="Step 7 — Daily narrative & tomorrow's plan">
      <div className="grid gap-3 sm:grid-cols-2">
        {NARRATIVE.map(([k, label]) => <TextArea key={k} label={label} value={(draft[k] as string) ?? ''} onChange={(e) => set(k, e.target.value as never)} rows={2} />)}
      </div>
    </Card>
  )
}

// ============================================================================
// Step 8 — review & submit
// ============================================================================
function StepReview({ draft, issues, progressFor, actById, materials, stock, onSubmit, busy, canSubmit, goTo }: {
  draft: DprDraft; issues: { errors: string[]; warnings: string[] }; progressFor: (l: DprActivityLine) => ReturnType<typeof computeProgress>
  actById: Map<string, Activity>; materials: Array<{ id: string; code: string; unit: string }>; stock: Stock
  onSubmit: () => void; busy: string | null; canSubmit: boolean; goTo: (s: number) => void
}) {
  const [ack, setAck] = useState(false)
  const totalHead = draft.manpower.reduce((s, m) => s + headcount(m), 0)
  const byTrade = draft.manpower.reduce<Record<string, number>>((acc, m) => ({ ...acc, [m.trade || '—']: (acc[m.trade || '—'] ?? 0) + headcount(m) }), {})
  return (
    <div className="flex flex-col gap-4">
      <Card title="Step 8 — Review & submit">
        {issues.errors.length > 0 && <Alert><b>Must fix before submitting:</b><ul className="list-disc pl-5">{issues.errors.map((e) => <li key={e}>{e}</li>)}</ul></Alert>}
        {issues.warnings.length > 0 && (
          <div className="mt-2"><Alert tone="amber"><b>Warnings:</b><ul className="list-disc pl-5">{issues.warnings.map((e) => <li key={e}>{e}</li>)}</ul>
            <label className="mt-2 flex items-center gap-2 font-semibold"><input type="checkbox" className="h-5 w-5" checked={ack} onChange={(e) => setAck(e.target.checked)} />I have reviewed these warnings</label></Alert></div>
        )}
        {!issues.errors.length && !issues.warnings.length && <Alert tone="green">All checks passed.</Alert>}
      </Card>
      <Card title={`Activities (${draft.activities.length})`} actions={<button className="btn btn-ghost" onClick={() => goTo(1)}>Edit</button>}>
        <div className="overflow-x-auto">
          <table className="table">
            <thead><tr><th>Activity</th><th className="num">Today</th><th className="num">Prev.</th><th className="num">Cum.</th><th className="num">Balance</th><th className="num">Actual %</th><th className="num">Var.</th></tr></thead>
            <tbody>{draft.activities.map((l) => {
              const a = actById.get(l.activity_id), p = progressFor(l)
              return <tr key={l.key}><td><b>{a?.code}</b> {a?.name}</td><td className="num">{formatQty(l.qty_today ?? 0)} {a?.unit}</td><td className="num">{formatQty(p.cumulative - (l.qty_today ?? 0))}</td><td className="num">{formatQty(p.cumulative)}</td><td className="num">{formatQty(p.balance)}</td><td className="num">{p.actualPct?.toFixed(1) ?? '—'}</td><td className="num">{p.variancePct?.toFixed(1) ?? '—'}</td></tr>
            })}</tbody>
          </table>
        </div>
      </Card>
      <div className="grid gap-4 md:grid-cols-2">
        <Card title={`Manpower — ${totalHead} people`} actions={<button className="btn btn-ghost" onClick={() => goTo(2)}>Edit</button>}>
          <ul className="text-sm">{Object.entries(byTrade).map(([t, n]) => <li key={t}>{t}: <b>{n}</b></li>)}</ul>
        </Card>
        <Card title={`Equipment — ${draft.equipment.length}`} actions={<button className="btn btn-ghost" onClick={() => goTo(3)}>Edit</button>}>
          <ul className="text-sm">{draft.equipment.map((e) => <li key={e.key}>{e.equipment_code} {e.equipment_type}: {e.operating_hours} h working, {e.breakdown_hours} h breakdown</li>)}</ul>
        </Card>
        <Card title={`Materials — ${draft.materials.length}`} actions={<button className="btn btn-ghost" onClick={() => goTo(4)}>Edit</button>}>
          <ul className="text-sm">{draft.materials.map((m) => { const mat = materials.find((x) => x.id === m.material_id); return <li key={m.key}>{mat?.code}: +{m.received_qty} / −{m.issued_qty} → closing ≈ {formatQty(closingStock({ opening: stock.get(m.material_id) ?? 0, received: m.received_qty, rejected: m.rejected_qty, issued: m.issued_qty, consumed: m.consumed_qty, returned: m.returned_qty }))} {mat?.unit}</li> })}</ul>
        </Card>
        <Card title="Safety & quality" actions={<button className="btn btn-ghost" onClick={() => goTo(5)}>Edit</button>}>
          <p className="text-sm">{draft.toolbox_talks ?? 0} toolbox talks · {draft.permits_issued ?? 0} permits · {draft.safety.length} observations · {draft.inspections.length} inspections · {draft.ncrs.length} NCRs · {draft.constraints.length} constraints · {draft.actions.length} actions</p>
        </Card>
      </div>
      <Card title="Narrative" actions={<button className="btn btn-ghost" onClick={() => goTo(6)}>Edit</button>}>
        <dl className="text-sm grid gap-1">{NARRATIVE.filter(([k]) => (draft[k] as string)?.trim()).map(([k, l]) => <div key={k}><dt className="font-semibold inline">{l}: </dt><dd className="inline">{draft[k] as string}</dd></div>)}</dl>
      </Card>
      <div className="flex flex-wrap gap-2">
        <button className="btn btn-success min-h-14 flex-1" disabled={!!busy || !canSubmit || issues.errors.length > 0 || (issues.warnings.length > 0 && !ack)} onClick={onSubmit}>
          {busy === 'submit' ? 'Submitting…' : 'Submit for review'}
        </button>
        <Link className="btn btn-secondary min-h-14" to={draft.dpr_no ? `/dpr/${draft.id}` : '#'} aria-disabled={!draft.dpr_no}>Preview / PDF</Link>
      </div>
      {!canSubmit && <p className="text-sm text-slate-500">Your role cannot submit reports; save the draft and ask your supervisor to submit.</p>}
    </div>
  )
}

// ============================================================================
// Voice / typed statement → confirmed structured entry
// ============================================================================
interface VoiceResult { activityId: string; quantity: number; location: string; shift: string; workers: number; workerClass: 'skilled' | 'semi_skilled' | 'unskilled'; trade: string; equipment: Array<{ id: string | null; code: string; type: string; count: number }> }

function VoiceConfirm({ proposal, onClose, onConfirm, activities, equipment }: { proposal: VoiceProposal | null; onClose: () => void; onConfirm: (r: VoiceResult) => void; activities: Activity[]; equipment: Array<{ id: string; code: string; type: string }> }) {
  const [r, setR] = useState<VoiceResult | null>(null)
  useEffect(() => {
    if (!proposal) return setR(null)
    setR({
      activityId: proposal.activity?.value.id ?? '', quantity: proposal.quantity?.value ?? 0, location: proposal.location?.value ?? '',
      shift: proposal.shift?.value ?? 'day', workers: proposal.workers?.value ?? 0, workerClass: 'skilled', trade: '',
      equipment: proposal.equipment.map((e) => {
        const m = equipment.find((x) => x.type.toLowerCase().includes(e.type.toLowerCase()) || e.type.toLowerCase().includes(x.type.toLowerCase()))
        return { id: m?.id ?? null, code: m?.code ?? 'UNLISTED', type: m?.type ?? e.type, count: e.count }
      }),
    })
  }, [proposal, equipment])
  if (!proposal || !r) return null
  const uncertain = (f: string) => proposal.uncertain.includes(f) ? <Badge tone="amber">Please confirm</Badge> : <Badge tone="green">Recognised</Badge>
  const act = activities.find((a) => a.id === r.activityId)
  return (
    <Modal open onClose={onClose} title="Confirm voice entry">
      <div className="grid gap-3">
        <p className="text-sm italic text-slate-600 dark:text-slate-400">“{proposal.transcript}”</p>
        <div className="flex items-center justify-between"><span className="label mb-0">Activity</span>{uncertain('activity')}</div>
        <select className="input" value={r.activityId} onChange={(e) => setR({ ...r, activityId: e.target.value })} aria-label="Activity">
          <option value="">— choose the activity —</option>{activities.map((a) => <option key={a.id} value={a.id}>{a.code} — {a.name} ({a.unit})</option>)}
        </select>
        <div className="grid grid-cols-2 gap-2">
          <div><div className="flex justify-between"><span className="label">Quantity</span>{uncertain('quantity')}</div>
            <input className="input num" type="number" inputMode="decimal" value={r.quantity} onChange={(e) => setR({ ...r, quantity: Number(e.target.value) })} aria-label="Quantity" /></div>
          <div><span className="label">Unit</span><div className="input bg-slate-50 dark:bg-slate-800">{proposal.unit?.value ?? '—'}{act?.unit && proposal.unit && act.unit !== proposal.unit.value && <span className="text-red-700"> ≠ activity unit {act.unit}</span>}</div></div>
        </div>
        <div><div className="flex justify-between"><span className="label">Location</span>{uncertain('location')}</div><input className="input" value={r.location} onChange={(e) => setR({ ...r, location: e.target.value })} aria-label="Location" /></div>
        <div className="grid grid-cols-2 gap-2">
          <div><div className="flex justify-between"><span className="label">Shift</span>{uncertain('shift')}</div>
            <select className="input" value={r.shift} onChange={(e) => setR({ ...r, shift: e.target.value })} aria-label="Shift"><option value="day">Day</option><option value="night">Night</option><option value="general">General</option></select></div>
          <NumberInput label="Workers" value={r.workers} step={1} onChange={(v) => setR({ ...r, workers: Math.round(v ?? 0) })} />
        </div>
        {r.workers > 0 && (
          <div className="grid grid-cols-2 gap-2">
            <Select label="Workers are" value={r.workerClass} onChange={(e) => setR({ ...r, workerClass: e.target.value as VoiceResult['workerClass'] })}><option value="skilled">Skilled</option><option value="semi_skilled">Semi-skilled</option><option value="unskilled">Unskilled</option></Select>
            <TextInput label="Trade (required)" list="trades" value={r.trade} onChange={(e) => setR({ ...r, trade: e.target.value })} />
          </div>
        )}
        {r.equipment.map((e, i) => (
          <div key={i} className="text-sm rounded bg-slate-50 dark:bg-slate-800 p-2">Equipment: {e.count} × {e.type} → <b>{e.id ? e.code : 'not in register (will be added as unlisted)'}</b></div>
        ))}
        <p className="text-xs text-slate-500">Nothing is added until you confirm. Check each value; uncertain fields are marked.</p>
        <button className="btn btn-primary" disabled={!r.activityId || r.quantity < 0 || (r.workers > 0 && !r.trade.trim())} onClick={() => onConfirm(r)}>Add to report</button>
      </div>
    </Modal>
  )
}
