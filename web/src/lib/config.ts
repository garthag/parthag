/**
 * Server connection. A web deployment bakes the address in at build time
 * (VITE_ variables). The Android and Windows apps are generic: on first start
 * they ask for the server address and public key once (or a connection code
 * shared by an administrator) and remember it on the device.
 *
 * Only the PUBLIC key is ever accepted here. Secret / service-role keys are
 * refused because they bypass every permission.
 */
export interface ServerConfig { url: string; anonKey: string; source: 'build' | 'device' }

const KEY = 'cdpr.server'
const CODE_PREFIX = 'CDPR1:'

export function loadConfig(): ServerConfig | null {
  const url = import.meta.env.VITE_SUPABASE_URL as string | undefined
  const anonKey = import.meta.env.VITE_SUPABASE_ANON_KEY as string | undefined
  if (url && anonKey) return { url: url.replace(/\/+$/, ''), anonKey, source: 'build' }
  try {
    const s = JSON.parse(localStorage.getItem(KEY) ?? 'null') as { url?: string; anonKey?: string } | null
    if (s?.url && s.anonKey) return { url: s.url, anonKey: s.anonKey, source: 'device' }
  } catch { /* unreadable storage: treat as not configured */ }
  return null
}

export function saveConfig(url: string, anonKey: string) {
  localStorage.setItem(KEY, JSON.stringify({ url: normaliseUrl(url), anonKey: anonKey.trim() }))
}

export function clearConfig() {
  localStorage.removeItem(KEY)
}

export function normaliseUrl(u: string): string {
  const t = u.trim().replace(/\/+$/, '')
  return /^https?:\/\//i.test(t) ? t : `https://${t}`
}

/** Returns an error message, or null when the URL and key look usable. */
export function checkConfig(url: string, anonKey: string): string | null {
  let parsed: URL
  try { parsed = new URL(normaliseUrl(url)) } catch { return 'The server address is not a valid web address.' }
  if (parsed.protocol !== 'https:' && !['localhost', '127.0.0.1'].includes(parsed.hostname) && !parsed.hostname.startsWith('192.168.')) {
    return 'Use an https:// address (plain http is only allowed for a local test server).'
  }
  const k = anonKey.trim()
  if (!k) return 'Enter the public key.'
  if (/^sb_secret_/.test(k)) return 'That is a SECRET key. Use the publishable (public) key — never put the secret key in an app.'
  const parts = k.split('.')
  if (parts.length === 3) {
    try {
      const payload = JSON.parse(atob(parts[1].replace(/-/g, '+').replace(/_/g, '/')))
      if (payload.role === 'service_role') return 'That is the SERVICE ROLE key. Use the anon (public) key — the service role key bypasses all permissions.'
      if (payload.role !== 'anon') return 'This does not look like the anon (public) key.'
    } catch { return 'The key is not readable — copy it again in full.' }
  } else if (!/^sb_publishable_/.test(k)) {
    return 'This does not look like a Supabase public key (it starts with "sb_publishable_" or "eyJ").'
  }
  return null
}

/** One-line code an administrator can share so colleagues connect without typing. */
export function makeConnectionCode(url: string, anonKey: string): string {
  const json = JSON.stringify({ u: normaliseUrl(url), k: anonKey.trim() })
  return CODE_PREFIX + btoa(unescape(encodeURIComponent(json))).replace(/=+$/, '')
}

export function parseConnectionCode(code: string): { url: string; anonKey: string } | null {
  const c = code.trim()
  try {
    if (c.startsWith(CODE_PREFIX)) {
      const o = JSON.parse(decodeURIComponent(escape(atob(c.slice(CODE_PREFIX.length)))))
      if (typeof o.u === 'string' && typeof o.k === 'string') return { url: o.u, anonKey: o.k }
    }
  } catch { /* fall through */ }
  return null
}

/** Confirms the server answers with this key (Supabase Auth public settings endpoint). */
export async function testConnection(url: string, anonKey: string): Promise<string | null> {
  try {
    const res = await fetch(`${normaliseUrl(url)}/auth/v1/settings`, { headers: { apikey: anonKey.trim() } })
    if (res.ok) return null
    if (res.status === 401 || res.status === 403) return 'The server rejected the key. Copy the public key again.'
    return `The server answered with an error (${res.status}). Check the address.`
  } catch {
    return 'Could not reach the server. Check the address and your internet connection.'
  }
}

/** Accepts ?connect=CODE once (from a shared link), stores it and removes it from the address bar. */
export function consumeConnectLink(): boolean {
  try {
    const u = new URL(location.href)
    const code = u.searchParams.get('connect')
    if (!code) return false
    const parsed = parseConnectionCode(code)
    u.searchParams.delete('connect')
    history.replaceState(null, '', u.pathname + (u.search ? u.search : '') + u.hash)
    if (!parsed || checkConfig(parsed.url, parsed.anonKey)) return false
    saveConfig(parsed.url, parsed.anonKey)
    return true
  } catch {
    return false
  }
}
