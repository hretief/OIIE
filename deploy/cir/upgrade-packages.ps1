<#
.SYNOPSIS
    Refreshes every NuGet reference in CirProvider to its latest stable version.

.DESCRIPTION
    Pinned versions in the csproj go stale, and hand-editing them is how the
    Worker 1.x / 2.x mismatch happened. `dotnet add package` without -Version
    resolves the latest stable and rewrites the csproj, so this is the
    authoritative way to move the project forward.

    Run after changing TargetFramework: package resolution is framework-aware.

    Resolution is restricted to nuget.org. A machine-level private feed that
    returns 401 will fail `dotnet add package` outright, even though `dotnet
    build` only warns about it (NU1900).

.EXAMPLE
    .\upgrade-packages.ps1
#>
[CmdletBinding()]
param(
    # Restricting the source avoids private feeds that need credentials.
    # CIR/NuGet.config already clears inherited sources; this is belt and braces.
    [string] $Source = 'https://api.nuget.org/v3/index.json'
)

$ErrorActionPreference = 'Stop'
$project = Join-Path (Split-Path -Parent $PSScriptRoot) 'CirProvider'

$packages = @(
    'Microsoft.Azure.Functions.Worker',
    'Microsoft.Azure.Functions.Worker.Sdk',
    'Microsoft.Azure.Functions.Worker.Extensions.Http.AspNetCore',
    'Microsoft.Azure.Functions.Worker.ApplicationInsights',
    'Microsoft.Data.SqlClient',
    'Azure.Identity'
)

<#
    Deliberately excluded. Microsoft.ApplicationInsights 3.x dropped
    Microsoft.ApplicationInsights.Extensibility.ITelemetryInitializer, which
    Worker.ApplicationInsights still binds to. Upgrading these produces a
    worker that fails at startup with:

        System.TypeLoadException: Could not load type
        'Microsoft.ApplicationInsights.Extensibility.ITelemetryInitializer'

    and the host reports only "Function host is not running". They stay pinned
    in the csproj until Worker.ApplicationInsights targets the 3.x API.
#>
$pinned = @(
    'Microsoft.ApplicationInsights',
    'Microsoft.ApplicationInsights.WorkerService'
)

Push-Location $project
try {
    foreach ($p in $packages) {
        Write-Host "Updating $p ..." -ForegroundColor Cyan
        dotnet add package $p --source $Source
        if ($LASTEXITCODE -ne 0) { throw "Failed to update $p." }
    }

    Write-Host "`nPinned (not upgraded):" -ForegroundColor Yellow
    $pinned | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }

    Write-Host "`nRestoring and building..." -ForegroundColor Cyan
    dotnet build --configuration Release
    if ($LASTEXITCODE -ne 0) { throw 'Build failed after the package update.' }

    Write-Host "`nVerifying the Application Insights pin held..." -ForegroundColor Cyan
    $resolved = dotnet list package --include-transitive | Out-String
    if ($resolved -match 'Microsoft\.ApplicationInsights\s+.*?\s+3\.') {
        throw @'
Microsoft.ApplicationInsights resolved to 3.x. That breaks the Functions worker
at startup (TypeLoadException on ITelemetryInitializer). Check for a transitive
dependency pulling it up, and keep the explicit 2.23.0 reference in the csproj.
'@
    }
    Write-Host 'Application Insights is on the 2.x line.' -ForegroundColor Green

    Write-Host "`nDone. Resolved versions:" -ForegroundColor Green
    dotnet list package
}
finally {
    Pop-Location
}
