#!/usr/bin/env pwsh
# ============================================================================
# ISBM Service Provider — Azure Deployment Script
#
# Deploys infrastructure (Bicep) and publishes the Function App code.
#
# Usage:
#   # Greenfield (everything new)
#   .\deploy.ps1 -ResourceGroup rg-isbm -Location eastus2
#
#   # Brownfield (reuse existing resources)
#   .\deploy.ps1 -ResourceGroup hilmarretiefrg `
#     -ExistingServiceBusName mndotdev `
#     -ExistingStorageAccountName mndot `
#     -ExistingKeyVaultName mndot
#
#   # Infrastructure only (skip code publish)
#   .\deploy.ps1 -ResourceGroup rg-isbm -SkipPublish
#
#   # Code publish only (skip infrastructure)
#   .\deploy.ps1 -ResourceGroup rg-isbm -FunctionAppName isbm-func-44p2f3n6dv7p4 -SkipInfra
# ============================================================================

param(
    [Parameter(Mandatory)]
    [string]$ResourceGroup,

    [string]$Location = "eastus2",
    [string]$BaseName = "isbm",

    # Hosting plan. Basic or higher keeps the host resident so the Service Bus
    # triggered notification dispatch and expiry functions run promptly.
    # Y1 (Consumption) cannot run Always On and defers background work.
    [ValidateSet("Y1", "B1", "B2", "EP1")]
    [string]$PlanSku = "B1",
    [bool]$AlwaysOn = $true,

    # Brownfield — pass existing resource names (empty = create new)
    [string]$ExistingServiceBusName = "",
    [string]$ExistingStorageAccountName = "",
    [string]$ExistingKeyVaultName = "",
    [string]$ExistingAppInsightsName = "",

    # Function App name (required for -SkipInfra; otherwise read from Bicep output)
    [string]$FunctionAppName = "",

    [switch]$SkipInfra,
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDir
$bicepFile = Join-Path $repoRoot "infra" "main.bicep"
$projectDir = Join-Path $repoRoot "IsbmProvider"

function Write-Step { param([string]$msg) Write-Host "`n=== $msg ===" -ForegroundColor Cyan }
function Write-Done { param([string]$msg) Write-Host "  $msg" -ForegroundColor Green }
function Write-Info { param([string]$msg) Write-Host "  $msg" -ForegroundColor Yellow }

# ============================================================================
# Pre-flight checks
# ============================================================================
Write-Step "Pre-flight checks"

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    Write-Error "Azure CLI (az) not found. Install from https://aka.ms/installazurecli"
}
Write-Done "Azure CLI found"

if (-not $SkipPublish) {
    if (-not (Get-Command func -ErrorAction SilentlyContinue)) {
        Write-Error "Azure Functions Core Tools (func) not found. Install from https://aka.ms/func-install"
    }
    Write-Done "Functions Core Tools found"
}

if (-not $SkipInfra -and -not (Test-Path $bicepFile)) {
    Write-Error "Bicep file not found at $bicepFile"
}

if (-not $SkipPublish -and -not (Test-Path (Join-Path $projectDir "IsbmProvider.csproj"))) {
    Write-Error "Project not found at $projectDir"
}

# Verify logged in
$account = az account show 2>&1 | ConvertFrom-Json -ErrorAction SilentlyContinue
if (-not $account) { Write-Error "Not logged in. Run: az login" }
Write-Done "Logged in as $($account.user.name) (subscription: $($account.name))"

# ============================================================================
# Ensure resource group exists
# ============================================================================
Write-Step "Resource Group: $ResourceGroup"

$rgExists = az group exists --name $ResourceGroup 2>&1
if ($rgExists -eq "false") {
    Write-Info "Creating resource group in $Location..."
    az group create --name $ResourceGroup --location $Location --output none
    Write-Done "Resource group created"
} else {
    Write-Done "Resource group exists"
}

# ============================================================================
# Deploy infrastructure (Bicep)
# ============================================================================
if (-not $SkipInfra) {
    Write-Step "Deploying infrastructure (Bicep)"

    Write-Info "Running Bicep deployment..."

    # Write parameters to a temp file to avoid shell escaping issues
    # (Service Bus connection strings contain semicolons, equals signs, and plus signs)
    $paramObj = @{}
    $paramObj["baseName"] = @{ value = $BaseName }
    $paramObj["planSku"] = @{ value = $PlanSku }
    $paramObj["alwaysOn"] = @{ value = $AlwaysOn }
    # The Function App authenticates to Service Bus with its managed identity,
    # so no key is fetched here and none is passed to the template.
    if ($ExistingServiceBusName) {
        $paramObj["existingServiceBusName"] = @{ value = $ExistingServiceBusName }
    }
    if ($ExistingStorageAccountName) { $paramObj["existingStorageAccountName"] = @{ value = $ExistingStorageAccountName } }
    if ($ExistingKeyVaultName) { $paramObj["existingKeyVaultName"] = @{ value = $ExistingKeyVaultName } }
    if ($ExistingAppInsightsName) { $paramObj["existingAppInsightsName"] = @{ value = $ExistingAppInsightsName } }

    $tempParams = Join-Path ([System.IO.Path]::GetTempPath()) "isbm-deploy-params-$([guid]::NewGuid().ToString('N').Substring(0,8)).json"
    $paramFile = @{
        "`$schema" = "https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#"
        contentVersion = "1.0.0.0"
        parameters = $paramObj
    }
    $paramFile | ConvertTo-Json -Depth 5 | Set-Content -Path $tempParams -Encoding utf8
    Write-Info "Parameters written to $tempParams"

    # Run deployment — let warnings go to console (stderr), capture only JSON (stdout)
    $deploymentName = "isbm-$(Get-Date -Format 'yyyyMMddHHmmss')"
    az deployment group create `
        --name $deploymentName `
        --resource-group $ResourceGroup `
        --template-file $bicepFile `
        --parameters "@$tempParams" `
        --output none
    Remove-Item $tempParams -ErrorAction SilentlyContinue

    if ($LASTEXITCODE -ne 0) { Write-Error "Bicep deployment failed (exit code $LASTEXITCODE)" }

    # Fetch outputs from the named deployment
    $result = az deployment group show `
        --name $deploymentName `
        --resource-group $ResourceGroup `
        --query properties.outputs `
        --output json 2>$null
    $deployment = $result | ConvertFrom-Json -ErrorAction SilentlyContinue

    if ($LASTEXITCODE -ne 0 -or -not $deployment) {
        Write-Error "Could not retrieve deployment outputs"
    }

    $FunctionAppName = $deployment.functionAppName.value

    Write-Done "Deployment succeeded"
    Write-Info "Mode: $($deployment.mode.value)"
    Write-Info "Function App: $($deployment.functionAppName.value)"
    Write-Info "URL: $($deployment.functionAppUrl.value)"
    Write-Info "Service Bus: $($deployment.serviceBusNamespace.value)"
    Write-Info "Storage: $($deployment.storageAccountName.value)"
    Write-Info "Key Vault: $($deployment.keyVaultUri.value)"
    Write-Info "SQL: $($deployment.sqlServerFqdn.value)"
    Write-Info "App Insights: $($deployment.appInsightsName.value)"
}

# ============================================================================
# Build and publish Function App
# ============================================================================
if (-not $SkipPublish) {
    if (-not $FunctionAppName) {
        Write-Error "Function App name not known. Either deploy infra first, or pass -FunctionAppName."
    }

    Write-Step "Building project"
    Push-Location $projectDir
    dotnet build --configuration Release --output ./publish 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        dotnet build --configuration Release --output ./publish
        Pop-Location
        Write-Error "Build failed"
    }
    Write-Done "Build succeeded"
    Pop-Location

    Write-Step "Publishing to $FunctionAppName"
    Push-Location $projectDir
    func azure functionapp publish $FunctionAppName
    if ($LASTEXITCODE -ne 0) {
        Pop-Location
        Write-Error "Publish failed"
    }
    Pop-Location
    Write-Done "Published successfully"
}

# ============================================================================
# Post-deploy verification
# ============================================================================
if (-not $SkipPublish -and $FunctionAppName) {
    Write-Step "Post-deploy verification"

    $baseUrl = "https://$FunctionAppName.azurewebsites.net/api"
    Write-Info "Testing: $baseUrl/configuration/supported-operations"

    Start-Sleep -Seconds 5   # give the app a moment to warm up

    try {
        $config = Invoke-RestMethod -Uri "$baseUrl/configuration/supported-operations" -TimeoutSec 30
        Write-Done "Configuration discovery responded"
        Write-Info "Security Level: $($config.securityLevelConformance)"
        Write-Info "Conformance: $($config.conformanceStatement)"
    }
    catch {
        Write-Info "Warm-up may still be in progress. Try manually:"
        Write-Info "  curl $baseUrl/configuration/supported-operations"
    }
}

# ============================================================================
# Summary
# ============================================================================
Write-Host "`n============================================" -ForegroundColor White
Write-Host " Deployment Complete" -ForegroundColor White
Write-Host "============================================" -ForegroundColor White
if ($FunctionAppName) {
    Write-Host ""
    Write-Host "  Function App:  $FunctionAppName" -ForegroundColor White
    Write-Host "  URL:           https://$FunctionAppName.azurewebsites.net/api" -ForegroundColor White
    Write-Host ""
    Write-Host "  Next steps:" -ForegroundColor White
    Write-Host "    # Run end-to-end tests against Azure"
    Write-Host "    .\Testing\test-isbm.ps1 -BaseUrl `"https://$FunctionAppName.azurewebsites.net/api`""
    Write-Host ""
    Write-Host "    # Run conformance tests"
    Write-Host "    .\Testing\conformance-tests.ps1 -BaseUrl `"https://$FunctionAppName.azurewebsites.net/api`""
}
Write-Host ""
