import { useEffect, useState } from 'react'
import { Link, useNavigate, useSearchParams } from 'react-router-dom'
import { useProject } from '../lib/project'
import { useLoad, useRealtime, useSyncStatus, must } from '../lib/hooks'
import { supabase, friendlyError } from '../lib/supabase'
import { drafts, media, outbox, type DraftRecord, type MediaItem, type OutboxItem } from '../lib/local'
import { sync } from '../lib/sync'
import { download, toCsv } from '../lib/csv'
import type { DprHeader, DprStatus } from '../lib/types'
import { Alert, Badge, Card, DprStatusBadge, Empty, PageHeader, Spinner, Tabs } from '../components/ui'

export default function DprList() {
  const { project, can } = useProject()
  const [params, setParams] = useSearchParams()
  const tab = (params.get('tab') as 'server' | 'device') ?? 'server'
  const nav = useNavigate()
  return (
    <div>
      <PageHeader title="Daily progress reports" subtitle={project?.name}
        actions={can('dpr', 'create') && <button className="btn btn-primary" onClick={() => nav('/dpr/new')}>＋ New DPR</button>} />
      <Tabs value={tab} onChange={(t) => setParams({ tab: t })} tabs={[['server', 'All reports'], ['device', 'On this device']]} />
      {tab === 'server' ? <ServerList /> : <DeviceList />}
    </div>
  )
}

function ServerList() {
  const { project } = useProject()
  const [status, setStatus] = useState<DprStatus | ''>('')
  const [from, setFrom] = useState('')
  const [to, setTo] = useState('')
  const list = useLoad(async () => {
    let q = supabase.from('dpr_headers').select('id,dpr_no,report_date,shift,status,revision,submitted_at,approved_at,prepared_by,profiles!dpr_headers_prepared_by_fkey(full_name)')
      .eq('project_id', project!.id).order('report_date', { ascending: false }).order('revision', { ascending: false }).limit(200)
    if (status) q = q.eq('status', status)
    if (from) q = q.gte('report_date', from)
    if (to) q = q.lte('report_date', to)
    return must(q) as unknown as Promise<Array<DprHeader & { profiles: { full_name: string } | null }>>
  }, [project!.id, status, from, to])
  useRealtime('dpr_headers', project?.id, () => void list.reload())
  return (
    <div className="flex flex-col gap-3">
      <div className="grid grid-cols-2 gap-2 sm:grid-cols-4">
        <select className="input" value={status} onChange={(e) => setStatus(e.target.value as DprStatus)} aria-label="Status filter">
          <option value="">All statuses</option>{['draft', 'submitted', 'under_review', 'approved', 'rejected'].map((s) => <option key={s} value={s}>{s.replace('_', ' ')}</option>)}
        </select>
        <input className="input" type="date" value={from} onChange={(e) => setFrom(e.target.value)} aria-label="From date" />
        <input className="input" type="date" value={to} onChange={(e) => setTo(e.target.value)} aria-label="To date" />
        <button className="btn btn-secondary" disabled={!list.data?.length} onClick={() => download(`${project!.code}_dprs.csv`, toCsv(list.data!.map((d) => ({ ...d, profiles: d.profiles?.full_name })) as never))}>Export CSV</button>
      </div>
      {list.error && <Alert>{list.error}</Alert>}
      {list.loading && !list.data ? <Spinner /> : !list.data?.length ? <Empty title="No reports match" /> : (
        <ul className="flex flex-col gap-2">
          {list.data.map((d) => (
            <li key={d.id}>
              <Link to={['draft', 'rejected'].includes(d.status) ? `/dpr/${d.id}` : `/dpr/${d.id}`} className="card flex flex-wrap items-center justify-between gap-2 p-3 hover:ring-2 hover:ring-brand-500">
                <div><div className="font-bold">{d.dpr_no}{d.revision ? ` · Rev ${d.revision}` : ''}</div>
                  <div className="text-sm text-slate-500">{d.report_date} · {d.shift} shift · {d.profiles?.full_name}</div></div>
                <DprStatusBadge status={d.status} revision={d.revision} />
              </Link>
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}

const SYNC_LABEL: Record<string, ['gray' | 'blue' | 'green' | 'red' | 'amber', string]> = {
  local: ['amber', 'Saved locally'], queued: ['amber', 'Waiting to synchronise'], syncing: ['blue', 'Synchronising'], synced: ['green', 'Synchronised'],
  failed: ['red', 'Sync failed'], conflict: ['red', 'Conflict requires review'],
}

function DeviceList() {
  const { project } = useProject()
  const status = useSyncStatus()
  const [items, setItems] = useState<DraftRecord[]>([])
  const [ops, setOps] = useState<OutboxItem[]>([])
  const [files, setFiles] = useState<MediaItem[]>([])
  const [msg, setMsg] = useState<string | null>(null)
  useEffect(() => {
    void drafts.byProject(project!.id).then((d) => setItems(d.sort((a, b) => b.savedAt - a.savedAt)))
    void outbox.all().then(setOps)
    void media.all().then((m) => setFiles(m.filter((x) => x.projectId === project!.id && x.state !== 'uploaded')))
  }, [project, status])
  const act = async (f: () => Promise<void>) => { try { setMsg(null); await f() } catch (e) { setMsg(friendlyError(e)) } }
  return (
    <div className="flex flex-col gap-4">
      <Alert tone="blue">Reports and files saved on this device are kept in the browser's storage until they reach the server. Browsers can clear this storage (for example when the phone runs low on space or site data is cleared), so synchronise regularly — the device is not a backup.</Alert>
      {msg && <Alert>{msg}</Alert>}
      <Card title={`Reports on this device (${items.length})`}>
        {!items.length ? <Empty title="Nothing stored on this device" /> : (
          <ul className="flex flex-col gap-2">
            {items.map((r) => {
              const op = ops.find((o) => o.dprId === r.id)
              return (
                <li key={r.id} className="rounded-lg border border-slate-200 dark:border-slate-700 p-3">
                  <div className="flex flex-wrap items-center justify-between gap-2">
                    <div><b>{r.draft.dpr_no ?? 'New DPR (not yet numbered)'}</b> <span className="text-sm text-slate-500">{r.draft.report_date} · {r.draft.shift} · edited {new Date(r.savedAt).toLocaleString()}</span></div>
                    <Badge tone={SYNC_LABEL[r.syncState][0]}>{SYNC_LABEL[r.syncState][1]}</Badge>
                  </div>
                  {(r.error || op?.lastError) && <p className="mt-1 text-sm text-red-700 dark:text-red-400">{r.error ?? op?.lastError}</p>}
                  <div className="mt-2 flex flex-wrap gap-2">
                    <Link className="btn btn-secondary" to={`/dpr/edit/${r.id}`}>Open</Link>
                    {r.syncState === 'local' && <button className="btn btn-primary" onClick={() => void act(() => sync.queueSave(r.draft))}>Send to server</button>}
                    {r.syncState === 'failed' && <button className="btn btn-primary" onClick={() => void act(() => sync.queueSave(r.draft))}>Retry</button>}
                    {r.syncState === 'conflict' && (
                      <>
                        <button className="btn btn-primary" onClick={() => void act(() => sync.resolveKeepMine(r.id))}>Keep my version (overwrite server)</button>
                        <button className="btn btn-secondary" onClick={() => void act(async () => { if (confirm('Discard your local changes and use the server copy?')) await sync.resolveUseServer(r.id) })}>Use server version</button>
                      </>
                    )}
                    {r.syncState === 'synced' && <button className="btn btn-ghost" onClick={() => void act(async () => { await drafts.delete(r.id); await sync.refreshCounts() })}>Remove local copy</button>}
                  </div>
                </li>
              )
            })}
          </ul>
        )}
      </Card>
      <Card title={`Files waiting for upload (${files.length})`}>
        {!files.length ? <Empty title="No files waiting" /> : (
          <ul className="flex flex-col gap-2">
            {files.map((f) => (
              <li key={f.id} className="flex flex-wrap items-center justify-between gap-2 rounded border border-slate-200 dark:border-slate-700 p-2 text-sm">
                <span>{f.category} · {f.caption || f.filename} · {(f.size / 1024 / 1024).toFixed(1)} MB</span>
                <span className="flex items-center gap-2">
                  <Badge tone={f.state === 'failed' ? 'red' : f.state === 'uploading' ? 'blue' : 'amber'}>{f.state === 'failed' ? 'Upload failed' : f.state === 'uploading' ? `Uploading ${Math.round((sync.uploadProgress.get(f.id) ?? 0) * 100)}%` : 'Waiting'}</Badge>
                  {f.error && <span className="text-red-700">{f.error}</span>}
                  {f.state === 'failed' && <button className="btn btn-ghost" onClick={() => void sync.retryMedia(f.id)}>Retry</button>}
                  <button className="btn btn-ghost text-red-700" onClick={() => void act(async () => { if (confirm('Delete this file from the device? It has not been uploaded.')) { await media.delete(f.id); await sync.refreshCounts() } })}>Delete</button>
                </span>
              </li>
            ))}
          </ul>
        )}
      </Card>
    </div>
  )
}
