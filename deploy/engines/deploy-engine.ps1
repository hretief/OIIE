<#
.SYNOPSIS
    Provisions and deploys the integration engine function apps.

.DESCRIPTION
    One script for all four engines, because they differ only in their name,
    their peer provider, and their settings prefix. A script per engine would
    be four copies of the same 200 lines drifting apart -- the providers are
    already in that position and it is not a pattern worth extending.

    Under docs/azure-resource-naming-guidance.md an integration engine is an
    'engn' resource, NOT 'eng':

        acme-engn-cms-dev
        acme-engn-mms-dev
        acme-engn-eng-dev
        acme-engn-reglocation-dev

    'eng' is already the SYSTEM code for Engineering, so 'acme-eng-eng-dev'
    would be ambiguous. That collision is the whole reason the type code is
    four letters.

    WHAT AN ENGINE DOES NOT GET
    No database, and no SQL identity. An engine holds an ISBM session, reads
    BODs, and calls its provider over the provider's published HTTP API. If an
    engine ever needs a connection string the boundary it exists to prove has
    already been broken -- see participant-abstraction-spec.md 5.2. This is
    why there is no provision-databases.ps1 next to this file and no
    'CREATE USER' step printed at the end.

    Engines authenticate to their peers with function keys, which this script
    reads from the peer apps and applies. The keys are real secrets; they are
    written to app settings and never printed.

.NOTES
    Requires the Az CLI, Azure Functions Core Tools, and the .NET SDK.
    Run it yourself; it is not run automatically.

.EXAMPLE
    ./deploy-engine.ps1 -Engine cms -Environment dev

.EXAMPLE
    ./deploy-engine.ps1 -Engine all -Environment dev -InfraOnly

.EXAMPLE
    ./deploy-engine.ps1 -Engine mms -Environment dev -WhatIf
#>

[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [ValidateSet('cms', 'mms', 'eng', 'reglocation', 'all')]
    [string]$Engine,

    [Parameter(Mandatory)]
    [ValidateSet('dev', 'prod')]
    [string]$Environment,

    [string]$ResourceGroup = 'HilmarRetiefRG',
    [string]$Location      = 'eastus',

    # Basic rather than Consumption, matching the providers. An engine polls on
    # a timer, and a Consumption cold start between polls looks like a dropped
    # message during a demo.
    [string]$PlanSku = 'B1',

    # Skip the code publish and only ensure the infrastructure exists.
    [switch]$InfraOnly
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# The engine table. 'system' is the naming-guidance system code; 'project' is
# the folder to publish from; 'prefix' is the options binding prefix; 'peerVar'
# is the setting that points at the engine's own provider.
#
# 'reglocation' is deliberately unhyphenated -- the system code is a single
# segment of <account>-<type>-<system>-<env>, so an internal hyphen would make
# the name parse as a different system in anything that splits on '-'.
#
# 'Extra' deliberately omits the *Topics__0 settings. Configuration array
# binding APPENDS to whatever the property was initialised with rather than
# replacing it, so setting SitesTopics__0 to the value the code already
# defaults to yields ["oiie/ccom:SyncSites", "oiie/ccom:SyncSites"] -- a double
# subscription, and every publication handled twice. To override a topic list
# you must set every index AND ensure the code default is empty.
# ---------------------------------------------------------------------------
$engines = @{
    cms = @{
        System  = 'cms'
        Project = 'CmsEngine'
        Prefix  = 'CmsEngine'
        PeerVar = 'CmsBaseUrl'
        PeerKey = 'CmsApiKey'
        PeerApi = 'cms'
        Extra   = @{
            'SitesIngestEnabled'   = 'true'
            'MaxMessagesPerPoll'   = '25'
            'SourceId'             = 'CMS'
            'LogicalId'            = 'CMS'
            'RegisterInCir'        = 'true'
        }
        Schedules = @{ 'CmsEngineSitesIngestSchedule' = '0 */2 * * * *' }
    }
    mms = @{
        System  = 'mms'
        Project = 'MmsEngine'
        Prefix  = 'MmsEngine'
        PeerVar = 'MmsBaseUrl'
        PeerKey = 'MmsApiKey'
        PeerApi = 'mms'
        Extra   = @{
            'SitesIngestEnabled'   = 'true'
            'MaxMessagesPerPoll'   = '25'
            'SourceId'             = 'MMS'
            'LogicalId'            = 'MMS'
            'RegisterInCir'        = 'true'
        }
        Schedules = @{ 'MmsEngineSitesIngestSchedule' = '0 */2 * * * *' }
    }
    eng = @{
        System  = 'eng'
        Project = 'EngEngine'
        Prefix  = 'EngEngine'
        PeerVar = 'EngBaseUrl'
        PeerKey = 'EngApiKey'
        PeerApi = 'eng'
        # Enabled stays false: this engine's channel is derived from an iTwin
        # federation id, and turning it on before an iTwin exists gives a poll
        # loop that fails every 15 seconds. Set IModelId then flip Enabled by
        # hand. (ENG's setting is named IModelId, but the channel value is an
        # iTwin federation id.)
        Extra   = @{
            'Enabled'   = 'false'
            'Domain'    = 'engineering'
            'Topics__0' = 'oiie:sc01/ccom:SyncSegments'
            'SourceId'  = 'ENG'
            'LogicalId' = 'ENG'
        }
        Schedules = @{ 'EngEnginePollSchedule' = '*/15 * * * * *' }
    }
    reglocation = @{
        System  = 'reglocation'
        Project = 'RegLocationEngine'
        Prefix  = 'RegLocationEngine'
        PeerVar = 'RegLocationBaseUrl'
        PeerKey = 'RegLocationApiKey'
        PeerApi = 'reglocation'
        # Same reasoning as ENG, but the setting here is ITwinFederationId:
        # REG-LOCATION has no iModel concept. SitesIngest is the exception --
        # its channel is enterprise-level, so it can run before any iTwin
        # exists.
        Extra   = @{
            'Enabled'            = 'false'
            'IngestEnabled'      = 'false'
            'SitesIngestEnabled' = 'true'
            'Domain'             = 'operations'
            'InboundDomain'      = 'engineering'
            'SourceId'           = 'REG-LOCATION'
            'LogicalId'          = 'REG-LOCATION'
        }
        Schedules = @{
            'RegLocationEngineSweepSchedule'       = '0 */5 * * * *'
            'RegLocationEngineIngestSchedule'      = '0 */2 * * * *'
            'RegLocationEngineSitesIngestSchedule' = '0 */2 * * * *'
        }
    }
}

$targets = if ($Engine -eq 'all') { 'eng', 'reglocation', 'cms', 'mms' } else { @($Engine) }

# Shared across every app in the environment. Do not reintroduce a per-app plan.
$planName    = "acme-plan-$Environment"
$storageName = "acmestorage${Environment}01"

$repoRoot = Join-Path $PSScriptRoot '..' '..' | Resolve-Path

# ---------------------------------------------------------------------------
# Shared infrastructure. Created once regardless of how many engines follow.
# ---------------------------------------------------------------------------
$storageExists = az storage account show `
    --resource-group $ResourceGroup --name $storageName --query 'name' -o tsv 2>$null

if (-not $storageExists) {
    if ($PSCmdlet.ShouldProcess($storageName, 'Create storage account')) {
        Write-Host "Creating storage account '$storageName'..." -ForegroundColor Cyan
        az storage account create `
            --resource-group $ResourceGroup `
            --name $storageName `
            --location $Location `
            --sku Standard_LRS `
            --kind StorageV2 `
            --min-tls-version TLS1_2 `
            --allow-blob-public-access false `
            --output none
        if ($LASTEXITCODE -ne 0) { throw "Failed to create storage account '$storageName'." }
    }
}

$planExists = az functionapp plan show `
    --resource-group $ResourceGroup --name $planName --query 'name' -o tsv 2>$null

if (-not $planExists) {
    if ($PSCmdlet.ShouldProcess($planName, 'Create app service plan')) {
        Write-Host "Creating plan '$planName' ($PlanSku)..." -ForegroundColor Cyan
        az functionapp plan create `
            --resource-group $ResourceGroup `
            --name $planName `
            --location $Location `
            --sku $PlanSku `
            --output none
        if ($LASTEXITCODE -ne 0) { throw "Failed to create plan '$planName'." }
    }
}

# Report what the plan actually IS, not what -PlanSku defaults to. The two
# diverge the moment anyone scales it, and a script that keeps printing the
# creation default is how you end up chasing a capacity problem while staring
# at a number that has been wrong for months. This plan runs B3, not B1.
$actualSku = az appservice plan show `
    --resource-group $ResourceGroup --name $planName --query 'sku.name' -o tsv 2>$null
if (-not $actualSku) { $actualSku = $PlanSku }

# ---------------------------------------------------------------------------
# Peer endpoints. Every engine talks to ISBM; the site-ingesting ones also
# register identities in CIR. Keys are read once and reused.
#
# Note this targets acme-api-isbm-dev, NOT the older isbm-func-* app that the
# local.settings.json files point at. The engines must share a broker with the
# publishers, so if you change one you change all of them.
# ---------------------------------------------------------------------------
function Get-FunctionKey {
    param([string]$AppName)

    $key = az functionapp keys list `
        --resource-group $ResourceGroup `
        --name $AppName `
        --query 'functionKeys.default' -o tsv 2>$null

    if ($LASTEXITCODE -ne 0 -or -not $key) {
        Write-Host "  Could not read a function key from '$AppName'." -ForegroundColor Yellow
        Write-Host '  Leaving it empty; the engine will get 401 until it is set.' -ForegroundColor Yellow
        return ''
    }
    return $key
}

$isbmApp = "acme-api-isbm-$Environment"
$cirApp  = "acme-api-cir-$Environment"

Write-Host ''
Write-Host 'Reading peer function keys...' -ForegroundColor Cyan
$isbmUrl = "https://$isbmApp.azurewebsites.net/api"
$cirUrl  = "https://$cirApp.azurewebsites.net/api"
$isbmKey = Get-FunctionKey $isbmApp
$cirKey  = Get-FunctionKey $cirApp

# ---------------------------------------------------------------------------
# Per-engine provisioning.
# ---------------------------------------------------------------------------
foreach ($name in $targets) {
    $cfg     = $engines[$name]
    $appName = "acme-engn-$($cfg.System)-$Environment"
    $peerApp = "acme-api-$($cfg.PeerApi)-$Environment"
    $prefix  = $cfg.Prefix

    Write-Host ''
    Write-Host "Deploying $($cfg.Project) to '$Environment'" -ForegroundColor Cyan
    Write-Host "  Function app : $appName"
    Write-Host "  Plan         : $planName ($actualSku)"
    Write-Host "  Storage      : $storageName"
    Write-Host "  Peer API     : $peerApp"
    Write-Host '  Database     : none -- an engine holds no data access'
    Write-Host ''

    $peerExists = az functionapp show `
        --resource-group $ResourceGroup --name $peerApp --query 'name' -o tsv 2>$null

    if (-not $peerExists) {
        throw "Peer provider '$peerApp' does not exist. Deploy it before its engine."
    }

    $appExists = az functionapp show `
        --resource-group $ResourceGroup --name $appName --query 'name' -o tsv 2>$null

    if ($appExists) {
        Write-Host "Function app '$appName' already exists. Skipping create." -ForegroundColor Yellow
    }
    elseif ($PSCmdlet.ShouldProcess($appName, 'Create function app')) {
        Write-Host "Creating function app '$appName'..." -ForegroundColor Cyan
        az functionapp create `
            --resource-group $ResourceGroup `
            --name $appName `
            --plan $planName `
            --storage-account $storageName `
            --runtime dotnet-isolated `
            --runtime-version 10 `
            --functions-version 4 `
            --output none
        if ($LASTEXITCODE -ne 0) { throw "Failed to create function app '$appName'." }
    }

    if ($PSCmdlet.ShouldProcess($appName, 'Configure')) {

        az functionapp update `
            --resource-group $ResourceGroup `
            --name $appName `
            --set httpsOnly=true `
            --output none

        # Azure now creates apps with SCM basic auth DISABLED. 'func ... publish'
        # authenticates that way, and without this it fails late and misleadingly
        # with "Timed out waiting for SCM to update the Environment Settings" --
        # which reads like a transient race and is not one.
        az resource update `
            --resource-group $ResourceGroup `
            --name scm `
            --namespace Microsoft.Web `
            --resource-type basicPublishingCredentialsPolicies `
            --parent "sites/$appName" `
            --set properties.allow=true `
            --output none

        $peerKey = Get-FunctionKey $peerApp
        $peerUrl = "https://$peerApp.azurewebsites.net/api"

        $settings = @(
            "$($prefix)__$($cfg.PeerVar)=$peerUrl"
            "$($prefix)__$($cfg.PeerKey)=$peerKey"
            "$($prefix)__Enterprise=acme"
            "$($prefix)__CirBaseUrl=$cirUrl"
            "$($prefix)__CirApiKey=$cirKey"
            "Isbm__BaseUrl=$isbmUrl"
            "Isbm__ApiKey=$isbmKey"
        )

        foreach ($k in $cfg.Extra.Keys)     { $settings += "$($prefix)__$k=$($cfg.Extra[$k])" }
        foreach ($k in $cfg.Schedules.Keys) { $settings += "$k=$($cfg.Schedules[$k])" }

        Write-Host 'Applying application settings...' -ForegroundColor Cyan
        az functionapp config appsettings set `
            --resource-group $ResourceGroup `
            --name $appName `
            --settings @settings `
            --output none
        if ($LASTEXITCODE -ne 0) { throw "Failed to apply application settings to '$appName'." }
    }

    if ($InfraOnly) {
        Write-Host 'InfraOnly specified; skipping code publish.' -ForegroundColor Yellow
    }
    elseif ($PSCmdlet.ShouldProcess($appName, 'Publish code')) {
        Write-Host 'Publishing...' -ForegroundColor Cyan
        Push-Location (Join-Path $repoRoot $cfg.Project)
        try {
            func azure functionapp publish $appName --dotnet-isolated
            if ($LASTEXITCODE -ne 0) { throw "Publish to '$appName' failed." }
        }
        finally {
            Pop-Location
        }
    }

    Write-Host ''
    Write-Host "Deployed $appName." -ForegroundColor Green
    Write-Host "  https://$appName.azurewebsites.net/api/engine/status"
}

Write-Host ''
Write-Host 'No SQL grant step is required -- engines have no database.' -ForegroundColor DarkGray
Write-Host 'If an engine ever needs one, the boundary in spec 5.2 has been broken.' -ForegroundColor DarkGray
