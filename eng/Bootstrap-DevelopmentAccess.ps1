# Run as an authorized subscription administrator. This does not alter shared SQL/network administrators.
$ErrorActionPreference = 'Stop'
$subscription = 'f180d56c-6007-4ca5-ad78-bf0765f5d6eb'
$group = 'briefcase-sync-development'
$operatorId = '25474c81-1ef9-41c1-8f06-32155e7f9bce'
$deploymentId = 'b615e122-2b22-4b38-9353-f54eb445a037'
$currentSubscription = & az account show --query id -o tsv
if ($LASTEXITCODE -ne 0 -or $currentSubscription -ne $subscription) { throw 'Select the configured development subscription first.' }
& az group create --name $group --location southcentralus --tags application=briefcase-sync environment=development --only-show-errors --output none
if ($LASTEXITCODE -ne 0) { throw 'Development resource-group creation requires subscription-level authorization.' }
$scope = "/subscriptions/$subscription/resourceGroups/$group"
foreach ($principal in @(@{id=$operatorId;type='User'},@{id=$deploymentId;type='ServicePrincipal'})) {
    foreach ($role in @('Contributor','User Access Administrator')) {
        & az role assignment create --assignee-object-id $principal.id --assignee-principal-type $principal.type --role $role --scope $scope --only-show-errors --output none
        if ($LASTEXITCODE -ne 0) { throw 'App-scoped deployment access could not be granted.' }
    }
}
Write-Host 'Development group and app-scoped deployment access configured. SQL publication rights remain a separate DBA action.'
