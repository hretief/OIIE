// ============================================================================
// ISBM Service Provider — Azure Infrastructure (Bicep)
//
// Two modes:
//   GREENFIELD  — leave the existing* parameters empty; creates everything new.
//   BROWNFIELD  — pass existing resource names to reuse your infrastructure.
//
// Usage (greenfield):
//   az deployment group create -g rg-isbm -f infra/main.bicep \
//     -p sqlAdminPassword='YourP@ssw0rd!'
//
// Usage (brownfield — PowerShell):
//   az deployment group create `
//     --resource-group rg-isbm `
//     --template-file infra/main.bicep `
//     --parameters existingServiceBusName='mndotdev' `
//       existingStorageAccountName='mndotst' `
//       existingSqlServerName='mndot-sql' `
//       existingKeyVaultName='mndot-kv' `
//       sqlAdminPassword='YourStr0ng!Pass'
// ============================================================================

targetScope = 'resourceGroup'

// ---- Parameters -----------------------------------------------------------

@description('Base name for NEW resources')
param baseName string = 'isbm'

@description('Azure region')
param location string = resourceGroup().location

@description('SQL Server admin username')
param sqlAdminUser string = 'isbmadmin'

@secure()
@description('SQL Server admin password (only needed if creating a new SQL Server)')
param sqlAdminPassword string = ''

@allowed(['Basic', 'S0', 'S1', 'S2'])
param sqlSku string = 'Basic'

@allowed(['Standard', 'Premium'])
param serviceBusSku string = 'Standard'

@allowed([2, 3, 4])
param securityLevel int = 3

@description('Function App OS (windows avoids LinuxDynamicWorkers conflict in mixed resource groups)')
@allowed(['windows', 'linux'])
param functionAppOs string = 'windows'

@description('App Service plan SKU. Basic or higher keeps the host resident so Service Bus triggered notification dispatch and expiry run promptly; Y1 only runs them when the scale controller allocates an instance.')
@allowed([
  'Y1'
  'B1'
  'B2'
  'EP1'
])
param planSku string = 'B1'

@description('Keep the host warm. Unavailable on Y1, so it is forced off there.')
param alwaysOn bool = true

// ---- Brownfield parameters ------------------------------------------------

@description('Existing Service Bus namespace name (empty = create new)')
param existingServiceBusName string = ''

@description('Existing Storage Account name (empty = create new)')
param existingStorageAccountName string = ''

@description('Existing SQL Server name (empty + skipSql=false = create new)')
param existingSqlServerName string = ''

param existingSqlDatabaseName string = 'IsbmProvider'

@description('Skip SQL Server entirely (channel store uses Table Storage)')
param skipSql bool = true

@description('Existing Key Vault name (empty = create new)')
param existingKeyVaultName string = ''

@description('Existing Application Insights name (empty = create new)')
param existingAppInsightsName string = ''

// ---- Flags ----------------------------------------------------------------

var createServiceBus  = empty(existingServiceBusName)
var createStorage     = empty(existingStorageAccountName)
var createSql         = !skipSql && empty(existingSqlServerName)
var createKeyVault    = empty(existingKeyVaultName)
var createAppInsights = empty(existingAppInsightsName)

var suffix = uniqueString(resourceGroup().id)
var funcAppName = '${baseName}-func-${suffix}'

// ============================================================================
// Application Insights
// ============================================================================

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = if (createAppInsights) {
  name: '${baseName}-la-${suffix}'
  location: location
  properties: { sku: { name: 'PerGB2018' }, retentionInDays: 30 }
}

resource appInsightsNew 'Microsoft.Insights/components@2020-02-02' = if (createAppInsights) {
  name: '${baseName}-ai-${suffix}'
  location: location
  kind: 'web'
  properties: { Application_Type: 'web', WorkspaceResourceId: logAnalytics.id }
}

resource appInsightsExisting 'Microsoft.Insights/components@2020-02-02' existing = if (!createAppInsights) {
  name: existingAppInsightsName
}

var aiConnectionString = createAppInsights
  ? appInsightsNew.properties.ConnectionString
  : appInsightsExisting.properties.ConnectionString

// ============================================================================
// Storage Account
// ============================================================================

resource storageNew 'Microsoft.Storage/storageAccounts@2023-05-01' = if (createStorage) {
  name: take(replace('${baseName}st${suffix}', '-', ''), 24)
  location: location
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: { supportsHttpsTrafficOnly: true, minimumTlsVersion: 'TLS1_2', allowBlobPublicAccess: false }
}

resource storageExisting 'Microsoft.Storage/storageAccounts@2023-05-01' existing = if (!createStorage) {
  name: existingStorageAccountName
}

var storageName = createStorage ? storageNew.name : storageExisting.name
var storageConnStr = createStorage
  ? 'DefaultEndpointsProtocol=https;AccountName=${storageNew.name};AccountKey=${storageNew.listKeys().keys[0].value}'
  : 'DefaultEndpointsProtocol=https;AccountName=${storageExisting.name};AccountKey=${storageExisting.listKeys().keys[0].value}'
var blobEndpoint = 'https://${storageName}.blob.${environment().suffixes.storage}'

// ============================================================================
// Service Bus
// ============================================================================

resource serviceBusNew 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' = if (createServiceBus) {
  name: '${baseName}-sb-${suffix}'
  location: location
  sku: { name: serviceBusSku, tier: serviceBusSku }
  properties: { minimumTlsVersion: '1.2' }
}

resource serviceBusExisting 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' existing = if (!createServiceBus) {
  name: existingServiceBusName
}

var sbName = createServiceBus ? serviceBusNew.name : serviceBusExisting.name

// Entities on NEW namespace
resource notifyTopicNew 'Microsoft.ServiceBus/namespaces/topics@2022-10-01-preview' = if (createServiceBus) {
  parent: serviceBusNew
  name: 'isbm-notifications'
  properties: { defaultMessageTimeToLive: 'P7D' }
}

resource notifySubNew 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2022-10-01-preview' = if (createServiceBus) {
  parent: notifyTopicNew
  name: 'dispatch'
  properties: { defaultMessageTimeToLive: 'P7D', maxDeliveryCount: 10 }
}

resource expiredQueueNew 'Microsoft.ServiceBus/namespaces/queues@2022-10-01-preview' = if (createServiceBus) {
  parent: serviceBusNew
  name: 'isbm-expired'
  properties: { defaultMessageTimeToLive: 'P7D', maxDeliveryCount: 10 }
}

// Entities on EXISTING namespace
resource notifyTopicExisting 'Microsoft.ServiceBus/namespaces/topics@2022-10-01-preview' = if (!createServiceBus) {
  parent: serviceBusExisting
  name: 'isbm-notifications'
  properties: { defaultMessageTimeToLive: 'P7D' }
}

resource notifySubExisting 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2022-10-01-preview' = if (!createServiceBus) {
  parent: notifyTopicExisting
  name: 'dispatch'
  properties: { defaultMessageTimeToLive: 'P7D', maxDeliveryCount: 10 }
}

resource expiredQueueExisting 'Microsoft.ServiceBus/namespaces/queues@2022-10-01-preview' = if (!createServiceBus) {
  parent: serviceBusExisting
  name: 'isbm-expired'
  properties: { defaultMessageTimeToLive: 'P7D', maxDeliveryCount: 10 }
}

// The Function App reaches Service Bus through its managed identity, which is
// granted Data Owner below. No SAS rule is created and no key is ever read,
// so there is no Service Bus secret to store, rotate or leak.
var sbFullyQualifiedNamespace = '${sbName}.servicebus.windows.net'

// ============================================================================
// SQL Server + Database
// ============================================================================

resource sqlServerNew 'Microsoft.Sql/servers@2023-08-01-preview' = if (createSql) {
  name: '${baseName}-sql-${suffix}'
  location: location
  properties: {
    administratorLogin: sqlAdminUser
    administratorLoginPassword: sqlAdminPassword
    minimalTlsVersion: '1.2'
  }
}

resource sqlFirewall 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = if (createSql) {
  parent: sqlServerNew
  name: 'AllowAzureServices'
  properties: { startIpAddress: '0.0.0.0', endIpAddress: '0.0.0.0' }
}

resource sqlDbNew 'Microsoft.Sql/servers/databases@2023-08-01-preview' = if (createSql) {
  parent: sqlServerNew
  name: 'IsbmProvider'
  location: location
  sku: { name: sqlSku }
  properties: { collation: 'SQL_Latin1_General_CP1_CI_AS' }
}

resource sqlServerExisting 'Microsoft.Sql/servers@2023-08-01-preview' existing = if (!createSql && !skipSql) {
  name: existingSqlServerName
}

var sqlFqdn = createSql ? sqlServerNew.properties.fullyQualifiedDomainName : (!skipSql ? sqlServerExisting.properties.fullyQualifiedDomainName : 'n/a')
var sqlDbName = createSql ? 'IsbmProvider' : existingSqlDatabaseName
var sqlConnStr = skipSql ? 'n/a' : 'Server=tcp:${sqlFqdn},1433;Database=${sqlDbName};User ID=${sqlAdminUser};Password=${sqlAdminPassword};Encrypt=True;TrustServerCertificate=False;'

// ============================================================================
// Key Vault
// ============================================================================

resource keyVaultNew 'Microsoft.KeyVault/vaults@2023-07-01' = if (createKeyVault) {
  name: take('${baseName}-kv-${suffix}', 24)
  location: location
  properties: {
    sku: { family: 'A', name: 'standard' }
    tenantId: subscription().tenantId
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
    enablePurgeProtection: false
  }
}

resource keyVaultExisting 'Microsoft.KeyVault/vaults@2023-07-01' existing = if (!createKeyVault) {
  name: existingKeyVaultName
}

var kvUri = createKeyVault ? keyVaultNew.properties.vaultUri : keyVaultExisting.properties.vaultUri

// ============================================================================
// Function App
// ============================================================================

var planTier = planSku == 'Y1' ? 'Dynamic' : (startsWith(planSku, 'EP') ? 'ElasticPremium' : 'Basic')

resource plan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: '${baseName}-plan-${suffix}'
  location: location
  sku: { name: planSku, tier: planTier }
  kind: functionAppOs == 'linux' ? 'functionapp' : 'functionapp'
  properties: { reserved: functionAppOs == 'linux' }
}

resource functionApp 'Microsoft.Web/sites@2023-12-01' = {
  name: funcAppName
  location: location
  kind: functionAppOs == 'linux' ? 'functionapp,linux' : 'functionapp'
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: functionAppOs == 'linux' ? 'DOTNET-ISOLATED|10.0' : null
      netFrameworkVersion: functionAppOs == 'windows' ? 'v10.0' : null
      minTlsVersion: '1.2'
      alwaysOn: planSku == 'Y1' ? false : alwaysOn
      appSettings: [
        { name: 'FUNCTIONS_WORKER_RUNTIME',                value: 'dotnet-isolated' }
        { name: 'FUNCTIONS_EXTENSION_VERSION',              value: '~4' }
        { name: 'AzureWebJobsStorage',                      value: storageConnStr }
        { name: 'WEBSITE_CONTENTAZUREFILECONNECTIONSTRING',  value: storageConnStr }
        { name: 'WEBSITE_CONTENTSHARE',                      value: funcAppName }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING',     value: aiConnectionString }
        { name: 'ServiceBusConnection__fullyQualifiedNamespace', value: sbFullyQualifiedNamespace }
        { name: 'ServiceBusConnection__credential',              value: 'managedidentity' }
        { name: 'BlobPayloadStore__serviceUri',              value: blobEndpoint }
        { name: 'KeyVault__uri',                             value: kvUri }
        { name: 'Isbm__SecurityLevelConformance',            value: string(securityLevel) }
        { name: 'Isbm__DefaultExpiryDuration',               value: 'P30D' }
        { name: 'Isbm__AdditionalInformationUrl',            value: 'https://${funcAppName}.azurewebsites.net/api/configuration/supported-operations' }
        { name: 'Isbm__NotifyTopic',                         value: 'isbm-notifications' }
        { name: 'Isbm__NotifySubscription',                  value: 'dispatch' }
        { name: 'Isbm__DeadLetterQueue',                     value: 'isbm-expired' }
        // Notification triggers are now live — uncomment to disable if needed:
        // { name: 'AzureWebJobs.NotifyOnMessage.Disabled',     value: 'true' }
        // { name: 'AzureWebJobs.NotifyOnExpiry.Disabled',      value: 'true' }
      ]
    }
  }
}

// ============================================================================
// RBAC — split into new/existing pairs (BCP420: scope must be compile-time)
// ============================================================================

// --- Storage Blob Data Contributor ---
resource blobRoleNew 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (createStorage) {
  name: guid(storageNew.id, functionApp.id, 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
  scope: storageNew
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource blobRoleExisting 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!createStorage) {
  name: guid(storageExisting.id, functionApp.id, 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
  scope: storageExisting
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// --- Key Vault Secrets Officer ---
resource kvRoleNew 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (createKeyVault) {
  name: guid(keyVaultNew.id, functionApp.id, 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7')
  scope: keyVaultNew
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7')
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource kvRoleExisting 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!createKeyVault) {
  name: guid(keyVaultExisting.id, functionApp.id, 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7')
  scope: keyVaultExisting
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7')
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// --- Service Bus Data Owner ---
resource sbRoleNew 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (createServiceBus) {
  name: guid(serviceBusNew.id, functionApp.id, '090c5cfd-751d-490a-894a-3ce6f1109419')
  scope: serviceBusNew
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '090c5cfd-751d-490a-894a-3ce6f1109419')
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource sbRoleExisting 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!createServiceBus) {
  name: guid(serviceBusExisting.id, functionApp.id, '090c5cfd-751d-490a-894a-3ce6f1109419')
  scope: serviceBusExisting
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '090c5cfd-751d-490a-894a-3ce6f1109419')
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// ============================================================================
// Outputs
// ============================================================================

output functionAppName string = functionApp.name
output functionAppUrl string = 'https://${functionApp.properties.defaultHostName}'
output serviceBusNamespace string = sbName
output sqlServerFqdn string = skipSql ? 'n/a (Table Storage)' : sqlFqdn
output sqlDatabaseName string = skipSql ? 'n/a (Table Storage)' : sqlDbName
output keyVaultUri string = kvUri
output storageAccountName string = storageName
output mode string = '${createServiceBus ? 'new' : 'existing'} SB | ${createStorage ? 'new' : 'existing'} Storage | ${createSql ? 'new' : 'existing'} SQL | ${createKeyVault ? 'new' : 'existing'} KV | ${createAppInsights ? 'new' : 'existing'} AI'
output deployCommand string = 'func azure functionapp publish ${functionApp.name}'
