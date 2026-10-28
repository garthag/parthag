import { validateUpload } from '../../../supabase/functions/drive/core'
import type { MediaItem } from './local'

export { validateUpload }
export type Category = MediaItem['category']

export async function sha256Hex(blob: Blob): Promise<string> {
  const buf = await crypto.subtle.digest('SHA-256', await blob.arrayBuffer())
  return [...new Uint8Array(buf)].map((b) => b.toString(16).padStart(2, '0')).join('')
}

export function categoryFor(mime: string, name = ''): Category {
  if (mime.startsWith('image/') && !/\.dwg$/i.test(name)) return 'photo'
  if (mime.startsWith('video/')) return 'video'
  if (mime.startsWith('audio/')) return 'audio'
  if (/\.(dwg|dxf)$/i.test(name)) return 'drawing'
  return 'document'
}

/** Resize/re-encode a photo only when the user explicitly asks for compression. */
export async function compressImage(file: Blob, maxDim = 1920, quality = 0.8): Promise<Blob> {
  const bmp = await createImageBitmap(file)
  const scale = Math.min(1, maxDim / Math.max(bmp.width, bmp.height))
  const canvas = Object.assign(document.createElement('canvas'), { width: Math.round(bmp.width * scale), height: Math.round(bmp.height * scale) })
  canvas.getContext('2d')!.drawImage(bmp, 0, 0, canvas.width, canvas.height)
  bmp.close()
  return new Promise((res, rej) => canvas.toBlob((b) => (b ? res(b) : rej(new Error('Compression failed'))), 'image/jpeg', quality))
}

export function pickRecorderMime(kind: 'audio' | 'video'): string {
  const options = kind === 'audio'
    ? ['audio/webm;codecs=opus', 'audio/mp4', 'audio/ogg;codecs=opus', 'audio/webm']
    : ['video/webm;codecs=vp9,opus', 'video/webm;codecs=vp8,opus', 'video/mp4', 'video/webm']
  return options.find((m) => typeof MediaRecorder !== 'undefined' && MediaRecorder.isTypeSupported(m)) ?? ''
}

export const baseMime = (m: string) => m.split(';')[0].trim()

export function fileExt(mime: string): string {
  const m = baseMime(mime)
  return ({ 'image/jpeg': 'jpg', 'image/png': 'png', 'video/webm': 'webm', 'video/mp4': 'mp4', 'audio/webm': 'webm', 'audio/mp4': 'm4a', 'audio/ogg': 'ogg', 'application/pdf': 'pdf' } as Record<string, string>)[m] ?? 'bin'
}

export const formatBytes = (n: number) => n < 1024 ** 2 ? `${(n / 1024).toFixed(0)} KB` : `${(n / 1024 ** 2).toFixed(1)} MB`

/** Limits for in-app recording (configurable here). */
export const LIMITS = { videoSeconds: 60, audioSeconds: 300 }
