# Builds dist\MailAgent\MailAgent.exe (and the installer if Inno Setup is installed).
# Usage (PowerShell, from anywhere):   .\windows\build.ps1
param([string]$Version = "1.0.0", [switch]$SkipInstall, [switch]$NoInstaller)
$ErrorActionPreference = "Stop"
Set-Location (Split-Path $PSScriptRoot -Parent)

if (-not $SkipInstall) {
    python -m pip install -r requirements.txt -r windows/requirements-windows.txt
}

python -m PyInstaller --noconfirm --clean --windowed `
    --name MailAgent `
    --icon windows/icon.ico `
    --add-data "mail_agent/web/templates;mail_agent/web/templates" `
    --add-data "mail_agent/web/static;mail_agent/web/static" `
    --add-data "windows/icon.ico;." `
    --collect-submodules webview `
    --collect-all pypdfium2 `
    --collect-all pypdfium2_raw `
    --collect-all winocr `
    --collect-all winrt `
    --collect-submodules openpyxl `
    --exclude-module tkinter `
    --collect-submodules keyring.backends `
    --hidden-import keyring.backends.Windows `
    windows/MailAgent.py
if ($LASTEXITCODE -ne 0) { throw "PyInstaller failed" }
Write-Host "Built dist\MailAgent\MailAgent.exe"

if (-not $NoInstaller) {
    $iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
    if (Test-Path $iscc) {
        & $iscc "/DMyAppVersion=$Version" windows/installer.iss
        if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed" }
    } else {
        Write-Host "Inno Setup 6 not found - skipping installer (get it from https://jrsoftware.org/isdl.php)"
    }
}
