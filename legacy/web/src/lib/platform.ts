import { Capacitor } from '@capacitor/core'

/** Which shell the app runs in: the Android app, the Windows app or a browser. */
export type Platform = 'android' | 'windows' | 'web'

declare global {
  interface Window { cdprDesktop?: { platform: string; version: string } }
}

export function platform(): Platform {
  if (Capacitor.isNativePlatform()) return 'android'
  if (typeof window !== 'undefined' && window.cdprDesktop) return 'windows'
  return 'web'
}

export const isAppShell = () => platform() !== 'web'

export function deviceLabel(): string {
  const p = platform()
  if (p === 'android') return 'Android app'
  if (p === 'windows') return 'Windows app'
  const ua = navigator.userAgent
  return /Android/i.test(ua) ? 'Android browser' : /iPhone|iPad/i.test(ua) ? 'iPhone browser' : /Windows/i.test(ua) ? 'Windows browser' : /Mac OS/i.test(ua) ? 'Mac browser' : 'Browser'
}
