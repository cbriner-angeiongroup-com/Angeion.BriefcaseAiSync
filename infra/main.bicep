targetScope = 'resourceGroup'

@description('Deploy in the dedicated briefcase-sync resource group for this environment.')
param location string = resourceGroup().location
@allowed(['development', 'qa', 'production'])
param environment string
param namePrefix string = 'briefcase-sync'
@description('Promote an immutable registry image digest, e.g. registry.azurecr.io/briefcase-sync@sha256:...')
param image string
@description('Azure wake-up pulse. Managed application schedules are stored in SQL; keep this every minute.')
param schedule string = '* * * * *'
@description('Use Manual while provisioning and validating. Switch to Schedule after acceptance.')
@allowed(['Manual', 'Schedule'])
param triggerType string = 'Manual'
@description('Stable source-instance/destination-tenant binding. Never change to bypass existing state.')
param syncScope string
param smartAdvocateApiUrl string
@allowed(['Pat', 'UsernamePassword'])
param sourceAuthenticationMode string = 'Pat'
@minValue(0)
param eligibleStatusId int = 0
param briefcaseApiUrl string = 'https://fbtrial.my.salesforce.com/'
@description('Managed identity SQL connection string. Database and contained user are deployed separately.')
param sqlConnectionString string
@description('Existing Key Vault URLs: sa-pat or sa-username/sa-password, plus briefcase-client-id, briefcase-client-secret and state-key. Access is separately granted to the runtime identity.')
param secretUrls object
param sendingEnabled bool = false
param mappingApproved bool = false
param retainersEnabled bool = false
param retainerCategoryId int = 0
param retainerSubcategoryId int = 0
@description('Optional existing Container Apps environment with approved network connectivity.')
param existingEnvironmentId string = ''
@description('Optional private ACR server. Separately grant AcrPull to the runtime identity.')
param registryServer string = ''

var suffix = uniqueString(resourceGroup().id)
var appName = '${namePrefix}-${environment}'
var sourceSecretNames = sourceAuthenticationMode == 'Pat' ? ['sa-pat'] : ['sa-username', 'sa-password']
var sourceAuthenticationEnvironment = sourceAuthenticationMode == 'Pat'
  ? [{ name: 'BRIEFCASE_SmartAdvocate__PersonalAccessToken', secretRef: 'sa-pat' }]
  : [{ name: 'BRIEFCASE_SmartAdvocate__Username', secretRef: 'sa-username' }, { name: 'BRIEFCASE_SmartAdvocate__Password', secretRef: 'sa-password' }]
var identityMap = { '${runtime.id}': {} }

resource runtime 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${appName}-runtime'
  location: location
}

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${appName}-logs'
  location: location
  properties: { sku: { name: 'PerGB2018' }, retentionInDays: 30 }
}

resource host 'Microsoft.App/managedEnvironments@2025-07-01' = if (empty(existingEnvironmentId)) {
  name: '${appName}-env'
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: { customerId: logs.properties.customerId, sharedKey: logs.listKeys().primarySharedKey }
    }
  }
}

resource storage 'Microsoft.Storage/storageAccounts@2025-01-01' = {
  name: 'bcs${suffix}'
  location: location
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
  properties: {
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
  }
}
resource blobs 'Microsoft.Storage/storageAccounts/blobServices@2025-01-01' = {
  parent: storage
  name: 'default'
  properties: { deleteRetentionPolicy: { enabled: true, days: 14 }, isVersioningEnabled: true }
}
resource assets 'Microsoft.Storage/storageAccounts/blobServices/containers@2025-01-01' = {
  parent: blobs
  name: 'retainer-assets'
  properties: { publicAccess: 'None' }
}
resource blobAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(assets.id, runtime.id, 'blob-contributor')
  scope: assets
  properties: {
    principalId: runtime.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
  }
}

resource job 'Microsoft.App/jobs@2025-07-01' = {
  name: appName
  location: location
  identity: { type: 'UserAssigned', userAssignedIdentities: identityMap }
  properties: {
    environmentId: empty(existingEnvironmentId) ? host!.id : existingEnvironmentId
    configuration: {
      triggerType: triggerType
      replicaTimeout: 3600
      replicaRetryLimit: 0
      manualTriggerConfig: triggerType == 'Manual' ? { parallelism: 1, replicaCompletionCount: 1 } : null
      scheduleTriggerConfig: triggerType == 'Schedule' ? { cronExpression: schedule, parallelism: 1, replicaCompletionCount: 1 } : null
      registries: empty(registryServer) ? [] : [{ server: registryServer, identity: runtime.id }]
      secrets: [for name in concat(sourceSecretNames, ['briefcase-client-id', 'briefcase-client-secret', 'state-key']): {
        name: name
        keyVaultUrl: secretUrls[name]
        identity: runtime.id
      }]
    }
    template: {
      containers: [{
        name: 'sync'
        image: image
        resources: { cpu: json('0.5'), memory: '1Gi' }
        env: concat([
          { name: 'BRIEFCASE_Scope', value: syncScope }
          { name: 'BRIEFCASE_ManagedConfigurationEnabled', value: 'true' }
          { name: 'BRIEFCASE_SqlConnectionString', value: sqlConnectionString }
          { name: 'BRIEFCASE_StateEncryptionKey', secretRef: 'state-key' }
          { name: 'BRIEFCASE_BlobServiceUri', value: storage.properties.primaryEndpoints.blob }
          { name: 'BRIEFCASE_ManagedIdentityClientId', value: runtime.properties.clientId }
          { name: 'AZURE_CLIENT_ID', value: runtime.properties.clientId }
          { name: 'BRIEFCASE_SmartAdvocate__ApiUrl', value: smartAdvocateApiUrl }
          { name: 'BRIEFCASE_SmartAdvocate__EligibleStatusId', value: string(eligibleStatusId) }
          { name: 'BRIEFCASE_SmartAdvocate__RetainersEnabled', value: string(retainersEnabled) }
          { name: 'BRIEFCASE_SmartAdvocate__RetainerCategoryId', value: string(retainerCategoryId) }
          { name: 'BRIEFCASE_SmartAdvocate__RetainerSubcategoryId', value: string(retainerSubcategoryId) }
          { name: 'BRIEFCASE_Briefcase__ApiUrl', value: briefcaseApiUrl }
          { name: 'BRIEFCASE_Briefcase__ClientId', secretRef: 'briefcase-client-id' }
          { name: 'BRIEFCASE_Briefcase__ClientSecret', secretRef: 'briefcase-client-secret' }
          { name: 'BRIEFCASE_SendingEnabled', value: string(sendingEnabled) }
          { name: 'BRIEFCASE_MappingApproved', value: string(mappingApproved) }
        ], sourceAuthenticationEnvironment)
      }]
    }
  }
}

output jobName string = job.name
output runtimePrincipalId string = runtime.properties.principalId
output runtimeClientId string = runtime.properties.clientId
output runtimeIdentityResourceId string = runtime.id
output blobServiceUri string = storage.properties.primaryEndpoints.blob
output logWorkspaceId string = logs.id
output storageAccountName string = storage.name
output containerAppsEnvironmentId string = empty(existingEnvironmentId) ? host!.id : existingEnvironmentId
