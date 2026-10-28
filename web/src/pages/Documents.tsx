import { useRef, useState } from 'react'
import { useProject } from '../lib/project'
import { useLoad, useRealtime, must } from '../lib/hooks'
import { supabase, friendlyError } from '../lib/supabase'
import { createFolderTree, driveEnabled, shareProjectFolder } from '../lib/driveClient'
import { categoryFor, formatBytes, sha256Hex, validateUpload, baseMime } from '../lib/media'
import { sync } from '../lib/sync'
import { uid, type Attachment } from '../lib/types'
import { ServerFile } from '../components/Evidence'
import { Alert, Card, PageHeader, Select, TextInput } from '../components/ui'

export default function Documents() {
  const { project, can } = useProject()
  const [cat, setCat] = useState('')
  const [msg, setMsg] = useState<{ tone: 'red' | 'green' | 'amber'; text: string } | null>(null)
  const [busy, setBusy] = useState(false)
  const [email, setEmail] = useState('')
  const [role, setRole] = useState<'reader' | 'commenter' | 'writer'>('reader')
  const [upCat, setUpCat] = useState<'document' | 'drawing'>('document')
  const [caption, setCaption] = useState('')
  const fileRef = useRef<HTMLInputElement>(null)
  const files = useLoad(async () => {
    let q = supabase.from('attachments').select('*').eq('project_id', project!.id).order('created_at', { ascending: false }).limit(300)
    if (cat) q = q.eq('category', cat)
    return must(q) as Promise<Attachment[]>
  }, [project!.id, cat])
  useRealtime('attachments', project?.id, () => void files.reload())
  const run = async (f: () => Promise<unknown>, ok: string) => {
    setBusy(true); setMsg(null)
    try { await f(); setMsg({ tone: 'green', text: ok }) } catch (e) { setMsg({ tone: 'red', text: friendlyError(e) }) } finally { setBusy(false) }
  }
  const upload = async (list: FileList) => {
    for (const f of Array.from(list)) {
      const mime = baseMime(f.type || 'application/octet-stream')
      const category = upCat === 'drawing' ? 'drawing' : categoryFor(mime, f.name)
      const bad = validateUpload(category, mime, f.size)
      if (bad) { setMsg({ tone: 'red', text: `${f.name}: ${bad}` }); continue }
      await sync.queueMedia({ id: uid(), projectId: project!.id, dprId: null, activityId: null, blob: f, filename: f.name, mime, size: f.size, sha256: await sha256Hex(f), category, caption: caption || f.name, capturedAt: new Date().toISOString() })
    }
    setCaption('')
    setMsg({ tone: 'green', text: 'Queued for upload. The original file is uploaded unchanged.' })
  }
  const requestDeletion = async (a: Attachment) => {
    if (!confirm('Request deletion? The record is kept for audit; an administrator removes the Drive file under the retention policy.')) return
    await run(() => must(supabase.from('attachments').update({ retention_status: 'deletion_requested' }).eq('id', a.id)), 'Deletion requested.')
    await files.reload()
  }
  return (
    <div className="flex flex-col gap-4">
      <PageHeader title="Documents" subtitle="Files live in the project Google Drive; the database keeps IDs, checksums and provenance." />
      {!driveEnabled && <Alert tone="amber">Google Drive integration is disabled in this deployment (VITE_DRIVE_ENABLED=false). Files stay queued on devices.</Alert>}
      {msg && <Alert tone={msg.tone}>{msg.text}</Alert>}
      <div className="grid gap-4 md:grid-cols-2">
        <Card title="Upload drawings & documents">
          <div className="grid gap-2">
            <Select label="Category" value={upCat} onChange={(e) => setUpCat(e.target.value as 'document' | 'drawing')}><option value="document">Document (PDF, XLSX, DOCX, CSV, image)</option><option value="drawing">Drawing (PDF, DWG, DXF, image)</option></Select>
            <TextInput label="Title / description" value={caption} onChange={(e) => setCaption(e.target.value)} />
            <button className="btn btn-primary" onClick={() => fileRef.current?.click()} disabled={!can('documents', 'create')}>Choose files…</button>
            <input ref={fileRef} type="file" multiple hidden onChange={(e) => { if (e.target.files) void upload(e.target.files); e.target.value = '' }} />
          </div>
        </Card>
        <Card title="Google Drive folders">
          <p className="text-sm mb-2">Root: {project!.drive_root_folder_id ? <a className="underline" target="_blank" rel="noreferrer" href={`https://drive.google.com/drive/folders/${project!.drive_root_folder_id}`}>open in Drive</a> : 'not created yet'} · Standard tree 01_Contracts … 15_Archive, with 04_DPR/Year/Month/… created on demand.</p>
          {can('documents', 'create') && <button className="btn btn-secondary mb-3" disabled={busy || !driveEnabled} onClick={() => void run(() => createFolderTree(project!.id), 'Folder tree created (existing folders were reused, not duplicated).')}>Create / verify folder tree</button>}
          <div className="grid gap-2 sm:grid-cols-[1fr_auto_auto] items-end">
            <TextInput label="Share project folder with (Google account e-mail)" type="email" value={email} onChange={(e) => setEmail(e.target.value)} />
            <Select label="Access" value={role} onChange={(e) => setRole(e.target.value as never)}><option value="reader">Viewer</option><option value="commenter">Commenter</option><option value="writer">Editor</option></Select>
            <button className="btn btn-secondary" disabled={busy || !email || !driveEnabled} onClick={() => void run(() => shareProjectFolder(project!.id, email, role), `Shared with ${email}.`)}>Share</button>
          </div>
          <p className="mt-2 text-xs text-slate-500">Project roles do not grant Drive access automatically. In-app viewing is permission-checked without Drive sharing. Share only when people need to work in Drive directly, and remove access in Drive when they leave. Avoid "anyone with the link".</p>
        </Card>
      </div>
      <Card title="Files" actions={<select className="input w-auto" value={cat} onChange={(e) => setCat(e.target.value)} aria-label="Category filter"><option value="">All categories</option>{['photo', 'video', 'audio', 'document', 'drawing', 'report'].map((c) => <option key={c}>{c}</option>)}</select>}>
        {files.error && <Alert>{files.error}</Alert>}
        <ul className="grid gap-2 sm:grid-cols-2 lg:grid-cols-3">
          {files.data?.map((a) => (
            <div key={a.id}>
              <ServerFile a={a} />
              <div className="flex flex-wrap justify-between text-xs text-slate-500 px-1">
                <span>{formatBytes(a.size_bytes)} · {new Date(a.created_at).toLocaleDateString('en-IN')} · {a.retention_status}</span>
                {can('documents', 'edit') && a.retention_status === 'active' && <button className="underline" onClick={() => void requestDeletion(a)}>Request deletion</button>}
              </div>
            </div>
          ))}
        </ul>
      </Card>
    </div>
  )
}
