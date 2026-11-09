import { useMemo, useState } from 'react'
import { useProject } from '../lib/project'
import { useLoad, useRealtime, must } from '../lib/hooks'
import { supabase } from '../lib/supabase'
import { sCurve } from '../lib/schedule/scurve'
import { schedule } from '../lib/schedule/cpm'
import { addDays, DEFAULT_CALENDAR } from '../lib/schedule/calendar'
import { formatINR } from '../lib/calc/money'
import { today } from '../lib/types'
import { ChartCard, LinesChart, BarsChart } from '../components/charts'
import { Alert, PageHeader, Spinner, Stat } from '../components/ui'
import { useRange, fresh } from './_range'

interface Entry { activity_id: string; report_date: string; qty_today: number; qty_accepted: number | null; delay_reason: string | null }

export default function Dashboards() {
  const { project, masters } = useProject()
  const r = useRange(30)
  const [area, setArea] = useState('')
  const [wbs, setWbs] = useState('')
  const [contractor, setContractor] = useState('')
  const [unit, setUnit] = useState('MT')

  const d = useLoad(async () => {
    const pid = project!.id
    const [entries, bl, mp, eq, boq, ledger, deps, counts] = await Promise.all([
      must(supabase.from('dpr_effective_entries').select('activity_id,report_date,qty_today,qty_accepted,delay_reason').eq('project_id', pid).lte('report_date', r.to).limit(20000)),
      project!.approved_baseline_id ? must(supabase.from('baseline_activity_dates').select('activity_id,start_date,finish_date,weight,total_qty').eq('baseline_id', project!.approved_baseline_id)) : Promise.resolve([]),
      must(supabase.from('manpower_daily').select('report_date,headcount,man_hours').eq('project_id', pid).gte('report_date', r.from).lte('report_date', r.to)),
      must(supabase.from('equipment_daily').select('equipment_code,operating_hours,idle_hours,breakdown_hours').eq('project_id', pid).gte('report_date', r.from).lte('report_date', r.to)),
      must(supabase.from('boq_status').select('item_no,contract_amount,executed_amount,certified_amount,billed_amount').eq('project_id', pid).order('item_no')),
      must(supabase.from('material_ledger').select('material_id,consumed_qty,txn_date').eq('project_id', pid).gte('txn_date', r.from).lte('txn_date', r.to)),
      must(supabase.from('activity_dependencies').select('predecessor_id,successor_id,type,lag_days').eq('project_id', pid)),
      Promise.all([
        supabase.from('dpr_headers').select('id', { count: 'exact', head: true }).eq('project_id', pid).in('status', ['submitted', 'under_review']),
        supabase.from('safety_observations').select('id', { count: 'exact', head: true }).eq('project_id', pid).neq('status', 'closed'),
        supabase.from('quality_ncrs').select('id', { count: 'exact', head: true }).eq('project_id', pid).neq('status', 'closed'),
        supabase.from('inspection_records').select('id', { count: 'exact', head: true }).eq('project_id', pid).eq('outcome', 'pending'),
        supabase.from('commissioning_checklists').select('id', { count: 'exact', head: true }).eq('project_id', pid).eq('stage', 'punch_list').neq('status', 'passed'),
        supabase.from('material_stock').select('material_id', { count: 'exact', head: true }).eq('project_id', pid).eq('below_reorder', true),
      ]),
    ])
    return { entries: entries as Entry[], bl: bl as Array<{ activity_id: string; start_date: string; finish_date: string; weight: number; total_qty: number }>, mp: mp as Array<{ report_date: string; headcount: number; man_hours: number }>,
      eq: eq as Array<{ equipment_code: string; operating_hours: number; idle_hours: number; breakdown_hours: number }>, boq: boq as Array<Record<string, number | string>>,
      ledger: ledger as Array<{ material_id: string; consumed_qty: number }>, deps: deps as Array<{ predecessor_id: string; successor_id: string; type: 'FS'; lag_days: number }>, counts: counts.map((c) => c.count ?? 0) }
  }, [project!.id, project!.approved_baseline_id, r.from, r.to])
  useRealtime('dpr_headers', project?.id, () => void d.reload())

  const acts = useMemo(() => masters.activities.filter((a) => a.status !== 'cancelled' && (!area || a.area_id === area) && (!wbs || a.wbs_id === wbs) && (!contractor || a.contractor_id === contractor)), [masters.activities, area, wbs, contractor])
  const calc = useMemo(() => {
    if (!d.data) return null
    const ids = new Set(acts.map((a) => a.id))
    const bl = new Map(d.data.bl.map((b) => [b.activity_id, b]))
    const entries = d.data.entries.filter((e) => ids.has(e.activity_id))
    const qty = (e: Entry) => Number(e.qty_accepted ?? e.qty_today)
    const curveActs = acts.map((a) => ({ id: a.id, weight: Number(bl.get(a.id)?.weight ?? a.weight), start: bl.get(a.id)?.start_date ?? a.planned_start, finish: bl.get(a.id)?.finish_date ?? a.planned_finish, totalQty: Number(a.total_qty) }))
    const start = project!.start_date ?? curveActs.map((c) => c.start).filter(Boolean).sort()[0] ?? r.from
    const end = [r.to, ...curveActs.map((c) => c.finish).filter(Boolean)].sort().at(-1) as string
    const curve = sCurve(curveActs, entries.map((e) => ({ activityId: e.activity_id, date: e.report_date, qty: qty(e) })), start, end, r.to)
    const at = curve.filter((p) => p.date <= r.to).at(-1)
    const weighted = (subset: typeof curveActs) => {
      const s = sCurve(subset, entries.map((e) => ({ activityId: e.activity_id, date: e.report_date, qty: qty(e) })), r.to, r.to)
      return s[0] ?? { planned: 0, actual: 0 }
    }
    const byGroup = (key: (a: typeof acts[number]) => string | null, label: (k: string) => string) => {
      const groups = new Map<string, typeof curveActs>()
      for (const a of acts) { const k = key(a) ?? ''; groups.set(k, [...(groups.get(k) ?? []), curveActs.find((c) => c.id === a.id)!]) }
      return [...groups].map(([k, list]) => ({ name: label(k), ...weighted(list) })).sort((x, y) => (y.actual ?? 0) - (x.actual ?? 0))
    }
    const unitActs = new Set(acts.filter((a) => a.unit === unit).map((a) => a.id))
    const production = Object.values(entries.filter((e) => unitActs.has(e.activity_id) && e.report_date >= r.from).reduce<Record<string, { date: string; qty: number }>>((acc, e) => {
      (acc[e.report_date] ??= { date: e.report_date, qty: 0 }).qty += qty(e); return acc
    }, {})).sort((a, b) => a.date.localeCompare(b.date))
    const manpower = Object.values(d.data.mp.reduce<Record<string, { date: string; headcount: number; man_hours: number }>>((acc, m) => {
      const x = (acc[m.report_date] ??= { date: m.report_date, headcount: 0, man_hours: 0 }); x.headcount += m.headcount; x.man_hours += Number(m.man_hours); return acc
    }, {})).sort((a, b) => a.date.localeCompare(b.date))
    const eq = Object.values(d.data.eq.reduce<Record<string, { code: string; w: number; t: number }>>((acc, e) => {
      const x = (acc[e.equipment_code] ??= { code: e.equipment_code, w: 0, t: 0 }); x.w += Number(e.operating_hours); x.t += Number(e.operating_hours) + Number(e.idle_hours) + Number(e.breakdown_hours); return acc
    }, {})).map((e) => ({ code: e.code, utilization: e.t ? Math.round((e.w * 1000) / e.t) / 10 : 0 }))
    const reasons = Object.entries(entries.filter((e) => e.delay_reason && e.report_date >= r.from).reduce<Record<string, number>>((acc, e) => {
      const k = e.delay_reason!.trim().toLowerCase().replace(/\s+/g, ' ').slice(0, 40); acc[k] = (acc[k] ?? 0) + 1; return acc
    }, {})).sort((a, b) => b[1] - a[1])
    const totalReasons = reasons.reduce((s, [, n]) => s + n, 0)
    let run = 0
    const pareto = reasons.map(([reason, count]) => { run += count; return { reason, count, cumulative_pct: Math.round((run * 1000) / totalReasons) / 10 } })
    const consumption = Object.values(d.data.ledger.reduce<Record<string, { code: string; consumed: number }>>((acc, l) => {
      const code = masters.materials.find((m) => m.id === l.material_id)?.code ?? '?'; (acc[code] ??= { code, consumed: 0 }).consumed += Number(l.consumed_qty); return acc
    }, {}))
    const manHours = manpower.reduce((s, m) => s + m.man_hours, 0)
    const prodTotal = production.reduce((s, p) => s + p.qty, 0)
    let critical = 0, behindCritical = 0, forecast: string | null = null
    try {
      const out = schedule(acts.map((a) => ({ id: a.id, code: a.code, duration: a.original_duration, isMilestone: a.is_milestone, actualStart: a.actual_start, actualFinish: a.actual_finish })),
        d.data.deps.map((x) => ({ predecessorId: x.predecessor_id, successorId: x.successor_id, type: x.type, lag: x.lag_days })),
        { projectStart: start, dataDate: today(), calendar: masters.calendar ? { workDays: masters.calendar.work_days, holidays: masters.calendar.holidays } : DEFAULT_CALENDAR })
      forecast = out.projectFinish
      for (const a of acts) {
        const res = out.activities.get(a.id)
        if (!res?.critical) continue
        critical++
        const b = bl.get(a.id)
        if (b && res.earlyFinish > b.finish_date) behindCritical++
      }
    } catch { /* circular logic is reported on the Schedule page */ }
    const delayed = acts.filter((a) => a.status !== 'completed' && (bl.get(a.id)?.finish_date ?? a.planned_finish ?? '9999') < r.to).length
    const milestones = acts.filter((a) => a.is_milestone && !a.actual_finish && (a.planned_finish ?? '') >= today() && (a.planned_finish ?? '') <= addDays(today(), 60))
    return {
      curve, at, production, prodTotal, manpower, eq, pareto, consumption, manHours, critical, behindCritical, forecast, delayed, milestones,
      areas: byGroup((a) => a.area_id, (k) => masters.areas.find((x) => x.id === k)?.code ?? 'Unassigned'),
      contractors: byGroup((a) => a.contractor_id, (k) => masters.contractors.find((x) => x.id === k)?.name ?? 'Unassigned'),
      todayQty: production.find((p) => p.date === r.to)?.qty ?? 0,
    }
  }, [d.data, acts, unit, r.from, r.to, masters, project])

  if (d.error) return <Alert>{d.error}</Alert>
  if (!d.data || !calc) return <Spinner />
  const c = d.data.counts
  const sv = (calc.at?.actual ?? 0) - (calc.at?.planned ?? 0)
  const units = [...new Set(masters.activities.map((a) => a.unit).filter(Boolean))] as string[]
  const lakh = (v: unknown) => Math.round(Number(v) / 1000) / 100
  return (
    <div className="flex flex-col gap-4">
      <PageHeader title="Dashboards" subtitle={`${project!.name} · ${fresh()}`} />
      <div className="card p-3 flex flex-wrap gap-2 items-end">
        {r.ui}
        <select className="input w-auto" value={area} onChange={(e) => setArea(e.target.value)} aria-label="Area"><option value="">All areas</option>{masters.areas.map((a) => <option key={a.id} value={a.id}>{a.code}</option>)}</select>
        <select className="input w-auto" value={wbs} onChange={(e) => setWbs(e.target.value)} aria-label="WBS"><option value="">All WBS</option>{masters.wbs.map((a) => <option key={a.id} value={a.id}>{a.code} {a.name}</option>)}</select>
        <select className="input w-auto" value={contractor} onChange={(e) => setContractor(e.target.value)} aria-label="Contractor"><option value="">All contractors</option>{masters.contractors.map((a) => <option key={a.id} value={a.id}>{a.name}</option>)}</select>
      </div>
      <div className="grid grid-cols-2 gap-3 md:grid-cols-4 lg:grid-cols-6">
        <Stat label="Planned progress" value={`${(calc.at?.planned ?? 0).toFixed(1)} %`} sub={`at ${r.to}`} />
        <Stat label="Actual progress" value={`${(calc.at?.actual ?? 0).toFixed(1)} %`} sub="weighted, accepted qty" />
        <Stat label="Schedule variance" value={`${sv > 0 ? '+' : ''}${sv.toFixed(1)} pts`} tone={sv < 0 ? 'bad' : 'good'} />
        <Stat label="Forecast finish" value={calc.forecast ?? '—'} tone={project!.completion_date && calc.forecast && calc.forecast > project!.completion_date ? 'bad' : undefined} sub={project!.completion_date ? `contract ${project!.completion_date}` : undefined} />
        <Stat label={`Production (${unit})`} value={calc.prodTotal.toLocaleString('en-IN', { maximumFractionDigits: 1 })} sub={`${calc.todayQty.toLocaleString('en-IN')} on ${r.to}`} />
        <Stat label="Labour productivity" value={calc.manHours ? (calc.prodTotal * 100 / calc.manHours).toFixed(2) : '—'} sub={`${unit} per 100 man-hours (all trades)`} />
        <Stat label="Activities delayed" value={calc.delayed} tone={calc.delayed ? 'bad' : 'good'} sub="past baseline finish, not complete" />
        <Stat label="Critical at risk" value={`${calc.behindCritical} / ${calc.critical}`} tone={calc.behindCritical ? 'bad' : 'good'} sub="critical, forecast > baseline" />
        <Stat label="Pending approvals" value={c[0]} tone={c[0] ? 'warn' : 'good'} />
        <Stat label="Open safety obs." value={c[1]} tone={c[1] ? 'warn' : 'good'} />
        <Stat label="Open NCRs / punch" value={`${c[2]} / ${c[4]}`} tone={c[2] + c[4] ? 'bad' : 'good'} />
        <Stat label="Inspections pending · shortages" value={`${c[3]} · ${c[5]}`} tone={c[5] ? 'bad' : undefined} />
      </div>

      <ChartCard title="S-curve: planned vs actual" caption={`Cumulative weighted physical progress, % · planned = linear over ${project!.approved_baseline_id ? 'approved baseline' : 'current planned'} dates; actual = accepted quantities (approved DPRs) up to ${r.to}. Planned beyond today is the plan, not a forecast.`}
        rows={calc.curve as never} columns={[['date', 'Date'], ['planned', 'Planned %'], ['actual', 'Actual %']]} empty={!calc.curve.length}>
        <LinesChart data={calc.curve as never} x="date" series={[['planned', 'Planned (baseline)'], ['actual', 'Actual']]} unit="%" />
      </ChartCard>
      <div className="grid gap-4 lg:grid-cols-2">
        <ChartCard title={`Daily production (${unit})`} caption={`${r.from} → ${r.to} · accepted quantity of activities measured in ${unit}`} rows={calc.production} columns={[['date', 'Date'], ['qty', unit]]} empty={!calc.production.length}>
          <BarsChart data={calc.production} x="date" series={[['qty', unit]]} />
        </ChartCard>
        <div className="-mt-2 lg:col-span-2 flex gap-2 text-sm items-center"><span>Production unit:</span>{units.map((u) => <button key={u} className={`btn min-h-9 ${u === unit ? 'btn-primary' : 'btn-secondary'}`} onClick={() => setUnit(u)}>{u}</button>)}</div>
        <ChartCard title="Manpower trend" caption={`${r.from} → ${r.to} · headcount per day`} rows={calc.manpower} columns={[['date', 'Date'], ['headcount', 'Headcount'], ['man_hours', 'Man-hours']]} empty={!calc.manpower.length}>
          <LinesChart data={calc.manpower} x="date" series={[['headcount', 'Headcount']]} />
        </ChartCard>
        <ChartCard title="Equipment utilisation" caption={`${r.from} → ${r.to} · working ÷ recorded hours`} rows={calc.eq} columns={[['code', 'Equipment'], ['utilization', 'Utilisation %']]} empty={!calc.eq.length}>
          <BarsChart data={calc.eq} x="code" series={[['utilization', 'Utilisation']]} unit="%" layout="vertical" />
        </ChartCard>
        <ChartCard title="Area-wise progress" caption={`Weighted % at ${r.to}`} rows={calc.areas as never} columns={[['name', 'Area'], ['planned', 'Planned %'], ['actual', 'Actual %']]} empty={!calc.areas.length}>
          <BarsChart data={calc.areas as never} x="name" series={[['planned', 'Planned'], ['actual', 'Actual']]} unit="%" layout="vertical" />
        </ChartCard>
        <ChartCard title="Contractor performance" caption={`Weighted planned vs actual % at ${r.to}`} rows={calc.contractors as never} columns={[['name', 'Contractor'], ['planned', 'Planned %'], ['actual', 'Actual %']]} empty={!calc.contractors.length}>
          <BarsChart data={calc.contractors as never} x="name" series={[['planned', 'Planned'], ['actual', 'Actual']]} unit="%" layout="vertical" />
        </ChartCard>
        <ChartCard title="Delay reasons (Pareto)" caption={`${r.from} → ${r.to} · count of activity entries citing each reason; cumulative % in the table`} rows={calc.pareto} columns={[['reason', 'Reason'], ['count', 'Entries'], ['cumulative_pct', 'Cumulative %']]} empty={!calc.pareto.length}>
          <BarsChart data={calc.pareto.slice(0, 10)} x="reason" series={[['count', 'Entries']]} layout="vertical" />
        </ChartCard>
        <ChartCard title="Material consumption" caption={`${r.from} → ${r.to} · consumed quantity per material (each in its own unit)`} rows={calc.consumption} columns={[['code', 'Material'], ['consumed', 'Consumed']]} empty={!calc.consumption.length}>
          <BarsChart data={calc.consumption} x="code" series={[['consumed', 'Consumed']]} layout="vertical" />
        </ChartCard>
        <ChartCard title="Quantity & billing summary (₹ lakh)" caption="Per BOQ item: contract value vs executed (accepted DPR qty × rate) vs certified vs billed. Execution is not a bill."
          rows={d.data.boq.map((b) => ({ item: b.item_no, contract: formatINR(b.contract_amount), executed: formatINR(b.executed_amount), certified: formatINR(b.certified_amount), billed: formatINR(b.billed_amount) }))}
          columns={[['item', 'Item'], ['contract', 'Contract'], ['executed', 'Executed'], ['certified', 'Certified'], ['billed', 'Billed']]} empty={!d.data.boq.length}>
          <BarsChart data={d.data.boq.map((b) => ({ item: b.item_no, contract: lakh(b.contract_amount), executed: lakh(b.executed_amount), certified: lakh(b.certified_amount), billed: lakh(b.billed_amount) }))}
            x="item" series={[['contract', 'Contract'], ['executed', 'Executed'], ['certified', 'Certified'], ['billed', 'Billed']]} />
        </ChartCard>
      </div>
      <section className="card p-4">
        <h2 className="font-bold mb-2">Upcoming milestones (60 days)</h2>
        {calc.milestones.length ? <ul className="text-sm">{calc.milestones.map((m) => <li key={m.id}>◆ {m.code} {m.name} — {m.planned_finish}</li>)}</ul> : <p className="text-sm text-slate-500">None.</p>}
      </section>
    </div>
  )
}
