import { isNumberWord, wordsToNumber } from './numbers'

/**
 * Turns a spoken/typed site statement into a PROPOSED structured DPR entry.
 * Nothing is committed automatically: every field carries a confidence and
 * the UI asks the user to confirm anything marked uncertain.
 *
 * "At the skip bridge, erected twelve metric tonnes of structural steel during
 *  day shift using one mobile crane and twenty-four workers."
 *  → location "skip bridge", quantity 12 MT, shift day,
 *    equipment [1 × mobile crane], workers 24, activity = best match.
 */
export interface ActivityRef { id: string; code: string; name: string; unit?: string | null; location?: string | null }

export interface ParsedField<T> { value: T; confidence: 'high' | 'medium' | 'low' }

export interface VoiceProposal {
  transcript: string
  location?: ParsedField<string>
  quantity?: ParsedField<number>
  unit?: ParsedField<string>
  activity?: ParsedField<{ id: string; code: string; name: string; score: number }>
  shift?: ParsedField<'day' | 'night' | 'general'>
  equipment: Array<{ count: number; type: string }>
  workers?: ParsedField<number>
  uncertain: string[]
}

const UNITS: Array<[RegExp, string]> = [
  [/^(metric\s+)?(tonnes?|tons?|mt)$/, 'MT'],
  [/^(kilograms?|kgs?|kilos?)$/, 'kg'],
  [/^(cubic\s+met(er|re)s?|cum|m3|cu\.?\s*m)$/, 'm³'],
  [/^(square\s+met(er|re)s?|sqm|m2|sq\.?\s*m)$/, 'm²'],
  [/^(running\s+met(er|re)s?|rmt|rm)$/, 'RM'],
  [/^(met(er|re)s?|m)$/, 'm'],
  [/^(millimet(er|re)s?|mm)$/, 'mm'],
  [/^(numbers?|nos?|pieces?|pcs|units?)$/, 'nos'],
  [/^(joints?)$/, 'joints'],
  [/^(inch(es)?|inch-?dia|dia-?inch(es)?)$/, 'inch-dia'],
  [/^(bags?)$/, 'bags'],
  [/^(hours?|hrs?)$/, 'hours'],
]
const MULTI_WORD_UNITS = ['metric tonnes', 'metric tonne', 'metric tons', 'metric ton', 'cubic meters', 'cubic metres',
  'cubic meter', 'cubic metre', 'square meters', 'square metres', 'running meters', 'running metres']

const WORKER_WORDS = /^(workers?|workmen|labou?rs?|labou?rers?|men|people|manpower|persons?|fitters?|welders?|riggers?|masons?|helpers?|operators?)$/
const STOP = new Set(['the', 'a', 'an', 'of', 'at', 'in', 'on', 'for', 'with', 'and', 'to', 'during', 'using', 'by', 'from', 'today', 'was', 'were', 'is', 'shift'])

function normaliseUnit(phrase: string): string | null {
  const p = phrase.toLowerCase().trim()
  for (const [re, unit] of UNITS) if (re.test(p)) return unit
  return null
}

const tokenize = (s: string) => s.replace(/[“”"]/g, '').replace(/(\d)-(\w)/g, '$1 $2').replace(/-/g, ' ')
  .split(/[\s,;:]+/).map((t) => t.replace(/\.$/, '')).filter(Boolean)

const stem = (w: string) => w.toLowerCase().replace(/(ing|ed|es|s)$/, '')

function similarity(text: string, candidate: string): number {
  const a = new Set(tokenize(text).map(stem).filter((w) => w.length > 2 && !STOP.has(w)))
  const b = tokenize(candidate).map(stem).filter((w) => w.length > 2 && !STOP.has(w))
  if (!b.length) return 0
  const hits = b.filter((w) => a.has(w)).length
  return hits / b.length
}

export function parseVoiceReport(transcript: string, activities: ActivityRef[] = []): VoiceProposal {
  const text = transcript.trim()
  const lower = text.toLowerCase()
  const tokens = tokenize(lower)
  const out: VoiceProposal = { transcript: text, equipment: [], uncertain: [] }

  // Location: "at the skip bridge," / "at grid C4" / "in the stockhouse" up to the first comma/verb.
  const loc = text.match(/\b(?:at|in|near|on)\s+(?:the\s+)?([^,.;]+?)(?=,|\.|;|\s+(?:we\s+|erected|completed|poured|installed|fabricated|welded|excavated|laid|did|done)\b)/i)
  if (loc) out.location = { value: loc[1].trim(), confidence: 'medium' }

  // Shift
  if (/\bnight\s+shift\b/.test(lower)) out.shift = { value: 'night', confidence: 'high' }
  else if (/\b(day|morning|general)\s+shift\b/.test(lower)) out.shift = { value: lower.includes('general shift') ? 'general' : 'day', confidence: 'high' }

  // Walk tokens: group number words, then look at what follows.
  for (let i = 0; i < tokens.length; i++) {
    if (!isNumberWord(tokens[i]) || tokens[i] === 'and' || tokens[i] === 'point' || tokens[i] === 'a' || tokens[i] === 'an') continue
    let j = i
    while (j < tokens.length && isNumberWord(tokens[j]) && !(tokens[j] === 'a' || tokens[j] === 'an')) j++
    while (j > i && (tokens[j - 1] === 'and' || tokens[j - 1] === 'point')) j--
    const n = wordsToNumber(tokens.slice(i, j))
    if (n == null) continue
    const next2 = tokens.slice(j, j + 2).join(' ')
    const next3 = tokens.slice(j, j + 3).join(' ')
    const unitPhrase = MULTI_WORD_UNITS.find((u) => next2 === u || next3.startsWith(u)) ?? tokens[j] ?? ''
    const unit = normaliseUnit(unitPhrase)
    const after = tokens.slice(j, j + 3)
    const workerIdx = after.findIndex((w) => WORKER_WORDS.test(w))
    if (unit && unit !== 'hours' && !out.quantity) {
      out.quantity = { value: n, confidence: 'high' }
      out.unit = { value: unit, confidence: 'high' }
    } else if (workerIdx >= 0 && workerIdx <= 1) {
      out.workers = { value: (out.workers?.value ?? 0) + n, confidence: 'high' }
    } else if (/\b(using|with|deployed|and)\b/.test(tokens.slice(Math.max(0, i - 2), i).join(' ')) || /crane|hydra|excavator|jcb|loader|truck|trailer|dg|welding|compressor|roller|mixer|pump/.test(after.join(' '))) {
      const typeWords: string[] = []
      for (let k = j; k < Math.min(j + 4, tokens.length); k++) {
        if (STOP.has(tokens[k]) || isNumberWord(tokens[k])) break
        typeWords.push(tokens[k])
      }
      if (typeWords.length && !WORKER_WORDS.test(typeWords[typeWords.length - 1])) {
        out.equipment.push({ count: n, type: typeWords.join(' ').replace(/s$/, '') })
      }
    } else if (!out.quantity) {
      out.quantity = { value: n, confidence: 'low' }
    }
    i = j - 1
  }

  // Activity: best name/description match against the project's activity list.
  if (activities.length) {
    const scored = activities
      .map((a) => ({ a, score: Math.max(similarity(text, a.name), a.code && lower.includes(a.code.toLowerCase()) ? 1 : 0) +
        (out.location && a.location && similarity(out.location.value, a.location) > 0.3 ? 0.15 : 0) +
        (out.unit && a.unit === out.unit.value ? 0.1 : 0) }))
      .sort((x, y) => y.score - x.score)
    const best = scored[0]
    if (best && best.score >= 0.3) {
      const second = scored[1]?.score ?? 0
      const confidence = best.score >= 0.75 && best.score - second >= 0.2 ? 'high' : best.score >= 0.5 ? 'medium' : 'low'
      out.activity = { value: { id: best.a.id, code: best.a.code, name: best.a.name, score: best.score }, confidence }
    }
  }

  if (!out.activity || out.activity.confidence !== 'high') out.uncertain.push('activity')
  if (!out.quantity || out.quantity.confidence !== 'high') out.uncertain.push('quantity')
  if (out.activity && out.unit && out.activity.value && activities.find((a) => a.id === out.activity!.value.id)?.unit &&
      activities.find((a) => a.id === out.activity!.value.id)?.unit !== out.unit.value) out.uncertain.push('unit')
  if (!out.shift) out.uncertain.push('shift')
  if (out.location?.confidence !== 'high') out.uncertain.push('location')
  return out
}
