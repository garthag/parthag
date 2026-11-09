import { Link } from 'react-router-dom'
import { useProject } from '../lib/project'
import { useAuth } from '../lib/auth'
import { useLoad, useRealtime, must } from '../lib/hooks'
import { supabase } from '../lib/supabase'
import type { DprHeader } from '../lib/types'
import { Alert, Card, DprStatusBadge, Empty, PageHeader, Spinner } from '../components/ui'

type Row = DprHeader & { profiles: { full_name: string } | null }

export default function Approvals() {
  const { project, can } = useProject()
  const { user } = useAuth()
  const q = useLoad(async () => {
    const sel = 'id,dpr_no,report_date,shift,status,revision,submitted_at,prepared_by,profiles!dpr_headers_prepared_by_fkey(full_name)'
    const [pending, mineRejected] = await Promise.all([
      must(supabase.from('dpr_headers').select(sel).eq('project_id', project!.id).in('status', ['submitted', 'under_review']).order('report_date')),
      must(supabase.from('dpr_headers').select(sel).eq('project_id', project!.id).eq('status', 'rejected').eq('prepared_by', user!.id).order('report_date')),
    ])
    return { pending: pending as unknown as Row[], rejected: mineRejected as unknown as Row[] }
  }, [project!.id, user?.id])
  useRealtime('dpr_headers', project?.id, () => void q.reload())
  if (q.error) return <Alert>{q.error}</Alert>
  if (!q.data) return <Spinner />
  const list = (rows: Row[]) => (
    <ul className="flex flex-col gap-2">
      {rows.map((d) => (
        <li key={d.id}><Link to={`/dpr/${d.id}`} className="card flex flex-wrap items-center justify-between gap-2 p-3 hover:ring-2 hover:ring-brand-500">
          <div><b>{d.dpr_no}{d.revision ? ` · Rev ${d.revision}` : ''}</b><div className="text-sm text-slate-500">{d.report_date} · {d.shift} · by {d.profiles?.full_name}{d.submitted_at ? ` · submitted ${new Date(d.submitted_at).toLocaleString('en-IN')}` : ''}</div></div>
          <DprStatusBadge status={d.status} revision={d.revision} /></Link></li>
      ))}
    </ul>
  )
  return (
    <div className="flex flex-col gap-4">
      <PageHeader title="Approvals" subtitle="Reports waiting for review; updates live as others act." />
      <Card title={`Waiting for review (${q.data.pending.length})`}>
        {!can('dpr', 'approve') && <p className="text-sm text-slate-500 mb-2">Your role can view but not approve reports.</p>}
        {q.data.pending.length ? list(q.data.pending) : <Empty title="Nothing waiting for review" />}
      </Card>
      <Card title={`My reports returned for correction (${q.data.rejected.length})`}>
        {q.data.rejected.length ? list(q.data.rejected) : <Empty title="No rejected reports" />}
      </Card>
    </div>
  )
}
