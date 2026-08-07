// Creates the CIR database on a pre-existing SQL logical server.
// Deployed as a module so the target can live in a different resource group.

param serverName string
param databaseName string

@description('Must match the existing server\'s region. Resolved by deploy.ps1 and passed in, because an existing resource\'s location is not known at the start of a deployment.')
param databaseLocation string
param skuName string = 'GP_S_Gen5'
param skuTier string = 'GeneralPurpose'
param skuFamily string = 'Gen5'
param skuCapacity int = 1
param autoPauseDelay int = 60
param maxSizeBytes int = 34359738368
param tags object = {}

resource server 'Microsoft.Sql/servers@2023-08-01-preview' existing = {
  name: serverName
}

resource database 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: server
  name: databaseName
  location: databaseLocation
  tags: tags
  sku: {
    name: skuName
    tier: skuTier
    family: skuFamily
    capacity: skuCapacity
  }
  properties: {
    collation: 'SQL_Latin1_General_CP1_CI_AS'
    autoPauseDelay: autoPauseDelay
    minCapacity: json('0.5')
    maxSizeBytes: maxSizeBytes
    zoneRedundant: false
  }
}

output databaseId string = database.id
output serverFqdn string = server.properties.fullyQualifiedDomainName
