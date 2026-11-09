targetScope = 'resourceGroup'

@description('Run in the explicitly selected existing Key Vault resource group with a role-assignment deployment identity.')
param keyVaultName string
param runtimePrincipalId string
param secretNames array = ['sa-pat', 'briefcase-client-id', 'briefcase-client-secret', 'state-key']
resource vault 'Microsoft.KeyVault/vaults@2023-07-01' existing = { name: keyVaultName }
resource secrets 'Microsoft.KeyVault/vaults/secrets@2023-07-01' existing = [for name in secretNames: {
  parent: vault
  name: name
}]
resource grants 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for (name, index) in secretNames: {
  name: guid(secrets[index].id, runtimePrincipalId, 'secret-reader')
  scope: secrets[index]
  properties: {
    principalId: runtimePrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
  }
}]
