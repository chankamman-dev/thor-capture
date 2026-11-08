# Measures CPU painting with fixed synthetic poses, without connecting to a device.
param([string]$OutputPath)
$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
$output = Join-Path $project 'evidence\animation-timing.txt'
if ($OutputPath) { $output = [IO.Path]::GetFullPath($OutputPath) }
New-Item -ItemType Directory -Path (Split-Path $output -Parent) -Force | Out-Null
$exe = Join-Path $project 'evidence\animation-check.exe'
$sources = @(Get-ChildItem (Join-Path $project 'src') -Filter '*.cs' -Recurse | ForEach-Object { $_.FullName })
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /optimize+ /target:exe /main:ThorCapture.AnimationCheck "/out:$exe" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:C:\Windows\Microsoft.NET\Framework64\v4.0.30319\Microsoft.VisualBasic.dll @sources (Join-Path $PSScriptRoot 'AnimationCheck.cs')
if ($LASTEXITCODE -ne 0) { throw 'Animation measurement build failed' }
& $exe $output
if ($LASTEXITCODE -ne 0) { throw 'Animation measurement failed' }
