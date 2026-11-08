# Validates a saved dual recording locally, without ADB. Originals are read only.
param([Parameter(Mandatory=$true)][string]$Source, [string]$OutputFolder)
$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
$sourcePath = (Resolve-Path -LiteralPath $Source).Path
if (-not $OutputFolder) { $OutputFolder = Join-Path $project 'evidence\saved-playback' }
$output = [IO.Path]::GetFullPath($OutputFolder)
$projectPrefix = [IO.Path]::GetFullPath((Join-Path $project 'evidence')) + '\'
if (-not $output.StartsWith($projectPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Output folder must remain inside project evidence' }
New-Item -ItemType Directory -Path $output -Force | Out-Null
$ffmpeg = (Get-Content -LiteralPath (Join-Path $project 'artifacts\ffmpeg-path.txt') -Raw).Trim()
$before = @(Get-ChildItem -LiteralPath (Split-Path $sourcePath -Parent) -File | Where-Object { $_.Name -like '*.mp4' -or $_.Name -like '*.input.csv' } | ForEach-Object { [pscustomobject]@{ path=$_.FullName; sha256=(Get-FileHash -LiteralPath $_.FullName).Hash } })
$exe = Join-Path $output 'playback-check.exe'
$sources = @(Get-ChildItem -LiteralPath (Join-Path $project 'src') -Filter '*.cs' -Recurse | ForEach-Object { $_.FullName })
& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' /nologo /optimize+ /target:exe /main:ThorCapture.PlaybackCheck "/out:$exe" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:C:\Windows\Microsoft.NET\Framework64\v4.0.30319\Microsoft.VisualBasic.dll @sources (Join-Path $PSScriptRoot 'PlaybackCheck.cs')
if ($LASTEXITCODE -ne 0) { throw 'Saved playback check build failed' }
$priorFfmpeg = $env:THOR_FFMPEG
try {
    $env:THOR_FFMPEG = $ffmpeg
    & $exe $sourcePath $output
    if ($LASTEXITCODE -ne 0) { throw 'Saved recording check failed' }
} finally { $env:THOR_FFMPEG = $priorFfmpeg }
$video = Join-Path $output 'saved-dual-framed.mp4'
& $ffmpeg -v error -i $video -f null -
if ($LASTEXITCODE -ne 0) { throw 'Saved export failed full decode' }
foreach ($original in $before) { if ((Get-FileHash -LiteralPath $original.path).Hash -ne $original.sha256) { throw ('Original changed: ' + $original.path) } }
[pscustomobject]@{ date=(Get-Date).ToString('o'); source=$sourcePath; framedVideo=$video; sha256=(Get-FileHash -LiteralPath $video).Hash; fullDecode=$true; originalsUnchanged=$true; originals=$before; liveDeviceTested=$false } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'verification.json') -Encoding UTF8
Get-FileHash -LiteralPath $video
