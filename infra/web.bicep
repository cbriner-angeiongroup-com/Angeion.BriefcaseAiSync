targetScope = 'resourceGroup'
param location string = resourceGroup().location
@allowed(['development', 'qa', 'production'])
param environment string
param namePrefix string = 'briefcase-sync'
param image string
param containerAppsEnvironmentId string
param storageAccountName string
param syncScope string
param sqlConnectionString string
param smartAdvocateApiUrl string
@allowed(['Pat', 'UsernamePassword'])
param sourceAuthenticationMode string = 'Pat'
param entraTenantId string
param entraClientId string
@description('Semicolon-delimited allowed application hosts. Must match the actual ingress/custom host.')
param allowedHosts string
@description('Versionless HTTPS Key Vault key URI. Grant this web identity wrapKey/unwrapKey access separately.')
param dataProtectionKeyUri string
@description('Key Vault URLs: sa-pat or sa-username/sa-password, plus state-key and entra-client-secret. No destination-write credentials are provided to the UI.')
param secretUrls object
param registryServer string = ''
@description('Explicit trusted reverse proxy addresses for this environment, validated during ingress acceptance.')
param knownProxies array = []
@description('Explicit trusted ingress CIDR ranges, where the platform supplies subnet-scoped proxy addresses.')
param knownProxyNetworks array = []

var appName = '${namePrefix}-${environment}-web'
var sourceSecretNames = sourceAuthenticationMode == 'Pat' ? ['sa-pat'] : ['sa-username', 'sa-password']
var sourceAuthenticationEnvironment = sourceAuthenticationMode == 'Pat'
  ? [{ name: 'BRIEFCASE_SmartAdvocate__PersonalAccessToken', secretRef: 'sa-pat' }]
  : [{ name: 'BRIEFCASE_SmartAdvocate__Username', secretRef: 'sa-username' }, { name: 'BRIEFCASE_SmartAdvocate__Password', secretRef: 'sa-password' }]
var proxySettings = [for (proxy, index) in knownProxies: { name: 'BRIEFCASE_Web__KnownProxies__${index}', value: proxy }]
var proxyNetworkSettings = [for (network, index) in knownProxyNetworks: { name: 'BRIEFCASE_Web__KnownProxyNetworks__${index}', value: network }]
resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = { name: appName }
resource storage 'Microsoft.Storage/storageAccounts@2025-01-01' existing = { name: storageAccountName }
resource blobs 'Microsoft.Storage/storageAccounts/blobServices@2025-01-01' existing = { parent: storage, name: 'default' }
resource keys 'Microsoft.Storage/storageAccounts/blobServices/containers@2025-01-01' = {
  parent: blobs
  name: 'web-keys'
  properties: { publicAccess: 'None' }
}
resource access 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keys.id, identity.id, 'web-keys')
  scope: keys
  properties: {
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
  }
}
resource web 'Microsoft.App/containerApps@2025-07-01' = {
  name: appName
  location: location
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${identity.id}': {} } }
  properties: {
    managedEnvironmentId: containerAppsEnvironmentId
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: { external: true, targetPort: 8080, transport: 'auto', allowInsecure: false }
      registries: empty(registryServer) ? [] : [{ server: registryServer, identity: identity.id }]
      secrets: [for name in concat(sourceSecretNames, ['state-key', 'entra-client-secret']): { name: name, keyVaultUrl: secretUrls[name], identity: identity.id }]
    }
    template: {
      scale: { minReplicas: 1, maxReplicas: 1 }
      containers: [{
        name: 'web'
        image: image
        resources: { cpu: json('0.5'), memory: '1Gi' }
        env: concat([
          { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
          { name: 'AllowedHosts', value: allowedHosts }
          { name: 'BRIEFCASE_Web__DemoMode', value: 'false' }
          { name: 'BRIEFCASE_Scope', value: syncScope }
          { name: 'BRIEFCASE_ManagedConfigurationEnabled', value: 'true' }
          { name: 'BRIEFCASE_SqlConnectionString', value: sqlConnectionString }
          { name: 'BRIEFCASE_StateEncryptionKey', secretRef: 'state-key' }
          { name: 'BRIEFCASE_ManagedIdentityClientId', value: identity.properties.clientId }
          { name: 'AZURE_CLIENT_ID', value: identity.properties.clientId }
          { name: 'BRIEFCASE_SmartAdvocate__ApiUrl', value: smartAdvocateApiUrl }
          { name: 'BRIEFCASE_Entra__TenantId', value: entraTenantId }
          { name: 'BRIEFCASE_Entra__ClientId', value: entraClientId }
          { name: 'BRIEFCASE_Entra__ClientSecret', secretRef: 'entra-client-secret' }
          { name: 'BRIEFCASE_Web__DataProtectionBlobUri', value: '${storage.properties.primaryEndpoints.blob}web-keys/keyring.xml' }
          { name: 'BRIEFCASE_Web__DataProtectionKeyUri', value: dataProtectionKeyUri }
        ], sourceAuthenticationEnvironment, proxySettings, proxyNetworkSettings)
        probes: [{ type: 'Liveness', httpGet: { path: '/health/live', port: 8080 }, initialDelaySeconds: 15, periodSeconds: 30 }]
      }]
    }
  }
}
output url string = 'https://${web.properties.configuration.ingress.fqdn}'
output webPrincipalId string = identity.properties.principalId
