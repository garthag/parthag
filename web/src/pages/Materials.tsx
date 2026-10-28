import { useState } from 'react'
import { useProject } from '../lib/project'
import { useLoad, useRealtime, must } from '../lib/hooks'
import { supabase } from '../lib/supabase'
import { formatQty } from '../lib/calc/money'
import { ChartCard, BarsChart } from '../components/charts'
import { Alert, Badge, Card, PageHeader, Stat } from '../components/ui'
import { fresh } from './_range'

export default function Materials() {
  const { project } = useProject()
  const [sel, setSel] = useState('')
  const stock = useLoad(async () => must(supabase.from('material_stock').select('*').eq('project_id', project!.id).order('code')), [project!.id])
  const ledger = useLoad(async () => sel ? must(supabase.from('material_ledger').select('*').eq('material_id', sel).order('txn_date')) : [], [sel])
  useRealtime('dpr_headers', project?.id, () => { void stock.reload(); void ledger.reload() })
  const rows = (stock.data ?? []) as Array<{ material_id: string; code: string; description: string; unit: string; stock_qty: number; reorder_level: number | null; below_reorder: boolean; last_txn_date: string | null }>
  const led = (ledger.data ?? []) as Array<Record<string, number | string>>
  return (
    <div className="flex flex-col gap-4">
      <PageHeader title="Materials & stores" subtitle="Store ledger from approved DPRs. Received → inspected/accepted → issued → consumed are tracked separately." />
      {stock.error && <Alert>{stock.error}</Alert>}
      <div className="grid grid-cols-2 gap-3 md:grid-cols-3">
        <Stat label="Materials tracked" value={rows.length} />
        <Stat label="Below re-order level" value={rows.filter((r) => r.below_reorder).length} tone={rows.some((r) => r.below_reorder) ? 'bad' : 'good'} />
      </div>
      <Card title="Current stock">
        <table className="table">
          <thead><tr><th>Code</th><th>Description</th><th className="num">Stock</th><th className="num">Re-order</th><th>Last movement</th><th /></tr></thead>
          <tbody>{rows.map((r) => (
            <tr key={r.material_id}><td className="font-semibold">{r.code}</td><td>{r.description}</td><td className="num">{formatQty(r.stock_qty)} {r.unit}</td><td className="num">{r.reorder_level == null ? '—' : formatQty(r.reorder_level)}</td>
              <td>{r.last_txn_date ?? '—'} {r.below_reorder && <Badge tone="red">Shortage risk</Badge>}</td>
              <td><button className="btn btn-ghost px-2" onClick={() => setSel(r.material_id)}>Ledger</button></td></tr>
          ))}</tbody>
        </table>
      </Card>
      {sel && (
        <>
          <ChartCard title={`Receipts vs consumption — ${rows.find((r) => r.material_id === sel)?.code}`} caption={`Per transaction date · ${rows.find((r) => r.material_id === sel)?.unit} · ${fresh()}`}
            rows={led} columns={[['txn_date', 'Date'], ['opening_qty', 'Opening'], ['received_qty', 'Received'], ['rejected_qty', 'Rejected'], ['issued_qty', 'Issued'], ['consumed_qty', 'Consumed'], ['closing_qty', 'Closing'], ['challan_no', 'Challan'], ['heat_batch_no', 'Heat/batch']]} empty={!led.length}>
            <BarsChart data={led.map((l) => ({ ...l, received_qty: Number(l.received_qty), consumed_qty: Number(l.consumed_qty) }))} x="txn_date" series={[['received_qty', 'Received'], ['consumed_qty', 'Consumed']]} />
          </ChartCard>
        </>
      )}
    </div>
  )
}
