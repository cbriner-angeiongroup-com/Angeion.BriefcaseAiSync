$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repositoryRoot 'src/BriefcaseSync.Web'
$secretPath = Join-Path $env:APPDATA 'Microsoft/UserSecrets/briefcase-sync-web/secrets.json'
$secrets = $null
$password = $null
$secretJson = $null
$loginSql = $null
try {
    if (-not (Test-Path -LiteralPath $secretPath)) { throw 'Configure SmartAdvocate development credentials first with eng/Configure-SmartAdvocateDevelopment.ps1.' }
    $secrets = Get-Content -Raw -LiteralPath $secretPath | ConvertFrom-Json
    if ($secrets.'SmartAdvocate:ApiUrl' -cne 'https://cwapps.n8protect.com/Development/VendorSmartAdvocate/' -or
        ([string]::IsNullOrWhiteSpace($secrets.'SmartAdvocate:PersonalAccessToken') -and
         ([string]::IsNullOrWhiteSpace($secrets.'SmartAdvocate:Username') -or [string]::IsNullOrWhiteSpace($secrets.'SmartAdvocate:Password')))) {
        throw 'Configure the expected SmartAdvocate development connection first.'
    }
    if ($secrets.Scope -and $secrets.Scope -cne 'local-smartadvocate-development-preview') {
        throw 'These web user secrets already belong to another scope. Preserve them before configuring a local preview workspace.'
    }
    # Developer setup is separate from runtime. Schema comes only from the SQL project.
    dotnet build (Join-Path $repositoryRoot 'database/BriefcaseSync.Database/BriefcaseSync.Database.sqlproj') -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'SQL project build failed.' }
    $dacpac = Join-Path $repositoryRoot 'database/BriefcaseSync.Database/bin/Release/BriefcaseSync.Database.dacpac'
    & sqlpackage /Action:Publish "/SourceFile:$dacpac" '/TargetConnectionString:Server=localhost;Initial Catalog=BriefcaseSync_LocalPreview;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;ConnectRetryCount=0;' /p:AllowIncompatiblePlatform=true /p:DropObjectsNotInSource=false 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Local SQL project publication failed. Verify local SQL Server and developer publication permissions.' }
    $key = $secrets.StateEncryptionKey
    if ([string]::IsNullOrWhiteSpace($key)) {
        $bytes = New-Object byte[] 32
        $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
        try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
        $key = [Convert]::ToBase64String($bytes)
    }
    if ([Convert]::FromBase64String($key).Length -ne 32) { throw 'The existing state key is invalid; its value was omitted.' }
    # Hex is safe in a SQL string literal. Credentials use stdin, never arguments or repository files.
    $password = [Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N') + '!aA1'
    $loginSql = @"
SET NOCOUNT ON;
USE [master];
IF SUSER_ID(N'BriefcaseSync_LocalPreviewWeb') IS NULL
    CREATE LOGIN [BriefcaseSync_LocalPreviewWeb] WITH PASSWORD=N'$password', CHECK_POLICY=ON, CHECK_EXPIRATION=OFF;
ELSE
    ALTER LOGIN [BriefcaseSync_LocalPreviewWeb] WITH PASSWORD=N'$password';
USE [BriefcaseSync_LocalPreview];
IF DATABASE_PRINCIPAL_ID(N'BriefcaseSync_LocalPreviewWeb') IS NULL
    CREATE USER [BriefcaseSync_LocalPreviewWeb] FOR LOGIN [BriefcaseSync_LocalPreviewWeb];
GRANT SELECT ON OBJECT::[sync].[Cases] TO [BriefcaseSync_LocalPreviewWeb];
GRANT SELECT ON OBJECT::[sync].[Runs] TO [BriefcaseSync_LocalPreviewWeb];
GRANT SELECT, INSERT, UPDATE ON OBJECT::[sync].[Configuration] TO [BriefcaseSync_LocalPreviewWeb];
GRANT INSERT ON OBJECT::[sync].[AccessAudit] TO [BriefcaseSync_LocalPreviewWeb];
"@
    $loginSql | & sqlcmd -S localhost -E -C -b 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Local restricted SQL login setup failed; diagnostic contents were omitted.' }
    $secretJson = @{
        'Scope' = 'local-smartadvocate-development-preview'
        'SqlConnectionString' = "Server=localhost;Initial Catalog=BriefcaseSync_LocalPreview;User ID=BriefcaseSync_LocalPreviewWeb;Password=$password;Encrypt=True;TrustServerCertificate=True;ConnectRetryCount=0;"
        'StateEncryptionKey' = $key
        'SendingEnabled' = 'false'
        'MappingApproved' = 'false'
    } | ConvertTo-Json
    $secretJson | dotnet user-secrets set --project $project 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Saving local preview user secrets failed.' }
    Write-Host 'Local preview configured: SQL project published locally; restricted runtime login; sending and approval disabled.'
    Write-Host 'Run: dotnet run --project src/BriefcaseSync.Web --launch-profile local-preview'
} finally {
    $secrets = $null; $password = $null; $secretJson = $null; $loginSql = $null; $key = $null
}
