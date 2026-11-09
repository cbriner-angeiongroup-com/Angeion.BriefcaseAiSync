$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$manifest = Get-Content -LiteralPath (Join-Path $taskRoot 'docs/design-system-provenance.json') -Raw | ConvertFrom-Json
$file = Join-Path $taskRoot 'src/BriefcaseSync.Web/Styles/reference/globals.css'
if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -ne $manifest.sha256) {
    throw 'Copied design-system CSS has drifted. Preserve reference bytes and make application overrides in Styles/app.css.'
}
Write-Output 'Verified the pinned design-system token snapshot.'
