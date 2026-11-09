// Renders the Android launcher icons and splash screens (and the Windows icon
// source) from public/icon.svg. Needs Playwright (Chromium).
//   NODE_PATH=$(npm root -g) node scripts/make-app-icons.cjs
const { chromium } = require('playwright')
const fs = require('fs')
const path = require('path')

const RES = path.join(__dirname, '../android/app/src/main/res')
const BRAND = '#0f3d5e'
const svg = fs.readFileSync(path.join(__dirname, '../public/icon.svg'), 'utf8')
// Logo without its rounded background tile, for adaptive-icon foregrounds and splashes.
const glyph = svg.replace(/<rect[^>]*rx="96"[^>]*\/>/, '')

const jobs = []
const dens = { mdpi: 1, hdpi: 1.5, xhdpi: 2, xxhdpi: 3, xxxhdpi: 4 }
for (const [d, k] of Object.entries(dens)) {
  const s = Math.round(48 * k), f = Math.round(108 * k)
  jobs.push({ out: `mipmap-${d}/ic_launcher.png`, w: s, h: s, html: `<div style="width:${s}px;height:${s}px">${svg.replace('<svg ', `<svg width="${s}" height="${s}" `)}</div>` })
  jobs.push({ out: `mipmap-${d}/ic_launcher_round.png`, w: s, h: s, html: `<div style="width:${s}px;height:${s}px;border-radius:50%;overflow:hidden;background:${BRAND}">${glyph.replace('<svg ', `<svg width="${s}" height="${s}" `)}</div>` })
  // Adaptive icon: the glyph must fit the central 66 % safe zone.
  const g = Math.round(f * 0.6), o = Math.round((f - g) / 2)
  jobs.push({ out: `mipmap-${d}/ic_launcher_foreground.png`, w: f, h: f, html: `<div style="width:${f}px;height:${f}px;position:relative">${glyph.replace('<svg ', `<svg style="position:absolute;left:${o}px;top:${o}px" width="${g}" height="${g}" `)}</div>` })
}
const splash = (out, w, h) => {
  const g = Math.round(Math.min(w, h) * 0.35)
  jobs.push({ out, w, h, html: `<div style="width:${w}px;height:${h}px;background:${BRAND};display:grid;place-items:center">${glyph.replace('<svg ', `<svg width="${g}" height="${g}" `)}</div>` })
}
for (const f of fs.readdirSync(RES).filter((x) => x.startsWith('drawable'))) {
  const p = path.join(RES, f, 'splash.png')
  if (!fs.existsSync(p)) continue
  const b = fs.readFileSync(p)
  splash(`${f}/splash.png`, b.readUInt32BE(16), b.readUInt32BE(20))
}
jobs.push({ out: '../../../../../../native/desktop/resources/icon.png', w: 512, h: 512, html: `<div style="width:512px;height:512px">${svg.replace('<svg ', '<svg width="512" height="512" ')}</div>` })

;(async () => {
  const browser = await chromium.launch()
  for (const j of jobs) {
    const page = await browser.newPage({ viewport: { width: j.w, height: j.h } })
    await page.setContent(`<html><body style="margin:0;background:transparent">${j.html}</body></html>`)
    const out = path.join(RES, j.out)
    fs.mkdirSync(path.dirname(out), { recursive: true })
    await page.screenshot({ path: out, omitBackground: true })
    await page.close()
  }
  await browser.close()
  fs.writeFileSync(path.join(RES, 'values/ic_launcher_background.xml'), `<?xml version="1.0" encoding="utf-8"?>\n<resources>\n    <color name="ic_launcher_background">${BRAND}</color>\n</resources>\n`)
  console.log(`Rendered ${jobs.length} images`)
})()
