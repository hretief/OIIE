<#
.SYNOPSIS
    Deploys the OIIE Sandbox to Azure App Service.

.DESCRIPTION
    Provisions infrastructure, publishes the app, and verifies the deployment
    actually works rather than merely that the upload succeeded.

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
    .\deploy.ps1 -Environment demo -StorageAccount mndotsandbox -SkipInfrastructure
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('dev', 'ci', 'demo')]
    [string]$Environment,

    [Parameter(Mandatory)]
    [string]$StorageAccount,

    [string]$ResourceGroup = 'HilmarRetiefRG',
    [string]$KeyVault = 'mndot',
    [string]$SqlServer = 'acme-sql-server',
    [string]$SubscriptionId,

    [string]$IsbmApp = 'isbm-func-44p2f3n6dv7p4',

    # Only needed when a per-developer database is being deployed.
    [string]$Alias,

    [string]$PlanSku = 'B1',

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

$appName = "oiie-sandbox-$Environment"
$isbmBaseUrl = "https://$IsbmApp.azurewebsites.net/api"

Write-Host "Environment : $Environment"
Write-Host "App         : $appName"
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

    $outputs = Invoke-Az -AsJson @(
        'deployment', 'group', 'create',
        '--resource-group', $ResourceGroup,
        '--template-file', (Join-Path $repoRoot 'infra/main.bicep'),
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
        '--query', 'properties.outputs',
        '-o', 'json'
    ) -Because 'Infrastructure deployment'

    Write-Host "  app identity: $($outputs.principalId.value)"
    Write-Host '  role assignments can take a few minutes to propagate'
}

# --- Build -----------------------------------------------------------------

$publishDir = Join-Path $repoRoot 'artifacts/publish'
$zipPath = Join-Path $repoRoot 'artifacts/sandbox.zip'

if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

Write-Host "`nBuilding..."

& dotnet publish (Join-Path $repoRoot 'SimHost/SimHost.csproj') `
    --configuration Release `
    --output $publishDir `
    --nologo

if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

# Personalities and Schemas are read at runtime and are not compiled in, so the
# build does not carry them. Without this the app starts and reports zero
# participants, which looks like a configuration error rather than a missing folder.
foreach ($folder in @(
    @{ Name = 'Personalities'; Source = 'SimHost/Personalities' },
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

# Developer settings must not ship: they name one developer's database and alias.
Remove-Item (Join-Path $publishDir 'appsettings.Development.json') -Force -ErrorAction SilentlyContinue

if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath

Write-Host "  package: $([math]::Round((Get-Item $zipPath).Length / 1MB, 1)) MB"

# --- Deploy ----------------------------------------------------------------

Write-Host "`nDeploying to $appName..."

Invoke-Az @(
    'webapp', 'deploy',
    '--resource-group', $ResourceGroup,
    '--name', $appName,
    '--src-path', $zipPath,
    '--type', 'zip',
    '--async', 'false'
) -Because 'Deployment'

# --- Verify ----------------------------------------------------------------

if ($SkipVerify) {
    Write-Host "`nDone (verification skipped)."
    return
}

$appUrl = "https://$appName.azurewebsites.net"
Write-Host "`nVerifying $appUrl"

# A successful upload is not a working app. Everything below has failed in this
# project at least once for reasons a zip deploy cannot detect.
$health = $null

for ($attempt = 1; $attempt -le 12; $attempt++) {
    try {
        $health = Invoke-RestMethod "$appUrl/health/participants" -TimeoutSec 30
        break
    }
    catch {
        Write-Host "  starting ($attempt)..." -ForegroundColor DarkGray
        Start-Sleep -Seconds 10
    }
}

if (-not $health) {
    throw "The app did not become healthy. Check the log stream: az webapp log tail -g $ResourceGroup -n $appName"
}

$participantCount = @($health.participants).Count
Write-Host "  participants   : $participantCount"

if ($participantCount -eq 0) {
    throw 'No participants loaded. The Personalities folder did not deploy, or Sandbox__PersonalitiesPath is wrong.'
}

Write-Host "  isbmConfigured : $($health.isbmConfigured)"
Write-Host "  storage        : $($health.storageConfigured)"

if (-not $health.storageConfigured) {
    Write-Warning 'Storage is not configured; BOD payload bodies will not be retained.'
}

# Key Vault and SQL are the two grants that take time to propagate, and the two
# that fail in ways the app cannot report until something asks it to connect.
try {
    $sql = Invoke-RestMethod "$appUrl/health/sql" -TimeoutSec 60
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

Write-Host "`nDeployed: $appUrl" -ForegroundColor Green

if (-not $health.adminKeyRequired) {
    Write-Warning 'Admin endpoints are NOT protected on this instance. Anyone who finds the URL can reset it.'
}

Write-Host ''
Write-Host 'Next:'
Write-Host "  `$key = az keyvault secret show --vault-name $KeyVault --name sandbox-admin-key-$Environment --query value -o tsv"
Write-Host "  .\Testing\test-sandbox.ps1 -SandboxUrl $appUrl -AdminKey `$key"
Write-Host ''
Write-Host 'A deployed app is addressable, so ISBM NotifyListener callbacks become'
Write-Host 'testable for the first time — Isbm__ListenerBaseUrl is already set.'
