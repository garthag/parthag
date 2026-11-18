# Builds the Drawing Reader Windows installer (DrawingReaderSetup-<version>.exe).
#
# Requirements: Python 3.12, Node.js 22, Tesseract-OCR and Inno Setup 6, e.g.
#   choco install tesseract innosetup -y
# Usage (from drawing-reader\backend):
#   powershell -ExecutionPolicy Bypass -File desktop\build-windows.ps1 [-Version 1.0.0] [-SkipFrontend]
param(
    [string]$Version = "1.0.0",
    [switch]$SkipFrontend
)
$ErrorActionPreference = "Stop"
$backend = Split-Path -Parent $PSScriptRoot
$frontend = Join-Path (Split-Path -Parent $backend) "frontend"

if (-not $SkipFrontend) {
    Write-Host "== Building web UI"
    Push-Location $frontend
    npm ci --no-audit --no-fund; if ($LASTEXITCODE) { throw "npm ci failed" }
    npm run build; if ($LASTEXITCODE) { throw "frontend build failed" }
    Pop-Location
}

Write-Host "== Locating Tesseract-OCR"
$tesseract = @("$env:ProgramFiles\Tesseract-OCR", "${env:ProgramFiles(x86)}\Tesseract-OCR", "$env:TESSERACT_DIR") |
    Where-Object { $_ -and (Test-Path (Join-Path $_ "tesseract.exe")) } | Select-Object -First 1
if (-not $tesseract) { throw "tesseract.exe not found; install it with: choco install tesseract" }
Write-Host "   using $tesseract"
$env:TESSERACT_DIR = $tesseract

Write-Host "== Bundling application (PyInstaller)"
Push-Location $backend
python -m PyInstaller --noconfirm --clean desktop\drawing_reader.spec
if ($LASTEXITCODE) { throw "PyInstaller failed" }
$dist = Join-Path $backend "dist\DrawingReader"

Write-Host "== Self-test of the bundle"
& (Join-Path $dist "drawing-reader-cli.exe") --self-test
if ($LASTEXITCODE) { throw "self-test failed" }

Write-Host "== Building installer (Inno Setup)"
$iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found; install it with: choco install innosetup" }
& $iscc "/DAppVersion=$Version" "/DDistDir=$dist" "/DOutputDir=$(Join-Path $backend 'dist')" desktop\installer.iss
if ($LASTEXITCODE) { throw "Inno Setup failed" }
Pop-Location

$installer = Join-Path $backend "dist\DrawingReaderSetup-$Version.exe"
Write-Host "== Done: $installer ($([math]::Round((Get-Item $installer).Length / 1MB)) MB)"
