$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
$expectedUrl = 'https://cwapps.n8protect.com/Development/VendorSmartAdvocate/'
$entityJson = $null
$entity = $null
$settings = $null
$form = $null
$secretJson = $null
try {
    try {
        $null = az account get-access-token --query expiresOn --only-show-errors -o tsv 2>$null
        if ($LASTEXITCODE -ne 0) { throw 'Sign-in required.' }
    } catch {
        throw 'Azure sign-in is required. Run az login --tenant 36e4d61d-ea81-465c-bc28-dcb1302d8d46, then rerun this script.'
    }
    # Read only the existing development partner. Never print settings or credentials.
    try {
        $entityJson = az storage entity show --account-name caseworksdevasa000 --auth-mode key --table-name CaseConnectorTradingPartner --partition-key default --row-key 17a78518-9aa4-42fe-aa63-9ecf741b8cbb --only-show-errors -o json 2>$null
        if ($LASTEXITCODE -ne 0) { throw 'Read failed.' }
    } catch { throw 'Unable to read the Azure development trading partner with the current Azure identity.' }
    try {
        $entity = ($entityJson -join "`n") | ConvertFrom-Json
        $settings = $entity.AgentConfigurationConnectorSettings | ConvertFrom-Json
    } catch { throw 'The Azure development partner configuration is invalid; its contents have been omitted.' }
    if (-not $entity.Enabled -or $entity.Name -cne 'Cameron - SmartAdvocate') {
        throw 'The expected development trading partner is missing or disabled.'
    }
    if ($settings.SmartAdvocateVendor_ApiClient_ApiUrl -cne $expectedUrl -or
        $settings.SmartAdvocateVendor_ApiClient_AuthenticationMethod -cne 'FormJsonPostToBearerToken' -or
        $settings.SmartAdvocateVendor_ApiClient_AuthenticationUrl -cne '/users/authenticate') {
        throw 'The development endpoint or authentication method changed; review the Azure configuration.'
    }
    try {
        $form = $settings.SmartAdvocateVendor_ApiClient_AuthenticationFormJson | ConvertFrom-Json
        if ($form -isnot [PSCustomObject] -or [string]::IsNullOrWhiteSpace($form.Username) -or
            [string]::IsNullOrWhiteSpace($form.Password)) { throw 'Missing credentials.' }
    } catch { throw 'The stored development authentication form is invalid; its value has been omitted.' }
    $secretJson = @{
        'SmartAdvocate:ApiUrl' = $expectedUrl
        'SmartAdvocate:PersonalAccessToken' = ''
        'SmartAdvocate:Username' = [string]$form.Username
        'SmartAdvocate:Password' = [string]$form.Password
        'SendingEnabled' = 'false'
        'MappingApproved' = 'false'
    } | ConvertTo-Json
    foreach ($hostName in @('BriefcaseSync.Worker', 'BriefcaseSync.Web')) {
        $projectRoot = Join-Path $repositoryRoot ('src/' + $hostName)
        try {
            # stdin keeps credentials out of process arguments and command history.
            $secretJson | dotnet user-secrets set --project $projectRoot 2>$null | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'Save failed.' }
        } catch { throw "Unable to save SmartAdvocate development user secrets for $hostName." }
        Write-Host "SmartAdvocate development connection configured in $hostName local .NET user secrets; sending and approval remain disabled."
    }
} finally {
    $form = $null
    $secretJson = $null
    $settings = $null
    $entity = $null
    $entityJson = $null
}
