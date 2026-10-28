import { useEffect, useState } from 'react'
import { useProject } from '../lib/project'
import { supabase, friendlyError } from '../lib/supabase'
import { useLoad, must } from '../lib/hooks'
import { ROLE_LABELS, type AppRole } from '../lib/types'
import { formatINR } from '../lib/calc/money'
import { MasterTable, type Row } from '../components/MasterTable'
import { Alert, Badge, Card, Modal, NumberInput, PageHeader, Select, Tabs, TextArea, TextInput } from '../components/ui'

type Tab = 'details' | 'areas' | 'wbs' | 'boq' | 'contractors' | 'equipment' | 'materials' | 'members' | 'calendar'

export default function ProjectSetup() {
  const { project, can } = useProject()
  const [tab, setTab] = useState<Tab>('details')
  if (!project) return null
  const m = (a: 'create' | 'edit' | 'delete') => can('masters', a)
  return (
    <div>
      <PageHeader title="Project setup" subtitle={`${project.code} · ${project.name}`} />
      <Tabs value={tab} onChange={setTab} tabs={[['details', 'Details'], ['areas', 'Sites & areas'], ['wbs', 'WBS'], ['boq', 'BOQ'],
        ['contractors', 'Contractors'], ['equipment', 'Equipment'], ['materials', 'Materials'], ['members', 'Members & roles'], ['calendar', 'Calendar']]} />
      {tab === 'details' && <Details />}
      {tab === 'areas' && (
        <MasterTable title="Areas, units & systems" table="areas" scope={{ column: 'project_id', value: project.id }}
          canCreate={m('create')} canEdit={m('edit')} canDelete={m('delete')} defaults={{ kind: 'area' }}
          columns={[{ key: 'code', label: 'Code', required: true }, { key: 'name', label: 'Name', required: true },
            { key: 'kind', label: 'Level', type: 'select', options: [['area', 'Area'], ['unit', 'Unit'], ['system', 'System']] }]} />
      )}
      {tab === 'wbs' && (
        <MasterTable title="Work breakdown structure" table="wbs_nodes" scope={{ column: 'project_id', value: project.id }} orderBy="sort_order"
          canCreate={m('create')} canEdit={m('edit')} canDelete={m('delete')}
          columns={[{ key: 'code', label: 'WBS code', required: true }, { key: 'name', label: 'Name', required: true },
            { key: 'sort_order', label: 'Order', type: 'number' }, { key: 'weight', label: 'Weight', type: 'number' }]} />
      )}
      {tab === 'boq' && <Boq />}
      {tab === 'contractors' && (
        <MasterTable title="Contractors & agencies (organisation-wide)" table="contractors" orderBy="name" scope={{ column: 'organization_id', value: project.organization_id }}
          canCreate canEdit canDelete={false} defaults={{ kind: 'subcontractor' }}
          columns={[{ key: 'name', label: 'Name', required: true }, { key: 'kind', label: 'Type', type: 'select', options: [['main_contractor', 'Main contractor'], ['subcontractor', 'Subcontractor'], ['supplier', 'Supplier'], ['consultant', 'Consultant'], ['agency', 'Labour agency'], ['client', 'Client']] },
            { key: 'contact', label: 'Contact' }]} />
      )}
      {tab === 'equipment' && (
        <MasterTable title="Plant & equipment register" table="equipment" scope={{ column: 'project_id', value: project.id }}
          canCreate={m('create')} canEdit={m('edit')} canDelete={m('delete')}
          columns={[{ key: 'code', label: 'Equipment ID', required: true }, { key: 'type', label: 'Type', required: true },
            { key: 'rated_capacity', label: 'Rated capacity' }, { key: 'certification_expiry', label: 'Inspection / certificate expiry', type: 'date',
              render: (r) => <Expiry date={r.certification_expiry as string | null} /> }]} />
      )}
      {tab === 'materials' && (
        <MasterTable title="Material master" table="materials" scope={{ column: 'project_id', value: project.id }}
          canCreate={m('create')} canEdit={m('edit')} canDelete={m('delete')}
          columns={[{ key: 'code', label: 'Code', required: true }, { key: 'description', label: 'Description', required: true },
            { key: 'specification', label: 'Specification', hideInList: true }, { key: 'grade', label: 'Grade' },
            { key: 'unit', label: 'Unit', required: true }, { key: 'reorder_level', label: 'Re-order level', type: 'number' }]} />
      )}
      {tab === 'members' && <Members />}
      {tab === 'calendar' && <Calendar />}
    </div>
  )
}

export function Expiry({ date }: { date: string | null }) {
  if (!date) return <>—</>
  const days = Math.round((Date.parse(date) - Date.now()) / 86400000)
  return <span className="inline-flex gap-1 items-center">{date} {days < 0 ? <Badge tone="red">Expired</Badge> : days <= 15 ? <Badge tone="amber">{days} d left</Badge> : null}</span>
}

function Details() {
  const { project, can, reloadProjects } = useProject()
  const [f, setF] = useState(project!)
  const [msg, setMsg] = useState<{ tone: 'red' | 'green'; text: string } | null>(null)
  useEffect(() => setF(project!), [project])
  const editable = can('project', 'edit')
  const set = (k: string, v: unknown) => setF((p) => ({ ...p, [k]: v }))
  const save = async () => {
    const { id, version, name, consultant, work_order_no, site_address, start_date, completion_date, timezone, currency, report_header, report_footer, allow_self_approval, money_decimals, qty_decimals } = f
    const { data, error } = await supabase.from('projects').update({ name, consultant, work_order_no, site_address, start_date: start_date || null, completion_date: completion_date || null,
      timezone, currency, report_header, report_footer, allow_self_approval, money_decimals, qty_decimals }).eq('id', id).eq('version', version).select('id')
    if (error) return setMsg({ tone: 'red', text: friendlyError(error) })
    if (!data?.length) return setMsg({ tone: 'red', text: 'Someone else changed the project. Reload the page and try again.' })
    setMsg({ tone: 'green', text: 'Saved.' })
    await reloadProjects()
  }
  return (
    <Card>
      <fieldset disabled={!editable} className="grid gap-3 sm:grid-cols-2">
        <TextInput label="Project code" value={f.code} disabled hint="Code is fixed; it is part of every DPR number." />
        <TextInput label="Project name" value={f.name} onChange={(e) => set('name', e.target.value)} />
        <TextInput label="Work order / contract no." value={f.work_order_no ?? ''} onChange={(e) => set('work_order_no', e.target.value)} />
        <TextInput label="Consultant" value={f.consultant ?? ''} onChange={(e) => set('consultant', e.target.value)} />
        <TextInput label="Site address" className="sm:col-span-2" value={f.site_address ?? ''} onChange={(e) => set('site_address', e.target.value)} />
        <TextInput label="Start date" type="date" value={f.start_date ?? ''} onChange={(e) => set('start_date', e.target.value)} />
        <TextInput label="Contract completion date" type="date" value={f.completion_date ?? ''} onChange={(e) => set('completion_date', e.target.value)} />
        <TextInput label="Reporting timezone" value={f.timezone} onChange={(e) => set('timezone', e.target.value)} hint="IANA name, e.g. Asia/Kolkata. Timestamps are stored in UTC." />
        <TextInput label="Currency" value={f.currency} maxLength={3} onChange={(e) => set('currency', e.target.value.toUpperCase())} />
        <NumberInput label="Money decimals" value={f.money_decimals} onChange={(v) => set('money_decimals', v ?? 2)} max={4} />
        <NumberInput label="Quantity decimals" value={f.qty_decimals} onChange={(v) => set('qty_decimals', v ?? 3)} max={6} />
        <TextArea label="Report header" className="sm:col-span-2" value={f.report_header ?? ''} onChange={(e) => set('report_header', e.target.value)} />
        <TextArea label="Report footer" className="sm:col-span-2" value={f.report_footer ?? ''} onChange={(e) => set('report_footer', e.target.value)} />
        <label className="flex items-center gap-3 sm:col-span-2">
          <input type="checkbox" className="h-6 w-6" checked={f.allow_self_approval} onChange={(e) => set('allow_self_approval', e.target.checked)} />
          <span><b>Allow self-approval</b> — permit a reviewer to approve a DPR they prepared or submitted (off by default; recorded in the audit trail).</span>
        </label>
      </fieldset>
      {msg && <div className="mt-3"><Alert tone={msg.tone}>{msg.text}</Alert></div>}
      {editable && <button className="btn btn-primary mt-3" onClick={() => void save()}>Save project</button>}
    </Card>
  )
}

function Boq() {
  const { project, can } = useProject()
  const [rev, setRev] = useState<Row | null>(null)
  const [qty, setQty] = useState<number | null>(null)
  const [rate, setRate] = useState<number | null>(null)
  const [reason, setReason] = useState('')
  const [msg, setMsg] = useState<string | null>(null)
  const [reloadFn, setReloadFn] = useState<() => void>(() => () => {})
  const status = useLoad(async () => must(supabase.from('boq_status').select('id,contract_amount,executed_amount,certified_amount,billed_amount,executed_qty,balance_qty,variation_amount').eq('project_id', project!.id)), [project!.id])
  const by = new Map((status.data ?? []).map((r) => [r.id as string, r]))
  const total = (k: string) => (status.data ?? []).reduce((s, r) => s + Number(r[k as keyof typeof r] ?? 0), 0)
  const submit = async () => {
    try {
      await must(supabase.rpc('revise_boq_item', { p_item: rev!.id, p_new_qty: qty, p_new_rate: rate, p_reason: reason }))
      setRev(null)
      reloadFn()
      await status.reload()
    } catch (e) { setMsg(friendlyError(e)) }
  }
  return (
    <div className="flex flex-col gap-3">
      <div className="grid grid-cols-2 gap-3 md:grid-cols-4">
        {[['Contract value', 'contract_amount'], ['Executed (accepted DPR qty × rate)', 'executed_amount'], ['Certified', 'certified_amount'], ['Billed', 'billed_amount']].map(([l, k]) => (
          <div key={k} className="card p-3"><div className="text-xs font-semibold text-slate-500">{l}</div><div className="text-lg font-extrabold tabular-nums">{formatINR(total(k))}</div></div>
        ))}
      </div>
      <Alert tone="blue">Physical progress, measurement, certification and billing are tracked separately. Approved DPR quantities never become a bill automatically. Tender quantity and rate change only through an authorised revision with a reason.</Alert>
      <MasterTable title="Bill of quantities" table="boq_items" orderBy="item_no" scope={{ column: 'project_id', value: project!.id }}
        canCreate={can('boq', 'create')} canEdit={can('boq', 'edit')} canDelete={false}
        columns={[
          { key: 'item_no', label: 'Item', required: true, createOnly: true }, { key: 'description', label: 'Description', required: true },
          { key: 'specification', label: 'Specification', hideInList: true }, { key: 'unit', label: 'Unit', required: true, createOnly: true },
          { key: 'tender_qty', label: 'Tender qty', type: 'number', createOnly: true }, { key: 'revised_qty', label: 'Approved revised qty', type: 'number', readOnly: true },
          { key: 'rate', label: 'Rate (₹)', type: 'number', createOnly: true, render: (r) => formatINR(r.rate as number) },
          { key: 'executed', label: 'Executed qty', readOnly: true, type: 'number', render: (r) => Number(by.get(r.id)?.executed_qty ?? 0).toLocaleString('en-IN') },
          { key: 'measured_qty', label: 'Measured qty', type: 'number' }, { key: 'certified_qty', label: 'Certified qty', type: 'number' },
          { key: 'billed_qty', label: 'Billed qty', type: 'number' },
          { key: 'amount', label: 'Contract amount', readOnly: true, render: (r) => formatINR(by.get(r.id)?.contract_amount as number) },
        ]}
        extraActions={(r, reload) => can('boq', 'approve') && (
          <button className="btn btn-ghost px-2" onClick={() => { setReloadFn(() => reload); setRev(r); setQty(Number(r.revised_qty ?? r.tender_qty)); setRate(Number(r.rate)); setReason(''); setMsg(null) }}>Revise</button>
        )} />
      <Modal open={!!rev} onClose={() => setRev(null)} title={`Authorised revision — ${rev?.item_no ?? ''}`}>
        <div className="grid gap-3">
          {msg && <Alert>{msg}</Alert>}
          <NumberInput label="New approved quantity" value={qty} onChange={setQty} />
          <NumberInput label="New rate (₹)" value={rate} onChange={setRate} />
          <TextArea label="Reason / change-order reference" value={reason} onChange={(e) => setReason(e.target.value)} required />
          <p className="text-xs text-slate-500">Previously approved DPRs and their PDFs are snapshots and will not change.</p>
          <button className="btn btn-primary" disabled={!reason.trim() || qty == null || rate == null} onClick={() => void submit()}>Record revision</button>
        </div>
      </Modal>
    </div>
  )
}

function Members() {
  const { project, can, masters, reloadMasters } = useProject()
  const [email, setEmail] = useState('')
  const [role, setRole] = useState<AppRole>('site_engineer')
  const [msg, setMsg] = useState<{ tone: 'red' | 'green'; text: string } | null>(null)
  const add = async () => {
    try {
      await must(supabase.rpc('add_project_member', { p_project: project!.id, p_email: email, p_role: role }))
      setMsg({ tone: 'green', text: `${email} added as ${ROLE_LABELS[role]}.` })
      setEmail('')
      await reloadMasters()
    } catch (e) { setMsg({ tone: 'red', text: friendlyError(e) }) }
  }
  const toggle = async (id: string, active: boolean) => {
    try { await must(supabase.rpc('set_project_member_active', { p_member: id, p_active: active })); await reloadMasters() } catch (e) { setMsg({ tone: 'red', text: friendlyError(e) }) }
  }
  return (
    <Card title="Members & roles">
      {can('members', 'create') && (
        <div className="grid gap-3 sm:grid-cols-[1fr_1fr_auto] items-end mb-4">
          <TextInput label="E-mail of a registered user" type="email" value={email} onChange={(e) => setEmail(e.target.value)} />
          <Select label="Project role" value={role} onChange={(e) => setRole(e.target.value as AppRole)}>
            {Object.entries(ROLE_LABELS).filter(([k]) => k !== 'org_admin').map(([k, l]) => <option key={k} value={k}>{l}</option>)}
          </Select>
          <button className="btn btn-primary" disabled={!email} onClick={() => void add()}>Add member</button>
        </div>
      )}
      {msg && <Alert tone={msg.tone}>{msg.text}</Alert>}
      <p className="text-xs text-slate-500 my-2">Database roles do not grant Google Drive access by themselves. Files are viewed through the app (permission-checked). Sharing the Drive folder directly is a separate step under Documents.</p>
      <table className="table">
        <thead><tr><th>Name</th><th>E-mail</th><th>Role</th><th>Status</th><th /></tr></thead>
        <tbody>
          {masters.members.map((m) => (
            <tr key={m.id}>
              <td>{m.profiles?.full_name}</td><td>{m.profiles?.email}</td><td>{ROLE_LABELS[m.role as AppRole] ?? m.role}</td>
              <td>{m.active ? <Badge tone="green">Active</Badge> : <Badge>Inactive</Badge>}</td>
              <td>{can('members', 'delete') && <button className="btn btn-ghost px-2" onClick={() => void toggle(m.id, !m.active)}>{m.active ? 'Deactivate' : 'Reactivate'}</button>}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </Card>
  )
}

const DAYS = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat']
function Calendar() {
  const { project, can, reloadMasters } = useProject()
  const cal = useLoad(async () => must(supabase.from('project_calendars').select('*').eq('project_id', project!.id).eq('is_default', true).single()), [project!.id])
  const [days, setDays] = useState<number[]>([])
  const [holidays, setHolidays] = useState('')
  const [hours, setHours] = useState<number | null>(8)
  const [msg, setMsg] = useState<{ tone: 'red' | 'green'; text: string } | null>(null)
  useEffect(() => {
    if (cal.data) { setDays(cal.data.work_days); setHolidays((cal.data.holidays as string[]).join('\n')); setHours(Number(cal.data.hours_per_day)) }
  }, [cal.data])
  const save = async () => {
    const hol = holidays.split(/[\s,]+/).filter(Boolean)
    if (hol.some((h) => !/^\d{4}-\d{2}-\d{2}$/.test(h))) return setMsg({ tone: 'red', text: 'Holidays must be dates like 2026-11-08, one per line.' })
    const { data, error } = await supabase.from('project_calendars').update({ work_days: days, holidays: hol, hours_per_day: hours }).eq('id', cal.data!.id).eq('version', cal.data!.version).select('id')
    if (error || !data?.length) return setMsg({ tone: 'red', text: error ? friendlyError(error) : 'Changed by someone else — reload.' })
    setMsg({ tone: 'green', text: 'Calendar saved. Re-run the schedule to apply it.' })
    await cal.reload()
    await reloadMasters()
  }
  if (!cal.data) return cal.error ? <Alert>{cal.error}</Alert> : null
  const editable = can('schedule', 'edit')
  return (
    <Card title={`Working calendar — ${cal.data.name}`}>
      <fieldset disabled={!editable} className="grid gap-3">
        <div>
          <span className="label">Working days</span>
          <div className="flex flex-wrap gap-2">
            {DAYS.map((d, i) => (
              <label key={d} className={`btn ${days.includes(i) ? 'btn-primary' : 'btn-secondary'}`}>
                <input type="checkbox" className="sr-only" checked={days.includes(i)} onChange={() => setDays((x) => x.includes(i) ? x.filter((y) => y !== i) : [...x, i].sort())} />{d}
              </label>
            ))}
          </div>
        </div>
        <NumberInput label="Hours per day" value={hours} onChange={setHours} max={24} />
        <TextArea label="Holidays (one date per line, YYYY-MM-DD)" value={holidays} onChange={(e) => setHolidays(e.target.value)} rows={5} />
        <div className="text-sm"><span className="label">Shifts</span>{(cal.data.shifts as Array<{ name: string; start: string; end: string }>).map((s) => `${s.name} ${s.start}–${s.end}`).join(' · ')}</div>
      </fieldset>
      {msg && <div className="mt-3"><Alert tone={msg.tone}>{msg.text}</Alert></div>}
      {editable && <button className="btn btn-primary mt-3" onClick={() => void save()}>Save calendar</button>}
    </Card>
  )
}
