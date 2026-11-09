targetScope = 'resourceGroup'
param location string = resourceGroup().location
param workspaceId string
param jobName string = 'briefcase-sync-development'
@description('Leave empty during initial provisioning. Notifications and rules remain disabled until an operator address and log acceptance are provided.')
param alertEmail string = ''
param alertsEnabled bool = false

resource actions 'Microsoft.Insights/actionGroups@2023-01-01' = {
  name: '${jobName}-operators'
  location: 'global'
  properties: {
    groupShortName: 'bcs-dev'
    enabled: !empty(alertEmail)
    emailReceivers: empty(alertEmail) ? [] : [{ name: 'operator', emailAddress: alertEmail, useCommonAlertSchema: true }]
  }
}
resource failureAlert 'Microsoft.Insights/scheduledQueryRules@2023-12-01' = {
  name: '${jobName}-failures'
  location: location
  kind: 'LogAlert'
  properties: {
    displayName: 'Briefcase development sync failures'
    description: 'Fixed worker events and aggregate outcomes only. Validate logs and notification delivery before enabling.'
    enabled: alertsEnabled && !empty(alertEmail)
    severity: 2
    evaluationFrequency: 'PT5M'
    windowSize: 'PT15M'
    scopes: [workspaceId]
    skipQueryValidation: true
    criteria: {
      allOf: [{
        query: replace('''
ContainerAppConsoleLogs_CL
| where ContainerJobName_s == '__JOB__'
| extend outcome = parse_json(Log_s)
| where tostring(outcome.event) in ('sync_failed', 'sync_stopped') or (tostring(outcome.event) == 'sync_finished' and (toint(outcome.failed) > 0 or toint(outcome.attention) > 0))
''', '__JOB__', jobName)
        timeAggregation: 'Count'
        operator: 'GreaterThan'
        threshold: 0
        failingPeriods: { numberOfEvaluationPeriods: 1, minFailingPeriodsToAlert: 1 }
      }]
    }
    actions: { actionGroups: [actions.id] }
    autoMitigate: true
  }
}
