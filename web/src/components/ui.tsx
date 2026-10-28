import { useEffect, useId, useRef, type ReactNode, type InputHTMLAttributes, type SelectHTMLAttributes, type TextareaHTMLAttributes } from 'react'
import type { DprStatus } from '../lib/types'

export function Field({ label, hint, error, children, className = '' }: { label: string; hint?: string; error?: string; children: (id: string) => ReactNode; className?: string }) {
  const id = useId()
  return (
    <div className={className}>
      <label htmlFor={id} className="label">{label}</label>
      {children(id)}
      {hint && !error && <p className="mt-1 text-xs text-slate-500">{hint}</p>}
      {error && <p role="alert" className="mt-1 text-sm text-red-700 dark:text-red-400">{error}</p>}
    </div>
  )
}

type InputProps = InputHTMLAttributes<HTMLInputElement> & { label: string; hint?: string; error?: string }
export function TextInput({ label, hint, error, className, ...rest }: InputProps) {
  return <Field label={label} hint={hint} error={error} className={className}>{(id) => <input id={id} className="input" aria-invalid={!!error} {...rest} />}</Field>
}

/** Numeric input that reports numbers (or null when blank). Uses the decimal keypad on phones. */
export function NumberInput({ label, value, onChange, hint, error, className, min = 0, step = 'any', ...rest }:
  Omit<InputProps, 'value' | 'onChange'> & { value: number | null | undefined; onChange: (v: number | null) => void }) {
  return (
    <Field label={label} hint={hint} error={error} className={className}>
      {(id) => (
        <input id={id} className="input num" type="number" inputMode="decimal" min={min} step={step} aria-invalid={!!error}
          value={value ?? ''} onChange={(e) => onChange(e.target.value === '' ? null : Number(e.target.value))} {...rest} />
      )}
    </Field>
  )
}

export function Select({ label, hint, error, className, children, ...rest }: SelectHTMLAttributes<HTMLSelectElement> & { label: string; hint?: string; error?: string }) {
  return <Field label={label} hint={hint} error={error} className={className}>{(id) => <select id={id} className="input" {...rest}>{children}</select>}</Field>
}

export function TextArea({ label, hint, error, className, ...rest }: TextareaHTMLAttributes<HTMLTextAreaElement> & { label: string; hint?: string; error?: string }) {
  return <Field label={label} hint={hint} error={error} className={className}>{(id) => <textarea id={id} className="input min-h-20" rows={3} {...rest} />}</Field>
}

export function Card({ title, actions, children, className = '' }: { title?: ReactNode; actions?: ReactNode; children: ReactNode; className?: string }) {
  return (
    <section className={`card p-4 ${className}`}>
      {(title || actions) && (
        <div className="flex flex-wrap items-center justify-between gap-2 mb-3">
          {title && <h2 className="text-lg font-bold">{title}</h2>}
          {actions && <div className="flex flex-wrap gap-2">{actions}</div>}
        </div>
      )}
      {children}
    </section>
  )
}

export function PageHeader({ title, subtitle, actions }: { title: string; subtitle?: ReactNode; actions?: ReactNode }) {
  return (
    <header className="flex flex-wrap items-end justify-between gap-3 mb-4">
      <div>
        <h1 className="text-2xl font-extrabold tracking-tight">{title}</h1>
        {subtitle && <p className="text-sm text-slate-600 dark:text-slate-400">{subtitle}</p>}
      </div>
      {actions && <div className="flex flex-wrap gap-2">{actions}</div>}
    </header>
  )
}

const TONES = {
  gray: 'bg-slate-200 text-slate-800 dark:bg-slate-700 dark:text-slate-100',
  blue: 'bg-sky-100 text-sky-900 dark:bg-sky-900 dark:text-sky-100',
  green: 'bg-emerald-100 text-emerald-900 dark:bg-emerald-900 dark:text-emerald-100',
  amber: 'bg-amber-100 text-amber-900 dark:bg-amber-900 dark:text-amber-100',
  red: 'bg-red-100 text-red-900 dark:bg-red-900 dark:text-red-100',
  purple: 'bg-violet-100 text-violet-900 dark:bg-violet-900 dark:text-violet-100',
}
export type Tone = keyof typeof TONES
export function Badge({ tone = 'gray', children }: { tone?: Tone; children: ReactNode }) {
  return <span className={`inline-flex items-center rounded-full px-2.5 py-0.5 text-xs font-bold whitespace-nowrap ${TONES[tone]}`}>{children}</span>
}

const STATUS: Record<DprStatus, [Tone, string]> = {
  draft: ['gray', 'Draft'], submitted: ['blue', 'Submitted'], under_review: ['purple', 'Under review'],
  approved: ['green', 'Approved'], rejected: ['red', 'Rejected'],
}
export function DprStatusBadge({ status, revision = 0 }: { status: DprStatus; revision?: number }) {
  const [tone, label] = STATUS[status] ?? ['gray', status]
  return <span className="inline-flex gap-1"><Badge tone={tone}>{label}</Badge>{revision > 0 && <Badge tone="amber">Revised · R{revision}</Badge>}</span>
}

export function Spinner({ label = 'Loading…' }: { label?: string }) {
  return (
    <div role="status" className="flex items-center gap-3 p-6 text-slate-600 dark:text-slate-300">
      <span className="h-5 w-5 animate-spin rounded-full border-2 border-brand-500 border-t-transparent" aria-hidden />
      {label}
    </div>
  )
}

export function Empty({ title, children }: { title: string; children?: ReactNode }) {
  return (
    <div className="rounded-xl border-2 border-dashed border-slate-300 dark:border-slate-700 p-6 text-center">
      <p className="font-semibold">{title}</p>
      {children && <div className="mt-2 text-sm text-slate-600 dark:text-slate-400">{children}</div>}
    </div>
  )
}

export function Alert({ tone = 'red', children }: { tone?: 'red' | 'amber' | 'green' | 'blue'; children: ReactNode }) {
  const cls = {
    red: 'bg-red-50 border-red-300 text-red-900 dark:bg-red-950 dark:border-red-800 dark:text-red-100',
    amber: 'bg-amber-50 border-amber-300 text-amber-900 dark:bg-amber-950 dark:border-amber-800 dark:text-amber-100',
    green: 'bg-emerald-50 border-emerald-300 text-emerald-900 dark:bg-emerald-950 dark:border-emerald-800 dark:text-emerald-100',
    blue: 'bg-sky-50 border-sky-300 text-sky-900 dark:bg-sky-950 dark:border-sky-800 dark:text-sky-100',
  }[tone]
  return <div role={tone === 'red' ? 'alert' : 'status'} className={`rounded-lg border p-3 text-sm ${cls}`}>{children}</div>
}

/** Accessible modal built on the native <dialog> element (focus trap + Esc handled by the browser). */
export function Modal({ open, onClose, title, children, wide }: { open: boolean; onClose: () => void; title: string; children: ReactNode; wide?: boolean }) {
  const ref = useRef<HTMLDialogElement>(null)
  useEffect(() => {
    const d = ref.current
    if (!d) return
    if (open && !d.open) d.showModal()
    if (!open && d.open) d.close()
  }, [open])
  return (
    <dialog ref={ref} onClose={onClose} aria-label={title}
      className={`m-auto w-[calc(100%-1rem)] ${wide ? 'max-w-4xl' : 'max-w-lg'} rounded-xl p-0 backdrop:bg-black/50 bg-white text-slate-900 dark:bg-slate-900 dark:text-slate-100`}>
      {open && (
        <div className="p-4 max-h-[85vh] overflow-y-auto">
          <div className="flex items-center justify-between mb-3">
            <h2 className="text-lg font-bold">{title}</h2>
            <button className="btn btn-ghost px-3" onClick={onClose} aria-label="Close">✕</button>
          </div>
          {children}
        </div>
      )}
    </dialog>
  )
}

export function Tabs<T extends string>({ tabs, value, onChange }: { tabs: Array<[T, string]>; value: T; onChange: (t: T) => void }) {
  return (
    <div role="tablist" className="flex gap-1 overflow-x-auto border-b border-slate-200 dark:border-slate-700 mb-4">
      {tabs.map(([k, label]) => (
        <button key={k} role="tab" aria-selected={value === k} onClick={() => onChange(k)}
          className={`min-h-11 px-4 font-semibold whitespace-nowrap border-b-3 -mb-px ${value === k ? 'border-brand-600 text-brand-700 dark:text-brand-100' : 'border-transparent text-slate-500'}`}>
          {label}
        </button>
      ))}
    </div>
  )
}

export function Stat({ label, value, sub, tone }: { label: string; value: ReactNode; sub?: ReactNode; tone?: 'good' | 'bad' | 'warn' }) {
  const color = tone === 'good' ? 'text-emerald-700 dark:text-emerald-400' : tone === 'bad' ? 'text-red-700 dark:text-red-400' : tone === 'warn' ? 'text-amber-700 dark:text-amber-400' : ''
  return (
    <div className="card p-3">
      <div className="text-xs font-semibold uppercase tracking-wide text-slate-500">{label}</div>
      <div className={`text-2xl font-extrabold tabular-nums ${color}`}>{value}</div>
      {sub && <div className="text-xs text-slate-500">{sub}</div>}
    </div>
  )
}
