import { useState } from 'react'
import { serverConfig } from '../lib/supabase'
import { makeConnectionCode } from '../lib/config'
import { deviceLabel, platform } from '../lib/platform'
import { Card } from './ui'

/**
 * Lets anyone signed in hand the server connection to another device: the
 * Android app, the Windows app, or a colleague's browser. The code holds only
 * the public address and public key — never a password or secret.
 */
export function ConnectDevicesCard() {
  const [copied, setCopied] = useState<string | null>(null)
  if (!serverConfig) return null
  const code = makeConnectionCode(serverConfig.url, serverConfig.anonKey)
  const webBase = platform() === 'web' ? location.origin : null
  const link = webBase ? `${webBase}/?connect=${encodeURIComponent(code)}` : null
  const copy = async (text: string, what: string) => {
    try { await navigator.clipboard.writeText(text); setCopied(what) } catch { setCopied(null) }
  }
  return (
    <Card title="Connect other devices (Android app, Windows app, browsers)">
      <p className="text-sm mb-2">This device: <b>{deviceLabel()}</b>, connected to <b>{new URL(serverConfig.url).host}</b>. Everything you do here appears live on every other connected phone and PC.</p>
      <ol className="text-sm list-decimal pl-5 mb-3 space-y-1">
        <li>Install the app on the other device (see Settings → Download the apps, or docs/APPS.md).</li>
        <li>On its first start, paste this <b>connection code</b> and tap <b>Connect</b>.</li>
        <li>Sign in with that person's own e-mail and password.</li>
      </ol>
      <div className="flex gap-2">
        <input className="input font-mono text-xs" readOnly value={code} aria-label="Connection code" onFocus={(e) => e.target.select()} />
        <button className="btn btn-primary" onClick={() => void copy(code, 'code')}>{copied === 'code' ? 'Copied' : 'Copy code'}</button>
      </div>
      {link && <button className="btn btn-ghost mt-2 px-0" onClick={() => void copy(link, 'link')}>{copied === 'link' ? 'Link copied' : 'Copy a ready-to-open link for browsers'}</button>}
      <p className="mt-2 text-xs text-slate-500">The code contains only the server address and its public key, which every installed copy of the app needs anyway. Access is still controlled by each person's sign-in and project role.</p>
    </Card>
  )
}
