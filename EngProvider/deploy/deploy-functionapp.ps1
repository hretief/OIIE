<#
.SYNOPSIS
    Provisions and deploys the EngProvider function app.

.DESCRIPTION
    EngProvider is the LOB API tier for ENG -- it emulates the engineering
    design tool itself. Under docs/azure-resource-naming-guidance.md that
    makes it an 'api' resource, not an 'engn' one:

        acme-api-eng-dev     development instance
        acme-api-eng-prod    production instance

    The engine that will front it is EngEngine and deploys separately as
    acme-engn-eng-<env>. It does not exist yet. Do not merge the two when it
    does: an engine may use only access a third party could be granted,
    whereas this API owns the ENG data directly. That boundary is the reason
    for the separate resource.

    Creates, if absent: a Basic B1 plan, a user-assigned identity, and the
    function app -- then publishes the code. The storage account is shared
    with the other providers in the environment and is expected to exist.

    The app authenticates to SQL as its user-assigned identity via
    'Active Directory Default'. Creating the Azure resources is not
    sufficient to make it work: the identity also needs a database user in
    the target database, which this script creates for you. That requires
    you to be an Entra admin on the SQL server.

    Schema objects are not created here. The app applies schema.sql at
    startup when Eng__AutoCreateSchema is true, which is why the script
    restarts the app after the grant and then waits for health.

.NOTES
    Requires the Az CLI, the .NET SDK, and the SqlServer PowerShell module.
    Azure Functions Core Tools is NOT required -- the code is packaged with
    'dotnet publish' and pushed to the Kudu zipdeploy endpoint.
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

    # Basic rather than Consumption, matching the CMS and CIR providers.
    # Consumption cold starts are long enough to be mistaken for a fault
    # during a demo.
    [string]$PlanSku = 'B1',

    # Skip the code publish and only ensure the infrastructure exists.
    [switch]$InfraOnly
)

$ErrorActionPreference = 'Stop'

# Invoke-Sqlcmd creates the SQL user below. Failing here beats failing after
# the infrastructure has already been created.
if (-not (Get-Command Invoke-Sqlcmd -ErrorAction SilentlyContinue)) {
    Import-Module SqlServer -ErrorAction SilentlyContinue
}
if (-not (Get-Command Invoke-Sqlcmd -ErrorAction SilentlyContinue)) {
    throw 'Invoke-Sqlcmd is not available. Run: Install-Module SqlServer -Scope CurrentUser'
}

# ---------------------------------------------------------------------------
# Names. All derived from the environment so the two deployments cannot
# diverge by hand-editing. Function app and storage names are GLOBALLY
# unique; 'acmestorage<env>' without the suffix is already taken by another
# tenant, which is why the storage name carries '01'.
# ---------------------------------------------------------------------------
$appName      = "acme-api-eng-$Environment"
# Shared across every provider in the environment, like storage and Service
# Bus. One B1 hosts them all; a plan per provider was six B1s billing
# continuously to run one app each. Do not reintroduce a per-provider plan.
$planName     = "acme-plan-$Environment"
$identityName = "acme-id-eng-$Environment"
$storageName  = "acmestorage${Environment}01"
$databaseName = "acme-db-eng-$Environment"

$projectPath = Join-Path $PSScriptRoot '..' | Resolve-Path

Write-Host ''
Write-Host "Deploying EngProvider to '$Environment'" -ForegroundColor Cyan
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
    # which reads like a transient race and is not one.
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

    # No ISBM or CIR settings. This app emulates a customer system and has no
    # integration to configure; the engine that does deploys separately.
    Write-Host 'Applying application settings...' -ForegroundColor Cyan
    az functionapp config appsettings set `
        --resource-group $ResourceGroup `
        --name $appName `
        --settings `
            "Eng__SqlConnectionString=$connectionString" `
            "Eng__AutoCreateSchema=true" `
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
    # 'func azure functionapp publish' is not reliable here: it repeatedly fails
    # with "Timed out waiting for SCM to update the Environment Settings" even
    # with SCM basic auth enabled above, and 'az functionapp deployment source
    # config-zip' answers 502 through the deployment gateway. Building the
    # package ourselves and pushing it to the Kudu async zipdeploy endpoint
    # avoids both, and is what actually got the dev app deployed.
    Write-Host 'Publishing...' -ForegroundColor Cyan

    $publishDir = Join-Path $projectPath 'bin/zipdeploy'
    $zipPath    = Join-Path $projectPath 'bin/eng-deploy.zip'

    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
    if (Test-Path $zipPath)    { Remove-Item $zipPath -Force }

    dotnet publish $projectPath -c Release -o $publishDir --nologo
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

    Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath

    # The publishing profile carries the SCM credentials; --query on a nested
    # object keeps the password out of the command line.
    $profileJson = az functionapp deployment list-publishing-profiles `
        --resource-group $ResourceGroup `
        --name $appName `
        --query "[?publishMethod=='MSDeploy'].{u:userName,p:userPWD}" -o json
    if ($LASTEXITCODE -ne 0) { throw 'Failed to read publishing profile.' }

    $creds = $profileJson | ConvertFrom-Json
    if (-not $creds) { throw "No MSDeploy publishing profile for '$appName'." }

    $basic = [Convert]::ToBase64String(
        [Text.Encoding]::ASCII.GetBytes("$($creds[0].u):$($creds[0].p)"))

    $response = Invoke-WebRequest `
        -Uri "https://$appName.scm.azurewebsites.net/api/publish?type=zip&isAsync=true" `
        -Method Post `
        -Headers @{ Authorization = "Basic $basic" } `
        -InFile $zipPath `
        -ContentType 'application/zip' `
        -TimeoutSec 600 `
        -UseBasicParsing

    if ($response.StatusCode -ge 400) {
        throw "Zip deploy to '$appName' failed with HTTP $($response.StatusCode)."
    }

    # The push is accepted asynchronously; the site needs a moment before the
    # new package is mounted and the grant/restart below means anything.
    Write-Host 'Package accepted; waiting for the site to pick it up...'
    Start-Sleep -Seconds 30
}

# ---------------------------------------------------------------------------
# SQL user for the managed identity.
#
# CREATE USER ... FROM EXTERNAL PROVIDER needs the SQL server to hold the
# Directory Readers role, which only a Global Admin can grant -- which is why
# this used to be a manual step. Creating the user from the identity's client
# ID as a SID needs no directory permission at all and is equivalent. This is
# the same approach deploy/cir/deploy.ps1 uses.
#
# The principal is the user-assigned identity, NOT the function app. With a
# system-assigned identity these names coincide, which is a common way to get
# this wrong; here the app authenticates as $identityName via AZURE_CLIENT_ID,
# so a user created for [$appName] would never be matched.
# ---------------------------------------------------------------------------
if ($PSCmdlet.ShouldProcess($databaseName, "Grant SQL access to $identityName")) {
    Write-Host 'Granting the identity access to SQL...' -ForegroundColor Cyan

    $sidBytes = ([guid]$identityClientId).ToByteArray()
    $sidHex   = '0x' + (($sidBytes | ForEach-Object { $_.ToString('X2') }) -join '')

    $grantSql = @"
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'$identityName')
BEGIN
    EXEC(N'CREATE USER [$identityName] WITH SID = $sidHex, TYPE = E');
END
ALTER ROLE db_datareader ADD MEMBER [$identityName];
ALTER ROLE db_datawriter ADD MEMBER [$identityName];
ALTER ROLE db_ddladmin  ADD MEMBER [$identityName];
"@

    # Entra token rather than a SQL login: the caller must already be an Entra
    # admin on the server. A stale CLI refresh token surfaces here as an
    # 'az account get-access-token' failure, not as a SQL error.
    $sqlToken = az account get-access-token --resource https://database.windows.net/ --query accessToken -o tsv
    if ($LASTEXITCODE -ne 0 -or -not $sqlToken) {
        throw 'Could not acquire a SQL access token. Run: az login --scope "https://database.windows.net//.default"'
    }

    # A serverless database may be resuming on a first deployment, so a couple
    # of connection failures here are expected rather than fatal.
    $attempt = 0
    while ($true) {
        $attempt++
        try {
            Invoke-Sqlcmd `
                -ServerInstance "$SqlServerName.database.windows.net" `
                -Database $databaseName `
                -AccessToken $sqlToken `
                -Query $grantSql `
                -ConnectionTimeout 90 `
                -ErrorAction Stop | Out-Null
            break
        }
        catch {
            if ($attempt -ge 4) { throw }
            Write-Host "Attempt $attempt failed: $($_.Exception.Message)"
            Write-Host 'Retrying in 30s (a serverless database may be resuming)...'
            Start-Sleep -Seconds 30
        }
    }

    Write-Host "Granted db_datareader, db_datawriter, db_ddladmin to $identityName" -ForegroundColor Green
}

# ---------------------------------------------------------------------------
# Restart, then verify.
#
# Order matters. The app applies schema.sql once at startup, so on a first
# deployment it runs BEFORE the SQL user above exists, fails, and logs it
# without taking the host down. Health then keeps reporting "Invalid object
# name 'dbo.Twin'" even after the grant lands, because nothing re-ran the
# bootstrap. Hence the restart, and hence it running after the grant.
# ---------------------------------------------------------------------------
if ($PSCmdlet.ShouldProcess($appName, 'Restart and verify')) {
    Write-Host 'Restarting so the schema bootstrap re-runs...' -ForegroundColor Cyan
    az functionapp restart --resource-group $ResourceGroup --name $appName --output none
    if ($LASTEXITCODE -ne 0) { throw "Failed to restart '$appName'." }

    $healthUrl = "https://$appName.azurewebsites.net/api/health"
    $healthy   = $false

    foreach ($i in 1..10) {
        Start-Sleep -Seconds 15
        try {
            $health = Invoke-WebRequest -Uri $healthUrl -UseBasicParsing -TimeoutSec 30 -SkipHttpErrorCheck
            $body = [Text.Encoding]::UTF8.GetString($health.RawContentStream.ToArray())
            if ($health.StatusCode -eq 200) {
                Write-Host "Health: $body" -ForegroundColor Green
                $healthy = $true
                break
            }
            Write-Host "Health returned $($health.StatusCode): $body"
        }
        catch {
            Write-Host "Health check attempt $i failed: $($_.Exception.Message)"
        }
    }

    if (-not $healthy) {
        throw "'$appName' did not report healthy after deployment. Check $healthUrl."
    }
}

Write-Host ''
Write-Host 'Deployed.' -ForegroundColor Green
Write-Host "  https://$appName.azurewebsites.net/api/health"
Write-Host ''
Write-Host 'db_ddladmin is needed only because Eng__AutoCreateSchema applies' -ForegroundColor DarkGray
Write-Host 'schema.sql at startup. Drop it once the schema is settled.' -ForegroundColor DarkGray
