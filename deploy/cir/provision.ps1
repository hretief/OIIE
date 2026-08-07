<#
.SYNOPSIS
    Provisions Azure infrastructure and publishes the ws-CIR Function App.

.DESCRIPTION
    Named provision.ps1 rather than deploy.ps1 deliberately: the project template
    reserves deploy/deploy.ps1 for a post-build step that takes -TargetDir and is
    invoked by an Exec task in the csproj. The two are unrelated and must not
    share a name.

.EXAMPLE
    # Greenfield: create a new SQL server alongside everything else
    .\provision.ps1 -ResourceGroup HilmarRetiefRG -Location eastus2

.EXAMPLE
    # Brownfield: add the CIR database to an existing SQL server
    .\provision.ps1 -ResourceGroup HilmarRetiefRG -Location eastus2 `
                 -SqlServerMode existing -ExistingSqlServerName acme-sql-server
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $ResourceGroup,
    [Parameter(Mandatory = $true)][string] $Location,
    [string] $BaseName = 'cir',
    [ValidateSet('windows', 'linux')][string] $FunctionAppOs = 'windows',
    [ValidateSet('8.0', '9.0', '10.0')][string] $DotnetVersion = '10.0',

    [ValidateSet('new', 'existing')][string] $SqlServerMode = 'new',
    [string] $ExistingSqlServerName = '',
    [string] $ExistingSqlServerResourceGroup = '',
    [string] $SqlDatabaseName = 'cir',

    # How both this script and the Function App authenticate to SQL.
    # Use 'sql' when the target server has no Microsoft Entra admin configured.
    [ValidateSet('entra', 'sql')][string] $SqlAuthMode = 'entra',
    [string] $SqlLogin = '',
    [SecureString] $SqlPassword,

    # Only used when SqlServerMode = 'new'. Inferred from the signed-in user if omitted.
    [string] $SqlAdminObjectId = '',
    [string] $SqlAdminLogin = '',
    [ValidateSet('User', 'Group', 'Application')][string] $SqlAdminPrincipalType = 'User',

    # ws-ISBM binding. Omit to leave the listener dormant.
    [string] $IsbmBaseUrl = '',
    [string] $IsbmApiKey = '',
    [string] $IsbmRequestChannelUri = '/OIIE/CIR/Request',
    [string] $IsbmPublicationChannelUri = '/OIIE/CIR/Publication',
    [string] $IsbmPollSchedule = '*/15 * * * * *',

    # Basic or higher keeps the host resident so the ISBM poll timer fires on
    # its own. Y1 only runs it when the scale controller allocates an instance.
    [ValidateSet('Y1', 'B1', 'B2', 'EP1')]
    [string] $PlanSku = 'B1',
    [string] $IsbmTopic = 'ws-CIR',
    [switch] $EnableIsbm,
    [switch] $DisableIsbm,

    [switch] $SkipPublish
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

function Write-Step($msg) { Write-Host "`n=== $msg ===" -ForegroundColor Cyan }

# az is a native executable: a non-zero exit does not throw under
# $ErrorActionPreference = 'Stop'. Every az call that matters is checked.
function Assert-LastExitCode($what) {
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit code $LASTEXITCODE)." }
}

if ($SqlServerMode -eq 'existing' -and [string]::IsNullOrWhiteSpace($ExistingSqlServerName)) {
    throw "-ExistingSqlServerName is required when -SqlServerMode is 'existing'."
}

if ($DotnetVersion -eq '10.0' -and $FunctionAppOs -eq 'linux') {
    throw ".NET 10 is not available on the Linux Consumption plan. Use -FunctionAppOs windows, or move to Flex Consumption."
}

# --- Preflight ---------------------------------------------------------------

Write-Step 'Preflight'

if (-not (Get-Command az -ErrorAction SilentlyContinue)) { throw 'az not found on PATH.' }

if (-not (Get-Module -ListAvailable -Name SqlServer)) {
    Write-Host 'Installing SqlServer PowerShell module (current user scope)...'
    Install-Module SqlServer -Scope CurrentUser -Force -AllowClobber
}
Import-Module SqlServer

$account = az account show | ConvertFrom-Json
Write-Host "Subscription : $($account.name)"
Write-Host "Tenant       : $($account.tenantId)"

$SqlDatabaseLocation = ''
$SqlPasswordPlain = ''

if ($SqlAuthMode -eq 'sql') {
    if ([string]::IsNullOrWhiteSpace($SqlLogin)) {
        $SqlLogin = Read-Host 'SQL login name'
    }
    if (-not $SqlPassword) {
        $SqlPassword = Read-Host "Password for '$SqlLogin'" -AsSecureString
    }
    $SqlPasswordPlain = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
        [Runtime.InteropServices.Marshal]::SecureStringToBSTR($SqlPassword))
    Write-Host "SQL auth     : login '$SqlLogin'"
}

if ($SqlServerMode -eq 'new') {
    if ([string]::IsNullOrWhiteSpace($SqlAdminObjectId)) {
        $signedIn = az ad signed-in-user show | ConvertFrom-Json
        if (-not $signedIn) { throw 'Could not resolve the signed-in user. Pass -SqlAdminObjectId and -SqlAdminLogin explicitly.' }
        $SqlAdminObjectId = $signedIn.id
        if ([string]::IsNullOrWhiteSpace($SqlAdminLogin)) { $SqlAdminLogin = $signedIn.userPrincipalName }
    }
    Write-Host "SQL admin    : $SqlAdminLogin ($SqlAdminObjectId)"
}
else {
    # Resolve the server's resource group and region up front. The database must
    # sit in the server's region, and Bicep needs that value at deployment start.
    $server = az sql server list --query "[?name=='$ExistingSqlServerName'] | [0]" | ConvertFrom-Json
    Assert-LastExitCode 'az sql server list'
    if (-not $server) {
        throw "SQL server '$ExistingSqlServerName' was not found in subscription '$($account.name)'."
    }

    if ([string]::IsNullOrWhiteSpace($ExistingSqlServerResourceGroup)) {
        $ExistingSqlServerResourceGroup = $server.resourceGroup
    }
    $SqlDatabaseLocation = $server.location

    Write-Host "SQL server   : existing '$ExistingSqlServerName'"
    Write-Host "  region     : $SqlDatabaseLocation"
    Write-Host "  group      : $ExistingSqlServerResourceGroup"

    $SqlAdminObjectId = ''
    $SqlAdminLogin = ''
}

# --- Resource group ----------------------------------------------------------

Write-Step "Resource group '$ResourceGroup'"

if ((az group exists --name $ResourceGroup) -eq 'false') {
    az group create --name $ResourceGroup --location $Location --output none
    Write-Host 'Created.'
}
else {
    Write-Host 'Already exists.'
}

# --- Bicep -------------------------------------------------------------------

Write-Step 'Carrying forward existing ISBM configuration'

# App settings in Bicep are declarative, so a redeploy without these parameters
# would silently erase the ISBM binding. Read whatever is already deployed and
# use it as the default.
$existingApp = az functionapp list -g $ResourceGroup `
    --query "[?starts_with(name, '$BaseName-func-')].name | [0]" -o tsv 2>$null

if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($existingApp)) {
    $settings = az functionapp config appsettings list -g $ResourceGroup -n $existingApp -o json | ConvertFrom-Json
    function Get-Setting($name) { ($settings | Where-Object { $_.name -eq $name }).value }

    if ([string]::IsNullOrWhiteSpace($IsbmBaseUrl)) { $IsbmBaseUrl = [string](Get-Setting 'Isbm__BaseUrl') }
    if ([string]::IsNullOrWhiteSpace($IsbmApiKey))  { $IsbmApiKey  = [string](Get-Setting 'Isbm__ApiKey') }

    $wasEnabled = [string](Get-Setting 'Isbm__Enabled') -eq 'True'
    if (-not $EnableIsbm -and -not $DisableIsbm -and $wasEnabled) {
        $EnableIsbm = [switch]$true
        Write-Host "Preserving the enabled ISBM binding on $existingApp."
    }

    if (-not [string]::IsNullOrWhiteSpace($IsbmBaseUrl)) {
        Write-Host "ISBM base URL: $IsbmBaseUrl"
    }
    else {
        Write-Host 'No ISBM configuration found; the listener will stay dormant.'
    }
}
else {
    Write-Host 'No existing Function App found; nothing to carry forward.'
}

if ($DisableIsbm) {
    $EnableIsbm = [switch]$false
    Write-Host 'ISBM listener explicitly disabled.'
}

Write-Step 'Deploying infrastructure'

$deploymentName = "cir-$(Get-Date -Format 'yyyyMMddHHmmss')"

az deployment group create `
    --name $deploymentName `
    --resource-group $ResourceGroup `
    --template-file "$root\infra\main.bicep" `
    --parameters "$root\infra\main.parameters.json" `
    --parameters baseName=$BaseName `
                 functionAppOs=$FunctionAppOs `
                 dotnetVersion=$DotnetVersion `
                 sqlServerMode=$SqlServerMode `
                 existingSqlServerName=$ExistingSqlServerName `
                 existingSqlServerResourceGroup=$ExistingSqlServerResourceGroup `
                 sqlDatabaseName=$SqlDatabaseName `
                 sqlDatabaseLocation=$SqlDatabaseLocation `
                 sqlAuthMode=$SqlAuthMode `
                 sqlLogin=$SqlLogin `
                 sqlPassword=$SqlPasswordPlain `
                 sqlAdminObjectId=$SqlAdminObjectId `
                 sqlAdminLogin=$SqlAdminLogin `
                 sqlAdminPrincipalType=$SqlAdminPrincipalType `
                 isbmBaseUrl=$IsbmBaseUrl `
                 isbmApiKey=$IsbmApiKey `
                 isbmRequestChannelUri=$IsbmRequestChannelUri `
                 isbmPublicationChannelUri=$IsbmPublicationChannelUri `
                 isbmPollSchedule=$IsbmPollSchedule `
                 planSku=$PlanSku `
                 isbmTopic=$IsbmTopic `
                 isbmEnabled=$($EnableIsbm.IsPresent.ToString().ToLower()) `
    --output none
Assert-LastExitCode 'az deployment group create'

$outputs = (az deployment group show `
        --name $deploymentName `
        --resource-group $ResourceGroup `
        --query properties.outputs | ConvertFrom-Json)
Assert-LastExitCode 'az deployment group show'

if (-not $outputs) { throw 'Deployment produced no outputs; nothing to configure.' }

$functionAppName = $outputs.functionAppName.value
$sqlServerName = $outputs.sqlServerName.value
$sqlServerRg = $outputs.sqlServerResourceGroup.value
$sqlServerFqdn = $outputs.sqlServerFqdn.value
$sqlDatabaseName = $outputs.sqlDatabaseName.value
$identityName = $outputs.identityName.value
$identityClientId = $outputs.identityClientId.value

Write-Host "Function App : $functionAppName"
Write-Host "SQL server   : $sqlServerFqdn (rg: $sqlServerRg)"
Write-Host "Database     : $sqlDatabaseName"
Write-Host "Identity     : $identityName"

# --- Firewall ----------------------------------------------------------------

function Add-SqlFirewallRule {
    param([string] $RuleName, [string] $Ip)
    az sql server firewall-rule create `
        --resource-group $sqlServerRg `
        --server $sqlServerName `
        --name $RuleName `
        --start-ip-address $Ip `
        --end-ip-address $Ip `
        --output none
    Write-Host "Firewall: allowed $Ip as '$RuleName'"
}

Write-Step 'SQL firewall'

# Best effort only. The address an HTTPS probe reports is not always the address
# SQL sees — split-tunnel VPNs and corporate proxies routinely route port 1433
# differently — so the authoritative rule is added from the connection error below.
try {
    $probeIp = (Invoke-RestMethod -Uri 'https://api.ipify.org?format=json' -TimeoutSec 15).ip
    Add-SqlFirewallRule -RuleName 'deploy-workstation' -Ip $probeIp
}
catch {
    Write-Host 'Could not determine the outbound IP by probe; relying on error-driven detection.'
}

# --- Grant the managed identity access ---------------------------------------

Write-Step 'Configuring SQL access'

$token = ''
if ($SqlAuthMode -eq 'entra') {
    $token = (az account get-access-token --resource https://database.windows.net/ --query accessToken -o tsv)
    Assert-LastExitCode 'az account get-access-token'
}

# In SQL-auth mode the Function App signs in with the same login, so there is no
# managed-identity principal to create. In Entra mode we create one from the
# identity's client ID as a SID: CREATE USER ... FROM EXTERNAL PROVIDER would
# require the server to hold Directory Readers, which a Global Admin must grant.
$grantSql = ''
if ($SqlAuthMode -eq 'entra') {
    if ([string]::IsNullOrWhiteSpace($identityClientId)) {
        throw 'The deployment did not return identityClientId; cannot create the SQL user.'
    }
    $sidBytes = ([guid]$identityClientId).ToByteArray()
    $sidHex = '0x' + (($sidBytes | ForEach-Object { $_.ToString('X2') }) -join '')

    $grantSql = @"
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'$identityName')
BEGIN
    EXEC(N'CREATE USER [$identityName] WITH SID = $sidHex, TYPE = E');
END
ALTER ROLE db_datareader  ADD MEMBER [$identityName];
ALTER ROLE db_datawriter  ADD MEMBER [$identityName];
ALTER ROLE db_ddladmin    ADD MEMBER [$identityName];
"@
}

function Invoke-CirSql {
    param([string] $Database, [string] $Query)

    $attempt = 0
    while ($true) {
        $attempt++
        try {
            if ($SqlAuthMode -eq 'sql') {
                return Invoke-Sqlcmd `
                    -ServerInstance $sqlServerFqdn `
                    -Database $Database `
                    -Username $SqlLogin `
                    -Password $SqlPasswordPlain `
                    -Query $Query `
                    -ConnectionTimeout 90 `
                    -ErrorAction Stop
            }

            return Invoke-Sqlcmd `
                -ServerInstance $sqlServerFqdn `
                -Database $Database `
                -AccessToken $token `
                -Query $Query `
                -ConnectionTimeout 90 `
                -ErrorAction Stop
        }
        catch {
            $message = $_.Exception.Message

            # Retrying these only wastes time — they are configuration or code
            # problems, not a database still waking up.
            $nonTransient = @(
                'A parameter cannot be found',
                'Login failed for user',
                'not currently configured to accept this token',
                'Cannot open database',
                'Invalid object name',
                'Incorrect syntax'
            )
            foreach ($pattern in $nonTransient) {
                if ($message -like "*$pattern*") {
                    Write-Host "Non-transient failure: $message" -ForegroundColor Red
                    throw
                }
            }

            # Azure names the blocked address in the error text. Open it and retry.
            if ($message -match "Client with IP address '([0-9a-fA-F\.:]+)' is not allowed") {
                $blockedIp = $Matches[1]
                Write-Host "SQL saw this client as $blockedIp. Adding a firewall rule..."
                Add-SqlFirewallRule -RuleName "deploy-client-$($blockedIp -replace '[\.:]', '-')" -Ip $blockedIp
                Write-Host 'Waiting 20s for the rule to propagate...'
                Start-Sleep -Seconds 20
                if ($attempt -lt 6) { continue }
            }

            if ($attempt -ge 5) {
                Write-Host "Final failure: $message" -ForegroundColor Red
                throw
            }

            Write-Host "Attempt $attempt failed: $message"
            Write-Host 'Retrying in 30s (a serverless database may be resuming)...'
            Start-Sleep -Seconds 30
        }
    }
}

if ($SqlAuthMode -eq 'entra') {
    Invoke-CirSql -Database $sqlDatabaseName -Query $grantSql | Out-Null
    Write-Host "Granted db_datareader, db_datawriter, db_ddladmin to $identityName"
}
else {
    # Verify the login actually reaches the database before we go any further.
    Invoke-CirSql -Database $sqlDatabaseName -Query 'SELECT 1' | Out-Null
    Write-Host "SQL login '$SqlLogin' verified against $sqlDatabaseName."
    Write-Host 'The Function App will use the same login via a Key Vault reference.'
}

# --- Compatibility level 170 (REGEXP_LIKE) -----------------------------------

Write-Step 'Setting compatibility level 170'

Invoke-CirSql -Database 'master' -Query "ALTER DATABASE [$sqlDatabaseName] SET COMPATIBILITY_LEVEL = 170;" | Out-Null

$level = Invoke-CirSql -Database $sqlDatabaseName -Query "SELECT compatibility_level AS lvl FROM sys.databases WHERE name = DB_NAME();"
Write-Host "Compatibility level is now $($level.lvl)"

# --- Publish -----------------------------------------------------------------

if (-not $SkipPublish) {
    Write-Step 'Publishing the Function App'

    if (-not (Get-Command func -ErrorAction SilentlyContinue)) {
        Write-Warning 'Azure Functions Core Tools (func) not found. Skipping publish.'
    }
    else {
        Push-Location "$root\CirProvider"
        try {
            dotnet build --configuration Release
            func azure functionapp publish $functionAppName
        }
        finally {
            Pop-Location
        }
    }
}

Write-Step 'Done'
Write-Host "Base URL: https://$($outputs.functionAppHostName.value)/api" -ForegroundColor Green
Write-Host "Health  : https://$($outputs.functionAppHostName.value)/api/health" -ForegroundColor Green
