param([switch]$Download)
# Installs only the fixed development dependency, in ignored evidence; no device access.
$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
$lock = Get-Content (Join-Path $project 'docs/release/ci-dependencies.json') -Raw | ConvertFrom-Json
$cache = Join-Path $project 'evidence/ci-dependencies'
[IO.Directory]::CreateDirectory($cache) | Out-Null
$archive = Join-Path $cache 'ffmpeg.zip'
if (-not (Test-Path -LiteralPath $archive)) {
    if (-not $Download) { throw 'Fixed CI FFmpeg cache missing; use -Download to fetch the reviewed dependency.' }
    $temporary = $archive + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -UseBasicParsing -Uri $lock.ffmpeg.url -OutFile $temporary -TimeoutSec 120
        if ((Get-FileHash -LiteralPath $temporary).Hash -ne $lock.ffmpeg.sha256) { throw 'Downloaded FFmpeg checksum mismatch.' }
        Move-Item -LiteralPath $temporary -Destination $archive
    }
    finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
}
if ((Get-FileHash -LiteralPath $archive).Hash -ne $lock.ffmpeg.sha256) { throw 'Cached FFmpeg checksum mismatch.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$target = Join-Path $cache 'extracted'
$boundary = [IO.Path]::GetFullPath($target).TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
$zip = [IO.Compression.ZipFile]::OpenRead($archive)
try {
    foreach ($entry in $zip.Entries) {
        $destination = [IO.Path]::GetFullPath((Join-Path $target $entry.FullName))
        if (-not $destination.StartsWith($boundary,[StringComparison]::OrdinalIgnoreCase)) { throw 'Archive entry escapes dependency directory.' }
    }
} finally { $zip.Dispose() }
Expand-Archive -LiteralPath $archive -DestinationPath $target -Force
$env:THOR_FFMPEG = Join-Path $target $lock.ffmpeg.exe
if (-not (Test-Path -LiteralPath $env:THOR_FFMPEG -PathType Leaf)) { throw 'Pinned FFmpeg executable missing.' }
$version = @(& $env:THOR_FFMPEG -version)
if ($LASTEXITCODE -ne 0 -or $version[0] -notmatch ('^ffmpeg version ' + [regex]::Escape($lock.ffmpeg.version) + '-')) { throw 'Pinned FFmpeg version check failed.' }
Write-Output ('Fixed CI dependency verified: FFmpeg ' + $lock.ffmpeg.version + '; SHA-256 archive check passed.')
