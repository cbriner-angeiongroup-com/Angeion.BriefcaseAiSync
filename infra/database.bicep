targetScope = 'resourceGroup'

@description('Deploy into the explicitly selected shared SQL server resource group. This template owns only the application database.')
param sqlServerName string
param databaseName string = 'briefcase-sync'
param location string = resourceGroup().location
resource server 'Microsoft.Sql/servers@2023-08-01' existing = { name: sqlServerName }
resource database 'Microsoft.Sql/servers/databases@2023-08-01' = {
  parent: server
  name: databaseName
  location: location
  sku: { name: 'S0', tier: 'Standard' }
  properties: { collation: 'SQL_Latin1_General_CP1_CI_AS' }
}
output databaseId string = database.id
