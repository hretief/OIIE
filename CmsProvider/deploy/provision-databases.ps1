<#
.SYNOPSIS
    Creates the CMS databases on acme-sql-server.

.DESCRIPTION
    Two databases, deliberately separate from the sandbox databases:

        acme-db-cms-dev    development instance
        acme-db-cms-prod   production instance

    Names follow docs/azure-resource-naming-guidance.md.

    This departs from deploy/sandbox/NAMING.md, which isolates sandbox
    participants by schema inside one database per environment. That
    convention is right for sandbox participants; it is wrong here. This
    app emulates a CUSTOMER system, and a customer's database is not a
    schema inside the integrator's. Keeping it separate is the point.

    Schema objects are not created here. The app applies schema.sql at
    startup, so the databases are created empty.

.NOTES
    Requires the Az CLI and an account with rights to create databases on
    the server. The deploy workflow runs this for the dev environment on every
    push; prod is only created when -Environment prod is passed by hand.
#>

[CmdletBinding()]
param(
    # Defaults to dev. Prod must be asked for explicitly -- it should never be
    # created as a side effect of standing up dev, which is what an unattended
    # CI run does.
    [ValidateSet('dev', 'prod', 'all')]
    [string]$Environment = 'dev',

    [string]$ResourceGroup = 'HilmarRetiefRG',
    [string]$SqlServerName = 'acme-sql-server',

    # Serverless with auto-pause matches the sandbox databases and keeps an
    # idle demo environment from accruing cost.
    [string]$Edition       = 'GeneralPurpose',
    [string]$ServiceObjective = 'GP_S_Gen5_1',
    [int]   $AutoPauseDelayMinutes = 60
)

$ErrorActionPreference = 'Stop'

$databases = switch ($Environment) {
    'all'   { @('acme-db-cms-dev', 'acme-db-cms-prod') }
    default { @("acme-db-cms-$Environment") }
}

foreach ($db in $databases) {

    $exists = az sql db show `
        --resource-group $ResourceGroup `
        --server $SqlServerName `
        --name $db `
        --query 'name' -o tsv 2>$null

    if ($exists) {
        Write-Host "Database '$db' already exists. Skipping." -ForegroundColor Yellow
        continue
    }

    Write-Host "Creating database '$db' on $SqlServerName..." -ForegroundColor Cyan

    az sql db create `
        --resource-group $ResourceGroup `
        --server $SqlServerName `
        --name $db `
        --edition $Edition `
        --service-objective $ServiceObjective `
        --auto-pause-delay $AutoPauseDelayMinutes `
        --output none

    if ($LASTEXITCODE -ne 0) {
        throw "Failed to create database '$db'."
    }

    Write-Host "Created '$db'." -ForegroundColor Green
}

Write-Host ''
Write-Host 'Connection strings:' -ForegroundColor Cyan
foreach ($db in $databases) {
    # Initial Catalog is quoted-safe as written: the hyphens in the database
    # name are fine in a connection string, though they need [brackets] in
    # T-SQL USE statements.
    Write-Host "  $db :" -NoNewline
    Write-Host " Server=tcp:$SqlServerName.database.windows.net,1433;Initial Catalog=$db;Authentication=Active Directory Default;Encrypt=True;"
}

Write-Host ''
Write-Host 'The app creates its own schema at startup (Cms__AutoCreateSchema=true).' -ForegroundColor Yellow
Write-Host 'Its identity needs db_ddladmin, db_datareader and db_datawriter on each database.' -ForegroundColor Yellow
