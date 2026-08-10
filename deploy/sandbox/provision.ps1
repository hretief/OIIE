<#
.SYNOPSIS
    Provisions one OIIE Sandbox environment: SQL database, schemas, per-participant
    contained users with scoped grants, blob container, and Key Vault secrets.

.DESCRIPTION
    Idempotent and verified. Every Azure CLI call is checked, and the run ends with
    a verification pass against the database — a provisioning script that reports
    success while doing nothing is worse than one that fails.

    T-SQL runs through Invoke-Sqlcmd with an Entra access token. Azure CLI has no
    command that executes T-SQL; `az sql db query` does not exist.

    Passwords are generated here, written to Key Vault, and never printed. Existing
    secrets are reused, so a re-run cannot desynchronise Key Vault from the database.

.NOTES
    Prerequisites:
      - Azure CLI, signed in (az login)
      - SqlServer module:  Install-Module SqlServer -Scope CurrentUser
      - Signed-in identity must be Entra admin on the SQL server
      - Client IP allowed through the server firewall (use -AddFirewallRule)

.EXAMPLE
    ./provision.ps1 -Environment dev -Alias hretief -AddFirewallRule -SkipStorage
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('dev', 'ci', 'demo')]
    [string]$Environment,

    [string]$Alias,

    [string]$SubscriptionId,
    [string]$ResourceGroup = 'HilmarRetiefRG',
    [string]$SqlServer = 'acme-sql-server',
    [string]$KeyVault = 'mndot',
    [string]$StorageAccount,

    [int]$AutoPauseDelayMinutes = 60,
    [double]$MaxVCore = 2,

    # Regenerates every password and applies it to both Key Vault and the database
    # in one pass. Use when the two have drifted apart.
    [switch]$RotatePasswords,

    [switch]$AddFirewallRule,
    [switch]$SkipDatabase,
    [switch]$SkipStorage
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# --- Preflight ---------------------------------------------------------------

if ($Environment -eq 'dev' -and [string]::IsNullOrWhiteSpace($Alias)) {
    throw "-Alias is required for the dev environment."
}

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw "Azure CLI not found. Install it and run 'az login'."
}

if (-not (Get-Module -ListAvailable -Name SqlServer)) {
    throw "SqlServer module not found. Run: Install-Module SqlServer -Scope CurrentUser"
}

Import-Module SqlServer -ErrorAction Stop

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

$databaseName = switch ($Environment) {
    'dev'  { "oiie-sandbox-dev-$Alias" }
    'ci'   { 'oiie-sandbox-ci' }
    'demo' { 'oiie-sandbox-demo' }
}

$blobPrefix = switch ($Environment) {
    'dev'  { "dev-$Alias" }
    'ci'   { 'ci' }
    'demo' { 'demo' }
}

$serverFqdn = "$SqlServer.database.windows.net"

$participants = [ordered]@{
    'eng'          = 'eng'
    'construct'    = 'construct'
    'reg-location' = 'reg_location'
    'reg-asset'    = 'reg_asset'
    'reg-product'  = 'reg_product'
    'reg-material' = 'reg_material'
    'mms'          = 'mms'

    # The "O&M Systems" actor of OIIE Scenario 11, which receives asset
    # installation and removal events published by MMS.
    'om-reliability' = 'om_reliability'
    'rdl'          = 'rdl'
}

$expectedSchemas = @(
    'eng', 'construct', 'reg_location', 'reg_asset',
    'reg_product', 'reg_material', 'mms', 'om_reliability',
    'rdl', 'sandbox', 'tower'
)

Write-Host "Environment : $Environment"
Write-Host "Database    : $databaseName"
Write-Host "SQL server  : $serverFqdn"
Write-Host "Key Vault   : $KeyVault"
Write-Host "Blob prefix : $blobPrefix"
Write-Host ""

if ($SubscriptionId) {
    Invoke-Az @('account', 'set', '--subscription', $SubscriptionId) -Because 'Setting subscription'
}

# --- Database ----------------------------------------------------------------

if (-not $SkipDatabase) {
    & az sql db show --resource-group $ResourceGroup --server $SqlServer `
        --name $databaseName --query name -o tsv 2>$null | Out-Null

    if ($LASTEXITCODE -ne 0) {
        Write-Host "Creating database $databaseName (serverless, auto-pause ${AutoPauseDelayMinutes}m)"
        Invoke-Az @(
            'sql', 'db', 'create',
            '--resource-group', $ResourceGroup,
            '--server', $SqlServer,
            '--name', $databaseName,
            '--edition', 'GeneralPurpose',
            '--compute-model', 'Serverless',
            '--family', 'Gen5',
            '--capacity', "$MaxVCore",
            '--min-capacity', '0.5',
            '--auto-pause-delay', "$AutoPauseDelayMinutes",
            '--backup-storage-redundancy', 'Local',
            '--output', 'none'
        ) -Because "Creating database $databaseName"
    }
    else {
        Write-Host "Database $databaseName already exists"
    }
}

# --- Firewall ----------------------------------------------------------------

if ($AddFirewallRule) {
    $clientIp = (Invoke-RestMethod -Uri 'https://api.ipify.org?format=json').ip
    $ruleName = "sandbox-$($env:COMPUTERNAME)".ToLowerInvariant()

    Write-Host "Allowing $clientIp through the server firewall as $ruleName"

    & az sql server firewall-rule create --resource-group $ResourceGroup `
        --server $SqlServer --name $ruleName `
        --start-ip-address $clientIp --end-ip-address $clientIp --output none 2>&1 | Out-Null

    if ($LASTEXITCODE -ne 0) {
        Invoke-Az @(
            'sql', 'server', 'firewall-rule', 'update',
            '--resource-group', $ResourceGroup, '--server', $SqlServer, '--name', $ruleName,
            '--start-ip-address', $clientIp, '--end-ip-address', $clientIp, '--output', 'none'
        ) -Because 'Updating firewall rule'
    }
}

# --- SQL access token --------------------------------------------------------

$accessToken = (Invoke-Az @(
    'account', 'get-access-token',
    '--resource', 'https://database.windows.net/',
    '--query', 'accessToken', '-o', 'tsv'
) -Because 'Acquiring SQL access token') | Select-Object -First 1

function Invoke-Sql {
    param(
        [Parameter(Mandatory)][string]$Query,
        [switch]$AsQuery,
        [int]$TimeoutSeconds = 120
    )

    $splat = @{
        ServerInstance = $serverFqdn
        Database       = $databaseName
        AccessToken    = $accessToken
        Query          = $Query
        QueryTimeout   = $TimeoutSeconds
        ErrorAction    = 'Stop'
    }

    if ($AsQuery) { return Invoke-Sqlcmd @splat }
    Invoke-Sqlcmd @splat | Out-Null
}

# --- Contained users ---------------------------------------------------------

Write-Host "`nProvisioning contained database users"

function Get-Fingerprint {
    <# First 12 hex characters of the SHA-256, matching /health/secrets. Comparing
       fingerprints tells you whether the vault and the running app agree, without
       either side printing a password. #>
    param([string]$Value)

    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Value)
    $hash = [System.Security.Cryptography.SHA256]::HashData($bytes)
    return ([System.Convert]::ToHexString($hash).Substring(0, 12)).ToLowerInvariant()
}

function Get-OrCreateSecret {
    param([string]$Name)

    if (-not $RotatePasswords) {
        $existing = & az keyvault secret show --vault-name $KeyVault --name $Name `
            --query value -o tsv 2>$null

        if ($LASTEXITCODE -eq 0 -and $existing) {
            $value = ($existing | Select-Object -First 1).Trim()
            Write-Host "  secret $Name reused (fingerprint $(Get-Fingerprint $value))"
            return $value
        }
    }

    # Alphanumeric only. A password beginning with '-' is parsed by az as an option
    # flag, and one containing quotes or '$' would need escaping in both the SQL
    # literal and the connection string. Upper, lower and digits already satisfy the
    # three-of-four category rule Azure SQL enforces, so symbols buy nothing here.
    # Ambiguous glyphs (0/O, 1/l/I) are excluded so a password can be read aloud or
    # retyped if it ever has to be.
    $alphabet = 'abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789'

    do {
        $bytes = [byte[]]::new(32)
        [System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
        $value = -join ($bytes | ForEach-Object { $alphabet[$_ % $alphabet.Length] })
    } until (
        $value -cmatch '[a-z]' -and $value -cmatch '[A-Z]' -and $value -cmatch '[0-9]'
    )

    Invoke-Az @(
        'keyvault', 'secret', 'set',
        '--vault-name', $KeyVault, '--name', $Name, "--value=$value", '--output', 'none'
    ) -Because "Writing secret $Name"

    Write-Host "  secret $Name set (fingerprint $(Get-Fingerprint $value))"
    return $value
}

$allUsers = @()
foreach ($participantId in $participants.Keys) {
    $allUsers += [pscustomobject]@{
        SecretName = "sandbox-sql-$Environment-$participantId"
        UserName   = "sb_$($participants[$participantId])"
    }
}
foreach ($service in @('orchestrator', 'tower')) {
    $allUsers += [pscustomobject]@{
        SecretName = "sandbox-sql-$Environment-$service"
        UserName   = "sb_$service"
    }
}

# Contained users, not server logins. A server login is shared by every database on
# the server, so dev, CI and demo would collide on sb_eng — and a skipped creation
# would leave the Key Vault secret disagreeing with the real password.
foreach ($entry in $allUsers) {
    $password = Get-OrCreateSecret -Name $entry.SecretName
    $escaped = $password -replace "'", "''"

    Invoke-Sql -Query @"
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = '$($entry.UserName)')
    CREATE USER [$($entry.UserName)] WITH PASSWORD = '$escaped';
ELSE
    ALTER USER [$($entry.UserName)] WITH PASSWORD = '$escaped';
"@
    Write-Host "  user $($entry.UserName) ready"
}

# --- Schemas and grants ------------------------------------------------------

Write-Host "`nApplying schema and grant model"

$grantScript = Join-Path $PSScriptRoot 'sql/01-schemas-and-grants.sql'
if (-not (Test-Path $grantScript)) {
    throw "Grant script not found at $grantScript"
}

$sqlText = (Get-Content $grantScript -Raw) -replace '\{\{DATABASE\}\}', $databaseName

# Invoke-Sqlcmd honours GO batch separators, which the grant script relies on.
# -Verbose surfaces PRINT output: without it, a batch that half-succeeds reports
# only the terminating error and none of the work that did land.
Invoke-Sqlcmd -ServerInstance $serverFqdn -Database $databaseName `
    -AccessToken $accessToken -Query $sqlText -QueryTimeout 300 `
    -ErrorAction Stop -Verbose 4>&1 |
    ForEach-Object { Write-Host "  $_" }

Write-Host "  applied"

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

# --- Verification ------------------------------------------------------------

Write-Host "`nVerifying"

$actualSchemas = @((Invoke-Sql -AsQuery -Query @"
SELECT name FROM sys.schemas WHERE name IN ('$($expectedSchemas -join "','")');
"@).name)

$missingSchemas = @($expectedSchemas | Where-Object { $_ -notin $actualSchemas })
if ($missingSchemas.Count -gt 0) {
    throw "Schemas missing after provisioning: $($missingSchemas -join ', ')"
}
Write-Host "  $($actualSchemas.Count) schemas present"

$expectedUsers = @($allUsers.UserName)
$actualUsers = @((Invoke-Sql -AsQuery -Query @"
SELECT name FROM sys.database_principals
WHERE type = 'S' AND authentication_type_desc = 'DATABASE' AND name LIKE 'sb[_]%';
"@).name)

$missingUsers = @($expectedUsers | Where-Object { $_ -notin $actualUsers })
if ($missingUsers.Count -gt 0) {
    throw "Contained users missing after provisioning: $($missingUsers -join ', ')"
}
Write-Host "  $($actualUsers.Count) contained users present"

# Isolation is the reason for the per-user design, so confirm it holds rather than
# assuming the grant script did its job.
$leaks = @(Invoke-Sql -AsQuery -Query @"
SELECT p.name AS principal_name, s.name AS schema_name
FROM sys.database_permissions perm
JOIN sys.database_principals p ON p.principal_id = perm.grantee_principal_id
JOIN sys.schemas s ON s.schema_id = perm.major_id
WHERE perm.class = 3
  AND perm.state_desc = 'GRANT'
  AND perm.permission_name IN ('SELECT','INSERT','UPDATE','DELETE')
  AND p.name LIKE 'sb[_]%'
  AND p.name NOT IN ('sb_orchestrator','sb_tower')
  AND s.name <> 'sandbox'
  AND s.name <> SUBSTRING(p.name, 4, 128);
"@)

if ($leaks.Count -gt 0) {
    Write-Warning "Cross-schema grants detected — the isolation model is not intact:"
    $leaks | ForEach-Object { Write-Warning "    $($_.principal_name) -> $($_.schema_name)" }
}
else {
    Write-Host "  no cross-schema grants; isolation intact"
}

# --- Developer configuration -------------------------------------------------

Write-Host "`nDone.`n"
Write-Host "Add to appsettings.Development.json:"
Write-Host ""
Write-Host @"
  "KeyVault": { "Uri": "https://$KeyVault.vault.azure.net/" },
  "Storage": { "Prefix": "$blobPrefix" },
  "Sandbox": { "Environment": "$Environment", "Database": "$databaseName" }
"@
Write-Host ""
Write-Host "Per-participant connection strings are built by SimHost from"
Write-Host "sandbox-sql-$Environment-{participantId}. Nothing needs pasting by hand."
