# Offline failure-path checks; never reads device state or user configuration.
$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
$testRoot = Join-Path $project ('evidence/release-scanner-' + [Guid]::NewGuid().ToString('N'))
$repo = Join-Path $testRoot 'repo'
$package = Join-Path $testRoot 'package'
[IO.Directory]::CreateDirectory($repo) | Out-Null
[IO.Directory]::CreateDirectory($package) | Out-Null
& git -c "safe.directory=$repo" -C $repo init --quiet
if ($LASTEXITCODE -ne 0) { throw 'Test repository initialization failed' }
$scanner = Join-Path $PSScriptRoot 'release-scan.ps1'
$shell = Join-Path $PSHOME 'powershell.exe'
if (-not (Test-Path -LiteralPath $shell)) { $shell = Join-Path $PSHOME 'pwsh.exe' }
$checks = 0
function Expect-Scan([string]$Scope, [int]$Expected, [string]$Report, [string]$Candidate = '', [string]$ScanRoot = $repo) {
    $args = @('-NoProfile','-File',$scanner,'-Scope',$Scope,'-Root',$ScanRoot,'-ReportPath',$Report)
    if ($Candidate) { $args += @('-PackagePath',$Candidate,'-Allowlist',(Join-Path $testRoot 'allowlist.txt')) }
    & $shell @args | Out-Null
    if ($LASTEXITCODE -ne $Expected) { throw "Scanner $Scope exit mismatch: expected $Expected, actual $LASTEXITCODE" }
    $script:checks++
}
$report = Join-Path $testRoot 'result.json'
[IO.File]::WriteAllText((Join-Path $repo 'safe.txt'),'synthetic safe fixture')
Expect-Scan 'WorkingTree' 0 $report
[IO.File]::WriteAllText((Join-Path $repo 'private.txt'),('Z:' + '\private\fixture' + "`n" + 'gh' + 'p_' + ('a' * 30)))
Expect-Scan 'WorkingTree' 1 $report
$redacted = [IO.File]::ReadAllText($report)
if ($redacted.Contains('fixture') -or $redacted.Contains(('a' * 30))) { throw 'Private matched content leaked into report' }
$checks++
& git -c "safe.directory=$repo" -C $repo add private.txt safe.txt
& git -c "safe.directory=$repo" -C $repo -c user.name=Fixture -c user.email=fixture@invalid.example commit --quiet -m 'Synthetic scanner fixture'
if ($LASTEXITCODE -ne 0) { throw 'Fixture commit failed' }
[IO.File]::Delete((Join-Path $repo 'private.txt'))
& git -c "safe.directory=$repo" -C $repo add -u
& git -c "safe.directory=$repo" -C $repo -c user.name=Fixture -c user.email=fixture@invalid.example commit --quiet -m 'Remove private fixture from current tree'
Expect-Scan 'WorkingTree' 0 $report
Expect-Scan 'History' 1 $report
if (-not ([IO.File]::ReadAllText($report)).Contains('private-drive-path')) { throw 'Removed historical private data was missed' }
$checks++
[IO.File]::WriteAllText((Join-Path $testRoot 'allowlist.txt'),"safe.txt`n")
[IO.File]::WriteAllText((Join-Path $package 'safe.txt'),'safe')
Expect-Scan 'Package' 0 $report $package
[IO.File]::WriteAllText((Join-Path $package 'unexpected.txt'),'safe')
Expect-Scan 'Package' 1 $report $package
[IO.File]::Delete((Join-Path $package 'unexpected.txt'))
[IO.File]::Delete((Join-Path $package 'safe.txt'))
Expect-Scan 'Package' 1 $report $package
[IO.File]::WriteAllText((Join-Path $package 'safe.txt'),'safe')
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = Join-Path $testRoot 'safe.zip'
[IO.Compression.ZipFile]::CreateFromDirectory($package,$zip)
Expect-Scan 'Package' 0 $report $zip
$archive = [IO.Compression.ZipFile]::Open($zip,[IO.Compression.ZipArchiveMode]::Update)
try { $entry = $archive.CreateEntry('../escape.txt'); $writer = New-Object IO.StreamWriter ($entry.Open()); try { $writer.Write('safe') } finally { $writer.Dispose() } }
finally { $archive.Dispose() }
Expect-Scan 'Package' 1 $report $zip
$archive = [IO.Compression.ZipFile]::Open($zip,[IO.Compression.ZipArchiveMode]::Update)
try { $entry=$archive.CreateEntry('safe.txt'); $writer=New-Object IO.StreamWriter ($entry.Open()); try { $writer.Write('safe') } finally { $writer.Dispose() } }
finally { $archive.Dispose() }
Expect-Scan 'Package' 1 $report $zip
if (-not ([IO.File]::ReadAllText($report)).Contains('duplicate-package-entry')) { throw 'Duplicate ZIP entry missed' }
$checks++
Expect-Scan 'WorkingTree' 2 $report '' (Join-Path $testRoot 'non-repo')
Write-Output ("PASS: {0} release scanner checks (safe, secret, redaction, removed history, allowlist, missing entry, ZIP traversal, duplicate, incomplete scan)." -f $checks)
