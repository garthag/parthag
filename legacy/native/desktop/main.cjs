// CONSTRUCT DPR for Windows (Electron). Runs the same web app, bundled inside
// the installer, through a private app:// address so it starts offline and
// keeps its local drafts in the user's profile.
const { app, BrowserWindow, Menu, net, protocol, session, shell } = require('electron')
const path = require('node:path')
const { pathToFileURL } = require('node:url')
const { resolveAsset } = require('./assets.cjs')

const ORIGIN = 'app://dpr'
const WEB_ROOT = app.isPackaged ? path.join(process.resourcesPath, 'web') : path.resolve(__dirname, '../../web/dist')

protocol.registerSchemesAsPrivileged([
  { scheme: 'app', privileges: { standard: true, secure: true, supportFetchAPI: true, corsEnabled: true, stream: true } },
])

// One window per user session; a second launch focuses the existing window.
if (!app.requestSingleInstanceLock()) app.quit()

let win = null
function createWindow() {
  win = new BrowserWindow({
    width: 1360, height: 860, minWidth: 380, minHeight: 600,
    title: 'CONSTRUCT DPR', backgroundColor: '#0f3d5e', show: false,
    icon: path.join(__dirname, 'resources', 'icon.png'),
    webPreferences: {
      preload: path.join(__dirname, 'preload.cjs'),
      contextIsolation: true, nodeIntegration: false, sandbox: true, spellcheck: true,
    },
  })
  win.once('ready-to-show', () => win.show())
  // Links to Google Drive, documentation, etc. open in the normal browser;
  // in-app blob previews (PDFs, files) open in a child window.
  win.webContents.setWindowOpenHandler(({ url }) => {
    if (url.startsWith('blob:')) return { action: 'allow' }
    if (/^https?:\/\//.test(url)) void shell.openExternal(url)
    return { action: 'deny' }
  })
  win.webContents.on('will-navigate', (e, url) => {
    if (!url.startsWith(ORIGIN)) { e.preventDefault(); if (/^https?:\/\//.test(url)) void shell.openExternal(url) }
  })
  void win.loadURL(ORIGIN + '/')
}

app.on('second-instance', () => { if (win) { if (win.isMinimized()) win.restore(); win.focus() } })

app.whenReady().then(() => {
  protocol.handle('app', (req) => {
    const file = resolveAsset(WEB_ROOT, new URL(req.url).pathname)
    return net.fetch(pathToFileURL(file).toString())
  })
  // Camera, microphone and location only for the app itself, only when the page asks
  // (which it does only after the user taps the matching button).
  const allowed = new Set(['media', 'geolocation', 'clipboard-sanitized-write', 'fullscreen'])
  session.defaultSession.setPermissionRequestHandler((wc, permission, callback, details) => {
    callback(allowed.has(permission) && (details.requestingUrl || wc.getURL()).startsWith(ORIGIN))
  })
  session.defaultSession.setPermissionCheckHandler((_wc, permission, origin) => allowed.has(permission) && String(origin).startsWith(ORIGIN))

  Menu.setApplicationMenu(Menu.buildFromTemplate([
    { label: 'File', submenu: [{ role: 'reload', label: 'Reload' }, { type: 'separator' }, { role: 'quit', label: 'Exit' }] },
    { label: 'View', submenu: [{ role: 'zoomIn' }, { role: 'zoomOut' }, { role: 'resetZoom' }, { type: 'separator' }, { role: 'togglefullscreen' }] },
    { label: 'Help', submenu: [{ label: 'User manual', click: () => void shell.openExternal('https://github.com/garthag/parthag/blob/main/docs/USER_MANUAL.md') }, { role: 'toggleDevTools', label: 'Developer tools' }] },
  ]))
  createWindow()
})

app.on('window-all-closed', () => app.quit())
