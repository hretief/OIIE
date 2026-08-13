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

@description('''
Extra browser origins allowed to call the API. The React Workflow Orchestration
app is served from its own origin, so without this its calls fail preflight.
''')
param allowedCorsOrigins array = []

// Two sites, one plan.
//
// The API keeps the historic `oiie-sandbox-{env}` name deliberately: it is the
// value already baked into Isbm__ListenerBaseUrl, the CIR's configuration and
// anything else holding a sandbox URL. Renaming it would break those silently,
// whereas the Blazor UI is new as a separate address and can take a new name.
var apiAppName = 'oiie-sandbox-${environmentName}'
var uiAppName = 'oiie-simhost-${environmentName}'
var planName = 'plan-oiie-sandbox-${environmentName}'
var insightsName = 'appi-oiie-sandbox-${environmentName}'
var workspaceName = 'log-oiie-sandbox-${environmentName}'

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
// Both sites run the same engine assembly and both reach the same database and
// blob container, so everything below the host boundary is identical. What
// differs is what each one is allowed to do:
//
//   API  -- owns /admin and /health, runs the inbox pump and outbox dispatcher.
//   UI   -- Blazor Server only. Runs NO pumps.
//
// That single-consumer rule is enforced in code, not here: SimHost/Program.cs
// calls AddSandboxCore but never AddSandboxMessagePumps. If both hosts pumped,
// two consumers would race the same ISBM sessions and messages would appear to
// vanish at random. Do not "helpfully" add the pumps to the UI.
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
  { name: 'Sandbox__SqlServer', value: sqlServerName }
  { name: 'Sandbox__Database', value: sqlDatabaseName }

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

resource uiApp 'Microsoft.Web/sites@2023-12-01' = {
  name: uiAppName
  location: location
  kind: 'app,linux'
  identity: {
    // The UI reads participant tables and payload blobs directly through Core,
    // so it needs the same data-plane identity as the API. It is a separate
    // principal: two sites cannot share one system-assigned identity.
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'

      // Explicit for the same reason as the API: the entry assembly should not
      // depend on what a previous deployment happened to leave behind.
      appCommandLine: 'dotnet SimHost.dll'

      // No pumps here, so an idle unload costs a slow first request rather than
      // silently stopping consumption. Left on regardless because a cold Blazor
      // start is a visibly broken-looking page.
      alwaysOn: true

      // Blazor Server is a SignalR circuit.
      webSocketsEnabled: true

      // Sticky sessions: a circuit is bound to the instance that created it.
      // Harmless on one instance, essential the moment there are two.
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      http20Enabled: true

      // Deliberately NOT /health/participants -- that route lives in the API now.
      // A health probe against a path this site does not serve would fail every
      // check and take the app out of rotation permanently.
      healthCheckPath: '/'

      appSettings: concat(sharedAppSettings, [
        // The UI serves no /admin routes of its own. Its reset and scenario-launch
        // buttons post to the API, and SandboxApiEndpoint reads this to find it.
        // Without it the UI falls back to its own base address and those actions
        // 404 against itself.
        { name: 'Sandbox__ApiBaseUrl', value: apiUrl }

        // Needed to authenticate those same calls: the API's admin guard applies
        // regardless of which host is calling.
        { name: 'Sandbox__AdminKey', value: adminKey }
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
//
// Granted to BOTH sites. The UI is not a thin client -- it reads participant
// tables and payload blobs through the same Core services the API uses, so it
// needs the same access. Each site has its own system-assigned principal.
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

resource uiKeyVaultGrant 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: keyVault
  name: guid(keyVault.id, uiApp.id, '4633458b-17de-408a-b874-0445c86b69e6')
  properties: {
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      '4633458b-17de-408a-b874-0445c86b69e6'
    )
    principalId: uiApp.identity.principalId
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

resource uiStorageGrant 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storage
  name: guid(storage.id, uiApp.id, 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
  properties: {
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
    )
    principalId: uiApp.identity.principalId
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

output uiAppName string = uiApp.name
output uiAppUrl string = 'https://${uiApp.properties.defaultHostName}'
output uiPrincipalId string = uiApp.identity.principalId

output insightsName string = insights.name
