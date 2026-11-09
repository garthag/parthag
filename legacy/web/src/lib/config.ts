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
  localStorage.setItem(KEY, JSON.stringify({ url: cleanUrl(url), anonKey: cleanKey(anonKey) }))
}

export function clearConfig() {
  localStorage.removeItem(KEY)
}

export function normaliseUrl(u: string): string {
  const t = u.trim().replace(/\/+$/, '')
  return /^https?:\/\//i.test(t) ? t : `https://${t}`
}

/**
 * Picks the server address out of whatever was pasted: the Project URL,
 * the Supabase dashboard address of the project, or a URL with extra paths.
 */
export function cleanUrl(raw: string): string {
  const t = raw.trim()
  const dash = /supabase\.com\/dashboard\/project\/([a-z0-9]{8,})/i.exec(t)
  if (dash) return `https://${dash[1].toLowerCase()}.supabase.co`
  const proj = /([a-z0-9]{8,})\.supabase\.(co|in)/i.exec(t)
  if (proj) return `https://${proj[1].toLowerCase()}.supabase.${proj[2].toLowerCase()}`
  try {
    const u = new URL(normaliseUrl(t))
    return `${u.protocol}//${u.host}`
  } catch {
    return normaliseUrl(t)
  }
}

/**
 * Picks the key out of whatever was pasted: copying from the dashboard often
 * brings labels ("anon public"), quotes, spaces or line breaks with it.
 */
export function cleanKey(raw: string): string {
  const find = (t: string) =>
    /sb_(publishable|secret)_[A-Za-z0-9_-]+/.exec(t)?.[0] ?? /eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+/.exec(t)?.[0]
  // Keys never contain spaces, so first look in the text as pasted (labels stay
  // separate); only if that fails, join lines in case the key wrapped.
  const asPasted = find(raw)
  if (asPasted && (!asPasted.startsWith('eyJ') || jwtPayload(asPasted))) return asPasted
  const joined = raw.replace(/\s+/g, '')
  return find(joined) ?? joined.replace(/^["'`]+|["'`,;]+$/g, '')
}

function jwtPayload(jwt: string): { role?: string; ref?: string } | null {
  try {
    let b = jwt.split('.')[1].replace(/-/g, '+').replace(/_/g, '/')
    while (b.length % 4) b += '='
    return JSON.parse(atob(b))
  } catch {
    return null
  }
}

/** Returns an error message, or null when the URL and key look usable. */
export function checkConfig(url: string, anonKey: string): string | null {
  const u = cleanUrl(url)
  let parsed: URL
  try { parsed = new URL(u) } catch { return 'The server address is not a valid web address. Use the Project URL, e.g. https://abcdefgh.supabase.co' }
  if (parsed.protocol !== 'https:' && !['localhost', '127.0.0.1'].includes(parsed.hostname) && !parsed.hostname.startsWith('192.168.')) {
    return 'Use an https:// address (plain http is only allowed for a local test server).'
  }
  const k = cleanKey(anonKey)
  if (!k) return 'Enter the public key.'
  if (/^https?:|supabase\.co/i.test(k)) return 'The key box contains a web address. Put the Project URL in the first box and the public key (starting with "sb_publishable_" or "eyJ") in the second.'
  if (/^sb_secret_/.test(k)) return 'That is the SECRET key. In Supabase → Project Settings → API Keys copy the PUBLISHABLE key (sb_publishable_…) instead.'
  if (/^sb_publishable_/.test(k)) return k.length < 30 ? 'The publishable key looks cut off. Use the copy button next to it in Supabase and paste again.' : null
  if (k.startsWith('eyJ')) {
    const p = jwtPayload(k)
    if (!p) return `The key is incomplete (${k.length} characters). The anon key is about 200 characters long — use the copy button next to it in Supabase and paste again.`
    if (p.role === 'service_role') return 'That is the SERVICE ROLE key. Under "Legacy API keys" copy the key labelled "anon public" instead.'
    if (p.role !== 'anon') return `This key is for "${p.role ?? 'unknown'}", not the public key. Copy the "anon public" or "publishable" key.`
    return null
  }
  return `This is not a Supabase public key (what was pasted starts with "${k.slice(0, 12)}…"). It must start with "sb_publishable_" or "eyJ".`
}

/** One-line code an administrator can share so colleagues connect without typing. */
export function makeConnectionCode(url: string, anonKey: string): string {
  const json = JSON.stringify({ u: cleanUrl(url), k: cleanKey(anonKey) })
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
    const res = await fetch(`${cleanUrl(url)}/auth/v1/settings`, { headers: { apikey: cleanKey(anonKey) } })
    if (res.ok) return null
    if (res.status === 401 || res.status === 403) {
      const ref = cleanKey(anonKey).startsWith('eyJ') ? jwtPayload(cleanKey(anonKey))?.ref : undefined
      const host = new URL(cleanUrl(url)).host
      if (ref && !host.startsWith(ref + '.')) return `This key belongs to a different Supabase project (${ref}) than the address ${host}. Copy both from the same project.`
      return 'The server did not accept this key. Make sure the address and the key come from the same Supabase project, and copy the key again with its copy button.'
    }
    if (res.status === 404) return 'Nothing found at that address. Use the Project URL (https://….supabase.co), not the dashboard page address.'
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
