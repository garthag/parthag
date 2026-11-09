import { useRegisterSW } from 'virtual:pwa-register/react'

/** Tells the user when a new version is ready instead of swapping code mid-entry. */
export function UpdatePrompt() {
  const { needRefresh: [needRefresh, setNeedRefresh], offlineReady: [offlineReady, setOfflineReady], updateServiceWorker } = useRegisterSW()
  if (!needRefresh && !offlineReady) return null
  return (
    <div role="status" className="fixed bottom-20 lg:bottom-4 right-4 left-4 sm:left-auto z-50 card p-3 flex items-center gap-3 shadow-lg">
      <span className="text-sm">{needRefresh ? 'A new version is available. Your drafts are safe.' : 'App ready to work offline.'}</span>
      {needRefresh && <button className="btn btn-primary" onClick={() => void updateServiceWorker(true)}>Update</button>}
      <button className="btn btn-ghost" onClick={() => { setNeedRefresh(false); setOfflineReady(false) }}>Dismiss</button>
    </div>
  )
}
