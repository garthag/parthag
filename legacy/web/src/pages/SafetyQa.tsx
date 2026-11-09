import { useState } from 'react'
import { Link } from 'react-router-dom'
import { useProject } from '../lib/project'
import { MasterTable, type Row } from '../components/MasterTable'
import { Badge, PageHeader, Tabs } from '../components/ui'

type Tab = 'safety' | 'inspections' | 'ncrs' | 'constraints' | 'actions' | 'commissioning'
const STATUS: Array<[string, string]> = [['open', 'Open'], ['in_progress', 'In progress'], ['closed', 'Closed']]
const statusBadge = (r: Row) => <Badge tone={r.status === 'closed' ? 'green' : r.status === 'in_progress' ? 'blue' : 'amber'}>{String(r.status)}</Badge>
const overdue = (r: Row) => r.target_date && r.status !== 'closed' && String(r.target_date) < new Date().toISOString().slice(0, 10)
  ? <span className="text-red-700 font-semibold">{String(r.target_date)} overdue</span> : String(r.target_date ?? '—')
const fromDpr = (r: Row) => r.dpr_id ? <Link className="underline" to={`/dpr/${r.dpr_id}`}>DPR</Link> : '—'

export default function SafetyQa() {
  const { project, can, masters } = useProject()
  const [tab, setTab] = useState<Tab>('safety')
  const scope = { column: 'project_id' as const, value: project!.id }
  const acts = masters.activities.map((a) => [a.id, `${a.code} — ${a.name}`] as [string, string])
  return (
    <div>
      <PageHeader title="Safety & QA/QC" subtitle="Registers raised from DPRs or directly. Items keep their own status after the DPR is approved; the DPR snapshot preserves what was reported." />
      <Tabs value={tab} onChange={setTab} tabs={[['safety', 'Safety'], ['inspections', 'Inspections'], ['ncrs', 'NCRs'], ['constraints', 'Constraints'], ['actions', 'Action items'], ['commissioning', 'Commissioning']]} />
      {tab === 'safety' && (
        <MasterTable title="Safety observations, permits & incidents" table="safety_observations" scope={scope} orderBy="observed_on"
          canCreate={can('safety', 'create')} canEdit={can('safety', 'edit')} canDelete={false} defaults={{ status: 'open', category: 'unsafe_condition' }}
          columns={[
            { key: 'observed_on', label: 'Date', type: 'date' },
            { key: 'category', label: 'Type', type: 'select', required: true, options: [['permit', 'Permit'], ['lifting_plan', 'Lifting plan'], ['work_at_height', 'Work at height'], ['unsafe_condition', 'Unsafe condition'], ['unsafe_act', 'Unsafe act'], ['near_miss', 'Near miss'], ['incident', 'Incident'], ['first_aid', 'First aid'], ['good_practice', 'Good practice'], ['other', 'Other']] },
            { key: 'description', label: 'Description', type: 'textarea', required: true },
            { key: 'severity', label: 'Severity', type: 'select', options: [['low', 'Low'], ['medium', 'Medium'], ['high', 'High'], ['critical', 'Critical']] },
            { key: 'corrective_action', label: 'Corrective action', type: 'textarea', hideInList: true }, { key: 'preventive_action', label: 'Preventive action', type: 'textarea', hideInList: true },
            { key: 'responsible', label: 'Responsible' }, { key: 'target_date', label: 'Target', type: 'date', render: overdue },
            { key: 'status', label: 'Status', type: 'select', options: STATUS, render: statusBadge }, { key: 'dpr', label: 'Source', readOnly: true, render: fromDpr },
          ]} />
      )}
      {tab === 'inspections' && (
        <MasterTable title="Inspection requests & outcomes" table="inspection_records" scope={scope} orderBy="created_at"
          canCreate={can('quality', 'create')} canEdit={can('quality', 'edit')} canDelete={false} defaults={{ outcome: 'pending' }}
          columns={[
            { key: 'request_no', label: 'Request no.' }, { key: 'activity_id', label: 'Activity', type: 'select', options: acts },
            { key: 'inspection_type', label: 'Type', required: true }, { key: 'is_hold_point', label: 'Hold point', type: 'bool' },
            { key: 'inspected_qty', label: 'Qty', type: 'number' },
            { key: 'outcome', label: 'Outcome', type: 'select', options: [['pending', 'Pending'], ['accepted', 'Accepted'], ['accepted_with_comments', 'Accepted w/ comments'], ['rejected', 'Rejected']] },
            { key: 'inspector', label: 'Inspector' }, { key: 'inspected_on', label: 'Date', type: 'date' }, { key: 'remarks', label: 'Remarks', type: 'textarea', hideInList: true },
            { key: 'dpr', label: 'Source', readOnly: true, render: fromDpr },
          ]} />
      )}
      {tab === 'ncrs' && (
        <MasterTable title="Non-conformance reports & observations" table="quality_ncrs" scope={scope} orderBy="created_at"
          canCreate={can('quality', 'create')} canEdit={can('quality', 'edit')} canDelete={false} defaults={{ status: 'open', kind: 'ncr' }}
          columns={[
            { key: 'ncr_no', label: 'NCR no.' }, { key: 'kind', label: 'Kind', type: 'select', options: [['ncr', 'NCR'], ['observation', 'Observation']] },
            { key: 'activity_id', label: 'Activity', type: 'select', options: acts, hideInList: true },
            { key: 'description', label: 'Description', type: 'textarea', required: true }, { key: 'rework_required', label: 'Rework', type: 'bool' },
            { key: 'corrective_action', label: 'Corrective action', type: 'textarea', hideInList: true }, { key: 'responsible', label: 'Responsible' },
            { key: 'target_date', label: 'Target', type: 'date', render: overdue }, { key: 'status', label: 'Status', type: 'select', options: STATUS, render: statusBadge },
            { key: 'dpr', label: 'Source', readOnly: true, render: fromDpr },
          ]} />
      )}
      {tab === 'constraints' && (
        <MasterTable title="Constraints" table="constraints" scope={scope} orderBy="created_at" canCreate={can('dpr', 'create')} canEdit={can('dpr', 'edit')} canDelete={false} defaults={{ status: 'open', category: 'material' }}
          columns={[
            { key: 'category', label: 'Category', type: 'select', options: ['material', 'equipment', 'labour', 'design', 'client', 'weather', 'permit', 'access', 'other'].map((c) => [c, c]) },
            { key: 'description', label: 'Constraint', type: 'textarea', required: true }, { key: 'activity_id', label: 'Activity', type: 'select', options: acts, hideInList: true },
            { key: 'responsible', label: 'Responsible' }, { key: 'target_date', label: 'Needed by', type: 'date', render: overdue },
            { key: 'status', label: 'Status', type: 'select', options: STATUS, render: statusBadge }, { key: 'dpr', label: 'Source', readOnly: true, render: fromDpr },
          ]} />
      )}
      {tab === 'actions' && (
        <MasterTable title="Action items" table="action_items" scope={scope} orderBy="created_at" canCreate={can('dpr', 'create')} canEdit={can('dpr', 'edit')} canDelete={false} defaults={{ status: 'open', priority: 'normal' }}
          columns={[
            { key: 'description', label: 'Action', type: 'textarea', required: true }, { key: 'responsible', label: 'Responsible' },
            { key: 'target_date', label: 'Target', type: 'date', render: overdue },
            { key: 'priority', label: 'Priority', type: 'select', options: [['low', 'Low'], ['normal', 'Normal'], ['high', 'High'], ['urgent', 'Urgent']] },
            { key: 'status', label: 'Status', type: 'select', options: STATUS, render: statusBadge }, { key: 'dpr', label: 'Source', readOnly: true, render: fromDpr },
          ]} />
      )}
      {tab === 'commissioning' && (
        <MasterTable title="Commissioning checklists & punch list" table="commissioning_checklists" scope={scope} orderBy="system"
          canCreate={can('quality', 'create')} canEdit={can('quality', 'edit')} canDelete={can('quality', 'delete')} defaults={{ status: 'open', stage: 'pre_commissioning' }}
          columns={[
            { key: 'system', label: 'System', required: true }, { key: 'subsystem', label: 'Subsystem' },
            { key: 'stage', label: 'Stage', type: 'select', options: [['pre_commissioning', 'Pre-commissioning'], ['flushing_cleaning', 'Flushing & cleaning'], ['loop_check', 'Loop check'], ['functional_test', 'Functional test'], ['trial_operation', 'Trial operation'], ['punch_list', 'Punch list'], ['performance_test', 'Performance test'], ['handover', 'Handover']] },
            { key: 'item', label: 'Check item', type: 'textarea', required: true },
            { key: 'status', label: 'Status', type: 'select', options: [['open', 'Open'], ['in_progress', 'In progress'], ['passed', 'Passed'], ['failed', 'Failed'], ['not_applicable', 'N/A']] },
            { key: 'punch_category', label: 'Punch cat.', type: 'select', options: [['A', 'A (before start-up)'], ['B', 'B'], ['C', 'C']] },
            { key: 'completed_on', label: 'Completed', type: 'date' }, { key: 'remarks', label: 'Remarks', hideInList: true },
          ]} />
      )}
    </div>
  )
}
