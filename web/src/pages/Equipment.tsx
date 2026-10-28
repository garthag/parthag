import { useProject } from '../lib/project'
import { useLoad, useRealtime, must } from '../lib/hooks'
import { supabase } from '../lib/supabase'
import { ChartCard, BarsChart } from '../components/charts'
import { Alert, Card, PageHeader, Stat } from '../components/ui'
import { Expiry } from './ProjectSetup'
import { useRange, fresh } from './_range'

interface Row { report_date: string; equipment_code: string; equipment_type: string; operating_hours: number; idle_hours: number; breakdown_hours: number }

export default function Equipment() {
  const { project, masters } = useProject()
  const r = useRange(30)
  const q = useLoad(async () => must(supabase.from('equipment_daily').select('*').eq('project_id', project!.id).gte('report_date', r.from).lte('report_date', r.to)) as Promise<Row[]>, [project!.id, r.from, r.to])
  useRealtime('dpr_headers', project?.id, () => void q.reload())
  const rows = q.data ?? []
  const by = Object.values(rows.reduce<Record<string, { code: string; working: number; idle: number; breakdown: number }>>((acc, x) => {
    const e = (acc[x.equipment_code] ??= { code: x.equipment_code, working: 0, idle: 0, breakdown: 0 })
    e.working += Number(x.operating_hours); e.idle += Number(x.idle_hours); e.breakdown += Number(x.breakdown_hours); return acc
  }, {})).map((e) => ({ ...e, utilization: Math.round((e.working * 1000) / Math.max(1, e.working + e.idle + e.breakdown)) / 10 }))
  const tot = by.reduce((s, e) => ({ w: s.w + e.working, i: s.i + e.idle, b: s.b + e.breakdown }), { w: 0, i: 0, b: 0 })
  const expiring = masters.equipment.filter((e) => e.certification_expiry && Date.parse(e.certification_expiry) - Date.now() < 15 * 86400000)
  return (
    <div className="flex flex-col gap-4">
      <PageHeader title="Plant & equipment" subtitle="Utilisation = working ÷ (working + idle + breakdown) hours, from approved DPRs." actions={r.ui} />
      {q.error && <Alert>{q.error}</Alert>}
      <div className="grid grid-cols-2 gap-3 md:grid-cols-4">
        <Stat label="Fleet utilisation" value={`${(tot.w * 100 / Math.max(1, tot.w + tot.i + tot.b)).toFixed(1)} %`} />
        <Stat label="Working hours" value={tot.w.toLocaleString('en-IN')} />
        <Stat label="Breakdown hours" value={tot.b.toLocaleString('en-IN')} tone={tot.b ? 'bad' : 'good'} />
        <Stat label="Certificates expiring ≤15 d" value={expiring.length} tone={expiring.length ? 'warn' : 'good'} />
      </div>
      <ChartCard title="Utilisation by equipment" caption={`${r.from} → ${r.to} · % of recorded hours · ${fresh()}`} rows={by} columns={[['code', 'Equipment'], ['working', 'Working h'], ['idle', 'Idle h'], ['breakdown', 'Breakdown h'], ['utilization', 'Utilisation %']]} empty={!by.length}>
        <BarsChart data={by} x="code" series={[['utilization', 'Utilisation']]} unit="%" layout="vertical" />
      </ChartCard>
      <ChartCard title="Hours by equipment" caption="Working vs idle vs breakdown hours" rows={by} columns={[['code', 'Equipment'], ['working', 'Working'], ['idle', 'Idle'], ['breakdown', 'Breakdown']]} empty={!by.length}>
        <BarsChart data={by} x="code" series={[['working', 'Working'], ['idle', 'Idle'], ['breakdown', 'Breakdown']]} unit=" h" />
      </ChartCard>
      <Card title="Register & certificates">
        <table className="table"><thead><tr><th>ID</th><th>Type</th><th>Capacity</th><th>Certificate expiry</th></tr></thead>
          <tbody>{masters.equipment.map((e) => <tr key={e.id}><td>{e.code}</td><td>{e.type}</td><td>{e.rated_capacity}</td><td><Expiry date={e.certification_expiry} /></td></tr>)}</tbody></table>
        <p className="mt-2 text-xs text-slate-500">Edit the register under Projects → Setup → Equipment.</p>
      </Card>
    </div>
  )
}
