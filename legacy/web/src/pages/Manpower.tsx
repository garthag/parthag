import { useProject } from '../lib/project'
import { useLoad, useRealtime, must } from '../lib/hooks'
import { supabase } from '../lib/supabase'
import { ChartCard, LinesChart, BarsChart } from '../components/charts'
import { Alert, PageHeader, Stat } from '../components/ui'
import { useRange, fresh } from './_range'

interface Row { report_date: string; shift: string; trade: string; contractor_id: string | null; area_id: string | null; headcount: number; man_hours: number; absent: number; overtime_hours: number }

export default function Manpower() {
  const { project, masters } = useProject()
  const r = useRange(30)
  const q = useLoad(async () => must(supabase.from('manpower_daily').select('*').eq('project_id', project!.id).gte('report_date', r.from).lte('report_date', r.to).order('report_date')) as Promise<Row[]>, [project!.id, r.from, r.to])
  useRealtime('dpr_headers', project?.id, () => void q.reload())
  const rows = q.data ?? []
  const byDate = Object.values(rows.reduce<Record<string, { date: string; headcount: number; man_hours: number }>>((acc, x) => {
    const d = (acc[x.report_date] ??= { date: x.report_date, headcount: 0, man_hours: 0 })
    d.headcount += x.headcount; d.man_hours += Number(x.man_hours); return acc
  }, {}))
  const group = (key: (x: Row) => string) => Object.entries(rows.reduce<Record<string, number>>((acc, x) => ({ ...acc, [key(x)]: (acc[key(x)] ?? 0) + Number(x.man_hours) }), {}))
    .map(([name, man_hours]) => ({ name, man_hours })).sort((a, b) => b.man_hours - a.man_hours)
  const contractor = (id: string | null) => masters.contractors.find((c) => c.id === id)?.name ?? 'Unassigned'
  const area = (id: string | null) => masters.areas.find((c) => c.id === id)?.code ?? 'Unassigned'
  const days = byDate.length || 1
  return (
    <div className="flex flex-col gap-4">
      <PageHeader title="Manpower" subtitle="Aggregated from approved DPRs by trade, agency, area and shift." actions={r.ui} />
      {q.error && <Alert>{q.error}</Alert>}
      <div className="grid grid-cols-2 gap-3 md:grid-cols-4">
        <Stat label="Average daily headcount" value={Math.round(byDate.reduce((s, d) => s + d.headcount, 0) / days)} />
        <Stat label="Total man-hours" value={Math.round(byDate.reduce((s, d) => s + d.man_hours, 0)).toLocaleString('en-IN')} />
        <Stat label="Overtime hours" value={Math.round(rows.reduce((s, x) => s + Number(x.overtime_hours), 0)).toLocaleString('en-IN')} />
        <Stat label="Absenteeism" value={`${(rows.reduce((s, x) => s + x.absent, 0) * 100 / Math.max(1, rows.reduce((s, x) => s + x.headcount + x.absent, 0))).toFixed(1)} %`} />
      </div>
      <ChartCard title="Daily headcount" caption={`${r.from} → ${r.to} · people per day (all shifts) · ${fresh()}`} rows={byDate} columns={[['date', 'Date'], ['headcount', 'Headcount'], ['man_hours', 'Man-hours']]} empty={!byDate.length}>
        <LinesChart data={byDate} x="date" series={[['headcount', 'Headcount']]} />
      </ChartCard>
      <div className="grid gap-4 md:grid-cols-2">
        {([['By trade', (x: Row) => x.trade], ['By agency', (x: Row) => contractor(x.contractor_id)], ['By area', (x: Row) => area(x.area_id)], ['By shift', (x: Row) => x.shift]] as const).map(([t, f]) => {
          const g = group(f)
          return (
            <ChartCard key={t} title={`Man-hours ${t.toLowerCase()}`} caption={`${r.from} → ${r.to} · man-hours incl. overtime`} rows={g} columns={[['name', t.replace('By ', '')], ['man_hours', 'Man-hours']]} empty={!g.length}>
              <BarsChart data={g.slice(0, 12)} x="name" series={[['man_hours', 'Man-hours']]} layout="vertical" />
            </ChartCard>
          )
        })}
      </div>
    </div>
  )
}
