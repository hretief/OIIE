<#
.SYNOPSIS
    Provisions and deploys the integration engine function apps.

.DESCRIPTION
    One script for all five engines, because they differ only in their name,
    their peer provider, and their settings prefix. A script per engine would
    be five copies of the same 200 lines drifting apart -- the providers are
    already in that position and it is not a pattern worth extending.

    Under docs/azure-resource-naming-guidance.md an integration engine is an
    'engn' resource, NOT 'eng':

        acme-engn-cms-dev
        acme-engn-mms-dev
        acme-engn-eng-dev
        acme-engn-reglocation-dev
        acme-engn-rdl-dev

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
    [ValidateSet('cms', 'mms', 'eng', 'reglocation', 'rdl', 'all')]
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
    [switch]$InfraOnly,

    # Overwrite the Enabled / Isbm__Enabled settings on an app that already has
    # them, rather than preserving what is there.
    #
    # These are normally preserved so a redeploy cannot silently switch an
    # engine an operator turned off back on. That protection also means an app
    # provisioned while the defaults were 'false' keeps that false forever, so
    # this switch exists to carry an existing deployment across the change.
    [switch]$ForceEnable
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
#
# 'RawExtra' is optional and holds settings that must NOT be prefixed with the
# engine name -- RDL's listener binds its channel from the shared 'Isbm'
# section, which no amount of prefixing would reach. RdlIsbmOptions.Topics is
# initialised empty precisely so Isbm__Topics__0 can be set without the
# doubling described above.
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
        # Enabled ships TRUE. It used to ship false, because this engine's
        # channel is derived from an iTwin federation id and polling before a
        # twin exists fails every 15 seconds -- but a disabled engine reports a
        # clean deploy and publishes nothing, and that silence cost more to
        # diagnose than the failing poll ever did. An operator who turns it off
        # by hand still keeps it off; see the preserve logic further down.
        #
        # ResolveClassIdentityFromCir / RegisterClassIdentityInCir drive the
        # outbound half of the class cross-reference: resolve the EC class
        # through CIR and, on a miss, register the mapping under the GUID RDL
        # holds. The second depends on EngRdl__Enabled, which is why that is on
        # too -- RDL is the only source of a governed class GUID.
        #
        # IModelId is deliberately not set. The engine discovers every iModel
        # ENG holds and publishes each onto its own iTwin's channel, so there is
        # nothing to pin. The setting survives only as an optional filter for
        # narrowing a deployment to one model on purpose -- and because an
        # iModel id is regenerated by a provider reset, anything set here goes
        # stale silently, which is what previously left the engine publishing
        # nothing until someone noticed.
        Extra   = @{
            'Enabled'                       = 'true'
            'Domain'                        = 'engineering'
            'Topics__0'                     = 'oiie:sc01/ccom:SyncSegments'
            'SourceId'                      = 'ENG'
            'LogicalId'                     = 'ENG'
            'ResolveClassIdentityFromCir'   = 'true'
            'RegisterClassIdentityInCir'    = 'true'
        }
        RawExtra = @{
            'EngRdl__Enabled'           = 'true'
            'EngRdl__RequestChannelUri' = '/OIIE/RDL/Request'
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
        # Same reasoning as ENG, and the setting here is ITwinFederationId:
        # REG-LOCATION has no iModel concept. All three legs ship on -- the
        # sites leg always could, since its channel is enterprise-level, and
        # the other two now do for the same reason ENG's does.
        Extra   = @{
            'Enabled'            = 'true'
            'IngestEnabled'      = 'true'
            'SitesIngestEnabled' = 'true'
            'Domain'             = 'operations'
            'InboundDomain'      = 'engineering'
            'SourceId'           = 'REG-LOCATION'
            'LogicalId'          = 'REG-LOCATION'
            # The inbound half of the class cross-reference. ENG registers that
            # its EC class means an RDL class; this registers that a
            # REG-LOCATION class_id means the same one. The category must match
            # what ENG writes -- the correspondence is the two entries sharing a
            # CIRID inside one category, so a mismatch here produces two
            # unrelated halves rather than a mapping.
            'RegisterClassIdentityInCir' = 'true'
            'ClassCategoryId'            = 'RDL-CLASS'
        }
        Schedules = @{
            'RegLocationEngineSweepSchedule'       = '0 */5 * * * *'
            'RegLocationEngineIngestSchedule'      = '*/10 * * * * *'
            'RegLocationEngineSitesIngestSchedule' = '*/15 * * * * *'
        }
    }
    rdl = @{
        System  = 'rdl'
        Project = 'RdlEngine'
        Prefix  = 'RdlEngine'
        PeerVar = 'RdlBaseUrl'
        PeerKey = 'RdlApiKey'
        PeerApi = 'rdl'
        # Unlike ENG and REG-LOCATION, this engine ships ENABLED -- see RawExtra
        # below. Its channel is the fixed literal /OIIE/RDL/Request rather than
        # a URI derived from an iTwin federation id, so there is no twin to wait
        # for and nothing to configure by hand before it can usefully poll.
        #
        # DefaultSetName and TaxonomySetNames__1 name the published taxonomy set.
        # The mapping is namespace id -> set name; a namespace with no entry is
        # still answered, under DefaultSetName suffixed with its id.
        Extra   = @{
            'SenderLogicalId'      = 'RDL'
            'DefaultSetName'       = 'ACME-RDL'
            'TaxonomySetNames__1'  = 'ACME-RDL'
            'Version'              = '1.0'
        }
        # Settings that are NOT prefixed with the engine name. The listener binds
        # its channel configuration from the shared 'Isbm' section, so these
        # cannot go in Extra -- that table prefixes every key it applies, which
        # would yield RdlEngine__Isbm__Enabled and bind to nothing.
        RawExtra = @{
            'Isbm__Enabled'           = 'true'
            'Isbm__RequestChannelUri' = '/OIIE/RDL/Request'
            'Isbm__Topics__0'         = 'OIIE:S35:V1.0/CCOM:GetTaxonomySet:R1.0'
            'Isbm__MaxMessagesPerPoll' = '20'
        }
        # Every 10s, not every minute. This is a request/response provider: a
        # consumer is blocked waiting while the request sits unread, so the
        # interval is the latency every caller pays. At one minute, ENG's
        # bounded wait expired before RDL had looked at the channel.
        Schedules = @{ 'RdlEngineDrainSchedule' = '*/10 * * * * *' }
    }
}

$targets = if ($Engine -eq 'all') { 'eng', 'reglocation', 'cms', 'mms', 'rdl' } else { @($Engine) }

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

        # 'Enabled' is a first-provision default, not a redeploy instruction.
        # Once an operator has turned an engine off deliberately -- to stop it
        # polling a broken channel, say -- reapplying the default would switch
        # it back silently, and the reverse is worse still: a disabled engine
        # reports success with nothing published. So the value is only written
        # when the app does not already carry one, unless -ForceEnable says
        # otherwise. Same reasoning as deploy/cir/deploy.ps1.
        #
        # Note the defaults themselves now ship 'true'; -ForceEnable is how an
        # app provisioned under the old 'false' defaults is brought across.
        $existingSettings = @()
        if ($appExists) {
            $existingSettings = az functionapp config appsettings list `
                --resource-group $ResourceGroup `
                --name $appName `
                --query '[].name' -o tsv 2>$null
        }

        foreach ($k in $cfg.Extra.Keys) {
            $settingName = "$($prefix)__$k"

            if ($k -eq 'Enabled' -and $existingSettings -contains $settingName -and -not $ForceEnable) {
                Write-Host "  Preserving the existing $settingName on $appName." -ForegroundColor DarkYellow
                continue
            }

            $settings += "$settingName=$($cfg.Extra[$k])"
        }

        # Unprefixed settings, for engines that bind part of their configuration
        # from a shared section rather than their own. Isbm__Enabled is preserved
        # on redeploy for the same reason as Enabled above: an operator who turned
        # a listener off to stop it polling a broken channel should not have it
        # turned back on by the next deploy, silently.
        if ($cfg.ContainsKey('RawExtra')) {
            foreach ($k in $cfg.RawExtra.Keys) {

                if ($k -eq 'Isbm__Enabled' -and $existingSettings -contains $k -and -not $ForceEnable) {
                    Write-Host "  Preserving the existing $k on $appName." -ForegroundColor DarkYellow
                    continue
                }

                $settings += "$k=$($cfg.RawExtra[$k])"
            }
        }

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
