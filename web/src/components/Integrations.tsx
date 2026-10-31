import { useState } from 'react'
import { useProject } from '../lib/project'
import { useLoad, useRealtime } from '../lib/hooks'
import { serverConfig, supabase } from '../lib/supabase'
import { Card, Tabs } from './ui'

const api = serverConfig?.url ?? 'https://your-project.supabase.co'

const SNIPPETS: Record<'python' | 'java' | 'curl', string> = {
  python: `from construct_dpr import ConstructDPR   # clients/python (standard library only)

api = ConstructDPR("${api}", ANON_KEY)
api.sign_in("erp-bot@company.com", PASSWORD)  # account with the "integration" role
project = api.project_by_code("PROJECT-CODE")
for row in api.activity_progress(project["id"]):
    print(row["code"], row["progress_pct"])
for event in api.follow_events(after=LAST_ID):  # dpr.approved, boq.revised, …
    handle(event)`,
  java: `// clients/java — Java 11+, no dependencies
var api = new ConstructDprClient("${api}", anonKey);
api.signIn("erp-bot@company.com", password);
String boq = api.boqStatus(projectId);          // JSON
String events = api.events(lastId, 500);        // ordered, resumable`,
  curl: `TOKEN=$(curl -s "${api}/auth/v1/token?grant_type=password" \\
  -H "apikey: $ANON_KEY" -H "Content-Type: application/json" \\
  -d '{"email":"erp-bot@company.com","password":"…"}' | jq -r .access_token)
curl "${api}/rest/v1/integration_events?id=gt.0&order=id&limit=100" \\
  -H "apikey: $ANON_KEY" -H "Authorization: Bearer $TOKEN"`,
}

/** How other software (ERP, Python/Java services, BI tools) connects, plus the live event feed. */
export function IntegrationsCard() {
  const { project, can } = useProject()
  const [lang, setLang] = useState<keyof typeof SNIPPETS>('python')
  const allowed = !!project && (can('reports', 'export') || can('project', 'export'))
  const events = useLoad(async () => {
    if (!allowed) return []
    const { data, error } = await supabase.from('integration_events').select('*').eq('project_id', project!.id).order('id', { ascending: false }).limit(15)
    if (error) throw error
    return data as Array<{ id: number; event_type: string; occurred_at: string; payload: Record<string, unknown> }>
  }, [project?.id, allowed])
  useRealtime('integration_events', allowed ? project?.id : null, () => void events.reload())
  return (
    <Card title="Integrations (ERP, Python, Java, Excel, Power BI)">
      <ul className="text-sm list-disc pl-5 mb-3 space-y-1">
        <li>REST API: <code className="break-all">{api}/rest/v1/</code> — machine-readable description (OpenAPI) at the same address. Any language that speaks HTTPS + JSON can connect.</li>
        <li>Create a separate account for each external system and add it to the project with the role <b>Integration</b>: it can read and export everything and sync activities/BOQ, but can never submit or approve reports.</li>
        <li>Never give the Supabase <i>service-role</i> key to another system: it bypasses every permission.</li>
        <li>Ready-made clients and examples: <code>clients/python</code>, <code>clients/java</code>; guide: <code>docs/INTEGRATIONS.md</code>.</li>
      </ul>
      <Tabs value={lang} onChange={setLang} tabs={[['python', 'Python'], ['java', 'Java'], ['curl', 'curl / any language']]} />
      <pre className="overflow-x-auto rounded-lg bg-slate-900 p-3 text-xs text-slate-100"><code>{SNIPPETS[lang]}</code></pre>
      <button className="btn btn-ghost mt-1" onClick={() => void navigator.clipboard?.writeText(SNIPPETS[lang])}>Copy</button>
      {allowed && (
        <div className="mt-3">
          <h3 className="font-bold mb-1">Event feed — latest for this project</h3>
          <p className="text-xs text-slate-500 mb-2">External systems read these in order using the event number as a cursor, so nothing is missed after downtime.</p>
          {events.data?.length ? (
            <table className="table text-xs">
              <thead><tr><th>#</th><th>Event</th><th>When</th><th>Details</th></tr></thead>
              <tbody>{events.data.map((e) => (
                <tr key={e.id}><td className="num">{e.id}</td><td className="font-mono">{e.event_type}</td><td className="whitespace-nowrap">{new Date(e.occurred_at).toLocaleString('en-IN')}</td>
                  <td>{[e.payload.dpr_no, e.payload.revision != null ? `Rev ${e.payload.revision}` : null, e.payload.item_no, e.payload.code, e.payload.reason].filter(Boolean).join(' · ')}</td></tr>
              ))}</tbody>
            </table>
          ) : <p className="text-sm text-slate-500">No events yet.</p>}
        </div>
      )}
    </Card>
  )
}
