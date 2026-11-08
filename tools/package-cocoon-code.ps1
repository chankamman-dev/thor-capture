param(
    [ValidateSet('Pack','Verify','Smoke')][string]$Mode = 'Verify',
    [string]$SourceRoot,
    [Parameter(Mandatory=$true)][string]$Candidate,
    [Parameter(Mandatory=$true)][ValidatePattern('^[0-9a-f]{40}$')][string]$ExpectedCommit,
    [ValidatePattern('^[0-9a-f]{64}$')][string]$ExpectedManifestSha256,
    [string]$DataRoot,
    [string]$Node = 'node'
)
# Offline code-only candidate; no installer, downloader, license decision or publication.
$ErrorActionPreference = 'Stop'
$entry = Join-Path $PSScriptRoot 'cocoon-code-package.cjs'
if ($Mode -eq 'Pack') {
    if (-not $SourceRoot) { throw 'SourceRoot is required for Pack.' }
    & $Node $entry pack $SourceRoot $Candidate $ExpectedCommit
}
elseif ($Mode -eq 'Verify') {
    & $Node $entry verify $Candidate $ExpectedCommit $ExpectedManifestSha256
}
else {
    if (-not $DataRoot -or -not $ExpectedManifestSha256) { throw 'Smoke requires a new DataRoot and the external manifest checksum.' }
    & $Node $entry smoke $Candidate $DataRoot $ExpectedCommit $ExpectedManifestSha256
}
if ($LASTEXITCODE -ne 0) { throw 'Cocoon code package validation failed; see the typed code above.' }
