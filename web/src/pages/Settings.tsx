import { useEffect, useState } from 'react'
import { useAuth } from '../lib/auth'
import { useProject } from '../lib/project'
import { useLoad, useTheme, must } from '../lib/hooks'
import { supabase, friendlyError } from '../lib/supabase'
import { db, requestPersistentStorage } from '../lib/local'
import { download } from '../lib/csv'
import { ROLE_LABELS, type AppRole } from '../lib/types'
import { Alert, Card, PageHeader, Select, TextInput } from '../components/ui'

const MODULES = ['project', 'members', 'masters', 'schedule', 'boq', 'commercial', 'dpr', 'resources', 'safety', 'quality', 'documents', 'reports']
const LETTER: Record<string, string> = { create: 'C', view: 'V', edit: 'E', submit: 'S', approve: 'A', reject: 'R', export: 'X', delete: 'D' }

export default function Settings() {
  const [theme, setTheme] = useTheme()
  const { profile, reloadProfile, signOut } = useAuth()
  const { project, can } = useProject()
  const [name, setName] = useState(profile?.full_name ?? '')
  const [phone, setPhone] = useState(profile?.phone ?? '')
  const [designation, setDesignation] = useState(profile?.designation ?? '')
  const [msg, setMsg] = useState<{ tone: 'red' | 'green'; text: string } | null>(null)
  const [storage, setStorage] = useState<{ used: number; quota: number; persisted: boolean } | null>(null)
  useEffect(() => { setName(profile?.full_name ?? ''); setPhone(profile?.phone ?? ''); setDesignation(profile?.designation ?? '') }, [profile])
  useEffect(() => {
    void (async () => {
      const est = await navigator.storage?.estimate?.()
      setStorage({ used: est?.usage ?? 0, quota: est?.quota ?? 0, persisted: (await navigator.storage?.persisted?.()) ?? false })
    })()
  }, [])
  const matrix = useLoad(async () => must(supabase.from('role_permissions').select('role,module,action')), [])

  const saveProfile = async () => {
    const { error } = await supabase.from('profiles').update({ full_name: name, phone, designation, updated_at: new Date().toISOString() }).eq('id', profile!.id)
    setMsg(error ? { tone: 'red', text: friendlyError(error) } : { tone: 'green', text: 'Profile saved.' })
    await reloadProfile()
  }
  const exportJson = async () => {
    try {
      const data = await must(supabase.rpc('export_project', { p_project: project!.id }))
      download(`${project!.code}_export_${new Date().toISOString().slice(0, 10)}.json`, JSON.stringify(data, null, 2), 'application/json')
    } catch (e) { setMsg({ tone: 'red', text: friendlyError(e) }) }
  }
  const clearSynced = async () => {
    if (!confirm('Remove cached project data and already-synchronised drafts from this device? Unsynchronised work is kept.')) return
    const d = await db()
    for (const r of await d.getAll('drafts')) if (r.syncState === 'synced') await d.delete('drafts', r.id)
    for (const m of await d.getAll('media')) if (m.state === 'uploaded') await d.delete('media', m.id)
    await d.clear('cache')
    setMsg({ tone: 'green', text: 'Cleared.' })
  }

  const grid = new Map<string, string>()
  for (const r of (matrix.data ?? []) as Array<{ role: string; module: string; action: string }>) grid.set(`${r.role}.${r.module}`, (grid.get(`${r.role}.${r.module}`) ?? '') + LETTER[r.action])
  const order = 'CVESARXD'
  return (
    <div className="flex flex-col gap-4">
      <PageHeader title="Settings" />
      {msg && <Alert tone={msg.tone}>{msg.text}</Alert>}
      <div className="grid gap-4 md:grid-cols-2">
        <Card title="Appearance">
          <Select label="Theme" value={theme} onChange={(e) => setTheme(e.target.value as never)}><option value="system">Follow device</option><option value="light">Light (best in sunlight)</option><option value="dark">Dark</option></Select>
        </Card>
        <Card title="My profile">
          <div className="grid gap-2">
            <TextInput label="Full name" value={name} onChange={(e) => setName(e.target.value)} />
            <TextInput label="Designation" value={designation} onChange={(e) => setDesignation(e.target.value)} />
            <TextInput label="Phone" type="tel" value={phone} onChange={(e) => setPhone(e.target.value)} />
            <div className="flex gap-2"><button className="btn btn-primary" onClick={() => void saveProfile()}>Save</button><button className="btn btn-secondary" onClick={() => void signOut()}>Sign out</button></div>
          </div>
        </Card>
        <Card title="Offline storage on this device">
          {storage && <p className="text-sm mb-2">Using {(storage.used / 1024 / 1024).toFixed(1)} MB of about {(storage.quota / 1024 / 1024 / 1024).toFixed(1)} GB available to the browser. Persistent storage: <b>{storage.persisted ? 'granted' : 'not granted'}</b>.</p>}
          <p className="text-xs text-slate-500 mb-2">Drafts and queued photos stay here until uploaded. The browser may still clear them (for example "clear site data" or low storage), so sync daily. Login sessions use the browser's storage; Google Drive credentials are never stored on the device.</p>
          <div className="flex flex-wrap gap-2">
            {!storage?.persisted && <button className="btn btn-secondary" onClick={() => void requestPersistentStorage().then((p) => setStorage((s) => s && { ...s, persisted: p }))}>Request persistent storage</button>}
            <button className="btn btn-secondary" onClick={() => void clearSynced()}>Clear synchronised data</button>
          </div>
        </Card>
        <Card title="Data export (portability)">
          <p className="text-sm mb-2">Download the complete project — masters, schedule, baselines, BOQ, every DPR revision, registers, approvals, report snapshots and Drive file references — as open JSON. CSV exports are on each register.</p>
          <button className="btn btn-primary" disabled={!project || !(can('project', 'export') || can('reports', 'export'))} onClick={() => void exportJson()}>Export project (JSON)</button>
        </Card>
      </div>
      <Card title="Privacy & security">
        <ul className="list-disc pl-5 text-sm space-y-1">
          <li>Access is enforced by the database (row-level security) for every request; the app only hides buttons you cannot use.</li>
          <li>Camera, microphone and location are used only after you tap the matching button; nothing is recorded in the background and location is not tracked.</li>
          <li>Speech-to-text in Chrome uses Google's online service and is off unless you enable it for a recording.</li>
          <li>Approved reports are immutable snapshots with a SHA-256 checksum; corrections create a new revision with a reason.</li>
          <li>Device time, GPS and photo metadata can be wrong or altered and are not proof of authenticity on their own.</li>
          <li>Retention: files are not deleted from the app; authorised users can request deletion, and administrators act on it under the organisation's policy (see docs/BACKUP_AND_RETENTION.md).</li>
        </ul>
      </Card>
      <Card title="Role & permission matrix">
        <p className="text-xs text-slate-500 mb-2">C create · V view · E edit · S submit · A approve · R reject · X export · D delete. Project administrators can override per project.</p>
        <div className="overflow-x-auto">
          <table className="table text-xs">
            <thead><tr><th>Role</th>{MODULES.map((m) => <th key={m}>{m}</th>)}</tr></thead>
            <tbody>{Object.entries(ROLE_LABELS).map(([role, label]) => (
              <tr key={role}><td className="font-semibold whitespace-nowrap">{label}</td>{MODULES.map((m) => <td key={m} className="font-mono">{[...(grid.get(`${role}.${m}`) ?? '')].sort((a, b) => order.indexOf(a) - order.indexOf(b)).join('') || '—'}</td>)}</tr>
            ))}</tbody>
          </table>
        </div>
      </Card>
      <Card title="About">
        <p className="text-sm">CONSTRUCT DPR — open-source (Apache-2.0). Built with React, Vite, Tailwind CSS, Supabase (PostgreSQL), Recharts, jsPDF, idb and Zod. See THIRD_PARTY_LICENSES.md.</p>
        <p className="text-xs text-slate-500 mt-1">Roles available: {(Object.keys(ROLE_LABELS) as AppRole[]).length}.</p>
      </Card>
    </div>
  )
}
