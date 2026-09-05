<#
.SYNOPSIS
    Provisions one OIIE Sandbox environment: blob container and the settings a
    developer needs locally.

.DESCRIPTION
    Idempotent and verified. Every Azure CLI call is checked - a provisioning
    script that reports success while doing nothing is worse than one that fails.

    The sandbox owns no database. Participant data belongs to the provider apps
    (ENG, REG-LOCATION), and each provider creates and bootstraps its own schema
    and reference data at startup, so there are no sandbox schemas, contained
    users or per-participant SQL secrets to provision here.

.NOTES
    Prerequisites:
      - Azure CLI, signed in (az login)

.EXAMPLE
    ./provision.ps1 -Environment dev -Alias hretief
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('dev', 'ci', 'demo')]
    [string]$Environment,

    [string]$Alias,

    [string]$SubscriptionId,
    [string]$ResourceGroup = 'HilmarRetiefRG',
    [string]$KeyVault = 'mndot',
    [string]$StorageAccount,

    [switch]$SkipStorage
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# --- Preflight ---------------------------------------------------------------

if ($Environment -eq 'dev' -and [string]::IsNullOrWhiteSpace($Alias)) {
    throw "-Alias is required for the dev environment; it names the per-developer blob prefix."
}

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw "Azure CLI not found. Install it and run 'az login'."
}

function Invoke-Az {
    <#
        Runs an az command and fails loudly. The previous version discarded both
        stderr and the exit code, which is how ten users were reported created when
        none were.
    #>
    param(
        [Parameter(Mandatory)][string[]]$Arguments,
        [string]$Because = 'Azure CLI call'
    )

    $output = & az @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "$Because failed (exit $LASTEXITCODE):`n$($output -join [Environment]::NewLine)"
    }
    return $output
}

# --- Names -------------------------------------------------------------------

$blobPrefix = switch ($Environment) {
    'dev'  { "dev-$Alias" }
    'ci'   { 'ci' }
    'demo' { 'demo' }
}

Write-Host "Environment : $Environment"
Write-Host "Key Vault   : $KeyVault"
Write-Host "Blob prefix : $blobPrefix"
Write-Host ""

if ($SubscriptionId) {
    Invoke-Az @('account', 'set', '--subscription', $SubscriptionId) -Because 'Setting subscription'
}

# --- Storage -----------------------------------------------------------------

if (-not $SkipStorage) {
    if ([string]::IsNullOrWhiteSpace($StorageAccount)) {
        Write-Warning "No -StorageAccount supplied; blob container not created."
    }
    else {
        Write-Host "`nEnsuring blob container sandbox-payloads on $StorageAccount"

        & az storage account show --name $StorageAccount --resource-group $ResourceGroup `
            --query name -o tsv 2>$null | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Storage account '$StorageAccount' not found in $ResourceGroup. " +
                  "Check the name, or pass -SkipStorage and create it separately."
        }

        Invoke-Az @(
            'storage', 'container', 'create',
            '--account-name', $StorageAccount,
            '--name', 'sandbox-payloads',
            '--auth-mode', 'login',
            '--output', 'none'
        ) -Because 'Creating blob container'

        Write-Host "  container ready (prefix for this environment: $blobPrefix)"
    }
}

# --- Developer configuration -------------------------------------------------

Write-Host "`nDone.`n"
Write-Host "Add to appsettings.Development.json:"
Write-Host ""
Write-Host @"
  "KeyVault": { "Uri": "https://$KeyVault.vault.azure.net/" },
  "Storage": { "Prefix": "$blobPrefix" },
  "Sandbox": { "Environment": "$Environment" }
"@
Write-Host ""
Write-Host "Provider databases are provisioned by the ENG and REG-LOCATION deployments;"
Write-Host "the sandbox holds no SQL of its own."
