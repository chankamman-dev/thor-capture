# Builds artifacts/ThorCapture.exe, the screen grabber for the handheld.
# Uses the in-box .NET Framework compiler, so nothing has to be installed.
param([string]$OutputPath)
$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "找不到 C# 编译器：$csc" }

$output = Join-Path $project 'artifacts\ThorCapture.exe'
if ($OutputPath) { $output = $OutputPath }
New-Item -ItemType Directory -Path (Split-Path $output -Parent) -Force | Out-Null
$sources = @(Get-ChildItem (Join-Path $project 'src') -Filter *.cs -Recurse | ForEach-Object { $_.FullName })

$options = @(
    '/nologo'
    '/target:winexe'
    '/optimize+'
    '/platform:anycpu'
    '/warn:4'
    "/out:$output"
    '/reference:System.dll'
    '/reference:System.Core.dll'
    '/reference:System.Drawing.dll'
    '/reference:System.Windows.Forms.dll'
    '/reference:C:\Windows\Microsoft.NET\Framework64\v4.0.30319\Microsoft.VisualBasic.dll'
)
$options += $sources
& $csc @options
if ($LASTEXITCODE -ne 0) { throw "ThorCapture 构建失败：$LASTEXITCODE" }
$audioServer = Join-Path $project 'third-party\scrcpy\scrcpy-server-v3.3.4'
if ((Get-FileHash -LiteralPath $audioServer).Hash -ne '8588238C9A5A00AA542906B6EC7E6D5541D9FFB9B5D0F6E1BC0E365E2303079E') { throw 'Audio server checksum mismatch' }
$audioTarget = Join-Path (Split-Path $output -Parent) 'scrcpy-server-v3.3.4'
Copy-Item -LiteralPath $audioServer -Destination $audioTarget -Force
$audioNotices = Join-Path (Split-Path $output -Parent) 'third-party\scrcpy'
New-Item -ItemType Directory -Path $audioNotices -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $project 'third-party\scrcpy\LICENSE'), (Join-Path $project 'third-party\scrcpy\NOTICE.md') -Destination $audioNotices -Force
Get-FileHash $output
