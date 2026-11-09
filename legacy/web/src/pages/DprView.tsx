import { useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router-dom'
import { useProject } from '../lib/project'
import { useAuth } from '../lib/auth'
import { useLoad, useRealtime, must } from '../lib/hooks'
import { usePresence } from '../lib/live'
import { supabase, friendlyError } from '../lib/supabase'
import { fileObjectUrl } from '../lib/driveClient'
import { sha256Hex } from '../lib/media'
import { sync } from '../lib/sync'
import { formatQty } from '../lib/calc/money'
import { download } from '../lib/csv'
import type { DprDocument } from '../lib/dprDoc'
import type { Attachment, DprStatus } from '../lib/types'
import { ServerFile } from '../components/Evidence'
import { Alert, Badge, Card, DprStatusBadge, Modal, PageHeader, Spinner, TextArea } from '../components/ui'

interface ReportRev { id: string; dpr_id: string; revision: number; snapshot: DprDocument; snapshot_sha256: string; approved_at: string; pdf_drive_file_id: string | null; pdf_generated_at: string | null; pdf_sha256: string | null }

export default function DprView() {
  const { id } = useParams()
  const nav = useNavigate()
  const { project, can } = useProject()
  const { user, profile } = useAuth()
  const [msg, setMsg] = useState<{ tone: 'red' | 'green' | 'amber'; text: string } | null>(null)
  const [busy, setBusy] = useState<string | null>(null)
  const [dialog, setDialog] = useState<null | 'reject' | 'approve' | 'revise' | 'comment'>(null)
  const [comment, setComment] = useState('')
  const [accepted, setAccepted] = useState<Record<string, number>>({})

  const data = useLoad(async () => {
    const doc = (await must(supabase.rpc('dpr_document', { p_id: id }))) as DprDocument
    const [revs, chain, files] = await Promise.all([
      must(supabase.from('report_revisions').select('*').eq('project_id', doc.project.id).eq('dpr_no', doc.header.dpr_no).order('revision')),
      must(supabase.from('dpr_headers').select('id,revision,status,revision_reason,approved_at').eq('project_id', doc.project.id).eq('seq', doc.header.seq).order('revision')),
      must(supabase.from('attachments').select('*').eq('dpr_id', id!).order('created_at')),
    ])
    return { doc, revs: revs as ReportRev[], chain: chain as Array<{ id: string; revision: number; status: DprStatus; revision_reason: string | null }>, files: files as Attachment[] }
  }, [id])
  useRealtime('dpr_headers', project?.id, () => void data.reload())
  useRealtime('approval_actions', project?.id, () => void data.reload())
  const others = usePresence(id ? `dpr:${id}` : null, user ? { id: user.id, name: profile?.full_name || user.email || 'User' } : null)

  if (data.error) return <Alert>{data.error}</Alert>
  if (!data.data) return <Spinner />
  const { doc, revs, chain, files } = data.data
  const h = doc.header
  const status = h.status as DprStatus
  const myRev = revs.find((r) => r.dpr_id === h.id)
  const mine = h.prepared_by === user?.id || h.submitted_by === user?.id
  const selfBlocked = mine && !project?.allow_self_approval

  const run = async (label: string, f: () => Promise<unknown>, ok?: string) => {
    setBusy(label)
    setMsg(null)
    try {
      await f()
      if (ok) setMsg({ tone: 'green', text: ok })
      setDialog(null)
      setComment('')
      await data.reload()
    } catch (e) {
      setMsg({ tone: 'red', text: friendlyError(e) })
    } finally {
      setBusy(null)
    }
  }
  const review = (decision: string, extra: Record<string, unknown> = {}) =>
    must(supabase.rpc('review_dpr', { p_id: h.id, p_decision: decision, p_comment: comment || null, p_expected_version: h.version, ...extra }))

  const makePdf = async (orientation: 'portrait' | 'landscape', forUpload: boolean) => {
    const { buildDprPdf, photoForPdf } = await import('../lib/pdf/dprPdf')
    const source = myRev?.snapshot ?? doc
    const photos = []
    for (const a of source.attachments.filter((x) => x.category === 'photo').slice(0, 24)) {
      try {
        const url = await fileObjectUrl(a.id)
        photos.push(await photoForPdf(await (await fetch(url)).blob(), a.caption ?? a.original_filename))
      } catch { /* photo unavailable offline — listed by count instead */ }
    }
    const pdf = buildDprPdf(source, { orientation, photos, watermark: myRev ? undefined : 'DRAFT - NOT APPROVED', reportRevisionId: myRev?.id, generatedBy: profile?.full_name })
    const blob = pdf.output('blob')
    const name = `${h.dpr_no}_R${h.revision}_${h.report_date}.pdf`
    if (!forUpload) return download(name, blob)
    if (!myRev) throw new Error('Only approved reports are published to Drive.')
    await sync.queueMedia({
      id: crypto.randomUUID(), projectId: doc.project.id, dprId: h.id, activityId: null, blob, filename: name, mime: 'application/pdf',
      size: blob.size, sha256: await sha256Hex(blob), category: 'report', caption: `Approved DPR ${h.dpr_no} Rev ${h.revision}`,
      capturedAt: new Date().toISOString(), reportRevisionId: myRev.id,
    })
  }

  const canEdit = ['draft', 'rejected'].includes(status) && (can('dpr', 'edit') || (h.prepared_by === user?.id && can('dpr', 'create')))
  return (
    <div className="flex flex-col gap-4">
      <PageHeader title={`${h.dpr_no}${h.revision ? ` · Rev ${h.revision}` : ''}`} subtitle={`${h.report_date} · ${h.shift} shift · ${h.area_name ?? 'Whole project'} · prepared by ${h.prepared_by_name ?? '—'}`}
        actions={<DprStatusBadge status={status} revision={h.revision} />} />
      {msg && <Alert tone={msg.tone}>{msg.text}</Alert>}
      {others.length > 0 && <p className="text-sm text-sky-800 dark:text-sky-300" role="status">👥 Also viewing: {others.map((o) => `${o.name} (${o.device})`).join(', ')}</p>}
      {!!h.revision_reason && <Alert tone="amber">Revision {h.revision} reason: {String(h.revision_reason)}</Alert>}

      <Card title="Actions">
        <div className="flex flex-wrap gap-2">
          {canEdit && <Link className="btn btn-primary" to={`/dpr/edit/${h.id}`}>Edit report</Link>}
          {canEdit && can('dpr', 'submit') && <button className="btn btn-success" disabled={!!busy} onClick={() => void run('submit', () => must(supabase.rpc('submit_dpr', { p_id: h.id, p_expected_version: h.version })), 'Submitted for review.')}>Submit for review</button>}
          {status === 'submitted' && can('dpr', 'approve') && <button className="btn btn-secondary" disabled={!!busy} onClick={() => void run('start', () => review('start_review'), 'Marked as under review.')}>Start review</button>}
          {['submitted', 'under_review'].includes(status) && can('dpr', 'approve') && (
            <button className="btn btn-success" disabled={!!busy || selfBlocked} title={selfBlocked ? 'Project policy: you cannot approve your own report' : undefined}
              onClick={() => { setAccepted(Object.fromEntries(doc.activities.map((a) => [a.id, Number(a.qty_inspected ?? a.qty_today)]))); setDialog('approve') }}>Approve…</button>
          )}
          {['submitted', 'under_review'].includes(status) && can('dpr', 'reject') && <button className="btn btn-danger" disabled={!!busy} onClick={() => setDialog('reject')}>Reject…</button>}
          {status === 'approved' && can('dpr', 'create') && chain.at(-1)?.id === h.id && <button className="btn btn-secondary" onClick={() => setDialog('revise')}>Start revision…</button>}
          <button className="btn btn-secondary" onClick={() => setDialog('comment')}>Comment</button>
          <button className="btn btn-secondary" disabled={!!busy} onClick={() => void run('pdf', () => makePdf('portrait', false))}>{busy === 'pdf' ? 'Building…' : 'Download PDF (A4 portrait)'}</button>
          <button className="btn btn-secondary" disabled={!!busy} onClick={() => void run('pdfl', () => makePdf('landscape', false))}>A4 landscape</button>
          <button className="btn btn-secondary" onClick={() => window.print()}>Print</button>
          {myRev && !myRev.pdf_drive_file_id && (can('reports', 'create') || can('dpr', 'approve')) && (
            <button className="btn btn-primary" disabled={!!busy} onClick={() => void run('upload', () => makePdf('portrait', true), 'Approved PDF queued for upload to Drive (04_DPR/…/Approved PDFs). Its file ID and checksum will be recorded against this revision.')}>Publish PDF to Drive</button>
          )}
        </div>
        {selfBlocked && ['submitted', 'under_review'].includes(status) && <p className="mt-2 text-sm text-slate-500">You prepared or submitted this report, so another authorised person must approve it.</p>}
        {myRev && (
          <p className="mt-3 text-xs text-slate-500 break-all">
            Approved snapshot {myRev.id} · SHA-256 {myRev.snapshot_sha256} · approved {new Date(myRev.approved_at).toLocaleString('en-IN')}
            {myRev.pdf_drive_file_id ? ` · PDF in Drive (${myRev.pdf_drive_file_id}), SHA-256 ${myRev.pdf_sha256}` : ' · PDF not yet published'}
          </p>
        )}
      </Card>

      {chain.length > 1 && (
        <Card title="Revisions">
          <ul className="flex flex-wrap gap-2">
            {chain.map((c) => (
              <li key={c.id}><button className={`btn ${c.id === h.id ? 'btn-primary' : 'btn-secondary'}`} onClick={() => nav(`/dpr/${c.id}`)}>Rev {c.revision} · {c.status}</button></li>
            ))}
          </ul>
          <p className="mt-2 text-xs text-slate-500">Earlier approved revisions stay unchanged. Progress figures use the latest approved revision.</p>
        </Card>
      )}

      <Card title="Activity progress">
        <div className="overflow-x-auto">
          <table className="table">
            <thead><tr><th>Activity</th><th className="num">Approved</th><th className="num">Prev.</th><th className="num">Reported today</th><th className="num">Accepted</th><th className="num">Cumulative</th><th className="num">Balance</th><th className="num">Plan %</th><th className="num">Actual %</th><th className="num">Var.</th></tr></thead>
            <tbody>
              {doc.activities.map((a) => (
                <tr key={a.id}>
                  <td><b>{a.activity_code}</b> {a.activity_name}<div className="text-xs text-slate-500">{String(a.location ?? '')} {a.delay_reason ? `· Delay: ${String(a.delay_reason)}` : ''}{a.overrun_authorized ? ` · Authorised overrun: ${String(a.overrun_reason)}` : ''}</div></td>
                  <td className="num">{formatQty(a.approved_total_qty)} {a.unit}</td><td className="num">{formatQty(a.prev_cum_qty)}</td><td className="num">{formatQty(a.qty_today)}</td>
                  <td className="num">{a.qty_accepted == null ? '—' : formatQty(a.qty_accepted)}</td><td className="num">{formatQty(a.cum_qty)}</td><td className="num">{formatQty(a.balance_qty)}</td>
                  <td className="num">{a.planned_progress_pct == null ? '—' : Number(a.planned_progress_pct).toFixed(1)}</td><td className="num">{a.actual_progress_pct == null ? '—' : Number(a.actual_progress_pct).toFixed(1)}</td>
                  <td className={`num ${Number(a.variance_pct) < 0 ? 'text-red-700' : ''}`}>{a.variance_pct == null ? '—' : Number(a.variance_pct).toFixed(1)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </Card>

      <div className="grid gap-4 md:grid-cols-2">
        <Card title={`Manpower — ${doc.manpower.reduce((s, m) => s + m.headcount, 0)} people`}>
          <table className="table"><thead><tr><th>Agency / trade</th><th className="num">Head</th><th className="num">Man-h</th></tr></thead>
            <tbody>{doc.manpower.map((m, i) => <tr key={i}><td>{m.contractor_name ?? '—'} · {m.trade}</td><td className="num">{m.headcount}</td><td className="num">{formatQty(m.man_hours, 1)}</td></tr>)}</tbody></table>
        </Card>
        <Card title="Equipment">
          <table className="table"><thead><tr><th>Equipment</th><th className="num">Work h</th><th className="num">B/D h</th><th className="num">Util.</th></tr></thead>
            <tbody>{doc.equipment.map((e, i) => <tr key={i}><td>{e.equipment_code} {e.equipment_type}{e.breakdown_reason ? <div className="text-xs text-red-700">{String(e.breakdown_reason)}</div> : null}</td><td className="num">{e.operating_hours}</td><td className="num">{e.breakdown_hours}</td><td className="num">{e.utilization_pct ?? '—'}%</td></tr>)}</tbody></table>
        </Card>
        <Card title="Materials">
          <table className="table"><thead><tr><th>Material</th><th className="num">Open</th><th className="num">Rcvd</th><th className="num">Issued</th><th className="num">Close</th></tr></thead>
            <tbody>{doc.materials.map((m, i) => <tr key={i}><td>{m.material_code}<div className="text-xs text-slate-500">{String(m.challan_no ?? '')} {String(m.heat_batch_no ?? '')} · {String(m.inspection_status)}</div></td><td className="num">{formatQty(m.opening_qty)}</td><td className="num">{formatQty(m.received_qty)}</td><td className="num">{formatQty(m.issued_qty)}</td><td className="num">{formatQty(m.closing_qty)}</td></tr>)}</tbody></table>
        </Card>
        <Card title="Safety & QA/QC">
          <p className="text-sm mb-2">{String(h.toolbox_talks)} toolbox talks · {String(h.permits_issued)} permits</p>
          <ul className="text-sm list-disc pl-5">
            {doc.safety.map((s, i) => <li key={`s${i}`}><Badge tone={s.severity === 'high' || s.severity === 'critical' ? 'red' : 'amber'}>{String(s.category)}</Badge> {String(s.description)} — {String(s.status)}</li>)}
            {doc.inspections.map((s, i) => <li key={`i${i}`}>Inspection {String(s.request_no ?? '')} {String(s.inspection_type)}: <b>{String(s.outcome)}</b>{s.is_hold_point ? ' (hold point)' : ''}</li>)}
            {doc.ncrs.map((s, i) => <li key={`n${i}`}><Badge tone="red">{String(s.kind).toUpperCase()}</Badge> {String(s.description)} — {String(s.status)}</li>)}
            {doc.constraints.map((s, i) => <li key={`c${i}`}>Constraint ({String(s.category)}): {String(s.description)}</li>)}
          </ul>
        </Card>
      </div>

      <Card title="Narrative">
        <dl className="grid gap-1 text-sm">
          {[['Weather', h.weather], ['Site conditions', h.site_conditions], ['Work completed', h.work_completed], ['Achievements', h.achievements], ['Delays', h.delay_reasons],
            ['Decisions required', h.decisions_required], ["Tomorrow's plan", h.tomorrow_plan], ['Priority actions', h.priority_actions]]
            .filter(([, v]) => v).map(([k, v]) => <div key={k as string}><dt className="inline font-semibold">{k as string}: </dt><dd className="inline">{String(v)}</dd></div>)}
        </dl>
      </Card>

      <Card title={`Evidence (${files.length})`}>
        {files.length ? <ul className="grid gap-2 sm:grid-cols-2 lg:grid-cols-3">{files.map((a) => <ServerFile key={a.id} a={a} />)}</ul> : <p className="text-sm text-slate-500">No files uploaded for this report.</p>}
      </Card>

      <Card title="Approval history">
        <ol className="flex flex-col gap-2 text-sm">
          {doc.approval_history.map((x, i) => (
            <li key={i} className="border-l-4 border-brand-500 pl-3"><b>{x.action.replace('_', ' ')}</b> by {x.by} · {new Date(x.at).toLocaleString('en-IN')}{x.from !== x.to && ` · ${x.from} → ${x.to}`}{x.comment && <div className="text-slate-600 dark:text-slate-400">“{x.comment}”</div>}</li>
          ))}
        </ol>
      </Card>

      <Modal open={dialog === 'reject'} onClose={() => setDialog(null)} title="Reject report">
        <TextArea label="Reason (required — the preparer will see this)" value={comment} onChange={(e) => setComment(e.target.value)} />
        <button className="btn btn-danger mt-3 w-full" disabled={!comment.trim() || !!busy} onClick={() => void run('reject', () => review('reject'), 'Report rejected with comments.')}>Reject</button>
      </Modal>
      <Modal open={dialog === 'comment'} onClose={() => setDialog(null)} title="Add comment">
        <TextArea label="Comment" value={comment} onChange={(e) => setComment(e.target.value)} />
        <button className="btn btn-primary mt-3 w-full" disabled={!comment.trim() || !!busy} onClick={() => void run('comment', () => review('comment'), 'Comment added.')}>Add comment</button>
      </Modal>
      <Modal open={dialog === 'revise'} onClose={() => setDialog(null)} title="Start a revision">
        <p className="text-sm mb-2">The approved revision stays unchanged. A new draft revision is created for correction and must be approved again.</p>
        <TextArea label="Reason for revision (required)" value={comment} onChange={(e) => setComment(e.target.value)} />
        <button className="btn btn-primary mt-3 w-full" disabled={!comment.trim() || !!busy} onClick={() => void run('revise', async () => {
          const newId = await must(supabase.rpc('start_dpr_revision', { p_id: h.id, p_reason: comment }))
          nav(`/dpr/edit/${newId}`)
        })}>Create revision</button>
      </Modal>
      <Modal open={dialog === 'approve'} onClose={() => setDialog(null)} title="Approve report" wide>
        <p className="text-sm mb-3">Confirm the <b>accepted</b> quantity for each activity (defaults to inspected or reported). Cumulative progress uses accepted quantities. Approval freezes this revision as an immutable snapshot.</p>
        <table className="table">
          <thead><tr><th>Activity</th><th className="num">Reported</th><th className="num">Inspected</th><th className="num">Accepted</th></tr></thead>
          <tbody>{doc.activities.map((a) => (
            <tr key={a.id}><td>{a.activity_code} {a.activity_name}</td><td className="num">{formatQty(a.qty_today)}</td><td className="num">{a.qty_inspected == null ? '—' : formatQty(a.qty_inspected as number)}</td>
              <td><input className="input num" type="number" inputMode="decimal" min={0} step="any" value={accepted[a.id] ?? ''} aria-label={`Accepted quantity for ${a.activity_code}`}
                onChange={(e) => setAccepted((x) => ({ ...x, [a.id]: Number(e.target.value) }))} /></td></tr>
          ))}</tbody>
        </table>
        <TextArea label="Approval comment (optional)" className="mt-3" value={comment} onChange={(e) => setComment(e.target.value)} />
        <button className="btn btn-success mt-3 w-full" disabled={!!busy} onClick={() => void run('approve', () => review('approve', { p_accepted: accepted }), 'Approved. Snapshot frozen; publish the PDF to Drive when ready.')}>Approve</button>
      </Modal>
    </div>
  )
}
