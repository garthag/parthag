import { describe, expect, it } from 'vitest'
import { checkConfig, makeConnectionCode, normaliseUrl, parseConnectionCode } from '../lib/config'

const jwt = (role: string) => `eyJhbGciOiJIUzI1NiJ9.${btoa(JSON.stringify({ role, iss: 'supabase' })).replace(/=+$/, '')}.sig`

describe('server connection', () => {
  it('accepts public keys and https addresses', () => {
    expect(checkConfig('abcdefgh.supabase.co', 'sb_publishable_123')).toBeNull()
    expect(checkConfig('https://abcdefgh.supabase.co/', jwt('anon'))).toBeNull()
    expect(normaliseUrl(' abcdefgh.supabase.co/ ')).toBe('https://abcdefgh.supabase.co')
  })
  it('refuses secret and service-role keys', () => {
    expect(checkConfig('https://x.supabase.co', 'sb_secret_abc')).toMatch(/SECRET/)
    expect(checkConfig('https://x.supabase.co', jwt('service_role'))).toMatch(/SERVICE ROLE/)
    expect(checkConfig('https://x.supabase.co', 'hello')).toMatch(/does not look like/)
  })
  it('refuses plain http except for local test servers', () => {
    expect(checkConfig('http://example.com', 'sb_publishable_1')).toMatch(/https/)
    expect(checkConfig('http://localhost:54321', 'sb_publishable_1')).toBeNull()
  })
  it('round-trips a connection code and rejects garbage', () => {
    const code = makeConnectionCode('x.supabase.co', 'sb_publishable_ÄΩ')
    expect(code.startsWith('CDPR1:')).toBe(true)
    expect(parseConnectionCode(code)).toEqual({ url: 'https://x.supabase.co', anonKey: 'sb_publishable_ÄΩ' })
    expect(parseConnectionCode('CDPR1:%%%')).toBeNull()
    expect(parseConnectionCode('nonsense')).toBeNull()
  })
})
