const test = require('node:test')
const assert = require('node:assert/strict')
const fs = require('node:fs')
const os = require('node:os')
const path = require('node:path')
const { resolveAsset } = require('../assets.cjs')

const root = fs.mkdtempSync(path.join(os.tmpdir(), 'cdpr-web-'))
fs.writeFileSync(path.join(root, 'index.html'), '<html>')
fs.mkdirSync(path.join(root, 'assets'))
fs.writeFileSync(path.join(root, 'assets', 'app.js'), '//')
fs.writeFileSync(path.join(path.dirname(root), 'secret.txt'), 'no')

test('serves files of the web build', () => {
  assert.equal(resolveAsset(root, '/assets/app.js'), path.join(root, 'assets', 'app.js'))
  assert.equal(resolveAsset(root, '/'), path.join(root, 'index.html'))
})
test('app routes fall back to index.html (single-page app)', () => {
  assert.equal(resolveAsset(root, '/dpr/123'), path.join(root, 'index.html'))
  assert.equal(resolveAsset(root, '/assets/missing.js?x=1'), path.join(root, 'index.html'))
})
test('never serves files outside the web build', () => {
  for (const p of ['/../secret.txt', '/%2e%2e/secret.txt', '/assets/../../secret.txt', '/%E0%A4%A']) {
    assert.equal(resolveAsset(root, p), path.join(root, 'index.html'), p)
  }
})
