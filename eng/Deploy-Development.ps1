param(
    [string]$WorkerImage = '',
    [string]$WebImage = '',
    [switch]$ConfigureSharedAccess,
    [switch]$ConfigureWebRedirect,
    [string[]]$KnownProxyNetworks = @()
)
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
$settings = Get-Content -Raw (Join-Path $repositoryRoot 'infra/development.settings.json') | ConvertFrom-Json
if (!$WorkerImage) { $WorkerImage = $settings.workerImage }
if (!$WebImage) { $WebImage = $settings.webImage }
foreach ($image in @($WorkerImage, $WebImage)) {
    if ($image -notmatch '^caseworksdevacr000\.azurecr\.io/briefcase-sync/(worker|web)@sha256:[a-f0-9]{64}$') {
        throw 'Use an immutable image digest from the selected development registry.'
    }
}
function Invoke-AzJson([string[]]$Arguments) {
    $output = & az @Arguments --only-show-errors -o json 2>$null
    if ($LASTEXITCODE -ne 0) { throw 'Azure operation failed; inspect deployment status with an authorized identity.' }
    return ($output -join "`n") | ConvertFrom-Json
}
function Deploy([string]$Name, [string]$Group, [string]$Template, [hashtable]$Parameters) {
    $parameterFile = Join-Path $repositoryRoot "artifacts/development-deployment/$Name.parameters.json"
    $wrapped = @{}; foreach ($key in $Parameters.Keys) { $wrapped[$key] = @{ value = $Parameters[$key] } }
    @{ parameters = $wrapped } | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $parameterFile -Encoding utf8
    $base = @('--resource-group', $Group, '--template-file', (Join-Path $repositoryRoot $Template), '--parameters', "@$parameterFile")
    $plan = Invoke-AzJson (@('deployment', 'group', 'what-if') + $base + @('--no-pretty-print'))
    $changes = @($plan.changes | Where-Object { $_.changeType -notin @('Ignore', 'NoChange') })
    if ($changes | Where-Object { $_.changeType -eq 'Delete' }) { throw 'Deployment preview contains a deletion; review it separately.' }
    if ($Group -eq $settings.sharedResourceGroup -and ($changes | Where-Object { $_.changeType -ne 'Create' })) {
        throw 'Shared-resource preview contains a change to an existing object; review it separately.'
    }
    $changes | Select-Object changeType,resourceId | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $repositoryRoot "artifacts/development-deployment/$Name.whatif.json")
    $deployment = Invoke-AzJson (@('deployment', 'group', 'create', '--name', $Name) + $base)
    if ($deployment.properties.provisioningState -ne 'Succeeded') { throw 'Azure deployment did not succeed.' }
    Write-Host "$Name deployed."
    return $deployment.properties.outputs
}
Push-Location $repositoryRoot
try {
    $account = Invoke-AzJson @('account', 'show')
    if ($account.id -ne $settings.subscriptionId) { throw 'Select the configured development subscription first.' }
    # Group creation requires subscription permission and is a separate administrator bootstrap step.
    $null = Invoke-AzJson @('group', 'show', '--name', $settings.resourceGroup)
    New-Item -ItemType Directory -Path artifacts/development-deployment -Force | Out-Null
    $identities = Deploy 'briefcase-sync-development-identities' $settings.resourceGroup 'infra/bootstrap.bicep' @{
        environment = 'development'; location = $settings.location
    }
    if ($ConfigureSharedAccess) {
        # One-time shared-scope bootstrap by the existing development administrator.
        # The federated deployment identity is deliberately not a shared-group access administrator.
        $null = Deploy 'briefcase-sync-development-access' $settings.sharedResourceGroup 'infra/development-access.bicep' @{
            workerPrincipalId = $identities.runtimePrincipalId.value; webPrincipalId = $identities.webPrincipalId.value
        }
    }
    # The SQL project is the sole schema publisher. CLI authentication never appears in process arguments.
    & dotnet build database/BriefcaseSync.Database/BriefcaseSync.Database.sqlproj -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'SQL project build failed.' }
    $connection = "Server=tcp:$($settings.sqlServer).database.windows.net,1433;Initial Catalog=$($settings.database);Authentication=Active Directory Default;Encrypt=True;TrustServerCertificate=False;ConnectRetryCount=0;Connect Timeout=15;"
    $dacpac = 'database/BriefcaseSync.Database/bin/Release/BriefcaseSync.Database.dacpac'
    $null = & sqlpackage /Action:DeployReport "/SourceFile:$dacpac" "/TargetConnectionString:$connection" /OutputPath:artifacts/development-deployment/database-report.xml /p:DropObjectsNotInSource=false 2>&1
    if ($LASTEXITCODE -ne 0) { throw 'SQL deploy-report access blocked. An authorized SQL publisher must complete this step.' }
    $null = & sqlpackage /Action:Publish "/SourceFile:$dacpac" "/TargetConnectionString:$connection" /p:DropObjectsNotInSource=false /p:BlockOnPossibleDataLoss=true 2>&1
    if ($LASTEXITCODE -ne 0) { throw 'SQL schema publication blocked.' }
    & dotnet run --project eng/AuthorizeDevelopmentSql -c Release -- --apply
    if ($LASTEXITCODE -ne 0) { throw 'SQL runtime user grants blocked. Use an authorized SQL administrator.' }
    $secretUrls = @{}
    foreach ($name in @('sa-username', 'sa-password', 'briefcase-client-id', 'briefcase-client-secret', 'state-key', 'entra-client-secret')) {
        $secretUrls[$name] = "https://$($settings.vault).vault.azure.net/secrets/briefcase-sync-development-$name"
    }
    $runtimeConnection = "Server=tcp:$($settings.sqlServer).database.windows.net,1433;Database=$($settings.database);Authentication=Active Directory Managed Identity;User Id=$($identities.runtimeClientId.value);Encrypt=True;TrustServerCertificate=False;ConnectRetryCount=0;"
    $worker = Deploy 'briefcase-sync-development-worker' $settings.resourceGroup 'infra/main.bicep' @{
        environment = 'development'; location = $settings.location; image = $WorkerImage
        syncScope = $settings.syncScope; smartAdvocateApiUrl = $settings.sourceApiUrl
        sourceAuthenticationMode = 'UsernamePassword'; briefcaseApiUrl = $settings.destinationApiUrl
        sqlConnectionString = $runtimeConnection; secretUrls = $secretUrls
        sendingEnabled = $false; mappingApproved = $false; retainersEnabled = $false
        eligibleStatusId = 0; registryServer = "$($settings.registry).azurecr.io"; triggerType = 'Manual'
    }
    $environment = Invoke-AzJson @('containerapp', 'env', 'show', '--ids', $worker.containerAppsEnvironmentId.value)
    $hostName = "briefcase-sync-development-web.$($environment.properties.defaultDomain)"
    if ($ConfigureWebRedirect) {
        # One-time interactive directory setup; the federated Azure deployment identity has no directory write grants.
        $null = Invoke-AzJson @('ad', 'app', 'update', '--id', $settings.webClientId, '--web-redirect-uris', "https://$hostName/signin-oidc", '--web-home-page-url', "https://$hostName/")
    }
    if ($KnownProxyNetworks.Count -eq 0) { throw 'Worker deployed. Supply approved ingress proxy CIDRs using -KnownProxyNetworks before deploying the web host.' }
    $webConnection = $runtimeConnection.Replace($identities.runtimeClientId.value, $identities.webClientId.value)
    $web = Deploy 'briefcase-sync-development-web' $settings.resourceGroup 'infra/web.bicep' @{
        environment = 'development'; location = $settings.location; image = $WebImage
        containerAppsEnvironmentId = $worker.containerAppsEnvironmentId.value; storageAccountName = $worker.storageAccountName.value
        syncScope = $settings.syncScope; sqlConnectionString = $webConnection; smartAdvocateApiUrl = $settings.sourceApiUrl
        sourceAuthenticationMode = 'UsernamePassword'; entraTenantId = $settings.tenantId; entraClientId = $settings.webClientId
        allowedHosts = $hostName; dataProtectionKeyUri = "https://$($settings.vault).vault.azure.net/keys/briefcase-sync-development-web-protection"
        secretUrls = $secretUrls; registryServer = "$($settings.registry).azurecr.io"; knownProxyNetworks = $KnownProxyNetworks
    }
    $null = Deploy 'briefcase-sync-development-monitoring' $settings.resourceGroup 'infra/monitoring.bicep' @{
        location = $settings.location; workspaceId = $worker.logWorkspaceId.value; alertsEnabled = $false
    }
    $health = Invoke-WebRequest "$($web.url.value)/health/live" -UseBasicParsing
    if ($health.StatusCode -ne 200) { throw 'Web liveness validation failed.' }
    Write-Host "Development hosts deployed: $($web.url.value). Worker remains Manual, sending-disabled and unapproved. Ingress sign-in/proxy acceptance and alert notification setup remain separate."
} finally { Pop-Location }
