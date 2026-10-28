import { useState } from 'react'
import { useForm } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import { z } from 'zod'
import { Link, useNavigate } from 'react-router-dom'
import { useProject } from '../lib/project'
import { useLoad, must } from '../lib/hooks'
import { supabase, friendlyError } from '../lib/supabase'
import { Alert, Badge, Card, Empty, Modal, PageHeader, TextInput, Select } from '../components/ui'

const schema = z.object({
  organization_id: z.string().min(1, 'Choose or create an organisation'),
  code: z.string().regex(/^[A-Za-z0-9._-]{1,30}$/, 'Letters, digits, . _ - only (max 30)'),
  name: z.string().trim().min(3, 'Enter the project name'),
  work_order_no: z.string().optional(),
  consultant: z.string().optional(),
  site_address: z.string().optional(),
  start_date: z.string().optional(),
  completion_date: z.string().optional(),
}).refine((v) => !v.start_date || !v.completion_date || v.completion_date >= v.start_date, { path: ['completion_date'], message: 'Completion must be after start' })
type Form = z.infer<typeof schema>

export default function Projects() {
  const { projects, project, setProjectId, reloadProjects } = useProject()
  const [q, setQ] = useState('')
  const [open, setOpen] = useState(false)
  const [err, setErr] = useState<string | null>(null)
  const [newOrg, setNewOrg] = useState('')
  const nav = useNavigate()
  const orgs = useLoad(async () => must(supabase.from('organizations').select('id,name').order('name')), [])
  const { register, handleSubmit, formState: { errors, isSubmitting }, setValue } = useForm<Form>({ resolver: zodResolver(schema) })

  const filtered = projects.filter((p) => `${p.code} ${p.name}`.toLowerCase().includes(q.toLowerCase()))

  const createOrg = async () => {
    try {
      const id = await must(supabase.rpc('create_organization', { p_name: newOrg }))
      await orgs.reload()
      setValue('organization_id', id as string)
      setNewOrg('')
    } catch (e) { setErr(friendlyError(e)) }
  }

  const onSubmit = async (f: Form) => {
    setErr(null)
    try {
      const { organization_id, ...rest } = f
      const id = await must(supabase.rpc('create_project', { p_org: organization_id, p: rest }))
      await reloadProjects()
      setProjectId(id as string)
      setOpen(false)
      nav('/projects/setup')
    } catch (e) { setErr(friendlyError(e)) }
  }

  return (
    <div className="flex flex-col gap-4">
      <PageHeader title="Projects" subtitle="Organisation → Client → Contract → Project → Site → Area → WBS → Activity"
        actions={<button className="btn btn-primary" onClick={() => setOpen(true)}>＋ New project</button>} />
      <input className="input" type="search" placeholder="Search projects by code or name…" value={q} onChange={(e) => setQ(e.target.value)} aria-label="Search projects" />
      {!filtered.length && <Empty title="No projects found">Create a project, load the demo from Home, or ask an administrator to add you.</Empty>}
      <div className="grid gap-3 md:grid-cols-2">
        {filtered.map((p) => (
          <Card key={p.id} className={p.id === project?.id ? 'ring-3 ring-brand-500' : ''}>
            <div className="flex items-start justify-between gap-2">
              <div>
                <div className="text-xs font-bold text-slate-500">{p.code}</div>
                <div className="text-lg font-bold">{p.name}</div>
                <div className="text-sm text-slate-500">{p.start_date ?? '—'} → {p.completion_date ?? '—'} · {p.currency} · {p.timezone}</div>
              </div>
              <Badge tone={p.status === 'active' ? 'green' : 'gray'}>{p.status}</Badge>
            </div>
            <div className="mt-3 flex gap-2">
              <button className="btn btn-primary" onClick={() => { setProjectId(p.id); nav('/') }}>{p.id === project?.id ? 'Current' : 'Open'}</button>
              <button className="btn btn-secondary" onClick={() => { setProjectId(p.id); nav('/projects/setup') }}>Setup</button>
            </div>
          </Card>
        ))}
      </div>
      <p className="text-sm text-slate-500">Need sample data? <Link to="/" className="underline">Home → Load demo project</Link> (only shown when you have no projects) or call <code>load_demo_project()</code>.</p>

      <Modal open={open} onClose={() => setOpen(false)} title="New project">
        <form onSubmit={handleSubmit(onSubmit)} className="grid gap-3">
          {err && <Alert>{err}</Alert>}
          <Select label="Organisation" {...register('organization_id')} error={errors.organization_id?.message}>
            <option value="">— choose —</option>
            {orgs.data?.map((o) => <option key={o.id} value={o.id}>{o.name}</option>)}
          </Select>
          <div className="flex gap-2 items-end">
            <TextInput className="flex-1" label="…or create organisation" value={newOrg} onChange={(e) => setNewOrg(e.target.value)} />
            <button type="button" className="btn btn-secondary" disabled={!newOrg.trim()} onClick={() => void createOrg()}>Create</button>
          </div>
          <p className="text-xs text-slate-500">Only organisation administrators can create projects; creating an organisation makes you its administrator.</p>
          <div className="grid grid-cols-2 gap-3">
            <TextInput label="Project code" placeholder="BF2-SKB" {...register('code')} error={errors.code?.message} />
            <TextInput label="Work order no." {...register('work_order_no')} />
          </div>
          <TextInput label="Project name" {...register('name')} error={errors.name?.message} />
          <TextInput label="Consultant" {...register('consultant')} />
          <TextInput label="Site address" {...register('site_address')} />
          <div className="grid grid-cols-2 gap-3">
            <TextInput label="Start date" type="date" {...register('start_date')} />
            <TextInput label="Contract completion" type="date" {...register('completion_date')} error={errors.completion_date?.message} />
          </div>
          <p className="text-xs text-slate-500">Defaults: INR, metric units, Asia/Kolkata reporting timezone, 6-day calendar — change them in Setup.</p>
          <button className="btn btn-primary" disabled={isSubmitting}>{isSubmitting ? 'Creating…' : 'Create project'}</button>
        </form>
      </Modal>
    </div>
  )
}
