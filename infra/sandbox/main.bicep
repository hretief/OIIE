@description('Environment this deployment serves. Drives naming and sizing.')
@allowed(['dev', 'ci', 'demo'])
param environmentName string = 'demo'

@description('Location for new resources. Defaults to the resource group.')
param location string = resourceGroup().location

@description('Existing Key Vault holding SQL passwords and ISBM tokens.')
param keyVaultName string = 'mndot'

@description('Existing storage account for BOD payload bodies.')
param storageAccountName string

@description('Existing SQL server hosting the Sandbox database.')
param sqlServerName string = 'acme-sql-server'

@description('Sandbox database for this environment.')
param sqlDatabaseName string

@description('ws-ISBM provider base URL, including /api.')
param isbmBaseUrl string

@description('Function key for the ws-ISBM provider.')
@secure()
param isbmApiKey string = ''

@description('Shared key required on /admin endpoints. Empty leaves them open.')
@secure()
param adminKey string = ''

@description('App Service plan SKU. B1 is the smallest that supports Always On.')
param planSku string = 'B1'

var appName = 'oiie-sandbox-${environmentName}'
var planName = 'plan-${appName}'
var insightsName = 'appi-${appName}'
var workspaceName = 'log-${appName}'

// ---------------------------------------------------------------------------
// Telemetry
//
// Shared with the ISBM and CIR providers so one correlation id reconstructs an
// exchange across all three. If those providers report elsewhere, point this at
// their workspace instead — a timeline split across workspaces is barely better
// than no timeline.
// ---------------------------------------------------------------------------

resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: workspaceName
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: insightsName
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: workspace.id
  }
}

// ---------------------------------------------------------------------------
// Hosting
// ---------------------------------------------------------------------------

resource plan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: planName
  location: location
  sku: {
    name: planSku
  }
  kind: 'linux'
  properties: {
    reserved: true
  }
}

resource app 'Microsoft.Web/sites@2023-12-01' = {
  name: appName
  location: location
  kind: 'app,linux'
  identity: {
    // Managed identity for Key Vault, Storage and telemetry. SQL still uses
    // contained users with passwords, because the per-participant grant model is
    // the point and a single managed identity would collapse it to one principal.
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'

      // The inbox pump and outbox dispatcher are hosted services. Without Always On
      // the app is unloaded when idle and stops consuming, which looks exactly like
      // a provider that has stopped delivering.
      alwaysOn: true

      // Blazor Server is a SignalR circuit.
      webSocketsEnabled: true

      // Sticky sessions: a circuit is bound to the instance that created it.
      // Harmless on one instance, essential the moment there are two.
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      http20Enabled: true

      healthCheckPath: '/health/participants'

      appSettings: [
        { name: 'ASPNETCORE_ENVIRONMENT', value: environmentName == 'demo' ? 'Production' : 'Staging' }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: insights.properties.ConnectionString }

        { name: 'Sandbox__Environment', value: environmentName }
        { name: 'Sandbox__SqlServer', value: sqlServerName }
        { name: 'Sandbox__Database', value: sqlDatabaseName }

        // Deployed alongside the app rather than a level up, unlike the local
        // layout where the solution root is the parent.
        //
        // This must be PersonalityPacks, not Personalities. SimHost/Personalities
        // is C# handler source; the deployed packs are PersonalityPacks/**/*.yaml,
        // published by the csproj. Pointing here at 'Personalities' made the app
        // read a stale folder left behind by an earlier deployment -- zip deploy
        // does not delete files -- and that folder parsed without error, so the
        // roster silently omitted participants added since.
        { name: 'Sandbox__PersonalitiesPath', value: 'PersonalityPacks' }
        { name: 'Sandbox__SchemasPath', value: 'Schemas' }

        { name: 'KeyVault__Uri', value: 'https://${keyVaultName}.vault.azure.net/' }

        { name: 'Storage__BlobServiceUri', value: 'https://${storageAccountName}.blob.core.windows.net' }
        { name: 'Storage__PayloadContainer', value: 'sandbox-payloads' }
        { name: 'Storage__Prefix', value: environmentName }

        { name: 'Isbm__ApiKey', value: isbmApiKey }

        // Without this the admin endpoints — reset, channel deletion, schema drop —
        // are callable by anyone who finds the URL.
        { name: 'Sandbox__AdminKey', value: adminKey }

        // Base URL for NotifyListener callbacks. Push delivery is not wired up yet,
        // but a deployed app is addressable, which a workstation is not — this is
        // what makes it testable at all.
        { name: 'Isbm__ListenerBaseUrl', value: 'https://${appName}.azurewebsites.net' }
      ]
    }
  }
}

// ---------------------------------------------------------------------------
// Data-plane grants
//
// Control-plane and data-plane RBAC are separate in Azure. Owner on the
// subscription grants neither of these, and the resulting 403 reads like an
// application bug rather than a missing role.
// ---------------------------------------------------------------------------

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: keyVaultName
}

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: storageAccountName
}

// Key Vault Secrets User
resource keyVaultGrant 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: keyVault
  name: guid(keyVault.id, app.id, '4633458b-17de-408a-b874-0445c86b69e6')
  properties: {
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      '4633458b-17de-408a-b874-0445c86b69e6'
    )
    principalId: app.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// Storage Blob Data Contributor
resource storageGrant 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storage
  name: guid(storage.id, app.id, 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
  properties: {
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
    )
    principalId: app.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// ---------------------------------------------------------------------------
// SQL access
//
// App Service outbound addresses are dynamic, so a per-IP rule is not workable.
// This rule permits any Azure service, which is broad — the database is protected
// by per-participant contained users rather than by network scope. Replace with a
// private endpoint or VNet integration if that is not acceptable.
// ---------------------------------------------------------------------------

resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' existing = {
  name: sqlServerName
}

resource allowAzureServices 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = {
  parent: sqlServer
  name: 'AllowAllWindowsAzureIps'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

// ---------------------------------------------------------------------------

output appName string = app.name
output appUrl string = 'https://${app.properties.defaultHostName}'
output principalId string = app.identity.principalId
output insightsName string = insights.name
