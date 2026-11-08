param([string]$Revision = 'HEAD', [string]$OutputPath)
# Produces facts from Git, not legal ownership claims. Never exports author emails.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $OutputPath) { $OutputPath = Join-Path $root 'docs/release/provenance-files.csv' }
function Repo-Git([string[]]$Arguments) {
    $result = @(& git -c "safe.directory=$root" -C $root @Arguments)
    if ($LASTEXITCODE -ne 0) { throw 'Git provenance enumeration failed' }
    return $result
}
$paths = Repo-Git @('ls-tree','-r','--name-only',$Revision)
$aliases = @{}; $aliasIndex = 1
foreach ($name in ((Repo-Git @('log','--format=%an',$Revision)) | Sort-Object -Unique)) {
    $aliases[$name] = ('Contributor-{0:D2}' -f $aliasIndex); $aliasIndex++
}
$rows = foreach ($path in $paths) {
    if ($path -eq 'docs/release/provenance-files.csv') { continue }
    $full = Join-Path $root $path
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { throw 'Provenance input missing in current checkout' }
    $first = @(Repo-Git @('log','--follow','--reverse','--format=%H',$Revision,'--',$path))[0]
    $last = @(Repo-Git @('log','-1','--format=%H',$Revision,'--',$path))[0]
    $authors = @((Repo-Git @('log','--follow','--format=%an',$Revision,'--',$path)) | Sort-Object -Unique | ForEach-Object { $aliases[$_] }) -join ';'
    $source = 'repository-local-development'; $license = 'owner-confirmation-pending'; $modified = 'repository-history'; $distribution = 'source-proposed-owner-approval'
    if ($path.StartsWith('third-party/scrcpy/')) { $source='Genymobile/scrcpy v3.3.4'; $license='Apache-2.0'; $modified=$(if($path.EndsWith('NOTICE.md')){'project-attribution-document'}else{'upstream-unmodified-record'}); $distribution='included-current-build-owner-bundling-decision' }
    if ($path -eq 'tests/fixtures/recording-preview.mp4') { $source='FFmpeg-generated-solid-color-fixture'; $modified='synthetic-generated'; $distribution='source-fixture-manual-review' }
    [pscustomobject]@{ path=$path; sha256=(Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash; firstCommit=$first; lastCommit=$last; source=$source; author=$authors; license=$license; modification=$modified; distribution=$distribution }
}
$temporary = $OutputPath + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
try { $rows | Export-Csv -LiteralPath $temporary -NoTypeInformation -Encoding UTF8; Move-Item -LiteralPath $temporary -Destination $OutputPath -Force }
finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
Write-Output ("Provenance: {0} per-file records." -f @($rows).Count)
