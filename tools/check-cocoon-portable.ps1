param(
    [Parameter(Mandatory=$true)][string]$ProductionRoot,
    [Parameter(Mandatory=$true)][string]$VerifiedCandidate,
    [Parameter(Mandatory=$true)][ValidatePattern('^[0-9a-f]{40}$')][string]$BackendCommit,
    [Parameter(Mandatory=$true)][ValidatePattern('^[0-9a-f]{64}$')][string]$ManifestSha256,
    [Parameter(Mandatory=$true)][string]$Node,
    [string]$EvidenceRoot = (Join-Path (Split-Path $PSScriptRoot -Parent) 'evidence/cocoon-portable')
)
# Compile a fixed backend-only entrypoint from an immutable evidence snapshot.
$ErrorActionPreference = 'Stop'
$ProductionRoot = [IO.Path]::GetFullPath($ProductionRoot)
$VerifiedCandidate = [IO.Path]::GetFullPath($VerifiedCandidate)
$EvidenceRoot = [IO.Path]::GetFullPath($EvidenceRoot)
$Node = [IO.Path]::GetFullPath($Node)
function Assert-Plain([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'PORTABLE_LINK_REJECTED' }
        }
        $parent = [IO.Path]::GetDirectoryName($current)
        if ($parent -eq $current) { break }; $current = $parent
    }
}
foreach ($target in @($ProductionRoot,$VerifiedCandidate,$EvidenceRoot,$Node)) { Assert-Plain $target }
if (-not (Test-Path -LiteralPath $Node -PathType Leaf)) { throw 'EXTERNAL_NODE_REQUIRED' }
if ($EvidenceRoot -eq $ProductionRoot) { throw 'EVIDENCE_EQUALS_PRODUCTION_ROOT' }
foreach ($protected in @((Join-Path $ProductionRoot 'src'),$VerifiedCandidate)) {
    $boundary = $protected.TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
    if ($EvidenceRoot.StartsWith($boundary,[StringComparison]::OrdinalIgnoreCase) -or $EvidenceRoot -eq $protected) { throw 'EVIDENCE_INSIDE_PROTECTED_SOURCE' }
}
$packageTool = Join-Path $PSScriptRoot 'cocoon-code-package.cjs'
$verification = @(& $Node $packageTool verify $VerifiedCandidate $BackendCommit $ManifestSha256)
if ($LASTEXITCODE -ne 0) { throw 'CANDIDATE_VERIFY_FAILED' }
$verified = ($verification -join "`n") | ConvertFrom-Json
$manifest = Get-Content -LiteralPath (Join-Path $VerifiedCandidate 'cocoon-code-manifest.json') -Raw | ConvertFrom-Json
[IO.Directory]::CreateDirectory($EvidenceRoot) | Out-Null
$runId = [Guid]::NewGuid().ToString('N'); $run = Join-Path $EvidenceRoot $runId
if (Test-Path -LiteralPath $run) { throw 'EVIDENCE_ALREADY_EXISTS' }
[IO.Directory]::CreateDirectory($run) | Out-Null
$app = Join-Path $run 'app'; $snapshot = Join-Path $run 'source'; $data = Join-Path $run 'empty-data'
[IO.Directory]::CreateDirectory($app) | Out-Null
[IO.Directory]::CreateDirectory($snapshot) | Out-Null
$srcRoot = Join-Path $ProductionRoot 'src'; Assert-Plain $srcRoot
$commit = (& git -c "safe.directory=$($ProductionRoot.Replace('\','/'))" -C $ProductionRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40}$') { throw 'PRODUCTION_COMMIT_REQUIRED' }
$sourceEntries = @()
foreach ($file in (Get-ChildItem -LiteralPath $srcRoot -Filter *.cs -Recurse -File | Sort-Object FullName)) {
    Assert-Plain $file.FullName
    $relative = $file.FullName.Substring($srcRoot.Length+1); $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $destination = Join-Path $snapshot $relative
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
    if (Test-Path -LiteralPath $destination) { throw 'SNAPSHOT_TARGET_EXISTS' }
    [IO.File]::Copy($file.FullName,$destination,$false)
    if ((Get-FileHash -LiteralPath $destination).Hash.ToLowerInvariant() -ne $hash) { throw 'PRODUCTION_SOURCE_CHANGED' }
    $sourceEntries += [pscustomobject]@{path=('src/'+$relative.Replace('\','/')); sha256=$hash}
}
if ($sourceEntries.Count -lt 1) { throw 'PRODUCTION_SOURCE_EMPTY' }
function Assert-SourceStable {
    $current = @(Get-ChildItem -LiteralPath $srcRoot -Filter *.cs -Recurse -File)
    if ($current.Count -ne $sourceEntries.Count) { throw 'PRODUCTION_SOURCE_CHANGED' }
    foreach ($entry in $sourceEntries) {
        $full = Join-Path $ProductionRoot $entry.path; Assert-Plain $full
        if ((Get-FileHash -LiteralPath $full).Hash.ToLowerInvariant() -ne $entry.sha256) { throw 'PRODUCTION_SOURCE_CHANGED' }
    }
}
Assert-SourceStable
foreach ($entry in $manifest.files) {
    $full = Join-Path $VerifiedCandidate $entry.path; $destination = Join-Path $app $entry.path
    Assert-Plain $full; [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
    [IO.File]::Copy($full,$destination,$false)
    if ((Get-FileHash -LiteralPath $destination).Hash.ToLowerInvariant() -ne $entry.sha256) { throw 'COPIED_COMPONENT_HASH_MISMATCH' }
}
function Assert-ComponentStable {
    $present = @(Get-ChildItem -LiteralPath (Join-Path $app 'cocoon') -Recurse -File)
    if ($present.Count -ne $manifest.files.Count) { throw 'COPIED_COMPONENT_UNEXPECTED_FILE' }
    foreach ($entry in $manifest.files) {
        $full = Join-Path $app $entry.path; Assert-Plain $full
        if ((Get-FileHash -LiteralPath $full).Hash.ToLowerInvariant() -ne $entry.sha256) { throw 'COPIED_COMPONENT_HASH_MISMATCH' }
    }
}
Assert-ComponentStable
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$exe = Join-Path $app 'CocoonPortableCheck.exe'
$sources = @(Get-ChildItem -LiteralPath $snapshot -Filter *.cs -Recurse -File | ForEach-Object FullName)
$sources += Join-Path $PSScriptRoot 'check-cocoon-portable.cs'
$options = @('/nologo','/target:exe','/main:ThorCapture.CocoonPortableCheck','/langversion:5','/optimize+','/warn:4',"/out:$exe",'/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:C:\Windows\Microsoft.NET\Framework64\v4.0.30319\Microsoft.VisualBasic.dll')
& $compiler @options @sources
if ($LASTEXITCODE -ne 0) { throw 'PORTABLE_COMPILE_FAILED' }
$log = @(& $exe $Node $data 2>&1)
$exit = $LASTEXITCODE
$log | Set-Content -LiteralPath (Join-Path $run 'probe.txt') -Encoding UTF8
Assert-SourceStable; Assert-ComponentStable
$again = @(& $Node $packageTool verify $VerifiedCandidate $BackendCommit $ManifestSha256)
if ($LASTEXITCODE -ne 0) { throw 'ORIGINAL_CANDIDATE_CHANGED' }
$result = [ordered]@{contract='thor.cocoon-portable-probe/1'; runId=$runId; productionCommit=$commit; sourceFiles=$sourceEntries; probeSourceSha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'check-cocoon-portable.cs')).Hash.ToLowerInvariant(); nodeSha256=(Get-FileHash -LiteralPath $Node).Hash.ToLowerInvariant(); backendCommit=$BackendCommit; manifestSha256=$ManifestSha256; executableSha256=(Get-FileHash -LiteralPath $exe).Hash.ToLowerInvariant(); fixedMain='ThorCapture.CocoonPortableCheck'; componentFiles=$manifest.files.Count; sourceUnchanged=$true; componentUnchanged=$true; exitCode=$exit; publicReleaseReady=$false}
$result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $run 'portable-result.json') -Encoding UTF8
if ($exit -ne 0 -or $log -notcontains 'PORTABLE_CSHARP_PROBE_PASS') { $log | Write-Output; throw 'PORTABLE_EXECUTION_FAILED' }
$log | Write-Output
Write-Output ('Portable source snapshot verified; run '+$runId+'; production '+$commit+'; public release remains pending.')
