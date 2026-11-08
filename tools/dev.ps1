# 开发模式：盯着 src\ 的改动，自动重新编译，并重启一个开发版窗口。
#   ./tools/dev.ps1
# 开发版是 artifacts\ThorCapture.dev.exe，和正式版用同一份设置、截图和游戏库数据，
# 所以开发时请先关掉正式版（两个窗口不能同时占用游戏库数据）。
# 编译失败时保留当前窗口，只在这里显示错误；按 Ctrl+C 退出（开发版窗口一并关闭）。
$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
$source = Join-Path $project 'src'
$exe = Join-Path $project 'artifacts\ThorCapture.dev.exe'
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$script:app = $null

function Build {
    $sources = @(Get-ChildItem $source -Filter *.cs -Recurse | ForEach-Object { $_.FullName })
    $temporary = $exe + '.building'
    $output = & $csc /nologo /target:winexe /optimize+ /warn:1 "/out:$temporary" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:C:\Windows\Microsoft.NET\Framework64\v4.0.30319\Microsoft.VisualBasic.dll @sources 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host ('[{0:HH:mm:ss}] 编译失败，窗口保持不变：' -f (Get-Date)) -ForegroundColor Red
        $output | Where-Object { "$_" -match 'error' } | Select-Object -First 15 | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
        Remove-Item -LiteralPath $temporary -ErrorAction SilentlyContinue
        return $false
    }
    return $true
}

function Stop-App {
    if ($script:app -eq $null -or $script:app.HasExited) { return }
    # Ask the window to close so it shuts its own game-library service down; only this process is ever stopped.
    [void]$script:app.CloseMainWindow()
    if (-not $script:app.WaitForExit(8000)) { $script:app.Kill(); [void]$script:app.WaitForExit(3000) }
}

function Start-App {
    Stop-App
    Move-Item -LiteralPath ($exe + '.building') -Destination $exe -Force
    $script:app = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe -Parent) -PassThru
    Write-Host ('[{0:HH:mm:ss}] 已重新编译并重启' -f (Get-Date)) -ForegroundColor Green
}

if (Get-Process -Name ThorCapture -ErrorAction SilentlyContinue) {
    Write-Host '正式版 ThorCapture.exe 正在运行：游戏库页会提示数据被占用。需要游戏库时请先关掉它。' -ForegroundColor Yellow
}
if (Build) { Start-App }

$watcher = New-Object System.IO.FileSystemWatcher $source, '*.cs'
$watcher.IncludeSubdirectories = $true
$watcher.NotifyFilter = [System.IO.NotifyFilters]'LastWrite, FileName'
Write-Host '正在监视 src\ 的改动（Ctrl+C 退出）…'
try {
    while ($true) {
        $change = $watcher.WaitForChanged([System.IO.WatcherChangeTypes]::All, 1000)
        if ($change.TimedOut) { continue }
        # Edits often arrive as several writes; wait until the files have been quiet for a moment.
        do { $more = $watcher.WaitForChanged([System.IO.WatcherChangeTypes]::All, 700) } while (-not $more.TimedOut)
        Write-Host ('[{0:HH:mm:ss}] 检测到改动：{1}，编译中…' -f (Get-Date), $change.Name)
        if (Build) { Start-App }
    }
}
finally {
    $watcher.Dispose()
    Stop-App
}
