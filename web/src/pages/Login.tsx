import { useState } from 'react'
import { supabase, friendlyError, isConfigured, serverConfig } from '../lib/supabase'
import { checkConfig, clearConfig, parseConnectionCode, saveConfig, testConnection } from '../lib/config'
import { deviceLabel, isAppShell } from '../lib/platform'
import { Alert, TextInput } from '../components/ui'

/** First start of the Android / Windows app (or an unconfigured web build): connect to the company server once. */
export function SetupRequired() {
  const [code, setCode] = useState('')
  const [url, setUrl] = useState('')
  const [key, setKey] = useState('')
  const [msg, setMsg] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const connect = async (u: string, k: string) => {
    setMsg(null)
    const bad = checkConfig(u, k)
    if (bad) return setMsg(bad)
    setBusy(true)
    const err = await testConnection(u, k)
    setBusy(false)
    if (err) return setMsg(err)
    saveConfig(u, k)
    location.reload()
  }
  return (
    <div className="min-h-dvh grid place-items-center p-4 bg-gradient-to-b from-brand-700 to-brand-900">
      <div className="card w-full max-w-md p-6 grid gap-4">
        <div className="flex items-center gap-3">
          <img src="/icon.svg" alt="" className="h-12 w-12" />
          <div><h1 className="text-2xl font-extrabold">Connect to your server</h1><p className="text-sm text-slate-500">One-time setup on this device ({deviceLabel()})</p></div>
        </div>
        <form className="grid gap-2" onSubmit={(e) => { e.preventDefault(); const p = parseConnectionCode(code); if (!p) return setMsg('That connection code is not valid. Ask your administrator to copy it again.'); void connect(p.url, p.anonKey) }}>
          <TextInput label="Connection code from your administrator" value={code} onChange={(e) => setCode(e.target.value)} placeholder="CDPR1:…" autoComplete="off" />
          <button className="btn btn-primary" disabled={busy || !code.trim()}>{busy ? 'Checking…' : 'Connect'}</button>
        </form>
        <details>
          <summary className="cursor-pointer text-sm font-semibold">Or enter the server address and public key</summary>
          <form className="grid gap-2 mt-2" onSubmit={(e) => { e.preventDefault(); void connect(url, key) }}>
            <TextInput label="Server address (Supabase Project URL)" value={url} onChange={(e) => setUrl(e.target.value)} placeholder="https://abcdefgh.supabase.co" inputMode="url" autoComplete="off" />
            <TextInput label="Public key (publishable / anon)" value={key} onChange={(e) => setKey(e.target.value)} autoComplete="off" />
            <button className="btn btn-secondary" disabled={busy || !url || !key}>{busy ? 'Checking…' : 'Connect'}</button>
          </form>
        </details>
        {msg && <Alert>{msg}</Alert>}
        <p className="text-xs text-slate-500">Administrators find the code in <b>Settings → Connect other devices</b>. Only the public key is used; the app refuses secret keys.</p>
      </div>
    </div>
  )
}

export function Login() {
  const [mode, setMode] = useState<'signin' | 'signup' | 'magic'>('signin')
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [name, setName] = useState('')
  const [msg, setMsg] = useState<{ tone: 'red' | 'green'; text: string } | null>(null)
  const [busy, setBusy] = useState(false)
  if (!isConfigured) return <SetupRequired />

  const submit = async (e: React.FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setMsg(null)
    try {
      if (mode === 'signin') {
        const { error } = await supabase.auth.signInWithPassword({ email, password })
        if (error) throw error
      } else if (mode === 'signup') {
        const { error } = await supabase.auth.signUp({ email, password, options: { data: { full_name: name } } })
        if (error) throw error
        setMsg({ tone: 'green', text: 'Account created. If e-mail confirmation is enabled, check your inbox, then sign in.' })
      } else {
        const { error } = await supabase.auth.signInWithOtp({ email, options: { emailRedirectTo: location.origin } })
        if (error) throw error
        setMsg({ tone: 'green', text: 'Check your e-mail for a sign-in link.' })
      }
    } catch (err) {
      setMsg({ tone: 'red', text: friendlyError(err) })
    } finally {
      setBusy(false)
    }
  }

  const google = async () => {
    const { error } = await supabase.auth.signInWithOAuth({ provider: 'google', options: { redirectTo: location.origin } })
    if (error) setMsg({ tone: 'red', text: `${friendlyError(error)} — is the Google provider enabled in Supabase Auth?` })
  }

  return (
    <div className="min-h-dvh grid place-items-center p-4 bg-gradient-to-b from-brand-700 to-brand-900">
      <div className="card w-full max-w-md p-6">
        <div className="flex items-center gap-3 mb-5">
          <img src="/icon.svg" alt="" className="h-12 w-12" />
          <div>
            <h1 className="text-2xl font-extrabold">CONSTRUCT DPR</h1>
            <p className="text-sm text-slate-500">Daily progress reporting & site management</p>
          </div>
        </div>
        {isAppShell() ? (
          <p className="text-sm text-slate-600 dark:text-slate-400 mb-4">Sign in with your e-mail and password. (Google sign-in is available in the browser version.)</p>
        ) : (
        <button className="btn btn-secondary w-full mb-4" onClick={() => void google()}>
          <svg aria-hidden width="18" height="18" viewBox="0 0 48 48"><path fill="#FFC107" d="M43.6 20.5H42V20H24v8h11.3C33.7 32.7 29.2 36 24 36c-6.6 0-12-5.4-12-12s5.4-12 12-12c3 0 5.8 1.1 7.9 3l5.7-5.7C34 6.1 29.3 4 24 4 12.9 4 4 12.9 4 24s8.9 20 20 20 20-8.9 20-20c0-1.3-.1-2.4-.4-3.5z"/><path fill="#FF3D00" d="m6.3 14.7 6.6 4.8C14.7 15.1 19 12 24 12c3 0 5.8 1.1 7.9 3l5.7-5.7C34 6.1 29.3 4 24 4 16.3 4 9.7 8.3 6.3 14.7z"/><path fill="#4CAF50" d="M24 44c5.2 0 9.9-2 13.4-5.2l-6.2-5.2C29.2 35.1 26.7 36 24 36c-5.2 0-9.6-3.3-11.3-8l-6.5 5C9.5 39.6 16.2 44 24 44z"/><path fill="#1976D2" d="M43.6 20.5H42V20H24v8h11.3c-.8 2.2-2.2 4.2-4.1 5.6l6.2 5.2C37 39.2 44 34 44 24c0-1.3-.1-2.4-.4-3.5z"/></svg>
          Continue with Google
        </button>
        )}
        {!isAppShell() && <div className="text-center text-xs text-slate-500 mb-4">or use e-mail</div>}
        <form onSubmit={submit} className="flex flex-col gap-3">
          {mode === 'signup' && <TextInput label="Full name" value={name} onChange={(e) => setName(e.target.value)} required autoComplete="name" />}
          <TextInput label="E-mail" type="email" value={email} onChange={(e) => setEmail(e.target.value)} required autoComplete="email" />
          {mode !== 'magic' && (
            <TextInput label="Password" type="password" value={password} onChange={(e) => setPassword(e.target.value)} required minLength={8}
              autoComplete={mode === 'signup' ? 'new-password' : 'current-password'} hint={mode === 'signup' ? 'At least 8 characters' : undefined} />
          )}
          {msg && <Alert tone={msg.tone}>{msg.text}</Alert>}
          <button className="btn btn-primary" disabled={busy}>{busy ? 'Please wait…' : mode === 'signin' ? 'Sign in' : mode === 'signup' ? 'Create account' : 'Send sign-in link'}</button>
        </form>
        <div className="mt-4 flex flex-wrap justify-between text-sm">
          {mode !== 'signin' && <button className="btn btn-ghost px-1" onClick={() => setMode('signin')}>Sign in with password</button>}
          {mode !== 'signup' && <button className="btn btn-ghost px-1" onClick={() => setMode('signup')}>Create account</button>}
          {mode !== 'magic' && <button className="btn btn-ghost px-1" onClick={() => setMode('magic')}>E-mail me a link</button>}
        </div>
        {serverConfig?.source === 'device' && (
          <button className="btn btn-ghost px-1 text-sm" onClick={() => { if (confirm('Disconnect this device from ' + serverConfig?.url + '?')) { clearConfig(); location.reload() } }}>
            Connected to {new URL(serverConfig.url).host} · change server
          </button>
        )}
        <p className="mt-4 text-xs text-slate-500">
          Signing in stores a session on this device. Your project data is stored in your organisation's database; photos and documents in its Google Drive. See Settings → Privacy.
        </p>
      </div>
    </div>
  )
}
