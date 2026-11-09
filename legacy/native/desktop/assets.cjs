// Maps app://dpr/<path> requests to files of the bundled web build.
// Pure and testable: no Electron imports.
const path = require('node:path')
const fs = require('node:fs')

/** Returns the absolute file to serve, never outside `root`; unknown routes get index.html (single-page app). */
function resolveAsset(root, urlPath) {
  let p
  try { p = decodeURIComponent(String(urlPath).split('?')[0].split('#')[0]) } catch { p = '/' }
  const target = path.resolve(root, '.' + path.posix.normalize('/' + p))
  const inside = target === root || target.startsWith(root + path.sep)
  if (inside && fs.existsSync(target) && fs.statSync(target).isFile()) return target
  return path.join(root, 'index.html')
}

module.exports = { resolveAsset }
