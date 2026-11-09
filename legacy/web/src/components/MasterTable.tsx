import { useMemo, useState } from 'react'
import { supabase, friendlyError } from '../lib/supabase'
import { useLoad, useRealtime } from '../lib/hooks'
import { download, toCsv } from '../lib/csv'
import { Alert, Card, Empty, Modal, Spinner } from './ui'

export interface Column {
  key: string
  label: string
  type?: 'text' | 'number' | 'date' | 'select' | 'bool' | 'textarea'
  options?: Array<[string, string]>
  required?: boolean
  readOnly?: boolean // shown, never edited
  createOnly?: boolean // entered when adding; changed later only through a dedicated workflow
  hideInList?: boolean
  render?: (row: Row) => React.ReactNode
}
export type Row = Record<string, unknown> & { id: string; version?: number }

/**
 * Generic list + add/edit/delete for a project- or organisation-scoped table.
 * Writes go straight to PostgREST; Row Level Security decides what is allowed.
 * Updates carry the row version so concurrent edits are detected, not lost.
 */
export function MasterTable({ title, table, scope, columns, canCreate, canEdit, canDelete, orderBy = 'code', defaults = {}, extraActions, select = '*' }: {
  title: string
  table: string
  scope: { column: 'project_id' | 'organization_id'; value: string }
  columns: Column[]
  canCreate: boolean
  canEdit: boolean
  canDelete: boolean
  orderBy?: string
  defaults?: Record<string, unknown>
  extraActions?: (row: Row, reload: () => void) => React.ReactNode
  select?: string
}) {
  const { data, error, loading, reload } = useLoad(async () => {
    const { data, error } = await supabase.from(table).select(select).eq(scope.column, scope.value).order(orderBy)
    if (error) throw error
    return data as unknown as Row[]
  }, [table, scope.value])
  useRealtime(table, scope.column === 'project_id' ? scope.value : null, () => void reload())
  const [q, setQ] = useState('')
  const [editing, setEditing] = useState<Row | 'new' | null>(null)
  const [form, setForm] = useState<Record<string, unknown>>({})
  const [msg, setMsg] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const rows = useMemo(() => (data ?? []).filter((r) => !q || JSON.stringify(r).toLowerCase().includes(q.toLowerCase())), [data, q])

  const open = (r: Row | 'new') => {
    setMsg(null)
    setEditing(r)
    setForm(r === 'new' ? { ...defaults } : { ...r })
  }

  const save = async () => {
    setBusy(true)
    setMsg(null)
    try {
      const body: Record<string, unknown> = {}
      for (const c of columns) {
        if (c.readOnly || (c.createOnly && editing !== 'new')) continue
        let v = form[c.key]
        if (v === '' || v === undefined) v = null
        if (c.type === 'number' && v != null) v = Number(v)
        if (c.required && (v == null || v === '')) throw new Error(`${c.label} is required`)
        body[c.key] = v
      }
      if (editing === 'new') {
        const { error } = await supabase.from(table).insert({ ...defaults, ...body, [scope.column]: scope.value })
        if (error) throw error
      } else if (editing) {
        const { data: upd, error } = await supabase.from(table).update(body).eq('id', editing.id).eq('version', editing.version ?? 1).select('id')
        if (error) throw error
        if (!upd?.length) throw new Error('Not saved: someone else changed this record (or you lack permission). Reload and try again.')
      }
      setEditing(null)
      await reload()
    } catch (e) {
      setMsg(friendlyError(e))
    } finally {
      setBusy(false)
    }
  }

  const remove = async (r: Row) => {
    if (!confirm('Delete this record? This is recorded in the audit trail.')) return
    const { error } = await supabase.from(table).delete().eq('id', r.id)
    if (error) alert(friendlyError(error))
    else await reload()
  }

  const shown = columns.filter((c) => !c.hideInList)
  const fmt = (c: Column, r: Row) => {
    if (c.render) return c.render(r)
    const v = r[c.key]
    if (c.type === 'bool') return v ? 'Yes' : 'No'
    if (c.type === 'select') return c.options?.find(([k]) => k === v)?.[1] ?? (v as string) ?? '—'
    if (c.type === 'number' && v != null) return Number(v).toLocaleString('en-IN', { maximumFractionDigits: 4 })
    return (v as string) ?? '—'
  }

  return (
    <Card title={title} actions={<>
      <button className="btn btn-secondary" disabled={!rows.length} onClick={() => download(`${table}.csv`, toCsv(rows, ['id', ...columns.map((c) => c.key)]))}>Export CSV</button>
      {canCreate && <button className="btn btn-primary" onClick={() => open('new')}>＋ Add</button>}
    </>}>
      <input className="input mb-3" type="search" placeholder={`Search ${title.toLowerCase()}…`} value={q} onChange={(e) => setQ(e.target.value)} aria-label={`Search ${title}`} />
      {error && <Alert>{error}</Alert>}
      {loading && !data ? <Spinner /> : !rows.length ? <Empty title="Nothing here yet" /> : (
        <div className="overflow-x-auto">
          <table className="table">
            <thead><tr>{shown.map((c) => <th key={c.key} className={c.type === 'number' ? 'num' : ''}>{c.label}</th>)}<th /></tr></thead>
            <tbody>
              {rows.map((r) => (
                <tr key={r.id}>
                  {shown.map((c) => <td key={c.key} className={c.type === 'number' ? 'num' : ''}>{fmt(c, r)}</td>)}
                  <td className="whitespace-nowrap text-right">
                    {extraActions?.(r, () => void reload())}
                    {canEdit && <button className="btn btn-ghost px-2" onClick={() => open(r)}>Edit</button>}
                    {canDelete && <button className="btn btn-ghost px-2 text-red-700" onClick={() => void remove(r)}>Delete</button>}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      <Modal open={editing != null} onClose={() => setEditing(null)} title={editing === 'new' ? `Add — ${title}` : `Edit — ${title}`}>
        <form className="grid gap-3" onSubmit={(e) => { e.preventDefault(); void save() }}>
          {msg && <Alert>{msg}</Alert>}
          {columns.filter((c) => !c.readOnly && !(c.createOnly && editing !== 'new')).map((c) => {
            const id = `f-${c.key}`
            const v = form[c.key]
            const set = (val: unknown) => setForm((f) => ({ ...f, [c.key]: val }))
            return (
              <div key={c.key}>
                <label htmlFor={id} className="label">{c.label}{c.required && ' *'}</label>
                {c.type === 'select' ? (
                  <select id={id} className="input" value={(v as string) ?? ''} onChange={(e) => set(e.target.value)}>
                    <option value="">—</option>
                    {c.options?.map(([k, l]) => <option key={k} value={k}>{l}</option>)}
                  </select>
                ) : c.type === 'bool' ? (
                  <input id={id} type="checkbox" className="h-6 w-6" checked={!!v} onChange={(e) => set(e.target.checked)} />
                ) : c.type === 'textarea' ? (
                  <textarea id={id} className="input" value={(v as string) ?? ''} onChange={(e) => set(e.target.value)} />
                ) : (
                  <input id={id} className={`input ${c.type === 'number' ? 'num' : ''}`} type={c.type === 'number' ? 'number' : c.type === 'date' ? 'date' : 'text'}
                    step="any" inputMode={c.type === 'number' ? 'decimal' : undefined} value={(v as string) ?? ''} required={c.required}
                    onChange={(e) => set(e.target.value)} />
                )}
              </div>
            )
          })}
          <button className="btn btn-primary" disabled={busy}>{busy ? 'Saving…' : 'Save'}</button>
        </form>
      </Modal>
    </Card>
  )
}
