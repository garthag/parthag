// Supabase Edge Function: `drive`
// Server-side gateway between the app and Google Drive. The browser never
// holds Google credentials; every request is authorised against the
// database with the caller's own JWT (so Row Level Security applies).
//
// POST {action:'tree', projectId}                 → create the standard folder tree
// POST {action:'start', attachmentId}             → begin / resume an upload; returns {offset} or {done}
// PUT  ?attachmentId=..&offset=..  (body = bytes) → upload one chunk; returns {offset} or {done}
// GET  ?attachmentId=..                           → stream the file to an authorised user
// POST {action:'share', projectId, email, role}   → share the project folder (project admins)
import { createClient, type SupabaseClient } from 'npm:@supabase/supabase-js@2'
import { CHUNK_SIZE, GoogleDrive, createStandardTree, ensureFolderPath, folderPathFor, safeFileName, validateUpload, type FolderStore } from './core.ts'
import { refreshTokenSource, serviceAccountTokenSource } from './google_auth.ts'

const env = (k: string) => Deno.env.get(k) ?? ''
const allowed = env('ALLOWED_ORIGINS').split(',').map((s) => s.trim()).filter(Boolean)

const tokenSource = env('GOOGLE_SERVICE_ACCOUNT_JSON')
  ? serviceAccountTokenSource(env('GOOGLE_SERVICE_ACCOUNT_JSON'))
  : refreshTokenSource(env('GOOGLE_CLIENT_ID'), env('GOOGLE_CLIENT_SECRET'), env('GOOGLE_REFRESH_TOKEN'))
const drive = new GoogleDrive(tokenSource, { rootFolderId: env('DRIVE_ROOT_FOLDER_ID') || null, sharedDriveId: env('GOOGLE_SHARED_DRIVE_ID') || null })
const admin = createClient(env('SUPABASE_URL'), env('SUPABASE_SERVICE_ROLE_KEY'), { auth: { persistSession: false } })

const store: FolderStore = {
  async getFolder(projectId, path) {
    const { data } = await admin.from('drive_folders').select('drive_folder_id').eq('project_id', projectId).eq('path', path).maybeSingle()
    return data?.drive_folder_id ?? null
  },
  async registerFolder(projectId, path, driveId, parent) {
    await admin.from('drive_folders').upsert({ project_id: projectId, path, drive_folder_id: driveId, parent_drive_folder_id: parent },
      { onConflict: 'project_id,path', ignoreDuplicates: true })
    const id = (await store.getFolder(projectId, path))!
    if (path === '') await admin.from('projects').update({ drive_root_folder_id: id }).eq('id', projectId).is('drive_root_folder_id', null)
    return id
  },
}

// Best-effort per-instance rate limit (put a gateway limit in front for production).
const hits = new Map<string, number[]>()
function rateLimited(user: string, perMinute = 240) {
  const now = Date.now()
  const list = (hits.get(user) ?? []).filter((t) => now - t < 60_000)
  list.push(now)
  hits.set(user, list)
  return list.length > perMinute
}

function cors(req: Request): Record<string, string> {
  const origin = req.headers.get('Origin') ?? ''
  const allow = !allowed.length ? '*' : allowed.includes(origin) ? origin : allowed[0]
  return {
    'Access-Control-Allow-Origin': allow,
    'Access-Control-Allow-Headers': 'authorization, apikey, content-type, x-client-info',
    'Access-Control-Allow-Methods': 'GET, POST, PUT, OPTIONS',
    'Access-Control-Expose-Headers': 'content-disposition',
    Vary: 'Origin',
  }
}

class HttpError extends Error { constructor(public status: number, msg: string) { super(msg) } }

async function projectInfo(projectId: string) {
  const { data, error } = await admin.from('projects').select('id, code, name, organization_id, organizations(name)').eq('id', projectId).single()
  if (error || !data) throw new HttpError(404, 'Project not found')
  // deno-lint-ignore no-explicit-any
  return { id: data.id, code: data.code, name: data.name, orgId: data.organization_id, orgName: (data as any).organizations?.name ?? 'Organisation' }
}

async function can(user: SupabaseClient, projectId: string, module: string, action: string) {
  const { data } = await user.rpc('has_perm', { p_project: projectId, p_module: module, p_action: action })
  return data === true
}

async function loadAttachment(user: SupabaseClient, id: string) {
  // Selecting through the caller's client applies RLS: invisible ⇒ not authorised.
  const { data, error } = await user.from('attachments').select('*').eq('id', id).maybeSingle()
  if (error) throw new HttpError(400, error.message)
  if (!data) throw new HttpError(404, 'Attachment not found or not accessible')
  return data
}

async function markUploaded(att: { id: string; size_bytes: number }, file: { id: string; webViewLink?: string; parents?: string[]; size?: string }, userId: string) {
  if (file.size && Number(file.size) !== Number(att.size_bytes)) {
    await admin.from('attachments').update({ upload_status: 'failed', upload_error: `Size mismatch: Drive ${file.size}, expected ${att.size_bytes}` }).eq('id', att.id)
    throw new HttpError(422, 'Uploaded size does not match; the upload will be retried')
  }
  await admin.from('attachments').update({
    drive_file_id: file.id, parent_folder_id: file.parents?.[0] ?? null, web_view_link: file.webViewLink ?? null,
    upload_status: 'uploaded', upload_error: null, uploaded_at: new Date().toISOString(), uploaded_by: userId,
  }).eq('id', att.id)
  await admin.from('drive_upload_sessions').delete().eq('attachment_id', att.id)
}

async function start(user: SupabaseClient, userId: string, attachmentId: string) {
  const att = await loadAttachment(user, attachmentId)
  if (att.upload_status === 'uploaded') return { done: true, driveFileId: att.drive_file_id }
  if (att.created_by !== userId) throw new HttpError(403, 'Only the person who captured this file can upload it')
  const bad = validateUpload(att.category, att.mime_type, att.size_bytes)
  if (bad) throw new HttpError(415, bad)

  // Reconcile: the bytes may already be in Drive from an interrupted attempt.
  const existing = await drive.findByAppProperties({ cdprAttachmentId: att.id })
  if (existing && (!existing.size || Number(existing.size) === Number(att.size_bytes))) {
    await markUploaded(att, existing, userId)
    return { done: true, driveFileId: existing.id }
  }

  const { data: sess } = await admin.from('drive_upload_sessions').select('*').eq('attachment_id', att.id).maybeSingle()
  if (sess) {
    const r = await drive.queryOffset(sess.session_uri, sess.total_bytes)
    if (r.kind === 'done') { await markUploaded(att, r.file, userId); return { done: true, driveFileId: r.file.id } }
    if (r.kind === 'partial') return { offset: r.next, chunkSize: CHUNK_SIZE }
    await admin.from('drive_upload_sessions').delete().eq('attachment_id', att.id)
  }

  let reportDate: string | null = null, dprNo: string | null = null
  if (att.dpr_id) {
    const { data: d } = await admin.from('dpr_headers').select('report_date, dpr_no, revision').eq('id', att.dpr_id).single()
    reportDate = d?.report_date ?? null
    dprNo = d ? `${d.dpr_no}${d.revision ? `-R${d.revision}` : ''}` : null
  }
  const project = await projectInfo(att.project_id)
  const folder = await ensureFolderPath(drive, store, project, folderPathFor(att.category, reportDate))
  const session = await drive.startResumable({
    name: safeFileName([dprNo ?? reportDate, att.category, att.id.slice(0, 8)], att.original_filename),
    parent: folder, mimeType: att.mime_type, size: att.size_bytes, description: att.caption ?? undefined,
    appProperties: { cdprAttachmentId: att.id, cdprProjectId: att.project_id, ...(att.sha256 ? { cdprSha256: att.sha256 } : {}) },
  })
  await admin.from('drive_upload_sessions').upsert({ attachment_id: att.id, session_uri: session, total_bytes: att.size_bytes, folder_id: folder })
  await admin.from('attachments').update({ upload_status: 'uploading', upload_attempts: (att.upload_attempts ?? 0) + 1 }).eq('id', att.id)
  return { offset: 0, chunkSize: CHUNK_SIZE }
}

async function chunk(user: SupabaseClient, userId: string, attachmentId: string, offset: number, body: Uint8Array) {
  const att = await loadAttachment(user, attachmentId)
  if (att.created_by !== userId) throw new HttpError(403, 'Not your upload')
  const { data: sess } = await admin.from('drive_upload_sessions').select('*').eq('attachment_id', att.id).maybeSingle()
  if (!sess) throw new HttpError(409, 'No active upload session; call start again')
  if (offset + body.byteLength > sess.total_bytes) throw new HttpError(400, 'Chunk beyond declared file size')
  const r = await drive.putChunk(sess.session_uri, body, offset, sess.total_bytes)
  if (r.kind === 'partial') return { offset: r.next }
  if (r.kind === 'expired') {
    await admin.from('drive_upload_sessions').delete().eq('attachment_id', att.id)
    return { restart: true }
  }
  await markUploaded(att, r.file, userId)
  return { done: true, driveFileId: r.file.id }
}

Deno.serve(async (req) => {
  const headers = cors(req)
  if (req.method === 'OPTIONS') return new Response('ok', { headers })
  const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { ...headers, 'Content-Type': 'application/json' } })
  try {
    const authHeader = req.headers.get('Authorization') ?? ''
    if (!authHeader.startsWith('Bearer ')) throw new HttpError(401, 'Sign in required')
    const user = createClient(env('SUPABASE_URL'), env('SUPABASE_ANON_KEY'), {
      global: { headers: { Authorization: authHeader } }, auth: { persistSession: false },
    })
    const { data: who, error: whoErr } = await user.auth.getUser(authHeader.slice(7))
    if (whoErr || !who.user) throw new HttpError(401, 'Invalid session')
    const userId = who.user.id
    if (rateLimited(userId)) throw new HttpError(429, 'Too many requests — slow down')
    const url = new URL(req.url)

    if (req.method === 'PUT') {
      const id = url.searchParams.get('attachmentId') ?? ''
      const offset = Number(url.searchParams.get('offset'))
      if (!id || !Number.isFinite(offset) || offset < 0) throw new HttpError(400, 'attachmentId and offset are required')
      const body = new Uint8Array(await req.arrayBuffer())
      if (body.byteLength > CHUNK_SIZE) throw new HttpError(413, 'Chunk too large')
      return json(await chunk(user, userId, id, offset, body))
    }

    if (req.method === 'GET') {
      const att = await loadAttachment(user, url.searchParams.get('attachmentId') ?? '')
      if (!att.drive_file_id) throw new HttpError(409, 'File has not finished uploading')
      const res = await drive.download(att.drive_file_id)
      if (!res.ok || !res.body) throw new HttpError(502, `Drive download failed (${res.status})`)
      return new Response(res.body, { headers: {
        ...headers, 'Content-Type': att.mime_type, 'Cache-Control': 'private, max-age=3600',
        'Content-Disposition': `inline; filename="${att.original_filename.replace(/["\\\r\n]/g, '_')}"`,
        'X-Content-Type-Options': 'nosniff',
      } })
    }

    const body = await req.json()
    if (body.action === 'start') return json(await start(user, userId, body.attachmentId))
    if (body.action === 'tree') {
      if (!(await can(user, body.projectId, 'documents', 'create'))) throw new HttpError(403, 'No permission')
      const now = new Date().toISOString()
      await createStandardTree(drive, store, await projectInfo(body.projectId), now.slice(0, 4), now.slice(5, 7))
      return json({ rootFolderId: await store.getFolder(body.projectId, '') })
    }
    if (body.action === 'share') {
      const { data: isAdmin } = await user.rpc('is_project_admin', { p_project: body.projectId })
      if (!isAdmin) throw new HttpError(403, 'Only project administrators can share the project folder')
      if (!['reader', 'commenter', 'writer'].includes(body.role)) throw new HttpError(400, 'Invalid role')
      const root = await ensureFolderPath(drive, store, await projectInfo(body.projectId), '')
      await drive.share(root, String(body.email), body.role)
      return json({ shared: true })
    }
    throw new HttpError(400, 'Unknown action')
  } catch (e) {
    const status = e instanceof HttpError ? e.status : 500
    console.error(status, (e as Error).message) // no personal data or tokens are logged
    return json({ error: (e as Error).message }, status)
  }
})
