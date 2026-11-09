import { useEffect, useRef, useState, useSyncExternalStore } from 'react'
import { media as mediaStore, type MediaItem } from '../lib/local'
import { sync } from '../lib/sync'
import { supabase } from '../lib/supabase'
import { fileObjectUrl } from '../lib/driveClient'
import { baseMime, categoryFor, compressImage, fileExt, formatBytes, LIMITS, pickRecorderMime, sha256Hex, validateUpload, type Category } from '../lib/media'
import { uid, type Activity, type Attachment } from '../lib/types'
import { Alert, Badge, Card } from './ui'

interface Pending {
  id: string; blob: Blob; name: string; mime: string; category: Category; caption: string
  activityId: string; compress: boolean; transcript?: string; url: string
}
interface Loc { latitude: number; longitude: number; accuracy: number | null; source: 'device_gps' | 'manual'; label?: string }

/**
 * Evidence capture for a DPR. Nothing is activated without an explicit tap:
 * the camera, microphone and location are requested only for the action the
 * user chose, and every captured file is reviewed (caption, activity, retake)
 * before it is queued for upload to the project's Google Drive.
 */
export function Evidence({ projectId, dprId, activities, onVoiceText, canAdd = true }: {
  projectId: string; dprId: string; activities: Activity[]; onVoiceText?: (text: string) => void; canAdd?: boolean
}) {
  const [mode, setMode] = useState<null | 'camera' | 'video' | 'voice' | 'type'>(null)
  const [pending, setPending] = useState<Pending[]>([])
  const [loc, setLoc] = useState<Loc | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [local, setLocal] = useState<MediaItem[]>([])
  const [server, setServer] = useState<Attachment[]>([])
  const fileRef = useRef<HTMLInputElement>(null)
  const status = useSyncExternalStore((cb) => sync.subscribe(cb), () => sync.status)

  useEffect(() => {
    let alive = true
    void mediaStore.byDpr(dprId).then((m) => alive && setLocal(m.sort((a, b) => a.createdAt - b.createdAt)))
    void supabase.from('attachments').select('*').eq('dpr_id', dprId).order('created_at').then(({ data }) => alive && setServer((data ?? []) as Attachment[]))
    return () => { alive = false }
  }, [dprId, status])

  const addPending = (blob: Blob, name: string, extra: Partial<Pending> = {}) => {
    const mime = baseMime(blob.type || 'application/octet-stream')
    const category = extra.category ?? categoryFor(mime, name)
    const bad = validateUpload(category, mime, blob.size)
    if (bad) return setError(`${name}: ${bad}`)
    setPending((p) => [...p, { id: uid(), blob, name, mime, category, caption: '', activityId: '', compress: false, url: URL.createObjectURL(blob), ...extra }])
  }

  const queue = async (p: Pending) => {
    try {
      const blob = p.compress && p.category === 'photo' ? await compressImage(p.blob) : p.blob
      const mime = baseMime(blob.type || p.mime)
      await sync.queueMedia({
        id: p.id, projectId, dprId, activityId: p.activityId || null, blob, filename: p.name, mime, size: blob.size,
        sha256: await sha256Hex(blob), category: p.category, transcript: p.transcript,
        caption: [p.caption, loc?.source === 'manual' ? `[location: ${loc.label}]` : ''].filter(Boolean).join(' ') || undefined,
        capturedAt: new Date().toISOString(),
        latitude: loc && Number.isFinite(loc.latitude) ? loc.latitude : null, longitude: loc && Number.isFinite(loc.longitude) ? loc.longitude : null,
        accuracy: loc?.accuracy ?? null, locationSource: loc?.source ?? null,
      })
      discard(p.id)
    } catch (e) {
      setError(`Could not queue ${p.name}: ${(e as Error).message}`)
    }
  }
  const discard = (id: string) => setPending((ps) => {
    const x = ps.find((q) => q.id === id)
    if (x) URL.revokeObjectURL(x.url)
    return ps.filter((q) => q.id !== id)
  })
  const update = (id: string, patch: Partial<Pending>) => setPending((ps) => ps.map((q) => (q.id === id ? { ...q, ...patch } : q)))

  // Duplicate protection: same bytes already attached to this report.
  const knownHashes = new Set([...local.map((m) => m.sha256), ...server.map((s) => s.sha256)].filter((h): h is string => !!h))

  return (
    <Card title="Evidence: photos, video, voice, documents">
      <details className="mb-3 text-sm">
        <summary className="cursor-pointer font-semibold">What is captured, where it goes, who can see it</summary>
        <ul className="mt-2 list-disc pl-5 text-slate-600 dark:text-slate-400 space-y-1">
          <li>The camera, microphone and location are used only when you tap the matching button, and only for that action. Nothing is recorded in the background.</li>
          <li>Files stay on this device until you add them to the report, then upload to your organisation's Google Drive project folder. The database keeps the Drive file ID, checksum, who uploaded it and when.</li>
          <li>Project members with document access can view them in this app. Drive sharing is managed separately by your administrator.</li>
          <li>Device time and location are recorded as reported by your phone. They can be inaccurate and do not by themselves prove authenticity.</li>
        </ul>
      </details>
      {error && <div className="mb-3"><Alert>{error} <button className="underline" onClick={() => setError(null)}>Dismiss</button></Alert></div>}
      {canAdd && (
        <div className="grid grid-cols-2 gap-2 sm:grid-cols-5">
          <button className="btn btn-secondary" onClick={() => setMode('camera')}>📷 Capture photo</button>
          <button className="btn btn-secondary" onClick={() => setMode('video')}>🎥 Record video</button>
          <button className="btn btn-secondary" onClick={() => fileRef.current?.click()}>🖼 Gallery / files</button>
          <button className="btn btn-secondary" onClick={() => setMode('voice')}>🎙 Voice note</button>
          <LocationButton loc={loc} setLoc={setLoc} />
        </div>
      )}
      <input ref={fileRef} type="file" multiple hidden
        accept="image/*,video/*,audio/*,application/pdf,.xlsx,.docx,.csv,.dwg,.dxf"
        onChange={(e) => { for (const f of Array.from(e.target.files ?? [])) addPending(f, f.name); e.target.value = '' }} />

      {mode === 'camera' && <CameraCapture onClose={() => setMode(null)} onShot={(b) => addPending(b, `photo_${Date.now()}.jpg`, { category: 'photo' })} />}
      {mode === 'video' && <VideoCapture onClose={() => setMode(null)} onDone={(b) => addPending(b, `video_${Date.now()}.${fileExt(b.type)}`, { category: 'video' })} />}
      {mode === 'voice' && (
        <VoiceRecorder onClose={() => setMode(null)}
          onSave={(b, t) => addPending(b, `voice_${Date.now()}.${fileExt(b.type)}`, { category: 'audio', transcript: t, caption: t.slice(0, 120) })}
          onConvert={onVoiceText} />
      )}

      {pending.length > 0 && (
        <div className="mt-4 flex flex-col gap-3">
          <h3 className="font-bold">Review before adding ({pending.length})</h3>
          {pending.map((p) => (
            <div key={p.id} className="grid gap-2 rounded-lg border border-slate-200 dark:border-slate-700 p-3 sm:grid-cols-[160px_1fr]">
              <Preview mime={p.mime} url={p.url} />
              <div className="grid gap-2">
                <div className="text-sm text-slate-500">{p.name} · {formatBytes(p.blob.size)} {knownHashes.size > 0 && <DupCheck blob={p.blob} known={knownHashes} />}</div>
                <input className="input" placeholder="Caption (what does this show?)" value={p.caption} onChange={(e) => update(p.id, { caption: e.target.value })} aria-label="Caption" />
                <select className="input" value={p.activityId} onChange={(e) => update(p.id, { activityId: e.target.value })} aria-label="Related activity">
                  <option value="">Related activity (optional)</option>
                  {activities.map((a) => <option key={a.id} value={a.id}>{a.code} — {a.name}</option>)}
                </select>
                {p.transcript != null && <textarea className="input" value={p.transcript} onChange={(e) => update(p.id, { transcript: e.target.value })} aria-label="Transcript" />}
                {p.category === 'photo' && (
                  <label className="flex items-center gap-2 text-sm"><input type="checkbox" className="h-5 w-5" checked={p.compress} onChange={(e) => update(p.id, { compress: e.target.checked })} />
                    Compress (smaller upload; original is not kept)</label>
                )}
                {loc && <div className="text-xs text-slate-500">📍 Location will be attached: {loc.label ?? `${loc.latitude.toFixed(5)}, ${loc.longitude.toFixed(5)}`}</div>}
                <div className="flex gap-2">
                  <button className="btn btn-primary" onClick={() => void queue(p)}>Add to report</button>
                  <button className="btn btn-secondary" onClick={() => discard(p.id)}>{p.category === 'photo' ? 'Retake / discard' : 'Discard'}</button>
                </div>
              </div>
            </div>
          ))}
        </div>
      )}

      <Attached local={local} server={server} />
    </Card>
  )
}

function DupCheck({ blob, known }: { blob: Blob; known: Set<string> }) {
  const [dup, setDup] = useState(false)
  useEffect(() => { void sha256Hex(blob).then((h) => setDup(known.has(h))) }, [blob, known])
  return dup ? <Badge tone="amber">Already attached to this report</Badge> : null
}

function Preview({ mime, url }: { mime: string; url: string }) {
  if (mime.startsWith('image/')) return <img src={url} alt="Preview" className="w-full max-h-40 object-contain rounded bg-black/5" />
  if (mime.startsWith('video/')) return <video src={url} controls className="w-full max-h-40 rounded" />
  if (mime.startsWith('audio/')) return <audio src={url} controls className="w-full" />
  return <div className="grid place-items-center h-24 rounded bg-slate-100 dark:bg-slate-800 text-3xl">📄</div>
}

const STATE: Record<string, ['gray' | 'blue' | 'green' | 'red' | 'amber', string]> = {
  queued: ['amber', 'Waiting to upload'], uploading: ['blue', 'Uploading'], uploaded: ['green', 'In Drive'], failed: ['red', 'Upload failed'],
  pending: ['amber', 'Upload pending'],
}

function Attached({ local, server }: { local: MediaItem[]; server: Attachment[] }) {
  const ids = new Set(server.map((s) => s.id))
  const onlyLocal = local.filter((m) => !ids.has(m.id) || m.state !== 'uploaded')
  if (!onlyLocal.length && !server.length) return null
  return (
    <div className="mt-4">
      <h3 className="font-bold mb-2">Attached to this report</h3>
      <ul className="grid gap-2 sm:grid-cols-2">
        {onlyLocal.filter((m) => !ids.has(m.id)).map((m) => (
          <li key={m.id} className="flex items-center justify-between gap-2 rounded border border-slate-200 dark:border-slate-700 p-2 text-sm">
            <span className="truncate">{m.category === 'photo' ? '📷' : m.category === 'video' ? '🎥' : m.category === 'audio' ? '🎙' : '📄'} {m.caption || m.filename}</span>
            <span className="flex items-center gap-2 whitespace-nowrap">
              <Badge tone={STATE[m.state][0]}>{STATE[m.state][1]}{m.state === 'uploading' && sync.uploadProgress.has(m.id) ? ` ${Math.round(sync.uploadProgress.get(m.id)! * 100)}%` : ''}</Badge>
              {m.state === 'failed' && <button className="btn btn-ghost px-2" title={m.error} onClick={() => void sync.retryMedia(m.id)}>Retry</button>}
            </span>
          </li>
        ))}
        {server.map((a) => <ServerFile key={a.id} a={a} />)}
      </ul>
    </div>
  )
}

export function ServerFile({ a }: { a: Attachment }) {
  const [url, setUrl] = useState<string | null>(null)
  const [err, setErr] = useState<string | null>(null)
  const open = async () => {
    try { const u = await fileObjectUrl(a.id); setUrl(u); if (!a.mime_type.startsWith('image/')) window.open(u, '_blank', 'noopener') } catch (e) { setErr((e as Error).message) }
  }
  useEffect(() => { if (a.upload_status === 'uploaded' && a.mime_type.startsWith('image/')) void open() // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [a.id, a.upload_status])
  return (
    <li className="rounded border border-slate-200 dark:border-slate-700 p-2 text-sm">
      {url && a.mime_type.startsWith('image/') && <img src={url} alt={a.caption ?? a.original_filename} className="mb-2 w-full max-h-48 object-cover rounded" />}
      <div className="flex items-center justify-between gap-2">
        <span className="truncate">{a.caption || a.original_filename}</span>
        <span className="flex items-center gap-1 whitespace-nowrap">
          <Badge tone={STATE[a.upload_status]?.[0] ?? 'gray'}>{STATE[a.upload_status]?.[1] ?? a.upload_status}</Badge>
          {a.upload_status === 'uploaded' && <button className="btn btn-ghost px-2" onClick={() => void open()}>Open</button>}
        </span>
      </div>
      {a.transcript && <p className="mt-1 text-xs text-slate-500">“{a.transcript}”</p>}
      {err && <p className="text-xs text-red-700">{err}</p>}
    </li>
  )
}

function LocationButton({ loc, setLoc }: { loc: Loc | null; setLoc: (l: Loc | null) => void }) {
  const [asking, setAsking] = useState(false)
  const [manual, setManual] = useState('')
  const [err, setErr] = useState<string | null>(null)
  if (loc) return <button className="btn btn-secondary" onClick={() => setLoc(null)} title="Remove location">📍 {loc.label ?? 'GPS set'} ✕</button>
  return (
    <>
      <button className="btn btn-secondary" onClick={() => setAsking(true)}>📍 Add location</button>
      {asking && (
        <div className="col-span-full rounded-lg border border-slate-300 dark:border-slate-700 p-3 text-sm grid gap-2">
          <p><b>Why:</b> to tag the evidence you add next with where it was taken. Location is read once, now — never tracked in the background. You can type a location instead.</p>
          {err && <Alert>{err}</Alert>}
          <div className="flex flex-wrap gap-2">
            <button className="btn btn-primary" onClick={() => navigator.geolocation.getCurrentPosition(
              (p) => { setLoc({ latitude: p.coords.latitude, longitude: p.coords.longitude, accuracy: p.coords.accuracy, source: 'device_gps', label: `±${Math.round(p.coords.accuracy)} m` }); setAsking(false) },
              (e) => setErr(e.code === 1 ? 'Location permission denied. You can type a location instead.' : 'Could not get a GPS fix. Try outdoors or type a location.'),
              { enableHighAccuracy: true, timeout: 15000, maximumAge: 0 })}>Use device GPS once</button>
            <input className="input flex-1" placeholder="or type: Grid C4, EL +12.5 m" value={manual} onChange={(e) => setManual(e.target.value)} aria-label="Manual location" />
            <button className="btn btn-secondary" disabled={!manual.trim()} onClick={() => { setLoc({ latitude: NaN, longitude: NaN, accuracy: null, source: 'manual', label: manual.trim() }); setAsking(false) }}>Use typed location</button>
            <button className="btn btn-ghost" onClick={() => setAsking(false)}>Cancel</button>
          </div>
        </div>
      )}
    </>
  )
}

function useMedia(constraints: MediaStreamConstraints | null) {
  const [stream, setStream] = useState<MediaStream | null>(null)
  const [error, setError] = useState<string | null>(null)
  useEffect(() => {
    if (!constraints) return
    let s: MediaStream | null = null
    let cancelled = false
    navigator.mediaDevices?.getUserMedia(constraints)
      .then((st) => { if (cancelled) st.getTracks().forEach((t) => t.stop()); else { s = st; setStream(st) } })
      .catch((e: DOMException) => setError(e.name === 'NotAllowedError' ? 'Permission denied. Allow access in the browser settings, or use Gallery / files instead.' : `Device not available: ${e.message}`))
    if (!navigator.mediaDevices) setError('This browser cannot access the camera/microphone here (HTTPS is required).')
    return () => { cancelled = true; s?.getTracks().forEach((t) => t.stop()) }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [JSON.stringify(constraints)])
  return { stream, error }
}

function CameraCapture({ onShot, onClose }: { onShot: (b: Blob) => void; onClose: () => void }) {
  const [facing, setFacing] = useState<'environment' | 'user'>('environment')
  const { stream, error } = useMedia({ video: { facingMode: facing, width: { ideal: 1920 }, height: { ideal: 1080 } }, audio: false })
  const video = useRef<HTMLVideoElement>(null)
  const [count, setCount] = useState(0)
  useEffect(() => { if (video.current && stream) video.current.srcObject = stream }, [stream])
  const shoot = () => {
    const v = video.current!
    const c = Object.assign(document.createElement('canvas'), { width: v.videoWidth, height: v.videoHeight })
    c.getContext('2d')!.drawImage(v, 0, 0)
    c.toBlob((b) => { if (b) { onShot(b); setCount((n) => n + 1) } }, 'image/jpeg', 0.9)
  }
  return (
    <div className="mt-3 rounded-lg bg-black p-2">
      {error ? <Alert>{error}</Alert> : <video ref={video} autoPlay playsInline muted className="w-full max-h-[60vh] rounded" />}
      <div className="mt-2 flex flex-wrap gap-2">
        <button className="btn btn-primary flex-1" disabled={!stream} onClick={shoot}>Capture ({count})</button>
        <button className="btn btn-secondary" onClick={() => setFacing((f) => (f === 'environment' ? 'user' : 'environment'))}>Switch camera</button>
        <button className="btn btn-secondary" onClick={onClose}>Done — camera off</button>
      </div>
    </div>
  )
}

function useRecorder(stream: MediaStream | null, kind: 'audio' | 'video', maxSeconds: number, onDone: (b: Blob) => void) {
  const rec = useRef<MediaRecorder | null>(null)
  const [secs, setSecs] = useState(0)
  const [recording, setRecording] = useState(false)
  useEffect(() => {
    if (!recording) return
    const t = setInterval(() => setSecs((s) => { if (s + 1 >= maxSeconds) rec.current?.stop(); return s + 1 }), 1000)
    return () => clearInterval(t)
  }, [recording, maxSeconds])
  const start = () => {
    if (!stream) return
    const mime = pickRecorderMime(kind)
    const r = new MediaRecorder(stream, mime ? { mimeType: mime } : undefined)
    const chunks: Blob[] = []
    r.ondataavailable = (e) => e.data.size && chunks.push(e.data)
    r.onstop = () => { setRecording(false); onDone(new Blob(chunks, { type: baseMime(r.mimeType || mime) })) }
    rec.current = r
    setSecs(0)
    r.start(1000)
    setRecording(true)
  }
  return { start, stop: () => rec.current?.stop(), recording, secs }
}

function VideoCapture({ onDone, onClose }: { onDone: (b: Blob) => void; onClose: () => void }) {
  const { stream, error } = useMedia({ video: { facingMode: 'environment', width: { ideal: 1280 } }, audio: true })
  const video = useRef<HTMLVideoElement>(null)
  useEffect(() => { if (video.current && stream) video.current.srcObject = stream }, [stream])
  const r = useRecorder(stream, 'video', LIMITS.videoSeconds, onDone)
  return (
    <div className="mt-3 rounded-lg bg-black p-2 text-white">
      {error ? <Alert>{error}</Alert> : <video ref={video} autoPlay playsInline muted className="w-full max-h-[60vh] rounded" />}
      <div className="mt-2 flex flex-wrap items-center gap-2">
        {r.recording ? <button className="btn btn-danger flex-1" onClick={r.stop}>■ Stop ({r.secs}s / {LIMITS.videoSeconds}s)</button>
          : <button className="btn btn-primary flex-1" disabled={!stream} onClick={r.start}>● Record (max {LIMITS.videoSeconds}s)</button>}
        <button className="btn btn-secondary" disabled={r.recording} onClick={onClose}>Done — camera off</button>
      </div>
    </div>
  )
}

type SpeechRec = { lang: string; continuous: boolean; interimResults: boolean; start(): void; stop(): void; onresult: ((e: { resultIndex: number; results: ArrayLike<{ isFinal: boolean; 0: { transcript: string } }> }) => void) | null; onerror: ((e: { error: string }) => void) | null }
const SpeechRecognitionCtor = (): (new () => SpeechRec) | null =>
  (window as unknown as { SpeechRecognition?: new () => SpeechRec; webkitSpeechRecognition?: new () => SpeechRec }).SpeechRecognition
  ?? (window as unknown as { webkitSpeechRecognition?: new () => SpeechRec }).webkitSpeechRecognition ?? null

function VoiceRecorder({ onSave, onConvert, onClose }: { onSave: (b: Blob, transcript: string) => void; onConvert?: (t: string) => void; onClose: () => void }) {
  const [armed, setArmed] = useState(false)
  const [transcribe, setTranscribe] = useState(false)
  const { stream, error } = useMedia(armed ? { audio: true, video: false } : null)
  const [blob, setBlob] = useState<Blob | null>(null)
  const [text, setText] = useState('')
  const recog = useRef<SpeechRec | null>(null)
  const r = useRecorder(stream, 'audio', LIMITS.audioSeconds, (b) => { setBlob(b); recog.current?.stop() })
  const Speech = SpeechRecognitionCtor()

  const start = () => {
    setBlob(null)
    r.start()
    if (transcribe && Speech) {
      const s = new Speech()
      s.lang = 'en-IN'
      s.continuous = true
      s.interimResults = false
      s.onresult = (e) => {
        let add = ''
        for (let i = e.resultIndex; i < e.results.length; i++) if (e.results[i].isFinal) add += e.results[i][0].transcript
        if (add) setText((t) => (t ? `${t} ${add.trim()}` : add.trim()))
      }
      s.onerror = () => { /* transcription is optional; the recording continues */ }
      recog.current = s
      s.start()
    }
  }

  return (
    <div className="mt-3 rounded-lg border border-slate-300 dark:border-slate-700 p-3 grid gap-3">
      {!armed ? (
        <>
          <p className="text-sm">Records audio from your microphone only while the red button is active. The recording is uploaded to the project Drive when you add it to the report.</p>
          {Speech ? (
            <label className="flex items-start gap-2 text-sm">
              <input type="checkbox" className="h-5 w-5 mt-0.5" checked={transcribe} onChange={(e) => setTranscribe(e.target.checked)} />
              <span>Also convert speech to text. <b>Note:</b> in Chrome, speech recognition is processed by Google's online speech service. Leave this off for sensitive content. You can always type instead.</span>
            </label>
          ) : <p className="text-xs text-slate-500">Speech-to-text is not available in this browser. Record audio and type the text below.</p>}
          <div className="flex gap-2"><button className="btn btn-primary" onClick={() => setArmed(true)}>Allow microphone</button><button className="btn btn-ghost" onClick={onClose}>Cancel</button></div>
        </>
      ) : error ? <Alert>{error}</Alert> : (
        <div className="flex flex-wrap items-center gap-2">
          {r.recording ? <button className="btn btn-danger flex-1" onClick={r.stop}>■ Stop ({r.secs}s)</button>
            : <button className="btn btn-primary flex-1" disabled={!stream} onClick={start}>● {blob ? 'Record again' : 'Start recording'}</button>}
          <button className="btn btn-secondary" disabled={r.recording} onClick={() => { setArmed(false); onClose() }}>Done — mic off</button>
        </div>
      )}
      {blob && <audio controls src={URL.createObjectURL(blob)} className="w-full" />}
      <div>
        <label className="label" htmlFor="voice-text">Transcript / typed statement — correct before saving</label>
        <textarea id="voice-text" className="input min-h-24" value={text} onChange={(e) => setText(e.target.value)}
          placeholder="e.g. At the skip bridge, erected twelve metric tonnes of structural steel during day shift using one mobile crane and twenty-four workers." />
      </div>
      <div className="flex flex-wrap gap-2">
        {onConvert && <button className="btn btn-primary" disabled={!text.trim()} onClick={() => onConvert(text)}>Convert to DPR entry…</button>}
        <button className="btn btn-secondary" disabled={!blob} onClick={() => { onSave(blob!, text); setBlob(null) }}>Keep voice note as evidence</button>
      </div>
    </div>
  )
}
