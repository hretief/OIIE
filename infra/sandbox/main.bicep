@description('Environment this deployment serves. Drives naming and sizing.')
@allowed(['dev', 'ci', 'demo'])
param environmentName string = 'demo'

@description('Location for new resources. Defaults to the resource group.')
param location string = resourceGroup().location

@description('Existing Key Vault holding SQL passwords and ISBM tokens.')
param keyVaultName string = 'mndot'

@description('Existing storage account for BOD payload bodies.')
param storageAccountName string

@description('Existing SQL server hosting the participant provider databases. The sandbox itself owns no database; this is used only for the firewall rule that lets the deployed sites reach the providers\' server.')
param sqlServerName string = 'acme-sql-server'

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

@description('''
Extra browser origins allowed to call the API. The React Workflow Orchestration
app is served from its own origin, so without this its calls fail preflight.
''')
param allowedCorsOrigins array = []

// One site, one plan.
//
// The API was `oiie-sandbox-{env}` until 2026-09. That name was kept while other
// systems were believed to hold it, but no deployed app does: the only setting
// carrying a sandbox URL is the API's own Isbm__ListenerBaseUrl, which is derived
// from apiAppName below and therefore follows the rename. Every Azure resource
// this solution owns is now named acme-*-dev, so a resource that does not match
// is a resource that does not belong to it.
var apiAppName = 'acme-api-sandbox-${environmentName}'
var planName = 'acme-plan-sandbox-${environmentName}'
var insightsName = 'appi-acme-sandbox-${environmentName}'
var workspaceName = 'log-acme-sandbox-${environmentName}'

var apiUrl = 'https://${apiAppName}.azurewebsites.net'

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
//
// One site: the API, which owns /admin and /health and serves the TypeScript UI
// from its own wwwroot. The Blazor operator UI that used to sit beside it is
// gone, and with it the single-consumer rule that mattered when two hosts ran
// the same engine assembly.
//
// A dedicated plan, despite the naming standard's one-plan-per-environment rule.
// That rule assumes a single OS and the estate has two: acme-plan-dev is
// kind=functionapp (Windows) and hosts the Function Apps, while this is a Linux
// App Service. Azure rejects a Linux site on a Windows plan with "The parameter
// LinuxFxVersion has an invalid value", which names the site's own setting and
// says nothing about the plan it was pointed at.
//
// The plan is still named to the standard, so it reads as part of the estate
// rather than as the leftover it replaced.
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

// Settings both sites need. Held in one place because a value that drifts
// between them -- a different database, a different personality path -- produces
// two hosts that disagree about the world while both looking healthy.
var sharedAppSettings = [
  { name: 'ASPNETCORE_ENVIRONMENT', value: environmentName == 'demo' ? 'Production' : 'Staging' }
  { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: insights.properties.ConnectionString }

  { name: 'Sandbox__Environment', value: environmentName }

  // Deployed alongside the app rather than a level up, unlike the local
  // layout where the solution root is the parent.
  //
  // This must be PersonalityPacks, not Personalities. Personalities is C#
  // handler source; the deployed packs are PersonalityPacks/**/*.yaml,
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
]

resource apiApp 'Microsoft.Web/sites@2023-12-01' = {
  name: apiAppName
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

      // Stated rather than inferred. Zip deploy never deletes, so this site --
      // which used to host SimHost -- still has SimHost.dll beside the API's own
      // assembly, and the auto-detected entry point picked the wrong one. The
      // symptom was a 404 on every route from an app reporting a successful
      // deployment.
      appCommandLine: 'dotnet Oiie.Sandbox.Api.dll'

      // The inbox pump and outbox dispatcher are hosted services. Without Always On
      // the app is unloaded when idle and stops consuming, which looks exactly like
      // a provider that has stopped delivering. This is why the API is not a
      // Function App: the workload is a resident poll loop, not a burst of events.
      alwaysOn: true

      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      http20Enabled: true

      healthCheckPath: '/health/participants'

      // The React app calls this API from its own origin.
      //
      // Omitted entirely when no origins are configured. App Service rejects a
      // cors block whose allowedOrigins is empty -- it answers BadRequest 51016
      // "HTTP request body must not be empty" rather than treating it as "no
      // CORS", which is an unhelpful message for the actual mistake.
      cors: empty(allowedCorsOrigins) ? null : {
        allowedOrigins: allowedCorsOrigins
        supportCredentials: false
      }

      appSettings: concat(sharedAppSettings, [
        // Without this the admin endpoints -- reset, channel deletion, schema drop --
        // are callable by anyone who finds the URL.
        { name: 'Sandbox__AdminKey', value: adminKey }

        // Base URL for NotifyListener callbacks. Push delivery is not wired up yet,
        // but a deployed app is addressable, which a workstation is not -- this is
        // what makes it testable at all. Points at the API because the API is what
        // holds the ISBM sessions.
        { name: 'Isbm__ListenerBaseUrl', value: apiUrl }
      ])
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
resource apiKeyVaultGrant 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: keyVault
  name: guid(keyVault.id, apiApp.id, '4633458b-17de-408a-b874-0445c86b69e6')
  properties: {
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      '4633458b-17de-408a-b874-0445c86b69e6'
    )
    principalId: apiApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// Storage Blob Data Contributor
resource apiStorageGrant 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storage
  name: guid(storage.id, apiApp.id, 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
  properties: {
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
    )
    principalId: apiApp.identity.principalId
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

output apiAppName string = apiApp.name
output apiAppUrl string = 'https://${apiApp.properties.defaultHostName}'
output apiPrincipalId string = apiApp.identity.principalId

output insightsName string = insights.name
