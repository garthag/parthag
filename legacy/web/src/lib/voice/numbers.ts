/** Converts spoken English numbers ("twenty-four", "two point five", "1,250") to numbers. */
const SMALL: Record<string, number> = {
  zero: 0, one: 1, a: 1, an: 1, two: 2, three: 3, four: 4, five: 5, six: 6, seven: 7, eight: 8, nine: 9,
  ten: 10, eleven: 11, twelve: 12, thirteen: 13, fourteen: 14, fifteen: 15, sixteen: 16, seventeen: 17,
  eighteen: 18, nineteen: 19, twenty: 20, thirty: 30, forty: 40, fourty: 40, fifty: 50, sixty: 60,
  seventy: 70, eighty: 80, ninety: 90, half: 0.5, dozen: 12,
}
const SCALE: Record<string, number> = { hundred: 100, thousand: 1000, lakh: 100000, lakhs: 100000, crore: 10000000 }
const DIGIT: Record<string, string> = { zero: '0', one: '1', two: '2', three: '3', four: '4', five: '5', six: '6', seven: '7', eight: '8', nine: '9' }

export const isNumberWord = (w: string) => w in SMALL || w in SCALE || w === 'and' || w === 'point' || /^[\d.,]+$/.test(w)

/** Parse a token sequence that is entirely a number; returns null if it is not. */
export function wordsToNumber(tokens: string[]): number | null {
  const words = tokens.map((t) => t.toLowerCase()).filter((t) => t !== 'and')
  if (!words.length) return null
  if (words.length === 1 && /^\d[\d,]*(\.\d+)?$/.test(words[0])) return Number(words[0].replace(/,/g, ''))
  const pointAt = words.indexOf('point')
  const intWords = pointAt >= 0 ? words.slice(0, pointAt) : words
  const fracWords = pointAt >= 0 ? words.slice(pointAt + 1) : []
  let total = 0, current = 0, seen = false
  for (const w of intWords) {
    if (/^\d+$/.test(w)) { current += Number(w); seen = true }
    else if (w in SMALL) { current += SMALL[w]; seen = true }
    else if (w in SCALE) {
      current = (current || 1) * SCALE[w]
      if (SCALE[w] >= 1000) { total += current; current = 0 }
      seen = true
    } else return null
  }
  let value = total + current
  if (fracWords.length) {
    const digits = fracWords.map((w) => DIGIT[w] ?? (/^\d$/.test(w) ? w : null))
    if (digits.some((d) => d == null)) return null
    value = Number(`${value}.${digits.join('')}`)
    seen = true
  }
  return seen ? value : null
}
