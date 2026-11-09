import Papa from 'papaparse'

export function toCsv(rows: Array<Record<string, unknown>>, columns?: string[]): string {
  return Papa.unparse(rows.map((r) => {
    const o: Record<string, unknown> = {}
    for (const c of columns ?? Object.keys(rows[0] ?? {})) {
      const v = r[c]
      o[c] = v != null && typeof v === 'object' ? JSON.stringify(v) : v
    }
    return o
  }), { columns })
}

export function download(filename: string, content: string | Blob, type = 'text/csv;charset=utf-8') {
  const blob = typeof content === 'string' ? new Blob(['﻿', content], { type }) : content // BOM so Excel reads UTF-8 (₹, m³)
  const url = URL.createObjectURL(blob)
  const a = Object.assign(document.createElement('a'), { href: url, download: filename })
  document.body.appendChild(a)
  a.click()
  a.remove()
  setTimeout(() => URL.revokeObjectURL(url), 5000)
}

export function parseCsv(text: string): Array<Record<string, string>> {
  const r = Papa.parse<Record<string, string>>(text.replace(/^﻿/, ''), { header: true, skipEmptyLines: true, transformHeader: (h) => h.trim() })
  if (r.errors.length) throw new Error(`CSV error on row ${r.errors[0].row}: ${r.errors[0].message}`)
  return r.data
}
