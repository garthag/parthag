import type { TokenSource } from './core.ts'

/**
 * Server-side Google credentials. Two supported options (set as edge-function
 * secrets, never in the frontend):
 *
 *  A) Document-controller account (works with free Gmail or Workspace):
 *     GOOGLE_CLIENT_ID, GOOGLE_CLIENT_SECRET, GOOGLE_REFRESH_TOKEN
 *  B) Service account + Shared Drive (Google Workspace):
 *     GOOGLE_SERVICE_ACCOUNT_JSON, GOOGLE_SHARED_DRIVE_ID
 *     (service accounts have no My Drive quota, so a Shared Drive is required)
 *
 * Scope: https://www.googleapis.com/auth/drive.file — the app can only see
 * files and folders it created itself.
 */
export const DRIVE_SCOPE = 'https://www.googleapis.com/auth/drive.file'

type Fetch = typeof fetch

class CachedToken implements TokenSource {
  private value: string | null = null
  private expires = 0
  constructor(private fetchToken: () => Promise<{ access_token: string; expires_in: number }>) {}
  async token() {
    if (this.value && Date.now() < this.expires - 60_000) return this.value
    const t = await this.fetchToken()
    this.value = t.access_token
    this.expires = Date.now() + t.expires_in * 1000
    return this.value
  }
}

export function refreshTokenSource(clientId: string, clientSecret: string, refreshToken: string, http: Fetch = fetch): TokenSource {
  return new CachedToken(async () => {
    const res = await http('https://oauth2.googleapis.com/token', {
      method: 'POST',
      headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
      body: new URLSearchParams({ client_id: clientId, client_secret: clientSecret, refresh_token: refreshToken, grant_type: 'refresh_token' }),
    })
    if (!res.ok) throw new Error(`Google token refresh failed: ${res.status} ${await res.text()}`)
    return res.json()
  })
}

const b64url = (data: ArrayBuffer | string) => {
  const bytes = typeof data === 'string' ? new TextEncoder().encode(data) : new Uint8Array(data)
  let s = ''
  for (const b of bytes) s += String.fromCharCode(b)
  return btoa(s).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '')
}

export function serviceAccountTokenSource(json: string, http: Fetch = fetch): TokenSource {
  const sa = JSON.parse(json) as { client_email: string; private_key: string; token_uri?: string }
  return new CachedToken(async () => {
    const now = Math.floor(Date.now() / 1000)
    const header = b64url(JSON.stringify({ alg: 'RS256', typ: 'JWT' }))
    const claims = b64url(JSON.stringify({
      iss: sa.client_email, scope: DRIVE_SCOPE, aud: sa.token_uri ?? 'https://oauth2.googleapis.com/token', iat: now, exp: now + 3600,
    }))
    const pem = sa.private_key.replace(/-----[^-]+-----/g, '').replace(/\s+/g, '')
    const der = Uint8Array.from(atob(pem), (c) => c.charCodeAt(0))
    const key = await crypto.subtle.importKey('pkcs8', der, { name: 'RSASSA-PKCS1-v1_5', hash: 'SHA-256' }, false, ['sign'])
    const sig = await crypto.subtle.sign('RSASSA-PKCS1-v1_5', key, new TextEncoder().encode(`${header}.${claims}`))
    const res = await http(sa.token_uri ?? 'https://oauth2.googleapis.com/token', {
      method: 'POST',
      headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
      body: new URLSearchParams({ grant_type: 'urn:ietf:params:oauth:grant-type:jwt-bearer', assertion: `${header}.${claims}.${b64url(sig)}` }),
    })
    if (!res.ok) throw new Error(`Service account token failed: ${res.status} ${await res.text()}`)
    return res.json()
  })
}
