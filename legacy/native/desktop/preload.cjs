// Tells the web app it runs inside the Windows app (no Node.js access is exposed).
const { contextBridge } = require('electron')
contextBridge.exposeInMainWorld('cdprDesktop', { platform: process.platform, version: process.env.npm_package_version || '' })
