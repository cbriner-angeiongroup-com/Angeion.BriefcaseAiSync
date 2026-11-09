using './main.bicep'

param environment = 'development'
param image = 'REPLACE_WITH_REGISTRY/briefcase-sync@sha256:REPLACE_WITH_DIGEST'
param triggerType = 'Manual'
param schedule = '* * * * *'
param syncScope = 'REPLACE_WITH_SOURCE_INSTANCE_AND_DESTINATION_TENANT'
param smartAdvocateApiUrl = 'https://REPLACE_WITH_SA_HOST/CaseSyncAPI/'
param eligibleStatusId = 0 // Verify this environment's main status ID. Production uses 32409; also check saved managed settings.
param sqlConnectionString = 'Server=tcp:REPLACE_WITH_SQL_SERVER.database.windows.net,1433;Database=briefcase-sync;Authentication=Active Directory Managed Identity;User Id=REPLACE_WITH_RUNTIME_CLIENT_ID;Encrypt=True;TrustServerCertificate=False;ConnectRetryCount=0;'
param secretUrls = {
  'sa-pat': 'https://REPLACE_WITH_VAULT.vault.azure.net/secrets/sa-pat'
  'briefcase-client-id': 'https://REPLACE_WITH_VAULT.vault.azure.net/secrets/briefcase-client-id'
  'briefcase-client-secret': 'https://REPLACE_WITH_VAULT.vault.azure.net/secrets/briefcase-client-secret'
  'state-key': 'https://REPLACE_WITH_VAULT.vault.azure.net/secrets/state-key'
}
