import { describe, expect, it } from 'vitest'
import { checkConfig, cleanKey, cleanUrl, makeConnectionCode, normaliseUrl, parseConnectionCode } from '../lib/config'

const b64 = (o: object) => btoa(JSON.stringify(o)).replace(/=+$/, '').replace(/\+/g, '-').replace(/\//g, '_')
const jwt = (role: string, ref = 'abcdefghijkl') => `eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.${b64({ iss: 'supabase', ref, role, iat: 1700000000, exp: 2000000000 })}.c2lnbmF0dXJlLXNpZ25hdHVyZQ`
const pub = 'sb_publishable_4Gk9tQ2xR7mN1pL8vW3yZ0aB_cD5eF6g'

describe('server connection', () => {
  it('accepts public keys and https addresses', () => {
    expect(checkConfig('abcdefgh.supabase.co', pub)).toBeNull()
    expect(checkConfig('https://abcdefgh.supabase.co/', jwt('anon'))).toBeNull()
    expect(normaliseUrl(' abcdefgh.supabase.co/ ')).toBe('https://abcdefgh.supabase.co')
  })
  it('cleans up what people actually paste', () => {
    expect(cleanKey(`anon public\n${jwt('anon')}\n Copy`)).toBe(jwt('anon'))
    expect(cleanKey(`  "${pub}",  `)).toBe(pub)
    const split = jwt('anon')
    expect(cleanKey(split.slice(0, 60) + '\n  ' + split.slice(60))).toBe(split)
    expect(cleanUrl('https://supabase.com/dashboard/project/abcdefghijkl/settings/api')).toBe('https://abcdefghijkl.supabase.co')
    expect(cleanUrl('https://abcdefghijkl.supabase.co/rest/v1/')).toBe('https://abcdefghijkl.supabase.co')
    expect(checkConfig('https://supabase.com/dashboard/project/abcdefghijkl', `anon public ${jwt('anon')}`)).toBeNull()
  })
  it('refuses secret and service-role keys with a clear fix', () => {
    expect(checkConfig('https://x.supabase.co', 'sb_secret_abcdefghijklmnopqrstuvwxyz')).toMatch(/SECRET.*PUBLISHABLE/)
    expect(checkConfig('https://x.supabase.co', jwt('service_role'))).toMatch(/SERVICE ROLE.*anon public/)
  })
  it('explains other mistakes', () => {
    expect(checkConfig('https://x.supabase.co', 'https://x.supabase.co')).toMatch(/web address/)
    expect(checkConfig('https://x.supabase.co', 'hello')).toMatch(/starts with "hello…"/)
    expect(checkConfig('https://x.supabase.co', jwt('anon').slice(0, 50))).toMatch(/incomplete/)
    expect(checkConfig('https://x.supabase.co', 'sb_publishable_abc')).toMatch(/cut off/)
  })
  it('refuses plain http except for local test servers', () => {
    expect(checkConfig('http://example.com', pub)).toMatch(/https/)
    expect(checkConfig('http://localhost:54321', pub)).toBeNull()
  })
  it('round-trips a connection code and rejects garbage', () => {
    const code = makeConnectionCode('x.supabase.co', pub)
    expect(parseConnectionCode(code)).toEqual({ url: 'https://x.supabase.co', anonKey: pub })
    expect(parseConnectionCode('CDPR1:%%%')).toBeNull()
    expect(parseConnectionCode('nonsense')).toBeNull()
  })
})
