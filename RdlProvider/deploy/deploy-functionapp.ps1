<#
.SYNOPSIS
    Provisions and deploys the RdlProvider function app.

.DESCRIPTION
    RdlProvider is the API tier over the Reference Data Library. Under
    docs/azure-resource-naming-guidance.md that makes it an 'api' resource:

        acme-api-rdl-dev     development instance
        acme-api-rdl-prod    production instance

    Creates, if absent: a user-assigned identity, a storage account, the
    shared plan, and the function app -- then publishes the code.

    WHY THERE IS NO provision-databases.ps1 NEXT TO THIS FILE
    RDL does not own a database. It is the second surface over the EIS
    database (acme-db-eis-{env}), which REG-LOCATION owns and creates; both
    apps read the same dbo.class_objects table. RdlProvider therefore has no
    AutoCreateSchema or AutoBootstrap setting, unlike RegLocationProvider --
    see the comment on RdlOptions. Two apps applying the same DDL would race
    on a cold start, and worse, would let the two definitions drift apart.

    The consequence for this script is that the database must already exist:
    it is a preflight check below, not something this script creates.

    THE GRANT IS READ-ONLY
    RdlProvider serves reads; the write paths in IRdlStore exist but the
    channel-facing engine only queries. So the identity gets db_datareader
    and nothing else. No db_ddladmin in particular -- REG-LOCATION owns the
    schema, and granting RDL the right to change it would make the ownership
    boundary above unenforceable rather than merely conventional. If a write
    path is genuinely needed later, add db_datawriter deliberately.

.NOTES
    Requires the Az CLI, Azure Functions Core Tools, the .NET SDK, and -- for
    the SQL grant -- sqlcmd and an account that is an Entra admin on the
    server. Run it yourself; it is not run automatically.

    Deploy this BEFORE deploy/engines/deploy-engine.ps1 -Engine rdl. That
    script reads this app's function key to configure the engine and fails
    fast if the app is absent.

.EXAMPLE
    ./deploy-functionapp.ps1 -Environment dev

.EXAMPLE
    ./deploy-functionapp.ps1 -Environment dev -WhatIf

.EXAMPLE
    ./deploy-functionapp.ps1 -Environment prod -SkipSqlGrant
#>

[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [ValidateSet('dev', 'prod')]
    [string]$Environment,

    [string]$ResourceGroup = 'HilmarRetiefRG',
    [string]$Location      = 'eastus',
    [string]$SqlServerName = 'acme-sql-server',

    # Basic rather than Consumption, matching the other providers. Consumption
    # cold starts are long enough to be mistaken for a fault during a demo.
    [string]$PlanSku = 'B1',

    # Skip the code publish and only ensure the infrastructure exists.
    [switch]$InfraOnly,

    # Skip the automated SQL grant and print the statements instead. Use this
    # when the running account is not an Entra admin on the server.
    [switch]$SkipSqlGrant
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# Names. All derived from the environment so the two deployments cannot
# diverge by hand-editing.
# ---------------------------------------------------------------------------
$appName      = "acme-api-rdl-$Environment"
# Shared across every provider in the environment. One B1 hosts them all; do
# not reintroduce a per-provider plan.
$planName     = "acme-plan-$Environment"
$identityName = "acme-id-rdl-$Environment"
$storageName  = "acmestorage${Environment}01"
# The EIS database, owned by REG-LOCATION and shared with this app. Named for
# the product rather than for either app, because it is one store with two
# surfaces over it -- see RegLocationProvider/deploy/README.md, 'Schema
# ownership'.
$databaseName = "acme-db-eis-$Environment"

$projectPath = Join-Path $PSScriptRoot '..' | Resolve-Path

Write-Host ''
Write-Host "Deploying RdlProvider to '$Environment'" -ForegroundColor Cyan
Write-Host "  Function app : $appName"
Write-Host "  Plan         : $planName ($PlanSku)"
Write-Host "  Identity     : $identityName"
Write-Host "  Storage      : $storageName"
Write-Host "  Database     : $databaseName on $SqlServerName (owned by REG-LOCATION, read-only here)"
Write-Host ''

# ---------------------------------------------------------------------------
# Preflight. The EIS database belongs to REG-LOCATION and is created by its
# provision-databases.ps1. Failing now is better than deploying an app that
# cannot reach its data.
#
# Authentication is checked FIRST, and separately. 'az sql db show' writes its
# errors to stderr and returns nothing on stdout, so an expired token and an
# absent database are indistinguishable if you only test for empty output --
# which made an auth failure report itself as "the database does not exist",
# sending you to look at the wrong thing entirely.
# ---------------------------------------------------------------------------
$account = az account show --query 'name' -o tsv 2>$null

if (-not $account) {
    throw "Not signed in to Azure, or the token has expired. Run 'az login' and retry. " +
          "(This check exists because an expired token otherwise reports itself below " +
          "as a missing database.)"
}

Write-Host "Azure subscription : $account" -ForegroundColor DarkGray

$dbOutput = az sql db show `
    --resource-group $ResourceGroup `
    --server $SqlServerName `
    --name $databaseName `
    --query 'name' -o tsv 2>&1

$dbQueryFailed = $LASTEXITCODE -ne 0
$dbText        = ($dbOutput | Out-String).Trim()

# A genuine 'not found' is the only acceptable failure here. Anything else --
# no such server, no permission, a network fault -- is reported as itself
# rather than being flattened into "the database does not exist".
if ($dbQueryFailed -and $dbText -notmatch 'ResourceNotFound|was not found|NotFound') {
    throw "Could not query database '$databaseName' on '$SqlServerName': $dbText"
}

$dbExists = if ($dbQueryFailed) { $null } else { $dbText }

if (-not $dbExists) {
    throw "Database '$databaseName' does not exist on server '$SqlServerName'. " +
          "It belongs to REG-LOCATION: run RegLocationProvider/deploy/provision-databases.ps1 first."
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

    # No Rdl__AutoCreateSchema or Rdl__AutoBootstrap: RdlOptions has neither,
    # because REG-LOCATION owns this database's DDL.
    Write-Host 'Applying application settings...' -ForegroundColor Cyan
    az functionapp config appsettings set `
        --resource-group $ResourceGroup `
        --name $appName `
        --settings `
            "Rdl__SqlConnectionString=$connectionString" `
            "AZURE_CLIENT_ID=$identityClientId" `
        --output none
    if ($LASTEXITCODE -ne 0) { throw 'Failed to apply application settings.' }
}

# ---------------------------------------------------------------------------
# SQL grant.
#
# The identity needs a contained database user before the app can read
# anything. This is attempted here rather than left as a manual step because
# forgetting it produces an app that starts cleanly and then fails on every
# request, which does not look like a permissions problem.
#
# db_datareader only -- see the header. RDL reads a table REG-LOCATION owns.
#
# CREATE USER is not idempotent, so it is guarded on sys.database_principals.
# ---------------------------------------------------------------------------
$grantSql = @"
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'$identityName')
BEGIN
    CREATE USER [$identityName] FROM EXTERNAL PROVIDER;
END
ALTER ROLE db_datareader ADD MEMBER [$identityName];
"@

if ($SkipSqlGrant) {
    Write-Host ''
    Write-Host 'SkipSqlGrant specified. Run this yourself as an Entra admin:' -ForegroundColor Yellow
    Write-Host $grantSql
}
elseif ($PSCmdlet.ShouldProcess($databaseName, "Grant SQL access to '$identityName'")) {

    Write-Host ''
    Write-Host "Granting '$identityName' read access to '$databaseName'..." -ForegroundColor Cyan

    # The client IP needs a firewall rule to reach the server. Added by name
    # so repeat runs update rather than accumulate, and so it is easy to find
    # and remove later.
    $clientIp = (Invoke-RestMethod -Uri 'https://api.ipify.org?format=json').ip
    $ruleName = "deploy-rdl-$env:USERNAME"

    az sql server firewall-rule create `
        --resource-group $ResourceGroup `
        --server $SqlServerName `
        --name $ruleName `
        --start-ip-address $clientIp `
        --end-ip-address $clientIp `
        --output none 2>$null

    $sqlFile = Join-Path ([System.IO.Path]::GetTempPath()) "rdl-grant-$([guid]::NewGuid()).sql"
    Set-Content -Path $sqlFile -Value $grantSql -Encoding UTF8

    try {
        # -G -U selects ActiveDirectoryInteractive, which can complete an MFA
        # prompt. Plain '-G' means ActiveDirectoryIntegrated, which cannot:
        # against a tenant with conditional access it fails with AADSTS50076
        # ("you must use multi-factor authentication") rather than prompting.
        # A browser window will open the first time.
        $adminUpn = az account show --query 'user.name' -o tsv 2>$null

        if ($adminUpn) {
            sqlcmd -S "$SqlServerName.database.windows.net" -d $databaseName `
                   -G -U $adminUpn -i $sqlFile -b
        }
        else {
            sqlcmd -S "$SqlServerName.database.windows.net" -d $databaseName -G -i $sqlFile -b
        }

        if ($LASTEXITCODE -ne 0) {
            Write-Host ''
            Write-Host 'SQL grant failed. Run it yourself as an Entra admin:' -ForegroundColor Yellow
            Write-Host $grantSql
        }
        else {
            Write-Host 'Granted.' -ForegroundColor Green
        }
    }
    finally {
        Remove-Item $sqlFile -ErrorAction SilentlyContinue
    }
}

# ---------------------------------------------------------------------------
# Publish. Last, so the app has its settings and its SQL user before it starts.
# ---------------------------------------------------------------------------
if ($InfraOnly) {
    Write-Host 'InfraOnly specified; skipping code publish.' -ForegroundColor Yellow
}
elseif ($PSCmdlet.ShouldProcess($appName, 'Publish code')) {
    Write-Host ''
    Write-Host 'Publishing...' -ForegroundColor Cyan
    Push-Location $projectPath
    try {
        # Retried because a newly created app frequently fails the first
        # publish with "Timed out waiting for SCM to update the Environment
        # Settings". The SCM site is still coming up; the message names a
        # settings problem and is not one, so a bare failure here sends you
        # looking in the wrong place. Retrying costs a minute and usually works.
        $maxAttempts = 3
        for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {

            func azure functionapp publish $appName --dotnet-isolated
            if ($LASTEXITCODE -eq 0) { break }

            if ($attempt -eq $maxAttempts) {
                throw "Publish to '$appName' failed after $maxAttempts attempts."
            }

            Write-Host ''
            Write-Host "Publish attempt $attempt failed; retrying in 30s..." -ForegroundColor Yellow
            Start-Sleep -Seconds 30
        }
    }
    finally {
        Pop-Location
    }
}

Write-Host ''
Write-Host 'Deployed.' -ForegroundColor Green
Write-Host "  https://$appName.azurewebsites.net/api/health"
Write-Host ''
Write-Host 'Next: deploy/engines/deploy-engine.ps1 -Engine rdl -Environment ' -NoNewline -ForegroundColor DarkGray
Write-Host $Environment -ForegroundColor DarkGray
