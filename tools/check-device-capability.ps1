param([switch]$HostOnly, [switch]$Live, [switch]$ConfirmLiveAuthorized,
    [string]$Serial, [string]$AdbPath = $env:THOR_ADB, [string]$FfmpegPath = $env:THOR_FFMPEG,
    [string]$Output)
# Default: offline template only. Never enumerate or contact a device implicitly.
$ErrorActionPreference = 'Stop'
if ($Live -and $HostOnly) { throw '不能同时使用 Live 和 HostOnly。' }
if ($Live -and (-not $ConfirmLiveAuthorized -or -not $Serial)) {
    throw '实机只读探测需要用户本轮明确同意，再传入 -Live -ConfirmLiveAuthorized -Serial；离线默认不连接设备。'
}
$project = Split-Path $PSScriptRoot -Parent
if (-not $Output) { $Output = Join-Path $project ('evidence\capability-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '.md') }
$Output = [IO.Path]::GetFullPath($Output)
if (Test-Path -LiteralPath $Output) { throw '拒绝覆盖现有验收记录。' }
$tool = Join-Path $project 'evidence\capability-check.exe'
New-Item -ItemType Directory -Path (Split-Path $tool -Parent) -Force | Out-Null
$sources = @(Get-ChildItem (Join-Path $project 'src') -Filter *.cs -Recurse | ForEach-Object { $_.FullName })
$sources += Join-Path $PSScriptRoot 'CapabilityCheck.cs'
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /target:exe /main:ThorCapture.CapabilityCheck /warn:4 "/out:$tool" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:C:\Windows\Microsoft.NET\Framework64\v4.0.30319\Microsoft.VisualBasic.dll @sources
if ($LASTEXITCODE -ne 0) { throw '能力工具编译失败。' }
$mode = '--offline'
if ($HostOnly) { $mode = '--host' }
if ($Live) { $mode = '--live' }
if (-not $AdbPath) { $AdbPath = '__not_configured__' }
if (-not $FfmpegPath) { $FfmpegPath = '__not_configured__' }
if (-not $Serial) { $Serial = '__offline__' }
$previous = $env:THOR_CAPABILITY_LIVE_AUTHORIZED
try {
    $env:THOR_CAPABILITY_LIVE_AUTHORIZED = if ($Live -and $ConfirmLiveAuthorized) { '1' } else { '0' }
    & $tool $mode $AdbPath $FfmpegPath $Serial $Output
    if ($LASTEXITCODE -ne 0) { throw '能力工具未成功完成；请检查结果状态，不能标为验收通过。' }
}
finally { $env:THOR_CAPABILITY_LIVE_AUTHORIZED = $previous }
Write-Output $Output
