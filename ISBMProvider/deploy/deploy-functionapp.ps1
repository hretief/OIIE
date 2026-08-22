<#
.SYNOPSIS
    Deploys a ws-ISBM Service Provider Function App under the naming
    convention in docs/azure-resource-naming-guidance.md.

.DESCRIPTION
    Creates a NEW, independent ISBM environment:

        acme-api-isbm-dev  / acme-api-isbm-prod    Function App
        acme-sb-dev        / acme-sb-prod          Service Bus namespace
        acmestoragedev01   / acmestorageprod01     Storage (blob payloads)

    The existing isbm-func-44p2f3n6dv7p4 app is NOT touched. It keeps its own
    Service Bus (mndotdev), storage (mndot) and Key Vault (mndot), so the
    sandbox and anything pointing at that hostname keeps working unchanged.

    The new environment starts empty. ISBM state -- channels, subscriptions,
    posted messages -- is not migrated. That is deliberate: subscription state
    is tied to session ids the old provider handed out, and replaying them
    against a new namespace would hand consumers sessions the new provider
    cannot honour. Consumers re-open sessions against the new host at cutover.

    Infrastructure is applied with infra/isbm/main.bicep, which now accepts
    explicit resource names. Code is published with Azure Functions Core Tools.

.NOTES
    Run once per environment. Re-running is safe; the Bicep deployment is
    idempotent and zip deploy overwrites the app content.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('dev', 'prod')]
    [string]$Environment,

    [string]$ResourceGroup = 'HilmarRetiefRG',
    [string]$Location      = 'eastus',

    # Basic keeps the host resident. Service Bus triggered notification
    # dispatch and expiry only run promptly on an always-on host; on Y1 they
    # wait for the scale controller, which makes expiry look broken.
    [ValidateSet('B1', 'B2', 'EP1')]
    [string]$PlanSku = 'B1',

    [ValidateSet(2, 3, 4)]
    [int]$SecurityLevel = 3,

    [switch]$SkipInfrastructure,
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'

$repoRoot   = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$bicepFile  = Join-Path $repoRoot 'infra\isbm\main.bicep'
$projectDir = Join-Path $repoRoot 'ISBMProvider'

$appName     = "acme-api-isbm-$Environment"
$planName    = "acme-plan-isbm-$Environment"
$sbName      = "acme-sb-$Environment"
$storageName = "acmestorage${Environment}01"
$keyVaultName = "acme-kv-isbm-$Environment"

Write-Host "Deploying ISBM to '$appName'" -ForegroundColor Cyan
Write-Host "  Service Bus : $sbName"
Write-Host "  Storage     : $storageName"
Write-Host "  Key Vault   : $keyVaultName"
Write-Host ''

# ---------------------------------------------------------------------------
# Infrastructure
# ---------------------------------------------------------------------------

if (-not $SkipInfrastructure) {

    # The storage account is shared with the CMS deployment, which already
    # created it. Reuse it rather than failing on a name clash: ISBM writes
    # blob payloads under its own container, so there is no collision.
    $storageExists = az storage account show `
        --resource-group $ResourceGroup `
        --name $storageName `
        --query 'name' -o tsv 2>$null

    $storageParams = @()
    if ($storageExists) {
        Write-Host "Reusing existing storage account '$storageName'." -ForegroundColor Yellow
        $storageParams = @("existingStorageAccountName=$storageName")
    }
    else {
        $storageParams = @("storageNameOverride=$storageName")
    }

    Write-Host 'Applying infrastructure...' -ForegroundColor Cyan

    az deployment group create `
        --resource-group $ResourceGroup `
        --template-file $bicepFile `
        --parameters `
            location=$Location `
            functionAppNameOverride=$appName `
            planNameOverride=$planName `
            serviceBusNameOverride=$sbName `
            keyVaultNameOverride=$keyVaultName `
            planSku=$PlanSku `
            securityLevel=$SecurityLevel `
            skipSql=true `
            $storageParams `
        --output none

    if ($LASTEXITCODE -ne 0) { throw 'Infrastructure deployment failed.' }

    Write-Host 'Infrastructure applied.' -ForegroundColor Green
}

# ---------------------------------------------------------------------------
# Publish
# ---------------------------------------------------------------------------

if (-not $SkipPublish) {

    # New Function Apps ship with SCM basic auth disabled, and zip deploy via
    # Core Tools needs it. This bit us on the CMS deployment.
    Write-Host 'Enabling SCM basic auth for publishing...' -ForegroundColor Cyan
    az resource update `
        --resource-group $ResourceGroup `
        --namespace Microsoft.Web `
        --resource-type basicPublishingCredentialsPolicies `
        --name scm `
        --parent "sites/$appName" `
        --set properties.allow=true `
        --output none

    Write-Host 'Publishing ISBM provider...' -ForegroundColor Cyan
    Push-Location $projectDir
    try {
        func azure functionapp publish $appName --dotnet-isolated

        if ($LASTEXITCODE -ne 0) {
            # Core Tools sometimes dies with "Timed out waiting for SCM to
            # update the Environment Settings" against a freshly created app,
            # and it does not recover on retry. The CLI zip deploy pushes the
            # same build through the same endpoint without that handshake, so
            # fall back rather than leaving the app empty and returning 502.
            Write-Host 'Core Tools publish failed; falling back to zip deploy...' -ForegroundColor Yellow

            $outDir = Join-Path $projectDir 'bin\zipout'
            $zipPath = Join-Path $projectDir "bin\$appName.zip"

            dotnet publish -c Release -o $outDir --nologo -v q
            if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

            if (Test-Path $zipPath) { Remove-Item $zipPath }
            Compress-Archive -Path (Join-Path $outDir '*') -DestinationPath $zipPath

            az functionapp deployment source config-zip `
                --resource-group $ResourceGroup `
                --name $appName `
                --src $zipPath `
                --build-remote false `
                --timeout 600 `
                --output none

            if ($LASTEXITCODE -ne 0) { throw 'Function publish failed (both methods).' }
        }
    }
    finally {
        Pop-Location
    }

    Write-Host 'Published.' -ForegroundColor Green
}

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------

$baseUrl = "https://$appName.azurewebsites.net/api"

Write-Host ''
Write-Host 'ISBM deployed.' -ForegroundColor Green
Write-Host "  Base URL : $baseUrl"
Write-Host ''
Write-Host 'Point CIR at this provider with:' -ForegroundColor Cyan
Write-Host "  .\CirProvider\deploy\deploy-functionapp.ps1 -Environment $Environment -IsbmBaseUrl $baseUrl"
Write-Host ''
Write-Host 'The old isbm-func-44p2f3n6dv7p4 app is untouched and still serving.' -ForegroundColor Yellow
Write-Host 'This new provider starts with no channels; they are created on demand.' -ForegroundColor Yellow
