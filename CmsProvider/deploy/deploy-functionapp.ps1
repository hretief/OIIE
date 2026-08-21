<#
.SYNOPSIS
    Provisions and deploys the CmsProvider function app.

.DESCRIPTION
    CmsProvider is the LOB API tier for CMS -- it emulates the Meridium
    product itself. Under docs/azure-resource-naming-guidance.md that makes
    it an 'api' resource, not an 'engn' one:

        acme-api-cms-dev     development instance
        acme-api-cms-prod    production instance

    The engine that will front it is CmsEngine and deploys separately as
    acme-engn-cms-<env>. Do not merge the two: an engine may use only access
    a third party could be granted, whereas this API owns the CMS data
    directly. That boundary is the reason for the separate resource.

    Creates, if absent: a Basic B1 plan, a user-assigned identity, a storage
    account, and the function app -- then publishes the code.

    The app authenticates to SQL as its user-assigned identity via
    'Active Directory Default'. Creating the Azure resources is NOT
    sufficient to make it work: the identity needs a database user in the
    target database. See the manual step printed at the end.

    Schema objects are not created here. The app applies schema.sql at
    startup when Cms__AutoCreateSchema is true.

.NOTES
    Requires the Az CLI, Azure Functions Core Tools, and the .NET SDK.
    Run it yourself; it is not run automatically.

.EXAMPLE
    ./deploy-functionapp.ps1 -Environment dev

.EXAMPLE
    ./deploy-functionapp.ps1 -Environment prod -WhatIf
#>

[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [ValidateSet('dev', 'prod')]
    [string]$Environment,

    [string]$ResourceGroup = 'HilmarRetiefRG',
    [string]$Location      = 'eastus',
    [string]$SqlServerName = 'acme-sql-server',

    # Basic rather than Consumption, matching the CIR provider. Consumption
    # cold starts are long enough to be mistaken for a fault during a demo.
    [string]$PlanSku = 'B1',

    # Skip the code publish and only ensure the infrastructure exists.
    [switch]$InfraOnly
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# Names. All derived from the environment so the two deployments cannot
# diverge by hand-editing. Function app and storage names are GLOBALLY
# unique; 'acmestorage<env>' without the suffix is already taken by another
# tenant, which is why the storage name carries '01'.
# ---------------------------------------------------------------------------
$appName      = "acme-api-cms-$Environment"
$planName     = "acme-plan-cms-$Environment"
$identityName = "acme-id-cms-$Environment"
$storageName  = "acmestorage${Environment}01"
$databaseName = "acme-db-cms-$Environment"

$projectPath = Join-Path $PSScriptRoot '..' | Resolve-Path

Write-Host ''
Write-Host "Deploying CmsProvider to '$Environment'" -ForegroundColor Cyan
Write-Host "  Function app : $appName"
Write-Host "  Plan         : $planName ($PlanSku)"
Write-Host "  Identity     : $identityName"
Write-Host "  Storage      : $storageName"
Write-Host "  Database     : $databaseName on $SqlServerName"
Write-Host ''

# ---------------------------------------------------------------------------
# Preflight. The database is created by provision-databases.ps1, not here.
# Failing now is better than deploying an app that cannot reach its data.
# ---------------------------------------------------------------------------
$dbExists = az sql db show `
    --resource-group $ResourceGroup `
    --server $SqlServerName `
    --name $databaseName `
    --query 'name' -o tsv 2>$null

if (-not $dbExists) {
    throw "Database '$databaseName' does not exist. Run provision-databases.ps1 first."
}

# ---------------------------------------------------------------------------
# Storage account. The Functions runtime requires one for its own state;
# it is shared across systems in an environment, so it carries no system code.
# ---------------------------------------------------------------------------
$storageExists = az storage account show `
    --resource-group $ResourceGroup `
    --name $storageName `
    --query 'name' -o tsv 2>$null

if ($storageExists) {
    Write-Host "Storage account '$storageName' already exists. Skipping." -ForegroundColor Yellow
}
elseif ($PSCmdlet.ShouldProcess($storageName, 'Create storage account')) {
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

# ---------------------------------------------------------------------------
# User-assigned identity. Preferred over system-assigned because it survives
# the app being deleted and recreated -- the SQL user granted below is keyed
# on this principal, and a system-assigned identity would orphan it.
# ---------------------------------------------------------------------------
$identityExists = az identity show `
    --resource-group $ResourceGroup `
    --name $identityName `
    --query 'name' -o tsv 2>$null

if ($identityExists) {
    Write-Host "Identity '$identityName' already exists. Skipping." -ForegroundColor Yellow
}
elseif ($PSCmdlet.ShouldProcess($identityName, 'Create user-assigned identity')) {
    Write-Host "Creating identity '$identityName'..." -ForegroundColor Cyan
    az identity create `
        --resource-group $ResourceGroup `
        --name $identityName `
        --location $Location `
        --output none
    if ($LASTEXITCODE -ne 0) { throw "Failed to create identity '$identityName'." }
}

$identityId = az identity show `
    --resource-group $ResourceGroup --name $identityName --query 'id' -o tsv 2>$null

$identityClientId = az identity show `
    --resource-group $ResourceGroup --name $identityName --query 'clientId' -o tsv 2>$null

# Under -WhatIf the identity was never created, so these are empty. That is
# expected and must not fail the dry run; on a real run it means the create
# above silently did not take, which must fail.
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
    --resource-group $ResourceGroup `
    --name $planName `
    --query 'name' -o tsv 2>$null

if ($planExists) {
    Write-Host "Plan '$planName' already exists. Skipping." -ForegroundColor Yellow
}
elseif ($PSCmdlet.ShouldProcess($planName, 'Create App Service plan')) {
    Write-Host "Creating plan '$planName'..." -ForegroundColor Cyan
    az appservice plan create `
        --resource-group $ResourceGroup `
        --name $planName `
        --location $Location `
        --sku $PlanSku `
        --output none
    if ($LASTEXITCODE -ne 0) { throw "Failed to create plan '$planName'." }
}

# ---------------------------------------------------------------------------
# Function app.
# ---------------------------------------------------------------------------
$appExists = az functionapp show `
    --resource-group $ResourceGroup `
    --name $appName `
    --query 'name' -o tsv 2>$null

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
        --assign-identity $identityId `
        --output none
    if ($LASTEXITCODE -ne 0) { throw "Failed to create function app '$appName'." }
}

if ($PSCmdlet.ShouldProcess($appName, 'Configure')) {

    # Bind the user-assigned identity explicitly. 'create' takes it above, but
    # this is idempotent and covers the already-exists path.
    az functionapp identity assign `
        --resource-group $ResourceGroup `
        --name $appName `
        --identities $identityId `
        --output none

    az functionapp update `
        --resource-group $ResourceGroup `
        --name $appName `
        --set httpsOnly=true `
        --output none

    # Azure now creates apps with SCM basic auth DISABLED. 'func ... publish'
    # authenticates that way, and without this it fails late and misleadingly
    # with "Timed out waiting for SCM to update the Environment Settings" --
    # which reads like a transient race and is not one. The older cir-func app
    # predates this default, which is why it publishes without the step.
    az resource update `
        --resource-group $ResourceGroup `
        --name scm `
        --namespace Microsoft.Web `
        --resource-type basicPublishingCredentialsPolicies `
        --parent "sites/$appName" `
        --set properties.allow=true `
        --output none

    # No secret here: the connection string names the identity to
    # authenticate as, and the token is acquired at runtime.
    $connectionString = "Server=tcp:$SqlServerName.database.windows.net,1433;" +
                        "Initial Catalog=$databaseName;" +
                        "Authentication=Active Directory Default;" +
                        "Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;"

    Write-Host 'Applying application settings...' -ForegroundColor Cyan
    az functionapp config appsettings set `
        --resource-group $ResourceGroup `
        --name $appName `
        --settings `
            "Cms__SqlConnectionString=$connectionString" `
            "Cms__AutoCreateSchema=true" `
            "AZURE_CLIENT_ID=$identityClientId" `
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
        if ($LASTEXITCODE -ne 0) { throw "Publish to '$appName' failed." }
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
# The principal is the user-assigned identity, NOT the function app. With a
# system-assigned identity these names coincide, which is a common way to get
# this wrong; here the app authenticates as $identityName via AZURE_CLIENT_ID,
# so a user created for [$appName] would never be matched.
Write-Host "  CREATE USER [$identityName] FROM EXTERNAL PROVIDER;"
Write-Host "  ALTER ROLE db_datareader ADD MEMBER [$identityName];"
Write-Host "  ALTER ROLE db_datawriter ADD MEMBER [$identityName];"
Write-Host "  ALTER ROLE db_ddladmin  ADD MEMBER [$identityName];"
Write-Host ''
# Order matters. The app applies schema.sql once at startup, so on a first
# deployment it runs BEFORE the SQL user above exists, fails, and logs it
# without taking the host down. Health then keeps reporting "Invalid object
# name 'dbo.Site'" even after the grant lands, because nothing re-ran the
# bootstrap. Hence the restart, and hence it printing after the grant.
Write-Host '2. Restart the app so the schema bootstrap re-runs:' -ForegroundColor Yellow
Write-Host "     az functionapp restart -g $ResourceGroup -n $appName"
Write-Host ''
Write-Host 'db_ddladmin is needed only because Cms__AutoCreateSchema applies' -ForegroundColor DarkGray
Write-Host 'schema.sql at startup. Drop it once the schema is settled.' -ForegroundColor DarkGray
