# Builds DrawingRegisterSetup-<version>.exe on Windows.
# Requirements: .NET 10 SDK and Inno Setup 6 (choco install innosetup -y).
# Usage:  powershell -ExecutionPolicy Bypass -File build-windows.ps1 [-Version 1.0.0] [-SkipTests]
param(
    [string]$Version = "1.0.0",
    [switch]$SkipTests
)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

if (-not $SkipTests) {
    Write-Host "== Unit tests"
    dotnet test tests\DrawingRegister.Core.Tests -c Release
    if ($LASTEXITCODE) { throw "tests failed" }
}

Write-Host "== Publishing self-contained win-x64 app"
$publish = Join-Path $PSScriptRoot "publish"
if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
dotnet publish src\DrawingRegister.App -c Release -r win-x64 --self-contained true `
    -p:Version=$Version -p:DebugType=none -o $publish
if ($LASTEXITCODE) { throw "publish failed" }

Write-Host "== Smoke test of the published app"
$report = Join-Path $env:TEMP "drawing-register-smoke.txt"
Remove-Item $report -ErrorAction SilentlyContinue
$p = Start-Process (Join-Path $publish "DrawingRegister.exe") -ArgumentList "--smoke-test", "`"$report`"" -Wait -PassThru
Get-Content $report -ErrorAction SilentlyContinue
if ($p.ExitCode -ne 0) { throw "smoke test failed (exit $($p.ExitCode))" }

Write-Host "== Building installer"
$iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found; install it with: choco install innosetup" }
& $iscc "/DAppVersion=$Version" "/DPublishDir=$publish" "/DOutputDir=$(Join-Path $PSScriptRoot 'dist')" installer\DrawingRegister.iss
if ($LASTEXITCODE) { throw "Inno Setup failed" }

$installer = Join-Path $PSScriptRoot "dist\DrawingRegisterSetup-$Version.exe"
Write-Host "== Done: $installer ($([math]::Round((Get-Item $installer).Length / 1MB)) MB)"
