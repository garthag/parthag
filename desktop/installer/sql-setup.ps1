<#
  BSL Project Control – installs Microsoft SQL Server 2022 Express for the app.
  Called by the installer (BSLProjectControl.nsi); can also be run by hand as Administrator:

    powershell -ExecutionPolicy Bypass -File sql-setup.ps1 -Mode LocalDB
    powershell -ExecutionPolicy Bypass -File sql-setup.ps1 -Mode Express

  -Mode LocalDB : SQL Server Express LocalDB – database on this PC only (per Windows user), no service.
  -Mode Express : SQL Server Express instance .\SQLEXPRESS – a Windows service other PCs can connect to.
  -Media        : bundled SqlLocalDB.msi / SQLEXPR_x64_ENU.exe; when missing the files are downloaded from Microsoft.
  Exit code 0 = installed (or already present), 1 = failed (details in the log).
#>
param(
    [Parameter(Mandatory)][ValidateSet('LocalDB', 'Express')][string]$Mode,
    [string]$Media = '',
    [string]$Work = (Join-Path $env:TEMP 'BSLSqlSetup'),
    [string]$Log = (Join-Path $env:TEMP 'BSLSqlSetup.log'),
    [string]$Database = 'BSL_ProjectControl'
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$SseiUrl = 'https://go.microsoft.com/fwlink/p/?linkid=2216019'   # Microsoft SQL Server 2022 Express bootstrapper

function Log([string]$m) {
    $line = '[{0:yyyy-MM-dd HH:mm:ss}] {1}' -f (Get-Date), $m
    Add-Content -Path $Log -Value $line
    Write-Output $line
}

function Get-Ssei {
    $exe = Join-Path $Work 'SQL2022-SSEI-Expr.exe'
    if (-not (Test-Path $exe)) {
        Log 'Downloading the SQL Server 2022 Express bootstrapper from Microsoft ...'
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri $SseiUrl -OutFile $exe -UseBasicParsing
    }
    return $exe
}

function Invoke-Sql([string]$server, [string]$sql) {
    $cn = New-Object System.Data.SqlClient.SqlConnection "Server=$server;Database=master;Integrated Security=True;Connect Timeout=60"
    $cn.Open()
    try { $cmd = $cn.CreateCommand(); $cmd.CommandText = $sql; $cmd.CommandTimeout = 120; [void]$cmd.ExecuteNonQuery() }
    finally { $cn.Close() }
}

try {
    New-Item -ItemType Directory -Force -Path $Work | Out-Null
    Log "=== SQL Server setup: $Mode ==="

    if ($Mode -eq 'LocalDB') {
        $msi = $null
        if ($Media -and (Test-Path $Media)) { $msi = $Media; Log "Using bundled $msi" }
        else {
            $ssei = Get-Ssei
            Log 'Downloading SQL Server Express LocalDB (about 55 MB) ...'
            $p = Start-Process $ssei -ArgumentList '/ACTION=Download', '/MEDIATYPE=LocalDB', "/MEDIAPATH=`"$Work`"", '/QUIET', '/HIDEPROGRESSBAR' -Wait -PassThru
            Log "Bootstrapper exit code $($p.ExitCode)"
            $msi = (Get-ChildItem $Work -Recurse -Filter 'SqlLocalDB.msi' | Select-Object -First 1).FullName
        }
        if (-not $msi) { throw 'SqlLocalDB.msi was not found (no bundled file and the download failed).' }

        Log 'Installing SQL Server Express LocalDB ...'
        $p = Start-Process msiexec.exe -ArgumentList '/i', "`"$msi`"", '/qn', 'IACCEPTSQLLOCALDBLICENSETERMS=YES', '/norestart', '/l*v', "`"$Work\localdb-msi.log`"" -Wait -PassThru
        # 0 ok, 3010 ok (restart later), 1638 another version already installed
        if ($p.ExitCode -notin 0, 3010, 1638) { throw "LocalDB setup failed with exit code $($p.ExitCode) (see $Work\localdb-msi.log)." }
        Log "LocalDB ready (msiexec exit code $($p.ExitCode)). Each Windows user gets the instance (localdb)\MSSQLLocalDB automatically."
    }
    else {
        $existing = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL' -Name 'SQLEXPRESS' -ErrorAction SilentlyContinue
        if ($existing) { Log 'Instance SQLEXPRESS already exists – installation skipped.' }
        else {
            $pkg = $null
            if ($Media -and (Test-Path $Media)) { $pkg = $Media; Log "Using bundled $pkg" }
            else {
                $ssei = Get-Ssei
                Log 'Downloading SQL Server 2022 Express (about 260 MB) ...'
                $p = Start-Process $ssei -ArgumentList '/ACTION=Download', '/MEDIATYPE=Core', "/MEDIAPATH=`"$Work`"", '/QUIET', '/HIDEPROGRESSBAR' -Wait -PassThru
                Log "Bootstrapper exit code $($p.ExitCode)"
                $pkg = (Get-ChildItem $Work -Recurse -Filter 'SQLEXPR*.exe' | Select-Object -First 1).FullName
            }
            if (-not $pkg) { throw 'The SQL Server Express package was not found (no bundled file and the download failed).' }

            $ext = Join-Path $Work 'express'
            Log 'Extracting SQL Server Express ...'
            Start-Process $pkg -ArgumentList '/Q', "/x:`"$ext`"" -Wait | Out-Null
            $setup = Join-Path $ext 'SETUP.EXE'
            if (-not (Test-Path $setup)) { throw "SETUP.EXE not found in $ext after extraction." }

            Log 'Installing SQL Server Express instance SQLEXPRESS (5 – 15 minutes) ...'
            $setupArgs = @('/Q', '/ACTION=Install', '/FEATURES=SQLENGINE', '/INSTANCENAME=SQLEXPRESS',
                      '/SQLSYSADMINACCOUNTS="BUILTIN\Administrators"', '/TCPENABLED=1',
                      '/SQLSVCSTARTUPTYPE=Automatic', '/BROWSERSVCSTARTUPTYPE=Automatic',
                      '/UPDATEENABLED=0', '/IACCEPTSQLSERVERLICENSETERMS', '/SUPPRESSPRIVACYSTATEMENTNOTICE')
            $p = Start-Process $setup -ArgumentList $setupArgs -Wait -PassThru
            if ($p.ExitCode -notin 0, 3010) {
                throw "SQL Server Express setup failed with exit code $($p.ExitCode). Logs: C:\Program Files\Microsoft SQL Server\160\Setup Bootstrap\Log"
            }
            Log "SQL Server Express installed (exit code $($p.ExitCode))."
        }

        Log 'Opening Windows Firewall for SQL Server Express (so other PCs can connect) ...'
        $sqlservr = Get-ChildItem "$env:ProgramFiles\Microsoft SQL Server" -Recurse -Filter 'sqlservr.exe' -ErrorAction SilentlyContinue |
                    Where-Object { $_.FullName -like '*SQLEXPRESS*' } | Select-Object -First 1
        if ($sqlservr) {
            & netsh advfirewall firewall delete rule name="SQL Server Express (BSL Project Control)" | Out-Null
            & netsh advfirewall firewall add rule name="SQL Server Express (BSL Project Control)" dir=in action=allow program="$($sqlservr.FullName)" enable=yes | Out-Null
        }
        & netsh advfirewall firewall delete rule name="SQL Server Browser (BSL Project Control)" | Out-Null
        & netsh advfirewall firewall add rule name="SQL Server Browser (BSL Project Control)" dir=in action=allow protocol=UDP localport=1434 | Out-Null

        Log "Creating database $Database and giving signed-in Windows users access ..."
        Start-Service 'MSSQL$SQLEXPRESS' -ErrorAction SilentlyContinue
        Invoke-Sql '.\SQLEXPRESS' @"
IF DB_ID(N'$Database') IS NULL CREATE DATABASE [$Database];
IF SUSER_ID(N'NT AUTHORITY\Authenticated Users') IS NULL CREATE LOGIN [NT AUTHORITY\Authenticated Users] FROM WINDOWS;
"@
        Invoke-Sql '.\SQLEXPRESS' @"
USE [$Database];
IF USER_ID(N'NT AUTHORITY\Authenticated Users') IS NULL CREATE USER [NT AUTHORITY\Authenticated Users] FOR LOGIN [NT AUTHORITY\Authenticated Users];
ALTER ROLE db_owner ADD MEMBER [NT AUTHORITY\Authenticated Users];
"@
        Log "SQL Server Express ready: server .\SQLEXPRESS (from other PCs: $env:COMPUTERNAME\SQLEXPRESS), database $Database."
    }
    exit 0
}
catch {
    Log "ERROR: $($_.Exception.Message)"
    exit 1
}
