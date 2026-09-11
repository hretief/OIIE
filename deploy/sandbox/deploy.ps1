<#
.SYNOPSIS
    Deploys the OIIE Sandbox to Azure App Service.

.DESCRIPTION
    Provisions infrastructure, publishes the apps, and verifies the deployment
    actually works rather than merely that the upload succeeded.

    The sandbox is ONE App Service:

      acme-api-sandbox-{env}  the API. Owns /admin and /health.

    The inbox pump and outbox dispatcher no longer run here: per DR-022 the ENG
    and REG-LOCATION engines own publish and ingest, so the sandbox copies were
    removed rather than migrated.

    The Blazor operator UI that used to deploy alongside it (oiie-simhost-{env})
    has been removed -- the demo uses the TypeScript UI in WorkflowOrchestration/,
    which is built into this API's wwwroot. Both the Azure sites and the Bicep
    resource are gone as of DR-024.

    Assumes deploy/provision.ps1 has already run for this environment: the storage
    container and Key Vault secrets come from there. This script adds the hosting.
    The sandbox owns no database of its own -- participant data lives in the
    provider apps, which are provisioned and deployed separately.

    Two things are copied into the publish output that are not part of the project:
    Personalities and Schemas. They live beside the solution locally and inside
    wwwroot when deployed, which is why the path settings differ per environment.

.NOTES
    Role assignments take a few minutes to propagate. A 403 reading Key Vault
    immediately after a first deployment is usually that, not a missing grant.

.EXAMPLE
    .\deploy.ps1 -Environment dev

.EXAMPLE
    .\deploy.ps1 -Environment dev -SkipInfrastructure
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('dev', 'ci', 'demo')]
    [string]$Environment,

    # Defaulted rather than mandatory: there is one storage account for this
    # solution, and requiring it per invocation only invites the wrong one being
    # typed. The previous default was carried in the examples rather than the
    # parameter, which is how mndotsandbox outlived its retirement.
    [string]$StorageAccount = 'acmestoragedev01',

    [string]$ResourceGroup = 'HilmarRetiefRG',
    [string]$KeyVault = 'mndot',
    [string]$SqlServer = 'acme-sql-server',
    [string]$SubscriptionId,

    [string]$IsbmApp = 'acme-api-isbm-dev',

    # The customer-system emulators the ENG and stewardship panels read through
    # to. Named per environment rather than hardcoded so a demo can point at a
    # different set than dev.
    #
    # Leave either empty to keep that panel on the sandbox's own participants,
    # which is what a deployment without the provider apps should do.
    [string]$EngApp = 'acme-api-eng-dev',
    [string]$RegLocationApp = 'acme-api-reglocation-dev',

    # CIR holds the identities of rows the other systems renumber from 1 on a
    # day-zero reset, so it has to be reachable for a reset to be complete. MMS
    # and CMS hold no data yet, but their reset endpoints exist and day zero
    # calls them, so they are configured here rather than reported as failures.
    [string]$CirApp = 'acme-api-cir-dev',
    [string]$MmsApp = 'acme-api-mms-dev',
    [string]$CmsApp = 'acme-api-cms-dev',

    # The integration engine behind ENG. Separate from the provider app: it
    # carries the SyncSites publish that add-iTwin triggers.
    [string]$EngEngineApp = 'acme-engn-eng-dev',

    # The integration engine behind REG-LOCATION. Day zero has to reach it: its
    # published-tag set is keyed so that it survives the registry being rebuilt,
    # so resetting REG-LOCATION's database without clearing this leaves an
    # engine that republishes nothing.
    [string]$RegLocationEngineApp = 'acme-engn-reglocation-dev',

    [string]$PlanSku = 'B1',

    # Browser origins allowed to call the API. Only needed if the React app is
    # ever hosted separately; it currently ships inside the API, so same-origin
    # requests need no policy at all.
    [string[]]$CorsOrigin = @(),

    # Skip building the React app. Useful when Node is unavailable or only the
    # .NET side changed -- the previously deployed wwwroot then stays as it is,
    # since zip deploy does not delete.
    [switch]$SkipWeb,

    [switch]$SkipInfrastructure,
    [switch]$SkipVerify,

    # Create the web app's RBAC role assignments (Key Vault, blob). Off by
    # default because ARM issues roleAssignments/write on every deployment even
    # when the assignment is unchanged, which a Contributor-only identity such
    # as CI cannot do. Needed once per new environment, by a principal with
    # User Access Administrator.
    [switch]$AssignRoles
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

$apiAppName = "acme-api-sandbox-$Environment"
$isbmBaseUrl = "https://$IsbmApp.azurewebsites.net/api"

Write-Host "Environment : $Environment"
Write-Host "API         : $apiAppName"
Write-Host "Storage     : $StorageAccount"
Write-Host ''

if ($SubscriptionId) {
    Invoke-Az @('account', 'set', '--subscription', $SubscriptionId) -Because 'Setting subscription'
}


# --- Admin key

# Resolved before the infrastructure block, and outside it, because the build
# guard further down needs the key to check the bundle for it. Leaving this
# inside -SkipInfrastructure meant the one switch people reach for when infra is
# already in place also silently disabled the leak check.
#
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


# --- Infrastructure

if (-not $SkipInfrastructure) {
    Write-Host 'Deploying infrastructure...'

    $isbmKey = & az functionapp keys list -g $ResourceGroup -n $IsbmApp `
        --query functionKeys.default -o tsv 2>$null
    if ($LASTEXITCODE -ne 0) { $isbmKey = '' }

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
        "isbmBaseUrl=$isbmBaseUrl",
        "isbmApiKey=$isbmKey",
        "planSku=$PlanSku",
        "adminKey=$adminKey",
        "allowedCorsOrigins=$corsJson",
        "assignRoles=$($AssignRoles.IsPresent.ToString().ToLowerInvariant())",
        '--query', 'properties.outputs',
        '-o', 'json'
    ) -Because 'Infrastructure deployment'

    # One site, one system-assigned principal. It needs the Key Vault and
    # Storage grants, and waits on the propagation delay.
    Write-Host "  api identity: $($outputs.apiPrincipalId.value)"
    Write-Host '  role assignments can take a few minutes to propagate'
}

# --- Provider read-through -------------------------------------------------
#
# The ENG and stewardship panels read the deployed customer-system emulators
# rather than the sandbox's own participants. The keys are fetched here rather
# than held in the template or the repo: they are rotatable secrets, and a
# deployment is the only place that legitimately needs to know them.
#
# Set outside the Bicep deployment because these are additive settings on an
# existing site. A provider whose key cannot be read is left unconfigured, and
# the API falls back to the sandbox participants for that panel -- a degraded
# but working demo, rather than a panel of authentication errors.

function Set-ProviderSettings {
    param(
        [Parameter(Mandatory)][string]$AppName,
        [Parameter(Mandatory)][string]$SettingPrefix,
        [Parameter(Mandatory)][string]$Label
    )

    if ([string]::IsNullOrWhiteSpace($AppName)) {
        Write-Host "  $Label`: not configured, panel stays on sandbox data"
        return
    }

    $key = & az functionapp keys list -g $ResourceGroup -n $AppName `
        --query functionKeys.default -o tsv 2>$null

    if ($LASTEXITCODE -ne 0 -or -not $key) {
        Write-Warning "  $Label`: could not read a function key for '$AppName'. Panel stays on sandbox data."
        return
    }

    $baseUrl = "https://$AppName.azurewebsites.net/api"

    # Double underscore is the configuration separator: Providers__Eng__BaseUrl
    # binds to Providers:Eng:BaseUrl.
    Invoke-Az @(
        'webapp', 'config', 'appsettings', 'set',
        '--resource-group', $ResourceGroup,
        '--name', $apiAppName,
        '--settings',
        "$SettingPrefix`__BaseUrl=$baseUrl",
        "$SettingPrefix`__Key=$key",
        '-o', 'none'
    ) -Because "$Label provider settings"

    Write-Host "  $Label`: $baseUrl"
}

Write-Host 'Configuring provider read-through...'
Set-ProviderSettings -AppName $EngApp -SettingPrefix 'Providers__Eng' -Label 'ENG'
Set-ProviderSettings -AppName $RegLocationApp -SettingPrefix 'Providers__RegLocation' -Label 'REG-LOCATION'

# These three drive no panel: they exist so day zero can reach every system that
# holds data. The reset reads Providers__*__BaseUrl as a fallback for its own
# Sandbox__* names, so one setting per provider now serves both purposes --
# previously REG-LOCATION was configured here and still skipped by the reset,
# which read a different name and said nothing.
Set-ProviderSettings -AppName $CirApp -SettingPrefix 'Providers__Cir' -Label 'CIR'
Set-ProviderSettings -AppName $MmsApp -SettingPrefix 'Providers__Mms' -Label 'MMS'
Set-ProviderSettings -AppName $CmsApp -SettingPrefix 'Providers__Cms' -Label 'CMS'

# The registry day zero drops. Matches the Enterprise the engines are deployed
# with in deploy/engines/deploy-engine.ps1; the sandbox has no way to derive it.
Invoke-Az @(
    'webapp', 'config', 'appsettings', 'set',
    '--resource-group', $ResourceGroup,
    '--name', $apiAppName,
    '--settings', 'Sandbox__CirRegistryId=acme',
    '-o', 'none'
) -Because 'CIR registry id'

# Read-through and the SyncSites bootstrap are configured separately: the
# Providers__* settings above drive the panels, while EngEngineClient reads the
# Sandbox__* pair below. Setting only the first leaves add-iTwin recording the
# twin and reporting that it was never announced, which is how a working panel
# and a silent engine coexisted.
function Set-EngBootstrapSettings {
    param(
        [Parameter(Mandatory)][string]$ProviderApp,
        [Parameter(Mandatory)][string]$EngineApp
    )

    if ([string]::IsNullOrWhiteSpace($ProviderApp) -or [string]::IsNullOrWhiteSpace($EngineApp)) {
        Write-Host '  ENG bootstrap: not configured, SyncSites will not be published'
        return
    }

    $engineKey = & az functionapp keys list -g $ResourceGroup -n $EngineApp `
        --query functionKeys.default -o tsv 2>$null

    if ($LASTEXITCODE -ne 0 -or -not $engineKey) {
        Write-Warning "  ENG bootstrap: could not read a function key for '$EngineApp'."
        return
    }

    Invoke-Az @(
        'webapp', 'config', 'appsettings', 'set',
        '--resource-group', $ResourceGroup,
        '--name', $apiAppName,
        '--settings',
        "Sandbox__EngProviderBaseUrl=https://$ProviderApp.azurewebsites.net/api",
        "Sandbox__EngEngineBaseUrl=https://$EngineApp.azurewebsites.net/api",
        "Sandbox__EngEngineKey=$engineKey",
        '-o', 'none'
    ) -Because 'ENG bootstrap settings'

    Write-Host "  ENG bootstrap: provider $ProviderApp, engine $EngineApp"
}

Set-EngBootstrapSettings -ProviderApp $EngApp -EngineApp $EngEngineApp

# The REG-LOCATION engine is reached only by day zero, so it needs a base URL
# and a key of its own rather than the ENG pair above.
function Set-RegLocationEngineSettings {
    param(
        [Parameter(Mandatory)][string]$EngineApp
    )

    if ([string]::IsNullOrWhiteSpace($EngineApp)) {
        Write-Host '  REG-LOCATION engine: not configured, day zero will report it as unreset'
        return
    }

    $engineKey = & az functionapp keys list -g $ResourceGroup -n $EngineApp `
        --query functionKeys.default -o tsv 2>$null

    if ($LASTEXITCODE -ne 0 -or -not $engineKey) {
        Write-Warning "  REG-LOCATION engine: could not read a function key for '$EngineApp'."
        return
    }

    Invoke-Az @(
        'webapp', 'config', 'appsettings', 'set',
        '--resource-group', $ResourceGroup,
        '--name', $apiAppName,
        '--settings',
        "Sandbox__RegLocationEngineBaseUrl=https://$EngineApp.azurewebsites.net/api",
        "Sandbox__RegLocationEngineKey=$engineKey",
        '-o', 'none'
    ) -Because 'REG-LOCATION engine settings'

    Write-Host "  REG-LOCATION engine: $EngineApp"
}

Set-RegLocationEngineSettings -EngineApp $RegLocationEngineApp

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

    # The Workflow Orchestration app ships inside the API, served from wwwroot.
    #
    # Same origin as the API it drives, so there is no CORS to configure and no
    # admin key in a browser bundle -- that key resets databases and deletes
    # channels, so keeping it server-side is the reason for hosting this way
    # rather than as a separate Static Web App.
    #
    # VITE_SANDBOX_API is deliberately left unset: an empty base makes the client
    # issue same-origin requests, which is exactly right here. Setting it would
    # pin the bundle to one environment's hostname.
    if ($Label -eq 'api' -and -not $SkipWeb) {
        $webRoot = Join-Path $repoRoot 'WorkflowOrchestration'

        if (-not (Test-Path (Join-Path $webRoot 'package.json'))) {
            Write-Warning "WorkflowOrchestration not found at $webRoot; the API will serve no UI."
        }
        else {
            Write-Host "`n  Building Workflow Orchestration..."

            Push-Location $webRoot
            try {
                # npm ci needs a lockfile; fall back to install so a fresh clone
                # without one still deploys rather than failing here.
                if (Test-Path 'package-lock.json') { & npm ci --no-audit --no-fund 2>&1 | Out-Null }
                else { & npm install --no-audit --no-fund 2>&1 | Out-Null }

                if ($LASTEXITCODE -ne 0) { throw 'npm install failed.' }

                # Cleared for the build only. Vite inlines every VITE_-prefixed
                # variable, and this bundle is served from the API's own public
                # wwwroot -- so an admin key present here is an admin key
                # published to anyone who opens the site. The app asks for it at
                # runtime instead and keeps it in sessionStorage.
                #
                # Set on the process rather than edited in .env.local because the
                # variable may equally come from the shell or CI.
                $priorKey = $env:VITE_SANDBOX_ADMIN_KEY
                $env:VITE_SANDBOX_ADMIN_KEY = ''

                try {
                    & npx vite build 2>&1 | Out-Null
                    if ($LASTEXITCODE -ne 0) { throw 'vite build failed.' }
                }
                finally {
                    $env:VITE_SANDBOX_ADMIN_KEY = $priorKey
                }
            }
            finally {
                Pop-Location
            }

            $dist = Join-Path $webRoot 'dist'
            if (-not (Test-Path (Join-Path $dist 'index.html'))) {
                throw 'vite build produced no index.html. The API would serve an empty wwwroot.'
            }

            # Fails the deploy rather than publishing a readable admin key.
            # Checked against the built output because that is what actually
            # ships: reasoning about which .env file won is how this got missed
            # the first time.
            if ($adminKey) {
                $leaked = Get-ChildItem (Join-Path $dist 'assets') -Filter *.js -ErrorAction SilentlyContinue |
                    Where-Object { Select-String -Path $_.FullName -SimpleMatch $adminKey -Quiet }

                if ($leaked) {
                    throw ("The admin key is present in the built bundle ($($leaked.Name)). " +
                           'Something set VITE_SANDBOX_ADMIN_KEY for the build. Refusing to ' +
                           'publish it to a public wwwroot.')
                }
            }

            $wwwroot = Join-Path $publishDir 'wwwroot'
            New-Item -ItemType Directory -Path $wwwroot -Force | Out-Null
            Copy-Item (Join-Path $dist '*') -Destination $wwwroot -Recurse -Force

            $webCount = @(Get-ChildItem $wwwroot -Recurse -File).Count
            Write-Host "  wwwroot : $webCount file(s)"
        }
    }

    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath

    Write-Host "  package: $([math]::Round((Get-Item $zipPath).Length / 1MB, 1)) MB"

    Write-Host "`nDeploying $Label to $AppName..."

    # --clean wipes wwwroot before unpacking, because zip deploy otherwise only
    # overwrites and never removes. A personality pack deleted from the repo
    # therefore stayed on the server and kept being loaded: PersonalityLoader
    # walks every personality.yaml it finds, so a removed participant went on
    # declaring channels that day zero dutifully recreated. The published output
    # is complete on its own, so there is nothing on the server worth keeping.
    Invoke-Az @(
        'webapp', 'deploy',
        '--resource-group', $ResourceGroup,
        '--name', $AppName,
        '--src-path', $zipPath,
        '--type', 'zip',
        '--clean', 'true',
        '--async', 'false'
    ) -Because "Deployment of $Label"
}

Publish-SandboxApp -ProjectPath 'Oiie.Sandbox.Api/Oiie.Sandbox.Api.csproj' `
    -AppName $apiAppName -Label 'api'

# --- Verify ----------------------------------------------------------------

if ($SkipVerify) {
    Write-Host "`nDone (verification skipped)."
    return
}

$apiUrl = "https://$apiAppName.azurewebsites.net"

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

# SQL connectivity is no longer checked here: the sandbox owns no database. Each
# participant provider connects to its own store and reports its own health.

Write-Host "`nDeployed API: $apiUrl" -ForegroundColor Green

# The React app is served by the API, so a successful API deployment does not by
# itself mean the UI shipped. Checked separately, because a missing wwwroot
# returns a clean 404 that nothing else here would notice.
if (-not $SkipWeb) {
    try {
        $shell = Invoke-WebRequest $apiUrl -TimeoutSec 60 -UseBasicParsing

        if ($shell.Content -match '<div id="root">') {
            Write-Host "Workflow Orchestration: $apiUrl" -ForegroundColor Green
        }
        else {
            Write-Warning "$apiUrl answered, but the response is not the Workflow Orchestration shell."
        }
    }
    catch {
        Write-Warning "Workflow Orchestration is not being served at $apiUrl : $($_.Exception.Message)"
    }
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
