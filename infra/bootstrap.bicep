targetScope = 'resourceGroup'

param location string = resourceGroup().location
@allowed(['development', 'qa', 'production'])
param environment string
param namePrefix string = 'briefcase-sync'
resource runtime 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${namePrefix}-${environment}-runtime'
  location: location
}
output runtimePrincipalId string = runtime.properties.principalId
output runtimeClientId string = runtime.properties.clientId
output runtimeIdentityResourceId string = runtime.id
resource web 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${namePrefix}-${environment}-web'
  location: location
}
output webPrincipalId string = web.properties.principalId
output webClientId string = web.properties.clientId
output webIdentityResourceId string = web.id
