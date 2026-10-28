import { useState, type ReactNode } from 'react'
import {
  Bar, BarChart, CartesianGrid, Cell, Legend, Line, LineChart, ResponsiveContainer, Tooltip, XAxis, YAxis,
} from 'recharts'

/** Categorical slots in fixed order (never cycled). Max 4 series per chart; more fold into "Other". */
export const SERIES = ['var(--series-1)', 'var(--series-2)', 'var(--series-3)', 'var(--series-4)']

const axis = { stroke: 'var(--chart-axis)', fontSize: 12, tickLine: false }
const tooltipStyle = { contentStyle: { background: 'var(--color-white, #fff)', borderRadius: 8, border: '1px solid #ccc', color: '#111', fontSize: 13 } }

/**
 * Chart frame: title, a caption stating range / unit / method / freshness,
 * and a "Table" toggle so values are never available only as colour.
 */
export function ChartCard({ title, caption, rows, columns, children, empty }: {
  title: string; caption: ReactNode; rows: Array<Record<string, unknown>>; columns: Array<[string, string]>; children: ReactNode; empty?: boolean
}) {
  const [table, setTable] = useState(false)
  return (
    <section className="card p-4">
      <div className="flex items-start justify-between gap-2">
        <h2 className="font-bold">{title}</h2>
        <button className="btn btn-ghost px-2 min-h-9 text-sm" onClick={() => setTable((t) => !t)} aria-pressed={table}>{table ? 'Chart' : 'Table'}</button>
      </div>
      <p className="text-xs text-slate-500 mb-2">{caption}</p>
      {empty ? <p className="py-10 text-center text-sm text-slate-500">No data for the selected filters.</p> : table ? (
        <div className="max-h-72 overflow-auto">
          <table className="table"><thead><tr>{columns.map(([k, l]) => <th key={k}>{l}</th>)}</tr></thead>
            <tbody>{rows.map((r, i) => <tr key={i}>{columns.map(([k]) => <td key={k} className={typeof r[k] === 'number' ? 'num' : ''}>{typeof r[k] === 'number' ? (r[k] as number).toLocaleString('en-IN', { maximumFractionDigits: 2 }) : String(r[k] ?? '—')}</td>)}</tr>)}</tbody></table>
        </div>
      ) : <div className="h-64">{children}</div>}
    </section>
  )
}

export function LinesChart({ data, x, series, unit }: { data: Array<Record<string, unknown>>; x: string; series: Array<[string, string]>; unit?: string }) {
  return (
    <ResponsiveContainer>
      <LineChart data={data} margin={{ top: 8, right: 12, bottom: 0, left: 0 }}>
        <CartesianGrid stroke="var(--chart-grid)" vertical={false} />
        <XAxis dataKey={x} {...axis} minTickGap={24} />
        <YAxis {...axis} width={44} unit={unit} />
        <Tooltip {...tooltipStyle} formatter={(v) => `${Number(v).toLocaleString('en-IN', { maximumFractionDigits: 2 })}${unit ?? ''}`} />
        {series.length > 1 && <Legend wrapperStyle={{ fontSize: 12 }} />}
        {series.slice(0, 4).map(([k, label], i) => (
          <Line key={k} type="monotone" dataKey={k} name={label} stroke={SERIES[i]} strokeWidth={2} dot={false} activeDot={{ r: 5 }} connectNulls={false} isAnimationActive={false} />
        ))}
      </LineChart>
    </ResponsiveContainer>
  )
}

export function BarsChart({ data, x, series, unit, layout = 'horizontal', colorBy }: {
  data: Array<Record<string, unknown>>; x: string; series: Array<[string, string]>; unit?: string; layout?: 'horizontal' | 'vertical'; colorBy?: (row: Record<string, unknown>) => string
}) {
  const vertical = layout === 'vertical'
  return (
    <ResponsiveContainer>
      <BarChart data={data} layout={vertical ? 'vertical' : 'horizontal'} margin={{ top: 8, right: 12, bottom: 0, left: vertical ? 8 : 0 }} barCategoryGap={vertical ? 6 : '20%'}>
        <CartesianGrid stroke="var(--chart-grid)" horizontal={!vertical} vertical={vertical} />
        {vertical ? <><XAxis type="number" {...axis} unit={unit} /><YAxis type="category" dataKey={x} {...axis} width={110} /></>
          : <><XAxis dataKey={x} {...axis} minTickGap={8} /><YAxis {...axis} width={44} unit={unit} /></>}
        <Tooltip {...tooltipStyle} cursor={{ fill: 'rgba(127,127,127,0.12)' }} formatter={(v) => `${Number(v).toLocaleString('en-IN', { maximumFractionDigits: 2 })}${unit ?? ''}`} />
        {series.length > 1 && <Legend wrapperStyle={{ fontSize: 12 }} />}
        {series.slice(0, 4).map(([k, label], i) => (
          <Bar key={k} dataKey={k} name={label} fill={SERIES[i]} radius={vertical ? [0, 4, 4, 0] : [4, 4, 0, 0]} stroke="var(--color-white, #fff)" strokeWidth={series.length > 1 ? 1 : 0} isAnimationActive={false}>
            {colorBy && data.map((r, j) => <Cell key={j} fill={colorBy(r)} />)}
          </Bar>
        ))}
      </BarChart>
    </ResponsiveContainer>
  )
}
