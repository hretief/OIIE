<#
.SYNOPSIS
    Deploys the OIIE Sandbox to Azure App Service.

.DESCRIPTION
    Provisions infrastructure, publishes the apps, and verifies the deployment
    actually works rather than merely that the upload succeeded.

    The sandbox is TWO App Services sharing one plan:

      oiie-sandbox-{env}  the API. Owns /admin and /health, runs the inbox pump
                          and outbox dispatcher. Keeps the historic name because
                          external callers already hold that URL.
      oiie-simhost-{env}  the Blazor operator UI. Serves no API routes and runs
                          no pumps; it calls the API over HTTP.

    Publish them separately. Zip deploy never deletes, so pushing one app's
    output into the other's slot leaves both sets of assemblies on the server
    and the wrong entry point may win.

    Assumes deploy/provision.ps1 has already run for this environment: the database,
    schemas, contained users and Key Vault secrets come from there. This script adds
    the hosting.

    Two things are copied into the publish output that are not part of the project:
    Personalities and Schemas. They live beside the solution locally and inside
    wwwroot when deployed, which is why the path settings differ per environment.

.NOTES
    Role assignments take a few minutes to propagate. A 403 reading Key Vault
    immediately after a first deployment is usually that, not a missing grant.

.EXAMPLE
    .\deploy.ps1 -Environment demo -StorageAccount mndotsandbox

.EXAMPLE
    .\deploy.ps1 -Environment demo -StorageAccount mndotsandbox -Target api

.EXAMPLE
    .\deploy.ps1 -Environment demo -StorageAccount mndotsandbox -SkipInfrastructure
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('dev', 'ci', 'demo')]
    [string]$Environment,

    [Parameter(Mandatory)]
    [string]$StorageAccount,

    # Which app to publish. Infrastructure covers both regardless.
    [ValidateSet('api', 'ui', 'both')]
    [string]$Target = 'both',

    [string]$ResourceGroup = 'HilmarRetiefRG',
    [string]$KeyVault = 'mndot',
    [string]$SqlServer = 'acme-sql-server',
    [string]$SubscriptionId,

    [string]$IsbmApp = 'isbm-func-44p2f3n6dv7p4',

    # Only needed when a per-developer database is being deployed.
    [string]$Alias,

    [string]$PlanSku = 'B1',

    # Browser origins allowed to call the API, for the React Workflow
    # Orchestration app once it is hosted somewhere.
    [string[]]$CorsOrigin = @(),

    [switch]$SkipInfrastructure,
    [switch]$SkipVerify
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# $PSScriptRoot is deploy/sandbox, so the repository root is two levels up.
$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent

function Invoke-Az {
    param([string[]]$Arguments, [string]$Because = 'Azure CLI call', [switch]$AsJson)

    $output = & az @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "$Because failed (exit $LASTEXITCODE):`n$($output -join [Environment]::NewLine)"
    }

    if (-not $AsJson) { return $output }

    # az writes warnings to stderr, and 2>&1 merges them into the same stream as the
    # JSON payload. Parsing the lot fails on the first "WARNING:", so keep only the
    # lines from the opening brace or bracket onwards.
    $lines = @($output | ForEach-Object { $_.ToString() })
    $start = 0

    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i].TrimStart().StartsWith('{') -or $lines[$i].TrimStart().StartsWith('[')) {
            $start = $i
            break
        }
    }

    $json = ($lines[$start..($lines.Count - 1)] -join [Environment]::NewLine)
    if ([string]::IsNullOrWhiteSpace($json)) { return $null }

    return $json | ConvertFrom-Json
}

# --- Preflight -------------------------------------------------------------

foreach ($tool in @('az', 'dotnet')) {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
        throw "$tool not found on PATH."
    }
}

if ($Environment -eq 'dev' -and [string]::IsNullOrWhiteSpace($Alias)) {
    throw '-Alias is required for the dev environment.'
}

$databaseName = switch ($Environment) {
    'dev' { "oiie-sandbox-dev-$Alias" }
    'ci' { 'oiie-sandbox-ci' }
    'demo' { 'oiie-sandbox-demo' }
}

$apiAppName = "oiie-sandbox-$Environment"
$uiAppName = "oiie-simhost-$Environment"
$isbmBaseUrl = "https://$IsbmApp.azurewebsites.net/api"

Write-Host "Environment : $Environment"
Write-Host "API         : $apiAppName"
Write-Host "UI          : $uiAppName"
Write-Host "Target      : $Target"
Write-Host "Database    : $databaseName"
Write-Host "Storage     : $StorageAccount"
Write-Host ''

if ($SubscriptionId) {
    Invoke-Az @('account', 'set', '--subscription', $SubscriptionId) -Because 'Setting subscription'
}

# The database must already exist. Deploying an app that cannot reach its database
# produces a running site that fails on every request, which is worse than a
# deployment that refuses to start.
& az sql db show --resource-group $ResourceGroup --server $SqlServer `
    --name $databaseName --query name -o tsv 2>$null | Out-Null

if ($LASTEXITCODE -ne 0) {
    throw "Database '$databaseName' does not exist. Run deploy/provision.ps1 -Environment $Environment first."
}

# --- Infrastructure --------------------------------------------------------

if (-not $SkipInfrastructure) {
    Write-Host 'Deploying infrastructure...'

    $isbmKey = & az functionapp keys list -g $ResourceGroup -n $IsbmApp `
        --query functionKeys.default -o tsv 2>$null
    if ($LASTEXITCODE -ne 0) { $isbmKey = '' }

    # Admin endpoints reset databases and delete channels. Unprotected on a
    # workstation is fine; unprotected on a public URL is a destructive API anyone
    # can call. Reused across deployments so existing scripts keep working.
    $adminSecret = "sandbox-admin-key-$Environment"
    $adminKey = & az keyvault secret show --vault-name $KeyVault --name $adminSecret `
        --query value -o tsv 2>$null

    if ($LASTEXITCODE -ne 0 -or -not $adminKey) {
        $alphabet = 'abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789'
        $bytes = [byte[]]::new(32)
        [System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
        $adminKey = -join ($bytes | ForEach-Object { $alphabet[$_ % $alphabet.Length] })

        Invoke-Az @(
            'keyvault', 'secret', 'set',
            '--vault-name', $KeyVault, '--name', $adminSecret, "--value=$adminKey", '--output', 'none'
        ) -Because 'Storing the admin key'

        Write-Host "  admin key created: $adminSecret"
    }
    else {
        $adminKey = ($adminKey | Select-Object -First 1).Trim()
        Write-Host "  admin key reused: $adminSecret"
    }

    # ConvertTo-Json must be called with -InputObject, not through the pipeline:
    # an empty array pipes zero items and yields an empty string rather than [].
    #
    # -AsArray must NOT be combined with @(): the wrapper already guarantees an
    # array, and -AsArray then wraps it again, so an empty list renders as [[]]
    # instead of []. That nested array is not empty, so the template's
    # empty() check passes it through and App Service rejects the resulting cors
    # block with BadRequest 51016 "HTTP request body must not be empty" -- a
    # message that names neither CORS nor the parameter.
    $corsJson = ConvertTo-Json -InputObject ([string[]]$CorsOrigin) -Compress -Depth 2

    # A single origin serialises as a bare string rather than an array, which the
    # template would reject as the wrong type.
    if ($CorsOrigin.Count -le 1) {
        $corsJson = '[' + (($CorsOrigin | ForEach-Object { '"' + $_ + '"' }) -join ',') + ']'
    }

    $outputs = Invoke-Az -AsJson @(
        'deployment', 'group', 'create',
        '--resource-group', $ResourceGroup,
        '--template-file', (Join-Path $repoRoot 'infra/sandbox/main.bicep'),
        '--parameters',
        "environmentName=$Environment",
        "keyVaultName=$KeyVault",
        "storageAccountName=$StorageAccount",
        "sqlServerName=$SqlServer",
        "sqlDatabaseName=$databaseName",
        "isbmBaseUrl=$isbmBaseUrl",
        "isbmApiKey=$isbmKey",
        "planSku=$PlanSku",
        "adminKey=$adminKey",
        "allowedCorsOrigins=$corsJson",
        '--query', 'properties.outputs',
        '-o', 'json'
    ) -Because 'Infrastructure deployment'

    # Two sites, two system-assigned principals. Both need the Key Vault and
    # Storage grants, and both wait on the same propagation delay.
    Write-Host "  api identity: $($outputs.apiPrincipalId.value)"
    Write-Host "  ui  identity: $($outputs.uiPrincipalId.value)"
    Write-Host '  role assignments can take a few minutes to propagate'
}

# --- Build and deploy ------------------------------------------------------

function Publish-SandboxApp {
    param(
        [Parameter(Mandatory)][string]$ProjectPath,
        [Parameter(Mandatory)][string]$AppName,
        [Parameter(Mandatory)][string]$Label
    )

    # Per-app output directories. A shared one would carry the previous app's
    # assemblies into this zip, and because zip deploy never deletes, the server
    # would end up with both entry points present.
    $publishDir = Join-Path $repoRoot "artifacts/publish-$Label"
    $zipPath = Join-Path $repoRoot "artifacts/sandbox-$Label.zip"

    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
    New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

    Write-Host "`nBuilding $Label..."

    & dotnet publish (Join-Path $repoRoot $ProjectPath) `
        --configuration Release `
        --output $publishDir `
        --nologo

    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $Label." }

    # Schemas are read at runtime and are not compiled in, so the build does not
    # carry them. Without this the app starts and reports zero participants, which
    # looks like a configuration error rather than a missing folder.
    #
    # Personality packs are NOT copied here. The csproj already publishes
    # PersonalityPacks/**/*.yaml, and Sandbox__PersonalitiesPath points at it.
    # An earlier version of this script copied SimHost/Personalities -- the C#
    # handler source -- into a Personalities/ folder, which the app then read in
    # preference to the real packs. Zip deployment does not remove files, so that
    # folder persisted across deployments and served stale fixtures indefinitely:
    # reg-location reported 2 property definitions after ControlAction was added,
    # and no error was raised because the folder did parse.
    foreach ($folder in @(
        @{ Name = 'Schemas'; Source = 'schemas' }
    )) {
        $source = Join-Path $repoRoot $folder.Source

        if (-not (Test-Path $source)) {
            Write-Warning "$($folder.Name) not found at $source"
            continue
        }

        Copy-Item $source -Destination (Join-Path $publishDir $folder.Name) -Recurse -Force
        $count = @(Get-ChildItem (Join-Path $publishDir $folder.Name) -Recurse -File).Count
        Write-Host "  $($folder.Name) : $count file(s)"
    }

    # Both apps load personalities through Core, so both need the packs. The
    # csproj Content glob links them out of Oiie.Sandbox.Core.
    $packCount = @(Get-ChildItem (Join-Path $publishDir 'PersonalityPacks') -Recurse -File -ErrorAction SilentlyContinue).Count
    if ($packCount -eq 0) {
        throw "PersonalityPacks did not publish for $Label. The csproj Content glob is the only thing that carries them."
    }
    Write-Host "  PersonalityPacks : $packCount file(s)"

    # Developer settings must not ship: they name one developer's database and alias.
    Remove-Item (Join-Path $publishDir 'appsettings.Development.json') -Force -ErrorAction SilentlyContinue

    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath

    Write-Host "  package: $([math]::Round((Get-Item $zipPath).Length / 1MB, 1)) MB"

    Write-Host "`nDeploying $Label to $AppName..."

    Invoke-Az @(
        'webapp', 'deploy',
        '--resource-group', $ResourceGroup,
        '--name', $AppName,
        '--src-path', $zipPath,
        '--type', 'zip',
        '--async', 'false'
    ) -Because "Deployment of $Label"
}

if ($Target -in @('api', 'both')) {
    Publish-SandboxApp -ProjectPath 'Oiie.Sandbox.Api/Oiie.Sandbox.Api.csproj' `
        -AppName $apiAppName -Label 'api'
}

if ($Target -in @('ui', 'both')) {
    Publish-SandboxApp -ProjectPath 'SimHost/SimHost.csproj' `
        -AppName $uiAppName -Label 'ui'
}

# --- Verify ----------------------------------------------------------------

if ($SkipVerify) {
    Write-Host "`nDone (verification skipped)."
    return
}

$apiUrl = "https://$apiAppName.azurewebsites.net"
$uiUrl = "https://$uiAppName.azurewebsites.net"

# Health lives on the API only. Verifying the UI against /health/participants
# would fail forever now that the route has moved.
if ($Target -eq 'ui') {
    Write-Host "`nVerifying $uiUrl"

    $ok = $false
    for ($attempt = 1; $attempt -le 12; $attempt++) {
        try {
            Invoke-WebRequest $uiUrl -TimeoutSec 30 -UseBasicParsing | Out-Null
            $ok = $true
            break
        }
        catch {
            Write-Host "  starting ($attempt)..." -ForegroundColor DarkGray
            Start-Sleep -Seconds 10
        }
    }

    if (-not $ok) {
        throw "The UI did not respond. Check the log stream: az webapp log tail -g $ResourceGroup -n $uiAppName"
    }

    Write-Host "`nDeployed: $uiUrl" -ForegroundColor Green
    Write-Host "The UI calls $apiUrl for reset and scenario launch; that app must be running."
    return
}

Write-Host "`nVerifying $apiUrl"

# A successful upload is not a working app. Everything below has failed in this
# project at least once for reasons a zip deploy cannot detect.
$health = $null

for ($attempt = 1; $attempt -le 12; $attempt++) {
    try {
        $health = Invoke-RestMethod "$apiUrl/health/participants" -TimeoutSec 30
        break
    }
    catch {
        Write-Host "  starting ($attempt)..." -ForegroundColor DarkGray
        Start-Sleep -Seconds 10
    }
}

if (-not $health) {
    throw "The app did not become healthy. Check the log stream: az webapp log tail -g $ResourceGroup -n $apiAppName"
}

$participantCount = @($health.participants).Count
Write-Host "  participants   : $participantCount"

if ($participantCount -eq 0) {
    throw 'No participants loaded. PersonalityPacks did not deploy, or Sandbox__PersonalitiesPath is wrong (it should be PersonalityPacks).'
}

Write-Host "  isbmConfigured : $($health.isbmConfigured)"
Write-Host "  storage        : $($health.storageConfigured)"

if (-not $health.storageConfigured) {
    Write-Warning 'Storage is not configured; BOD payload bodies will not be retained.'
}

# Key Vault and SQL are the two grants that take time to propagate, and the two
# that fail in ways the app cannot report until something asks it to connect.
try {
    $sql = Invoke-RestMethod "$apiUrl/health/sql" -TimeoutSec 60
    $failed = @($sql | Where-Object { -not $_.connected })

    if ($failed.Count -gt 0) {
        Write-Warning "SQL: $($failed[0].participantId) — $($failed[0].error)"
        Write-Warning 'If this is a Key Vault 403, the role assignment may still be propagating. Retry in a few minutes.'
    }
    else {
        Write-Host "  sql            : $(@($sql).Count) participant(s) connected as their own users"
    }
}
catch {
    Write-Warning "Could not reach /health/sql: $($_.Exception.Message)"
}

Write-Host "`nDeployed API: $apiUrl" -ForegroundColor Green
if ($Target -eq 'both') {
    Write-Host "Deployed UI : $uiUrl" -ForegroundColor Green
}

if (-not $health.adminKeyRequired) {
    Write-Warning 'Admin endpoints are NOT protected on this instance. Anyone who finds the URL can reset it.'
}

Write-Host ''
Write-Host 'Next:'
Write-Host "  `$key = az keyvault secret show --vault-name $KeyVault --name sandbox-admin-key-$Environment --query value -o tsv"
Write-Host "  .\Testing\test-sandbox.ps1 -SandboxUrl $apiUrl -AdminKey `$key"
Write-Host ''
Write-Host 'A deployed app is addressable, so ISBM NotifyListener callbacks become'
Write-Host 'testable for the first time — Isbm__ListenerBaseUrl is already set.'
