import { useRef, useState } from 'react'
import { useProject } from '../lib/project'
import { useFavorites, useLoad, useRealtime, must } from '../lib/hooks'
import { supabase, friendlyError } from '../lib/supabase'
import { parseCsv } from '../lib/csv'
import { MasterTable } from '../components/MasterTable'
import { Alert, Card, PageHeader } from '../components/ui'

export default function Activities() {
  const { project, masters, can, reloadMasters } = useProject()
  const { fav, toggle } = useFavorites(project?.id)
  const prog = useLoad(async () => must(supabase.from('activity_progress').select('activity_id,accepted_cum_qty,progress_pct,last_reported_on').eq('project_id', project!.id)), [project!.id])
  useRealtime('dpr_headers', project?.id, () => void prog.reload())
  const by = new Map((prog.data ?? []).map((r) => [r.activity_id as string, r]))
  const fileRef = useRef<HTMLInputElement>(null)
  const [msg, setMsg] = useState<{ tone: 'red' | 'green'; text: string } | null>(null)
  const opts = (rows: Array<{ id: string; code?: string; name: string }>) => rows.map((r) => [r.id, r.code ? `${r.code} — ${r.name}` : r.name] as [string, string])

  const importCsv = async (file: File) => {
    try {
      const rows = parseCsv(await file.text())
      const wbs = new Map(masters.wbs.map((w) => [w.code, w.id]))
      const areas = new Map(masters.areas.map((a) => [a.code, a.id]))
      const boq = new Map(masters.boq.map((b) => [b.item_no, b.id]))
      const bad: string[] = []
      const payload = rows.map((r, i) => {
        if (!r.code || !r.name) bad.push(`row ${i + 2}: code and name are required`)
        const num = (k: string) => (r[k] === undefined || r[k] === '' ? undefined : Number(r[k]))
        const date = (k: string) => (r[k] && /^\d{4}-\d{2}-\d{2}$/.test(r[k]) ? r[k] : r[k] ? (bad.push(`row ${i + 2}: ${k} must be YYYY-MM-DD`), undefined) : undefined)
        return Object.fromEntries(Object.entries({
          project_id: project!.id, code: r.code?.trim(), name: r.name?.trim(), unit: r.unit || undefined, location: r.location || undefined,
          wbs_id: r.wbs_code ? wbs.get(r.wbs_code) ?? (bad.push(`row ${i + 2}: unknown WBS ${r.wbs_code}`), undefined) : undefined,
          area_id: r.area_code ? areas.get(r.area_code) : undefined, boq_item_id: r.boq_item_no ? boq.get(r.boq_item_no) : undefined,
          total_qty: num('total_qty'), weight: num('weight'), original_duration: num('original_duration'),
          planned_start: date('planned_start'), planned_finish: date('planned_finish'),
          is_milestone: r.is_milestone ? /^(1|true|yes|y)$/i.test(r.is_milestone) : undefined, responsible: r.responsible || undefined,
        }).filter(([, v]) => v !== undefined))
      })
      if (bad.length) throw new Error(bad.slice(0, 8).join('; '))
      const { error } = await supabase.from('activities').upsert(payload, { onConflict: 'project_id,code' })
      if (error) throw error
      setMsg({ tone: 'green', text: `Imported ${payload.length} activities (existing codes updated).` })
      await reloadMasters()
    } catch (e) { setMsg({ tone: 'red', text: friendlyError(e) }) }
  }

  return (
    <div className="flex flex-col gap-4">
      <PageHeader title="Activity register" subtitle="Approved quantities, planned dates and accepted progress from approved DPRs."
        actions={can('masters', 'create') && <button className="btn btn-secondary" onClick={() => fileRef.current?.click()}>Import CSV</button>} />
      <input ref={fileRef} type="file" accept=".csv,text/csv" hidden onChange={(e) => { const f = e.target.files?.[0]; if (f) void importCsv(f); e.target.value = '' }} />
      {msg && <Alert tone={msg.tone}>{msg.text}</Alert>}
      <MasterTable title="Activities" table="activities" scope={{ column: 'project_id', value: project!.id }}
        canCreate={can('masters', 'create')} canEdit={can('masters', 'edit')} canDelete={can('masters', 'delete')}
        columns={[
          { key: 'fav', label: '★', readOnly: true, render: (r) => <button className="text-lg" onClick={() => toggle(r.id)} aria-label="Toggle favourite">{fav.includes(r.id) ? '★' : '☆'}</button> },
          { key: 'code', label: 'ID', required: true }, { key: 'name', label: 'Activity', required: true },
          { key: 'wbs_id', label: 'WBS', type: 'select', options: opts(masters.wbs) },
          { key: 'area_id', label: 'Area', type: 'select', options: opts(masters.areas), hideInList: true },
          { key: 'boq_item_id', label: 'BOQ item', type: 'select', options: masters.boq.map((b) => [b.id, `${b.item_no} — ${b.description}`]), hideInList: true },
          { key: 'contractor_id', label: 'Contractor', type: 'select', options: opts(masters.contractors), hideInList: true },
          { key: 'location', label: 'Location', hideInList: true }, { key: 'unit', label: 'Unit' },
          { key: 'total_qty', label: 'Approved qty', type: 'number' }, { key: 'weight', label: 'Weight', type: 'number', hideInList: true },
          { key: 'original_duration', label: 'Dur. (d)', type: 'number' },
          { key: 'planned_start', label: 'Planned start', type: 'date' }, { key: 'planned_finish', label: 'Planned finish', type: 'date' },
          { key: 'actual_start', label: 'Actual start', type: 'date', hideInList: true }, { key: 'actual_finish', label: 'Actual finish', type: 'date', hideInList: true },
          { key: 'is_milestone', label: 'Milestone', type: 'bool', hideInList: true },
          { key: 'constraint_type', label: 'Constraint', type: 'select', options: [['SNET', 'Start no earlier than'], ['FNLT', 'Finish no later than'], ['MSO', 'Must start on'], ['MFO', 'Must finish on']], hideInList: true },
          { key: 'constraint_date', label: 'Constraint date', type: 'date', hideInList: true },
          { key: 'responsible', label: 'Responsible', hideInList: true },
          { key: 'status', label: 'Status', type: 'select', options: [['not_started', 'Not started'], ['in_progress', 'In progress'], ['completed', 'Completed'], ['cancelled', 'Cancelled']] },
          { key: 'progress', label: 'Accepted cum. / %', readOnly: true, render: (r) => { const p = by.get(r.id); return p ? `${Number(p.accepted_cum_qty).toLocaleString('en-IN')} · ${p.progress_pct == null ? '—' : `${Number(p.progress_pct).toFixed(1)} %`}` : '—' } },
        ]} />
      <Card title="CSV import format">
        <p className="text-sm">Columns (header row required): <code>code, name, wbs_code, area_code, boq_item_no, unit, location, total_qty, weight, original_duration, planned_start, planned_finish, is_milestone, responsible</code>. Dates as YYYY-MM-DD. Rows with an existing code update that activity. For Primavera P6, export activities to Excel/CSV and map the columns as described in docs/P6_EXCHANGE.md.</p>
      </Card>
    </div>
  )
}
