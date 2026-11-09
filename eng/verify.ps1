param([switch]$SqlIntegration)
$ErrorActionPreference = 'Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    & ./eng/Verify-DesignSystem.ps1
    if ($SqlIntegration -and [string]::IsNullOrWhiteSpace($env:BRIEFCASE_TEST_SQL_MASTER)) {
        throw 'Set BRIEFCASE_TEST_SQL_MASTER to an isolated development SQL server with create-database rights.'
    }
    Push-Location src/BriefcaseSync.Web
    try {
        npm ci
        if ($LASTEXITCODE -ne 0) { throw 'UI dependency restore failed.' }
        npm run build:css
        if ($LASTEXITCODE -ne 0) { throw 'Tailwind build failed.' }
    } finally { Pop-Location }
    dotnet restore BriefcaseSync.sln --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
    dotnet test BriefcaseSync.sln -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    dotnet build database/BriefcaseSync.Database/BriefcaseSync.Database.sqlproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Database build failed.' }
    New-Item -ItemType Directory -Path artifacts -Force | Out-Null
    az bicep build --file infra/main.bicep --outfile artifacts/main.json
    if ($LASTEXITCODE -ne 0) { throw 'Application infrastructure build failed.' }
    az bicep build --file infra/database.bicep --outfile artifacts/database.json
    if ($LASTEXITCODE -ne 0) { throw 'Database infrastructure build failed.' }
    az bicep build --file infra/bootstrap.bicep --outfile artifacts/bootstrap.json
    if ($LASTEXITCODE -ne 0) { throw 'Identity bootstrap build failed.' }
    az bicep build --file infra/keyvault-access.bicep --outfile artifacts/keyvault-access.json
    if ($LASTEXITCODE -ne 0) { throw 'Key Vault access build failed.' }
    az bicep build --file infra/web.bicep --outfile artifacts/web.json
    if ($LASTEXITCODE -ne 0) { throw 'Web infrastructure build failed.' }
    az bicep build --file infra/development-access.bicep --outfile artifacts/development-access.json
    if ($LASTEXITCODE -ne 0) { throw 'Development access infrastructure build failed.' }
    az bicep build --file infra/monitoring.bicep --outfile artifacts/monitoring.json
    if ($LASTEXITCODE -ne 0) { throw 'Monitoring infrastructure build failed.' }
    dotnet restore eng/AuthorizeDevelopmentSql --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'SQL authorization tool restore failed.' }
    dotnet build eng/AuthorizeDevelopmentSql -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'SQL authorization tool build failed.' }
    dotnet publish src/BriefcaseSync.Worker -c Release --no-restore -o artifacts/worker /p:UseAppHost=false
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    dotnet publish src/BriefcaseSync.Web -c Release --no-restore -o artifacts/web /p:UseAppHost=false
    if ($LASTEXITCODE -ne 0) { throw 'Web publish failed.' }
}
finally { Pop-Location }
