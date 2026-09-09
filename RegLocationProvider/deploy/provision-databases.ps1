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
    the server. Run it yourself; it is not run automatically.

.EXAMPLE
    ./provision-databases.ps1

.EXAMPLE
    ./provision-databases.ps1 -Databases acme-db-eis-dev
#>

[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$ResourceGroup = 'HilmarRetiefRG',
    [string]$SqlServerName = 'acme-sql-server',

    # Serverless with auto-pause matches the sandbox databases and keeps an
    # idle demo environment from accruing cost.
    [string]$Edition          = 'GeneralPurpose',
    [string]$ServiceObjective = 'GP_S_Gen5_1',
    [int]   $AutoPauseDelayMinutes = 60,

    [string[]]$Databases = @('acme-db-eis-dev', 'acme-db-eis-prod')
)

$ErrorActionPreference = 'Stop'

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
