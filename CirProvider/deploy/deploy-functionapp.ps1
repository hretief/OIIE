<#
.SYNOPSIS
    Deploys the CIR provider Function App under the naming convention in
    docs/azure-resource-naming-guidance.md.

.DESCRIPTION
    Creates, if absent:

        acme-api-cir-dev / acme-api-cir-prod    Function App
        acme-plan-cir-dev / acme-plan-cir-prod  App Service plan
        acme-id-cir-dev / acme-id-cir-prod      user-assigned identity

    and wires the app to acme-db-cir-<env> on acme-sql-server (created by
    provision-databases.ps1) and to the ws-ISBM provider given by -IsbmBaseUrl.

    The existing cir-func-44p2f3n6 app is NOT touched. It keeps pointing at
    the legacy 'cir' database and the old ISBM host, so the sandbox keeps
    working exactly as it does today.

    The new registry starts EMPTY. No CIRIDs are migrated, which is only
    safe because none have been issued from the legacy registry yet -- if
    that changes, this becomes a data migration rather than a deployment.

.NOTES
    Storage is shared with the other providers in the environment
    (acmestorage<env>01), matching the naming guidance.
#>

[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [ValidateSet('dev', 'prod')]
    [string]$Environment,

    # Base URL of the ws-ISBM provider this CIR coordinates through, e.g.
    # https://acme-api-isbm-dev.azurewebsites.net/api
    [Parameter(Mandatory)]
    [string]$IsbmBaseUrl,

    # Function key for the ISBM provider. The CIR sends it as x-functions-key;
    # without it every ISBM call is 401 and the listener silently drains
    # nothing. Left empty, it is read from the matching ISBM app.
    [string]$IsbmApiKey = '',

    [string]$ResourceGroup  = 'HilmarRetiefRG',
    [string]$Location       = 'eastus',
    [string]$SqlServerName  = 'acme-sql-server',

    [string]$PlanSku = 'B1',

    # Channels the CIR listens on. These match the legacy deployment, and the
    # ISBM provider creates them on demand.
    [string]$IsbmRequestChannelUri     = '/OIIE/CIR/Request',
    [string]$IsbmPublicationChannelUri = '/OIIE/CIR/Publication',

    # Matches the legacy app. Fast enough to feel responsive in a demo.
    [string]$IsbmPollSchedule = '*/15 * * * * *',

    # Isbm__Enabled defaults to false in code so the listener stays dormant
    # until an ISBM provider exists. It does here, so turn it on; without
    # this the poll timer runs but drains nothing.
    [bool]$IsbmEnabled = $true,

    [switch]$InfraOnly
)

$ErrorActionPreference = 'Stop'

$repoRoot    = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$projectPath = Join-Path $repoRoot 'CirProvider'

$appName      = "acme-api-cir-$Environment"
$planName     = "acme-plan-cir-$Environment"
$identityName = "acme-id-cir-$Environment"
$storageName  = "acmestorage${Environment}01"
$databaseName = "acme-db-cir-$Environment"

if (-not $IsbmApiKey) {
    $isbmAppName = "acme-api-isbm-$Environment"
    Write-Host "Reading ISBM function key from '$isbmAppName'..." -ForegroundColor Cyan
    $IsbmApiKey = az functionapp keys list `
        --resource-group $ResourceGroup --name $isbmAppName `
        --query 'functionKeys.default' -o tsv 2>$null
    if (-not $IsbmApiKey) {
        throw "Could not read the function key from '$isbmAppName'. Pass -IsbmApiKey explicitly."
    }
}

Write-Host "Deploying CIR to '$appName'" -ForegroundColor Cyan
Write-Host "  Plan      : $planName ($PlanSku)"
Write-Host "  Identity  : $identityName"
Write-Host "  Database  : $databaseName"
Write-Host "  ISBM      : $IsbmBaseUrl"
Write-Host ''

# ---------------------------------------------------------------------------
# Storage. Shared across providers in the environment.
# ---------------------------------------------------------------------------
$storageExists = az storage account show `
    --resource-group $ResourceGroup --name $storageName --query 'name' -o tsv 2>$null

if ($storageExists) {
    Write-Host "Storage account '$storageName' already exists. Skipping." -ForegroundColor Yellow
}
elseif ($PSCmdlet.ShouldProcess($storageName, 'Create storage account')) {
    az storage account create `
        --resource-group $ResourceGroup --name $storageName --location $Location `
        --sku Standard_LRS --kind StorageV2 --min-tls-version TLS1_2 `
        --allow-blob-public-access false --output none
    if ($LASTEXITCODE -ne 0) { throw "Failed to create storage account '$storageName'." }
}

# ---------------------------------------------------------------------------
# User-assigned identity. Survives the app being deleted and recreated; the
# SQL user granted below is keyed on this principal.
# ---------------------------------------------------------------------------
$identityExists = az identity show `
    --resource-group $ResourceGroup --name $identityName --query 'name' -o tsv 2>$null

if ($identityExists) {
    Write-Host "Identity '$identityName' already exists. Skipping." -ForegroundColor Yellow
}
elseif ($PSCmdlet.ShouldProcess($identityName, 'Create user-assigned identity')) {
    az identity create `
        --resource-group $ResourceGroup --name $identityName --location $Location --output none
    if ($LASTEXITCODE -ne 0) { throw "Failed to create identity '$identityName'." }
}

$identityId = az identity show `
    --resource-group $ResourceGroup --name $identityName --query 'id' -o tsv 2>$null
$identityClientId = az identity show `
    --resource-group $ResourceGroup --name $identityName --query 'clientId' -o tsv 2>$null

if (-not $identityId) {
    if ($WhatIfPreference) {
        $identityId       = "<$identityName (not created: -WhatIf)>"
        $identityClientId = '<pending>'
    }
    else {
        throw "Identity '$identityName' could not be resolved after creation."
    }
}

# ---------------------------------------------------------------------------
# App Service plan.
# ---------------------------------------------------------------------------
$planExists = az appservice plan show `
    --resource-group $ResourceGroup --name $planName --query 'name' -o tsv 2>$null

if ($planExists) {
    Write-Host "Plan '$planName' already exists. Skipping." -ForegroundColor Yellow
}
elseif ($PSCmdlet.ShouldProcess($planName, 'Create App Service plan')) {
    az appservice plan create `
        --resource-group $ResourceGroup --name $planName --location $Location `
        --sku $PlanSku --output none
    if ($LASTEXITCODE -ne 0) { throw "Failed to create plan '$planName'." }
}

# ---------------------------------------------------------------------------
# Function app.
# ---------------------------------------------------------------------------
$appExists = az functionapp show `
    --resource-group $ResourceGroup --name $appName --query 'name' -o tsv 2>$null

if ($appExists) {
    Write-Host "Function app '$appName' already exists. Skipping create." -ForegroundColor Yellow
}
elseif ($PSCmdlet.ShouldProcess($appName, 'Create function app')) {
    az functionapp create `
        --resource-group $ResourceGroup --name $appName --plan $planName `
        --storage-account $storageName --runtime dotnet-isolated --runtime-version 10 `
        --functions-version 4 --assign-identity $identityId --output none
    if ($LASTEXITCODE -ne 0) { throw "Failed to create function app '$appName'." }
}

if ($PSCmdlet.ShouldProcess($appName, 'Configure')) {

    az functionapp identity assign `
        --resource-group $ResourceGroup --name $appName --identities $identityId --output none

    az functionapp update `
        --resource-group $ResourceGroup --name $appName --set httpsOnly=true --output none

    # New apps ship with SCM basic auth disabled, which makes 'func publish'
    # fail late with "Timed out waiting for SCM to update the Environment
    # Settings" -- misleading, since it is not a transient race.
    az resource update `
        --resource-group $ResourceGroup --name scm `
        --namespace Microsoft.Web --resource-type basicPublishingCredentialsPolicies `
        --parent "sites/$appName" --set properties.allow=true --output none

    # No secret: the connection string names the identity to authenticate as,
    # and the token is acquired at runtime via AZURE_CLIENT_ID.
    $connectionString = "Server=tcp:$SqlServerName.database.windows.net,1433;" +
                        "Initial Catalog=$databaseName;" +
                        "Authentication=Active Directory Default;" +
                        "Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;"

    Write-Host 'Applying application settings...' -ForegroundColor Cyan
    az functionapp config appsettings set `
        --resource-group $ResourceGroup `
        --name $appName `
        --settings `
            "Cir__SqlConnectionString=$connectionString" `
            "Cir__AutoCreateSchema=true" `
            "AZURE_CLIENT_ID=$identityClientId" `
            "Isbm__BaseUrl=$IsbmBaseUrl" `
            "Isbm__Enabled=$IsbmEnabled" `
            "Isbm__ApiKey=$IsbmApiKey" `
            "Isbm__RequestChannelUri=$IsbmRequestChannelUri" `
            "Isbm__PublicationChannelUri=$IsbmPublicationChannelUri" `
            "IsbmPollSchedule=$IsbmPollSchedule" `
        --output none
    if ($LASTEXITCODE -ne 0) { throw 'Failed to apply application settings.' }
}

# ---------------------------------------------------------------------------
# Publish.
# ---------------------------------------------------------------------------
if ($InfraOnly) {
    Write-Host 'InfraOnly specified; skipping code publish.' -ForegroundColor Yellow
}
elseif ($PSCmdlet.ShouldProcess($appName, 'Publish code')) {
    Write-Host 'Publishing...' -ForegroundColor Cyan
    Push-Location $projectPath
    try {
        func azure functionapp publish $appName --dotnet-isolated

        if ($LASTEXITCODE -ne 0) {
            # Same SCM handshake failure seen deploying ISBM to prod. The CLI
            # zip deploy pushes the same build without that handshake.
            Write-Host 'Core Tools publish failed; falling back to zip deploy...' -ForegroundColor Yellow

            $outDir  = Join-Path $projectPath 'bin\zipout'
            $zipPath = Join-Path $projectPath "bin\$appName.zip"

            dotnet publish -c Release -o $outDir --nologo -v q
            if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

            if (Test-Path $zipPath) { Remove-Item $zipPath }
            Compress-Archive -Path (Join-Path $outDir '*') -DestinationPath $zipPath

            az functionapp deployment source config-zip `
                --resource-group $ResourceGroup --name $appName --src $zipPath `
                --build-remote false --timeout 600 --output none

            if ($LASTEXITCODE -ne 0) { throw "Publish to '$appName' failed (both methods)." }
        }
    }
    finally {
        Pop-Location
    }
}

# ---------------------------------------------------------------------------
# The step this script cannot do for you.
# ---------------------------------------------------------------------------
Write-Host ''
Write-Host 'Deployed.' -ForegroundColor Green
Write-Host "  https://$appName.azurewebsites.net/api/health"
Write-Host ''
Write-Host 'REQUIRED MANUAL STEP -- the app cannot reach SQL until this is done.' -ForegroundColor Yellow
Write-Host "1. Connect to '$databaseName' as an Entra admin and run:"
Write-Host ''
# The principal is the user-assigned identity, NOT the function app.
Write-Host "  CREATE USER [$identityName] FROM EXTERNAL PROVIDER;"
Write-Host "  ALTER ROLE db_datareader ADD MEMBER [$identityName];"
Write-Host "  ALTER ROLE db_datawriter ADD MEMBER [$identityName];"
Write-Host "  ALTER ROLE db_ddladmin  ADD MEMBER [$identityName];"
Write-Host ''
# Order matters: the schema bootstrap runs once at startup, so on a first
# deployment it runs before the SQL user exists and fails without taking the
# host down. Nothing re-runs it until the app restarts.
Write-Host '2. Restart the app so the schema bootstrap re-runs:' -ForegroundColor Yellow
Write-Host "     az functionapp restart -g $ResourceGroup -n $appName"
Write-Host ''
Write-Host 'The old cir-func-44p2f3n6 app is untouched and still serving.' -ForegroundColor Yellow
Write-Host 'db_ddladmin is needed only while Cir__AutoCreateSchema is true.' -ForegroundColor DarkGray
