targetScope = 'resourceGroup'

// ---------------------------------------------------------------------------
// Parameters
// ---------------------------------------------------------------------------

@description('Short base name used to derive all resource names. Lowercase letters and digits only.')
@minLength(3)
@maxLength(11)
param baseName string = 'cir'

@description('Azure region for all resources.')
param location string = resourceGroup().location

@description('OS for the Function App plan. Resource groups that already contain Windows resources cannot host Linux dynamic workers.')
@allowed([ 'windows', 'linux' ])
param functionAppOs string = 'windows'

@description('.NET version for the isolated worker. .NET 10 is supported on every plan except Linux Consumption.')
@allowed([ '8.0', '9.0', '10.0' ])
param dotnetVersion string = '10.0'

@description('Whether to create a new SQL logical server or use one that already exists.')
@allowed([ 'new', 'existing' ])
param sqlServerMode string = 'new'

@description('Name of the pre-existing SQL logical server. Required when sqlServerMode is \'existing\'.')
param existingSqlServerName string = ''

@description('Resource group holding the pre-existing SQL server. Defaults to this deployment\'s resource group.')
param existingSqlServerResourceGroup string = ''

@description('Object ID (not the App ID) of the Entra principal that will be SQL server admin. Only used when creating a new server.')
param sqlAdminObjectId string = ''

@description('Display name / UPN of the Entra SQL admin principal. Only used when creating a new server.')
param sqlAdminLogin string = ''

@description('Principal type of the SQL admin.')
@allowed([ 'User', 'Group', 'Application' ])
param sqlAdminPrincipalType string = 'User'

@description('Name of the SQL database that holds the CIR registry.')
param sqlDatabaseName string = 'cir'

@description('Region for the CIR database. Only used with an existing server, where it must match the server\'s region.')
param sqlDatabaseLocation string = ''

@description('How the Function App authenticates to SQL. Use \'sql\' when the target server has no Microsoft Entra admin configured — without one the server cannot validate tokens at all.')
@allowed([ 'entra', 'sql' ])
param sqlAuthMode string = 'entra'

@description('SQL login name. Required when sqlAuthMode is \'sql\'.')
param sqlLogin string = ''

@secure()
@description('SQL login password. Required when sqlAuthMode is \'sql\'. Stored in Key Vault, never in app settings.')
param sqlPassword string = ''

@description('Serverless database auto-pause delay in minutes. Use -1 to disable auto-pause.')
param sqlAutoPauseDelayMinutes int = 60

@description('Maximum vCores for the serverless database.')
param sqlMaxVCores int = 1

@description('Base URL of the ws-ISBM Service Provider, e.g. https://isbm-func-x.azurewebsites.net/api. Leave empty to keep the listener dormant.')
param isbmBaseUrl string = ''

@secure()
@description('Function key for the ws-ISBM provider.')
param isbmApiKey string = ''

@description('Channel carrying the six request-response BODs.')
param isbmRequestChannelUri string = '/OIIE/CIR/Request'

@description('Channel carrying the five BODs that define no response.')
param isbmPublicationChannelUri string = '/OIIE/CIR/Publication'

@description('NCRONTAB expression for the ISBM poll. Six fields: the leading one is seconds.')
param isbmPollSchedule string = '*/15 * * * * *'

@description('Enable the ISBM listener. Off unless a base URL is supplied.')
param isbmEnabled bool = false

@description('Topic the CIR listens on. Must match what consumers and publishers use.')
param isbmTopic string = 'ws-CIR'

@description('App Service plan SKU. Basic or higher keeps the host resident so the ISBM poll timer fires on schedule; Y1 only runs it when the scale controller allocates an instance.')
@allowed([
  'Y1'
  'B1'
  'B2'
  'EP1'
])
param planSku string = 'B1'

@description('Keep the host warm. Unavailable on Y1, so it is forced off there.')
param alwaysOn bool = true

@description('Tags applied to every resource.')
param tags object = {
  workload: 'ws-cir'
  managedBy: 'bicep'
}

// ---------------------------------------------------------------------------
// Naming
// ---------------------------------------------------------------------------

var suffix = uniqueString(resourceGroup().id)
var storageName = toLower('${baseName}st${substring(suffix, 0, 8)}')
var functionAppName = '${baseName}-func-${substring(suffix, 0, 8)}'
var planName = '${baseName}-plan-${substring(suffix, 0, 8)}'
var newSqlServerName = '${baseName}-sql-${substring(suffix, 0, 8)}'
var workspaceName = '${baseName}-law-${substring(suffix, 0, 8)}'
var insightsName = '${baseName}-ai-${substring(suffix, 0, 8)}'
var identityName = '${baseName}-id-${substring(suffix, 0, 8)}'
var vaultName = '${baseName}-kv-${substring(suffix, 0, 8)}'

var isLinux = functionAppOs == 'linux'

var useExistingSql = sqlServerMode == 'existing'
var sqlServerResourceGroupName = empty(existingSqlServerResourceGroup) ? resourceGroup().name : existingSqlServerResourceGroup
var effectiveSqlServerName = useExistingSql ? existingSqlServerName : newSqlServerName

// Built rather than read from the resource so nothing has to reference a
// conditionally-deployed resource.
var effectiveSqlFqdn = '${effectiveSqlServerName}${environment().suffixes.sqlServerHostname}'

var useSqlAuth = sqlAuthMode == 'sql'
var sqlSecretName = 'cir-sql-connection'
var sqlBase = 'Server=tcp:${effectiveSqlFqdn},1433;Initial Catalog=${sqlDatabaseName};Encrypt=True;TrustServerCertificate=False;Connection Timeout=60;'
var sqlConnectionSql = '${sqlBase}User ID=${sqlLogin};Password=${sqlPassword};'
var sqlConnectionEntra = '${sqlBase}Authentication=Active Directory Managed Identity;User Id=${identity.properties.clientId};'

// ---------------------------------------------------------------------------
// Identity
// ---------------------------------------------------------------------------

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: identityName
  location: location
  tags: tags
}

// ---------------------------------------------------------------------------
// Observability
// ---------------------------------------------------------------------------

resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: workspaceName
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: insightsName
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: workspace.id
  }
}

// ---------------------------------------------------------------------------
// Storage (Functions runtime state)
// ---------------------------------------------------------------------------

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageName
  location: location
  tags: tags
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    allowBlobPublicAccess: false
  }
}

var storageConnectionString = 'DefaultEndpointsProtocol=https;AccountName=${storage.name};AccountKey=${storage.listKeys().keys[0].value};EndpointSuffix=${environment().suffixes.storage}'

// ---------------------------------------------------------------------------
// Azure SQL
// ---------------------------------------------------------------------------

resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = if (!useExistingSql) {
  name: newSqlServerName
  location: location
  tags: tags
  identity: { type: 'SystemAssigned' }
  properties: {
    version: '12.0'
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
    administrators: {
      administratorType: 'ActiveDirectory'
      login: sqlAdminLogin
      sid: sqlAdminObjectId
      principalType: sqlAdminPrincipalType
      tenantId: subscription().tenantId
      azureADOnlyAuthentication: true
    }
  }
}

resource sqlDatabase 'Microsoft.Sql/servers/databases@2023-08-01-preview' = if (!useExistingSql) {
  parent: sqlServer
  name: sqlDatabaseName
  location: location
  tags: tags
  sku: {
    name: 'GP_S_Gen5'
    tier: 'GeneralPurpose'
    family: 'Gen5'
    capacity: sqlMaxVCores
  }
  properties: {
    collation: 'SQL_Latin1_General_CP1_CI_AS'
    autoPauseDelay: sqlAutoPauseDelayMinutes
    minCapacity: json('0.5')
    maxSizeBytes: 34359738368
    zoneRedundant: false
  }
}

// Allows Function App outbound IPs (and any other Azure service) to reach the server.
resource allowAzureServices 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = if (!useExistingSql) {
  parent: sqlServer
  name: 'AllowAllWindowsAzureIps'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

// Brownfield: add only the database to a server that already exists, wherever it lives.
module sqlDatabaseOnExistingServer 'modules/sqldb-existing.bicep' = if (useExistingSql) {
  name: 'cir-db-on-existing-server'
  scope: resourceGroup(sqlServerResourceGroupName)
  params: {
    serverName: existingSqlServerName
    databaseName: sqlDatabaseName
    databaseLocation: empty(sqlDatabaseLocation) ? location : sqlDatabaseLocation
    skuCapacity: sqlMaxVCores
    autoPauseDelay: sqlAutoPauseDelayMinutes
    tags: tags
  }
}

// A SQL-auth connection string is a credential, so it lives in Key Vault and the
// app setting is a reference. Entra mode needs no secret at all.
module sqlSecret 'modules/sqlsecret.bicep' = if (useSqlAuth) {
  name: 'cir-sql-secret'
  params: {
    vaultName: vaultName
    location: location
    secretName: sqlSecretName
    secretValue: sqlConnectionSql
    principalId: identity.properties.principalId
    tags: tags
  }
}

// ---------------------------------------------------------------------------
// Compute
// ---------------------------------------------------------------------------

// Tier is implied by the SKU name, and a mismatched pair is rejected at deploy
// time rather than at run time.
var planTier = planSku == 'Y1' ? 'Dynamic' : (startsWith(planSku, 'EP') ? 'ElasticPremium' : 'Basic')

resource plan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: planName
  location: location
  tags: tags
  sku: {
    name: planSku
    tier: planTier
  }
  kind: isLinux ? 'functionapp,linux' : 'functionapp'
  properties: {
    reserved: isLinux
  }
}

resource functionApp 'Microsoft.Web/sites@2023-12-01' = {
  name: functionAppName
  location: location
  tags: tags
  kind: isLinux ? 'functionapp,linux' : 'functionapp'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identity.id}': {}
    }
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    keyVaultReferenceIdentity: identity.id
    siteConfig: {
      netFrameworkVersion: isLinux ? null : 'v${dotnetVersion}'
      linuxFxVersion: isLinux ? 'DOTNET-ISOLATED|${dotnetVersion}' : null
      use32BitWorkerProcess: false
      alwaysOn: planSku == 'Y1' ? false : alwaysOn
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      http20Enabled: true
      appSettings: [
        { name: 'FUNCTIONS_EXTENSION_VERSION', value: '~4' }
        { name: 'FUNCTIONS_WORKER_RUNTIME', value: 'dotnet-isolated' }
        { name: 'WEBSITE_RUN_FROM_PACKAGE', value: '1' }
        { name: 'AzureWebJobsStorage', value: storageConnectionString }
        { name: 'WEBSITE_CONTENTAZUREFILECONNECTIONSTRING', value: storageConnectionString }
        { name: 'WEBSITE_CONTENTSHARE', value: toLower(functionAppName) }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: insights.properties.ConnectionString }
        { name: 'AZURE_CLIENT_ID', value: identity.properties.clientId }
        {
          name: 'Cir__SqlConnectionString'
          value: useSqlAuth
            ? '@Microsoft.KeyVault(VaultName=${vaultName};SecretName=${sqlSecretName})'
            : sqlConnectionEntra
        }
        { name: 'Cir__AutoCreateSchema', value: 'true' }

        // ws-ISBM binding. Enabled only when a base URL is supplied, so a
        // deployment cannot start polling a broker that is not there.
        { name: 'Isbm__Enabled', value: string(isbmEnabled && !empty(isbmBaseUrl)) }
        { name: 'Isbm__BaseUrl', value: isbmBaseUrl }
        { name: 'Isbm__ApiKey', value: isbmApiKey }
        { name: 'Isbm__RequestChannelUri', value: isbmRequestChannelUri }
        { name: 'Isbm__PublicationChannelUri', value: isbmPublicationChannelUri }

        // Flat, unlike its siblings. The TimerTrigger resolves this through a
        // %...% binding expression, which WebJobs looks up as a literal setting
        // name before the double underscore is folded into a configuration
        // section. Named Isbm__PollSchedule it never resolves, and the function
        // fails indexing and is disabled at startup without failing the deploy.
        { name: 'IsbmPollSchedule', value: isbmPollSchedule }
        { name: 'Isbm__Topics__0', value: isbmTopic }
      ]
    }
  }
}

// ---------------------------------------------------------------------------
// Outputs — consumed by deploy.ps1
// ---------------------------------------------------------------------------

output functionAppName string = functionApp.name
output functionAppHostName string = functionApp.properties.defaultHostName
output sqlServerFqdn string = effectiveSqlFqdn
output sqlServerName string = effectiveSqlServerName
output sqlServerResourceGroup string = sqlServerResourceGroupName
output sqlDatabaseName string = sqlDatabaseName
output identityName string = identity.name
output identityClientId string = identity.properties.clientId
output identityPrincipalId string = identity.properties.principalId
output sqlAuthMode string = sqlAuthMode
output keyVaultName string = useSqlAuth ? vaultName : ''
output isbmEnabled bool = isbmEnabled && !empty(isbmBaseUrl)
output storageAccountName string = storage.name
output appInsightsName string = insights.name
