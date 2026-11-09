targetScope = 'resourceGroup'

@description('Run only in the selected shared development resource group. Creates app-scoped access grants, not shared-parent configuration changes.')
param registryName string = 'caseworksdevacr000'
param keyVaultName string = 'angdevkeyvault'
param workerPrincipalId string
param webPrincipalId string
param deploymentPrincipalId string = 'b615e122-2b22-4b38-9353-f54eb445a037'
param deploymentPushAssignmentName string = '3a9bd8c5-ab53-459a-bd98-c641b73026e2'
param objectNamePrefix string = 'briefcase-sync-development-'

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = { name: registryName }
resource vault 'Microsoft.KeyVault/vaults@2023-07-01' existing = { name: keyVaultName }
resource workerPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(registry.id, workerPrincipalId, 'AcrPull')
  scope: registry
  properties: {
    principalId: workerPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
  }
}
resource webPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(registry.id, webPrincipalId, 'AcrPull')
  scope: registry
  properties: {
    principalId: webPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
  }
}
// Reuse the grant created during development preparation; never duplicate a principal/scope/role grant.
resource deploymentPush 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: deploymentPushAssignmentName
  scope: registry
  properties: {
    principalId: deploymentPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '8311e382-0749-4cb8-b61a-304f252e45ec')
  }
}
module workerSecrets './keyvault-access.bicep' = {
  name: 'briefcase-sync-development-worker-secrets'
  params: {
    keyVaultName: keyVaultName
    runtimePrincipalId: workerPrincipalId
    secretNames: [for name in ['sa-username', 'sa-password', 'briefcase-client-id', 'briefcase-client-secret', 'state-key']: '${objectNamePrefix}${name}']
  }
}
module webSecrets './keyvault-access.bicep' = {
  name: 'briefcase-sync-development-web-secrets'
  params: {
    keyVaultName: keyVaultName
    runtimePrincipalId: webPrincipalId
    secretNames: [for name in ['sa-username', 'sa-password', 'state-key', 'entra-client-secret']: '${objectNamePrefix}${name}']
  }
}
resource protectionKey 'Microsoft.KeyVault/vaults/keys@2023-07-01' existing = {
  parent: vault
  name: '${objectNamePrefix}web-protection'
}
resource webKeyAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(protectionKey.id, webPrincipalId, 'web-protection')
  scope: protectionKey
  properties: {
    principalId: webPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'e147488a-f6f5-4113-8e2d-b22465e65bf6')
  }
}
