# Renders the device frame in every skin (and optionally the main window)
# into evidence/ui-check, using the newest upper/lower pair in the gallery.
#   ./tools/render-check.ps1          frames only
#   ./tools/render-check.ps1 -Window  also screenshot the main window
param([switch]$Window)
$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
$output = Join-Path $project 'evidence\ui-check'
New-Item -ItemType Directory -Path $output -Force | Out-Null
# Built beside ThorCapture.exe so the window finds the real gallery and settings.
$exe = Join-Path (Join-Path $project 'artifacts') 'render-check.exe'
$sources = @(Get-ChildItem (Join-Path $project 'src') -Filter *.cs -Recurse | ForEach-Object { $_.FullName })
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc /nologo /target:winexe /main:ThorCapture.RenderCheck "/out:$exe" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:C:\Windows\Microsoft.NET\Framework64\v4.0.30319\Microsoft.VisualBasic.dll @sources (Join-Path $PSScriptRoot 'RenderCheck.cs')
if ($LASTEXITCODE -ne 0) { throw 'Render check build failed' }
$arguments = @((Join-Path $project 'artifacts\captures'), $output)
# Offline demo library for the game library page (made-up titles and generated art).
$demo = Join-Path $project 'evidence\cocoon-demo-root'
if ($Window -and -not (Test-Path (Join-Path $demo '_data\cocoon_db'))) { & node (Join-Path $PSScriptRoot 'cocoon-demo.cjs') $demo | Out-Null }
if ($Window) { $arguments += 'window' }
# Invoke directly and wait: Start-Process from a non-interactive shell can open the window minimized.
& $exe @arguments | Out-Null
Remove-Item -LiteralPath $exe
if (Test-Path (Join-Path $output 'render-error.txt')) { throw (Get-Content (Join-Path $output 'render-error.txt') -Raw) }
if (Test-Path (Join-Path $output 'playback-render-error.txt')) { throw (Get-Content (Join-Path $output 'playback-render-error.txt') -Raw) }
Get-ChildItem $output -Filter *.png | Select-Object Name, LastWriteTime
