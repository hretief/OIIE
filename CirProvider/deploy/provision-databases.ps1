<#
.SYNOPSIS
    Creates the CIR databases on acme-sql-server.

.DESCRIPTION
    Two databases, following docs/azure-resource-naming-guidance.md:

        acme-db-cir-dev    development instance
        acme-db-cir-prod   production instance

    These are NEW and start empty. They do not replace the existing 'cir'
    database, which stays in place serving the grandfathered
    cir-func-44p2f3n6 app until a deliberate cutover. Nothing here touches
    it, and no data is migrated -- the registry starts at day zero, which
    is only safe because the CIRIDs it would hold have not been issued yet.

    Schema objects are not created here. The app applies its schema at
    startup, so the databases are created empty.

.NOTES
    Requires the Az CLI and an account with rights to create databases on
    the server. The deploy workflow runs this for the dev environment on every
    push; prod is only created when -Environment prod is passed by hand.
#>

[CmdletBinding(SupportsShouldProcess)]
param(
    # Defaults to dev. Prod must be asked for explicitly -- it should never be
    # created as a side effect of standing up dev, which is what an unattended
    # CI run does.
    [ValidateSet('dev', 'prod', 'all')]
    [string]$Environment = 'dev',

    [string]$ResourceGroup = 'HilmarRetiefRG',
    [string]$SqlServerName = 'acme-sql-server',

    # Serverless with auto-pause matches the CMS and sandbox databases and
    # keeps an idle demo environment from accruing cost.
    [string]$Edition          = 'GeneralPurpose',
    [string]$ServiceObjective = 'GP_S_Gen5_1',
    [int]   $AutoPauseDelayMinutes = 60
)

$ErrorActionPreference = 'Stop'

$databases = switch ($Environment) {
    'all'   { @('acme-db-cir-dev', 'acme-db-cir-prod') }
    default { @("acme-db-cir-$Environment") }
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

    if (-not $PSCmdlet.ShouldProcess($db, 'Create SQL database')) { continue }

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
    Write-Host "  $db :" -NoNewline
    Write-Host " Server=tcp:$SqlServerName.database.windows.net,1433;Initial Catalog=$db;Authentication=Active Directory Default;Encrypt=True;"
}

Write-Host ''
Write-Host 'The app creates its own schema at startup (Cir__AutoCreateSchema=true).' -ForegroundColor Yellow
Write-Host 'Its identity needs db_ddladmin, db_datareader and db_datawriter on each database.' -ForegroundColor Yellow
Write-Host 'deploy-functionapp.ps1 prints the exact statements.' -ForegroundColor Yellow
