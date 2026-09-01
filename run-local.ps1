<#
.SYNOPSIS
    Starts the OIIE sandbox stack locally, in dependency order.

.DESCRIPTION
    Each Functions host is a separate process on its own port, because that is what
    they are in deployment: peer systems that reach each other over HTTP. Running
    them in one process would make the boundaries disappear and the demo would stop
    proving anything.

    Ports follow the block already established in appsettings.Development.json and
    the launch profiles, so this script does not invent an addressing scheme:

        7071  ENG provider              7253  ISBM provider
        7072  CIR provider              7254  ENG engine
        7073  REG-LOCATION provider     7255  REG-LOCATION engine
        7074  MMS provider              7256  CMS engine
        7075  CMS provider              7257  MMS engine
        7241  Sandbox API (https)

    Start order matters in one direction only: ISBM must be up before any engine
    opens a subscription session. Providers and engines are otherwise independent --
    an engine whose provider is down logs a failure and leaves the message on the
    channel, which is the designed behaviour rather than a startup error.

.PARAMETER Include
    Which hosts to start. Defaults to everything needed to watch a site travel from
    ENG to CMS and MMS.

.PARAMETER SkipAzurite
    Do not start the storage emulator. Use when Azurite is already running.

.EXAMPLE
    .\run-local.ps1
    Starts the whole stack.

.EXAMPLE
    .\run-local.ps1 -Include Isbm,EngProvider,EngEngine,CmsProvider,CmsEngine
    Starts only the CMS half of the site flow.
#>
[CmdletBinding()]
param(
    [ValidateSet(
        'Azurite', 'Isbm', 'Cir',
        'EngProvider', 'EngEngine',
        'RegLocationProvider', 'RegLocationEngine',
        'CmsProvider', 'CmsEngine',
        'MmsProvider', 'MmsEngine',
        'SandboxApi')]
    [string[]]$Include = @(
        'Azurite', 'Isbm', 'Cir',
        'EngProvider', 'EngEngine',
        'RegLocationProvider', 'RegLocationEngine',
        'CmsProvider', 'CmsEngine',
        'MmsProvider', 'MmsEngine',
        'SandboxApi'),

    [switch]$SkipAzurite
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

# Ordered so that ISBM precedes every engine. PowerShell hashtables do not preserve
# insertion order, hence the explicit list of records.
$hosts = @(
    @{ Name = 'Isbm';                Project = 'ISBMProvider';        Port = 7253 }
    @{ Name = 'Cir';                 Project = 'CirProvider';         Port = 7072 }
    @{ Name = 'EngProvider';         Project = 'EngProvider';         Port = 7071 }
    @{ Name = 'RegLocationProvider'; Project = 'RegLocationProvider'; Port = 7073 }
    @{ Name = 'MmsProvider';         Project = 'MmsProvider';         Port = 7074 }
    @{ Name = 'CmsProvider';         Project = 'CmsProvider';         Port = 7075 }
    @{ Name = 'EngEngine';           Project = 'EngEngine';           Port = 7254 }
    @{ Name = 'RegLocationEngine';   Project = 'RegLocationEngine';   Port = 7255 }
    @{ Name = 'CmsEngine';           Project = 'CmsEngine';           Port = 7256 }
    @{ Name = 'MmsEngine';           Project = 'MmsEngine';           Port = 7257 }
)

function Test-PortFree([int]$Port) {
    -not (Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue)
}

# --- Storage emulator ------------------------------------------------------
#
# Every Functions host declares AzureWebJobsStorage as UseDevelopmentStorage=true,
# and a timer-triggered host will not start without it. The engines are all timer
# triggered, so this is not optional for the site flow.
if ($Include -contains 'Azurite' -and -not $SkipAzurite) {
    if (Test-PortFree 10000) {
        $azurite = Get-Command azurite -ErrorAction SilentlyContinue

        if (-not $azurite) {
            # Visual Studio ships one. Preferred over asking for a global npm
            # install, since the IDE is already a prerequisite here.
            $bundled = Get-ChildItem 'C:\Program Files\Microsoft Visual Studio' `
                -Recurse -Filter azurite.exe -ErrorAction SilentlyContinue |
                Select-Object -First 1

            if (-not $bundled) {
                throw "Azurite not found. Install with 'npm install -g azurite', or pass -SkipAzurite if storage is already running."
            }

            $azurite = $bundled.FullName
        }
        else {
            $azurite = $azurite.Source
        }

        $data = Join-Path $env:TEMP 'oiie-azurite'
        New-Item -ItemType Directory -Force -Path $data | Out-Null

        Write-Host "Starting Azurite (data in $data)" -ForegroundColor Cyan
        Start-Process -FilePath $azurite `
            -ArgumentList '--silent', '--location', $data `
            -WindowStyle Minimized
        Start-Sleep -Seconds 2
    }
    else {
        Write-Host "Azurite already listening on 10000; leaving it alone." -ForegroundColor DarkGray
    }
}

# --- Functions hosts -------------------------------------------------------
#
# Built once up front rather than letting ten 'func start' processes each trigger
# their own build of the shared projects. Concurrent builds of Oiie.Ccom and
# Oiie.Isbm.Client race on the same output files and fail intermittently.
Write-Host "Building the solution..." -ForegroundColor Cyan
dotnet build (Join-Path $root 'OpenOM.slnx') -v:q --nologo | Out-Null

if ($LASTEXITCODE -ne 0) {
    throw "Build failed. Fix the build before starting hosts."
}

$started = @()

foreach ($h in $hosts) {
    if ($Include -notcontains $h.Name) { continue }

    $dir = Join-Path $root $h.Project

    if (-not (Test-Path $dir)) {
        Write-Warning "$($h.Name): project directory not found at $dir; skipping."
        continue
    }

    if (-not (Test-PortFree $h.Port)) {
        Write-Warning "$($h.Name): port $($h.Port) is already in use; skipping. Stop the existing host or it will shadow this one."
        continue
    }

    Write-Host ("Starting {0,-20} http://localhost:{1}/api" -f $h.Name, $h.Port) -ForegroundColor Green

    # --no-build because the solution was built above. Each host gets its own
    # window so its logs stay readable and it can be stopped independently.
    Start-Process -FilePath 'pwsh' `
        -ArgumentList '-NoExit', '-Command',
            "Set-Location '$dir'; `$Host.UI.RawUI.WindowTitle = '$($h.Name)'; func start --port $($h.Port) --no-build" `
        -WindowStyle Normal

    $started += $h

    # Staggered because ten hosts racing to JIT and open SQL connections at once
    # makes the slow ones look like failures.
    Start-Sleep -Milliseconds 700
}

# --- Sandbox API -----------------------------------------------------------
if ($Include -contains 'SandboxApi') {
    $dir = Join-Path $root 'Oiie.Sandbox.Api'

    if (Test-PortFree 7241) {
        Write-Host ("Starting {0,-20} https://localhost:7241" -f 'SandboxApi') -ForegroundColor Green

        Start-Process -FilePath 'pwsh' `
            -ArgumentList '-NoExit', '-Command',
                "Set-Location '$dir'; `$Host.UI.RawUI.WindowTitle = 'SandboxApi'; dotnet run --no-build" `
            -WindowStyle Normal
    }
    else {
        Write-Warning "SandboxApi: port 7241 already in use; skipping."
    }
}

Write-Host ""
Write-Host "Hosts starting. Give them ~15 seconds to finish binding." -ForegroundColor Cyan
Write-Host ""
Write-Host "Check the engines are configured and listening:" -ForegroundColor Cyan
Write-Host "  irm http://localhost:7256/api/engine/status | ConvertTo-Json   # CMS"
Write-Host "  irm http://localhost:7257/api/engine/status | ConvertTo-Json   # MMS"
Write-Host ""
Write-Host "Drain the sites channel on demand instead of waiting for the 2-minute timer:" -ForegroundColor Cyan
Write-Host "  irm -Method Post http://localhost:7256/api/engine/ingest-sites"
Write-Host "  irm -Method Post http://localhost:7257/api/engine/ingest-sites"
Write-Host ""
Write-Host "Stop everything with:  Get-Process func,dotnet | Stop-Process" -ForegroundColor DarkGray
