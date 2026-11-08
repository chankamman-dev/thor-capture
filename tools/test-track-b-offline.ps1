param([string[]]$PackageRoots, [string]$FfmpegPath = $env:THOR_FFMPEG)
# Compile feature packages together without merging branches or changing Track A.
# This script never accepts --live and runs only fake-device/synthetic stream tests.
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
if (-not $PackageRoots) {
    $parentRoot = Split-Path $projectRoot -Parent
    $PackageRoots = @('thor-capture-pkg1-capability', 'thor-capture-pkg2-triggers', 'thor-capture-pkg4-thumbs', 'thor-capture-pkg5-editing') |
        ForEach-Object { Join-Path $parentRoot $_ }
}
if (-not $FfmpegPath -or -not (Test-Path -LiteralPath $FfmpegPath -PathType Leaf)) { throw 'Set THOR_FFMPEG to an existing FFmpeg before this offline check.' }
$sourceFiles = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Recurse -Filter *.cs)
$testFiles = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'tests') -Recurse -Filter *.cs)
$baseFiles = @($sourceFiles + $testFiles | Where-Object { -not $_.FullName.StartsWith((Join-Path $projectRoot 'src\Features') + '\', [StringComparison]::OrdinalIgnoreCase) -and $_.Name -ne 'ImageExportTests.cs' })
$allFiles = [Collections.Generic.List[string]]::new()
@($sourceFiles + $testFiles) | ForEach-Object { $allFiles.Add($_.FullName) }
$relativeTests = @{}
$testFiles | ForEach-Object { $relativeTests[$_.FullName.Substring($projectRoot.Length + 1)] = $_.FullName }
$fixtureRoots = [Collections.Generic.List[string]]::new()
$fixtureRoots.Add((Join-Path $projectRoot 'tests\fixtures'))
foreach ($requestedRoot in $PackageRoots) {
    $packageRoot = (Resolve-Path -LiteralPath $requestedRoot).Path.TrimEnd('\')
    if ([string]::Equals($packageRoot, $projectRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Do not include this package twice.' }
    foreach ($file in $baseFiles) {
        $relative = $file.FullName.Substring($projectRoot.Length + 1)
        $other = Join-Path $packageRoot $relative
        if (-not (Test-Path -LiteralPath $other -PathType Leaf) -or (Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath $other).Hash) {
            throw "Track A baselines differ; rebase packages before combined verification: $relative"
        }
    }
    Get-ChildItem -LiteralPath (Join-Path $packageRoot 'src\Features') -Recurse -Filter *.cs | ForEach-Object {
        $relative = $_.FullName.Substring($packageRoot.Length + 1)
        if (Test-Path -LiteralPath (Join-Path $projectRoot $relative)) { throw "Duplicate feature path: $relative" }
        $allFiles.Add($_.FullName)
    }
    Get-ChildItem -LiteralPath (Join-Path $packageRoot 'tests') -Recurse -Filter *.cs | ForEach-Object {
        $relative = $_.FullName.Substring($packageRoot.Length + 1)
        if ($relativeTests.ContainsKey($relative)) {
            if ((Get-FileHash -LiteralPath $relativeTests[$relative]).Hash -ne (Get-FileHash -LiteralPath $_.FullName).Hash) { throw "Conflicting test path: $relative" }
        }
        else { $allFiles.Add($_.FullName); $relativeTests[$relative] = $_.FullName }
    }
    $fixtureRoots.Add((Join-Path $packageRoot 'tests\fixtures'))
}
$virtualProject = Join-Path $projectRoot 'evidence\20260927-track-b\project'
$outputRoot = Join-Path $virtualProject 'evidence'
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
foreach ($fixtureRoot in $fixtureRoots) {
    if (-not (Test-Path -LiteralPath $fixtureRoot)) { continue }
    Get-ChildItem -LiteralPath $fixtureRoot -Recurse -File | ForEach-Object {
        $relative = $_.FullName.Substring($fixtureRoot.Length + 1)
        $target = Join-Path (Join-Path $virtualProject 'tests\fixtures') $relative
        if ((Test-Path -LiteralPath $target) -and (Get-FileHash -LiteralPath $target).Hash -ne (Get-FileHash -LiteralPath $_.FullName).Hash) { throw "Conflicting fixture path: $relative" }
        New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
        Copy-Item -LiteralPath $_.FullName -Destination $target
    }
}
$outputExe = Join-Path $outputRoot 'thor-track-b-tests.exe'
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /target:exe /main:ThorCapture.Tests /warn:4 "/out:$outputExe" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:C:\Windows\Microsoft.NET\Framework64\v4.0.30319\Microsoft.VisualBasic.dll @allFiles
if ($LASTEXITCODE -ne 0) { throw 'Combined feature compilation failed.' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'third-party\scrcpy\scrcpy-server-v3.3.4') -Destination $outputRoot
$previousFilter = $env:THOR_TEST_FILTER
try {
    Remove-Item Env:\THOR_TEST_FILTER -ErrorAction SilentlyContinue
    & $outputExe --stream-check $FfmpegPath
    if ($LASTEXITCODE -ne 0) { throw 'Combined offline stream regression failed.' }
}
finally { $env:THOR_TEST_FILTER = $previousFilter }
