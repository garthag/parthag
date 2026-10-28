import { useState } from 'react'
import { addDays } from '../lib/schedule/calendar'
import { today } from '../lib/types'

/** Shared date-range filter (one row, above the charts). */
export function useRange(days = 30) {
  const [from, setFrom] = useState(addDays(today(), -days + 1))
  const [to, setTo] = useState(today())
  const ui = (
    <div className="flex flex-wrap gap-2 items-end">
      {[7, 30, 90].map((d) => <button key={d} className="btn btn-secondary min-h-10" onClick={() => { setFrom(addDays(today(), -d + 1)); setTo(today()) }}>{d} days</button>)}
      <label className="text-sm">From <input className="input w-auto" type="date" value={from} onChange={(e) => setFrom(e.target.value)} /></label>
      <label className="text-sm">To <input className="input w-auto" type="date" value={to} onChange={(e) => setTo(e.target.value)} /></label>
    </div>
  )
  return { from, to, ui }
}

export const fresh = () => `Data as of ${new Date().toLocaleTimeString('en-IN', { hour: '2-digit', minute: '2-digit' })}; approved reports only (latest revision).`
