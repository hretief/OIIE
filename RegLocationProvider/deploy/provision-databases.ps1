<#
.SYNOPSIS
    Creates the EIS databases on acme-sql-server.

.DESCRIPTION
    Two databases, following the same model as CMS and ENG:

        acme-db-eis-dev    development instance
        acme-db-eis-prod   production instance

    Names follow docs/azure-resource-naming-guidance.md.

    Named for EIS rather than for REG-LOCATION because more than one app
    reads it. EIS is one product with one persistence layer; REG-LOCATION and
    RDL are different functional surfaces over it, deployed as separate
    function apps. REG-LOCATION is the authority for which tags exist and what
    they identify; RDL is the curation surface for the class vocabulary in
    dbo.class_objects. Naming the database after one of its two consumers
    would imply the other is a guest in someone else's system.

    Schema objects are not created here. RegLocationProvider applies
    schema.sql and then bootstrap.sql at startup -- it owns the DDL for both
    apps -- so the databases are created empty.

.NOTES
    Requires the Az CLI and an account with rights to create databases on
    the server. The deploy workflow runs this for the dev environment on every
    push; prod is only created when -Environment prod is passed by hand.

.EXAMPLE
    ./provision-databases.ps1

.EXAMPLE
    ./provision-databases.ps1 -Environment prod
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

    # Serverless with auto-pause matches the sandbox databases and keeps an
    # idle demo environment from accruing cost.
    [string]$Edition          = 'GeneralPurpose',
    [string]$ServiceObjective = 'GP_S_Gen5_1',
    [int]   $AutoPauseDelayMinutes = 60,

    # Derived from -Environment unless named explicitly.
    [string[]]$Databases
)

$ErrorActionPreference = 'Stop'

if (-not $Databases) {
    $Databases = switch ($Environment) {
        'all'   { @('acme-db-eis-dev', 'acme-db-eis-prod') }
        default { @("acme-db-eis-$Environment") }
    }
}

foreach ($db in $Databases) {

    $exists = az sql db show `
        --resource-group $ResourceGroup `
        --server $SqlServerName `
        --name $db `
        --query 'name' -o tsv 2>$null

    if ($exists) {
        Write-Host "Database '$db' already exists. Skipping." -ForegroundColor Yellow
        continue
    }

    if (-not $PSCmdlet.ShouldProcess($db, 'Create database')) { continue }

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
Write-Host 'Done. The databases are empty; RegLocationProvider creates the' -ForegroundColor Green
Write-Host 'schema and seeds it at startup.' -ForegroundColor Green
