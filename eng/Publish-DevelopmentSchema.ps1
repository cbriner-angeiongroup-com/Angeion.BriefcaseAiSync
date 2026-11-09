$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
$target = 'Server=tcp:caseworksdevsql001.database.windows.net,1433;Initial Catalog=BriefcaseSyncDev;Authentication=Active Directory Default;Encrypt=True;TrustServerCertificate=False;ConnectRetryCount=0;Connect Timeout=15;'
$artifactDirectory = Join-Path $repositoryRoot 'artifacts/development-deployment'
$dacpac = Join-Path $repositoryRoot 'database/BriefcaseSync.Database/bin/Release/BriefcaseSync.Database.dacpac'
function Invoke-Publisher([string]$Action, [string]$ReportPath = '') {
    $arguments = @("/Action:$Action", "/SourceFile:$dacpac", "/TargetConnectionString:$target", '/p:DropObjectsNotInSource=false', '/p:BlockOnPossibleDataLoss=true', '/p:DropPermissionsNotInSource=false', '/p:DropRoleMembersNotInSource=false')
    if ($ReportPath) { $arguments += "/OutputPath:$ReportPath" }
    $publisherOutput = & sqlpackage @arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        $details = $publisherOutput -join "`n"
        $code = if ($details -match 'Login failed') { 'SQL_LOGIN_NOT_AUTHORIZED' }
            elseif ($details -match 'Firewall|not allowed to access|Client with IP address') { 'SQL_NETWORK_ACCESS_BLOCKED' }
            elseif ($details -match 'permission') { 'SQL_PERMISSION_DENIED' }
            else { 'SQL_SCHEMA_DEPLOYMENT_FAILED' }
        throw $code
    }
}
function Read-Operations([string]$Path) {
    [xml]$report = Get-Content -Raw -LiteralPath $Path
    return @($report.SelectNodes("//*[local-name()='Operations']/*[local-name()='Operation']"))
}
Push-Location $repositoryRoot
try {
    New-Item -ItemType Directory -Path $artifactDirectory -Force | Out-Null
    & dotnet build database/BriefcaseSync.Database/BriefcaseSync.Database.sqlproj -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'SQL_PROJECT_BUILD_FAILED' }
    $before = Join-Path $artifactDirectory 'BriefcaseSyncDev-before.xml'
    Invoke-Publisher 'DeployReport' $before
    $operations = @(Read-Operations $before)
    if ($operations | Where-Object { $_.Name -ne 'Create' }) {
        throw 'NON_ADDITIVE_SCHEMA_CHANGE_REVIEW_REQUIRED'
    }
    foreach ($operation in $operations) {
        foreach ($item in $operation.Item) { Write-Host "$($operation.Name): $($item.Value)" }
    }
    if ($operations.Count -gt 0) { Invoke-Publisher 'Publish' }
    $after = Join-Path $artifactDirectory 'BriefcaseSyncDev-after.xml'
    Invoke-Publisher 'DeployReport' $after
    if (@(Read-Operations $after).Count -gt 0) { throw 'SCHEMA_VERIFICATION_HAS_PENDING_CHANGES' }
    @{ server = 'caseworksdevsql001'; database = 'BriefcaseSyncDev'; status = 'PublishedAndVerified'; pendingOperations = 0; dacpacSha256 = (Get-FileHash -Algorithm SHA256 $dacpac).Hash } |
        ConvertTo-Json | Set-Content (Join-Path $artifactDirectory 'BriefcaseSyncDev-publication.json') -Encoding utf8
    Write-Host 'SCHEMA_PUBLISHED_AND_VERIFIED: BriefcaseSyncDev; no pending DACPAC changes.'
} finally { Pop-Location }
