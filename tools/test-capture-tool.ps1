param([switch]$Live, [switch]$Stream, [string]$Filter)
# Builds every file under src/ and tests/ (recursively) into one test exe and runs it.
#   -Filter <text>  only run suites whose class name contains <text>; core checks always run
#   -Live           also run the real-device checks (one authorised Thor connected)
$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
$testOutput = Join-Path $project 'evidence\thor-capture-tests.exe'
New-Item -ItemType Directory -Path (Split-Path $testOutput -Parent) -Force | Out-Null
$captureSources = @(Get-ChildItem (Join-Path $project 'src') -Filter *.cs -Recurse | ForEach-Object { $_.FullName })
$testSources = @(Get-ChildItem (Join-Path $project 'tests') -Filter *.cs -Recurse | ForEach-Object { $_.FullName })
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /target:exe /main:ThorCapture.Tests /warn:4 "/out:$testOutput" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:C:\Windows\Microsoft.NET\Framework64\v4.0.30319\Microsoft.VisualBasic.dll @captureSources @testSources
if ($LASTEXITCODE -ne 0) { throw 'Test build failed' }
$env:THOR_TEST_FILTER = $Filter
try {
    if ($Live -and $Stream) { throw 'Use -Live or -Stream separately' }
    if ($Live) { & $testOutput --live }
    elseif ($Stream) {
        $streamFfmpeg = $env:THOR_FFMPEG
        if (-not $streamFfmpeg) { $streamFfmpeg = [IO.File]::ReadAllText((Join-Path $project 'artifacts\ffmpeg-path.txt')).Trim() }
        if (-not (Test-Path -LiteralPath $streamFfmpeg)) { throw 'Streaming integration requires the configured FFmpeg' }
        & $testOutput --stream-check $streamFfmpeg
    }
    else { & $testOutput }
}
finally { Remove-Item Env:\THOR_TEST_FILTER -ErrorAction SilentlyContinue }
if ($LASTEXITCODE -ne 0) { throw 'Capture regression failed' }
