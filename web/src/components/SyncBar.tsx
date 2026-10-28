import { Link } from 'react-router-dom'
import { useSyncStatus } from '../lib/hooks'
import { sync } from '../lib/sync'

const ago = (t: number | null) => {
  if (!t) return 'never'
  const s = Math.round((Date.now() - t) / 1000)
  return s < 60 ? 'just now' : s < 3600 ? `${Math.round(s / 60)} min ago` : new Date(t).toLocaleString()
}

/** Always-visible connection and synchronisation status (database and file uploads shown separately). */
export function SyncBar() {
  const s = useSyncStatus()
  const problems = s.failedSaves + s.conflicts + s.failedUploads
  const tone = !s.online ? 'bg-amber-500 text-black' : problems ? 'bg-red-700 text-white' : s.pendingSaves || s.pendingUploads || s.syncing ? 'bg-sky-700 text-white' : 'bg-emerald-800 text-white'
  return (
    <div className={`${tone} text-xs sm:text-sm px-3 py-1.5 flex flex-wrap items-center gap-x-4 gap-y-1`} role="status" aria-live="polite">
      <span className="font-bold">{s.online ? (s.syncing ? '⟳ Synchronising…' : '● Online') : '○ Offline — work is saved on this device'}</span>
      <span>Data: {s.pendingSaves ? `${s.pendingSaves} waiting to sync` : 'synchronised'}{s.conflicts ? ` · ${s.conflicts} conflict(s) need review` : ''}{s.failedSaves ? ` · ${s.failedSaves} failed` : ''}</span>
      <span>Files: {s.pendingUploads ? `${s.pendingUploads} uploading/queued` : 'none queued'}{s.failedUploads ? ` · ${s.failedUploads} failed` : ''}</span>
      <span className="opacity-80">Last sync {ago(s.lastSyncAt)}</span>
      {(problems > 0 || s.pendingSaves > 0) && <Link to="/dpr?tab=device" className="underline font-semibold">Review</Link>}
      {s.online && <button className="underline" onClick={() => void sync.run()}>Sync now</button>}
    </div>
  )
}
