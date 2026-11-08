param(
    [ValidateSet('WorkingTree','History','Package')][string]$Scope = 'WorkingTree',
    [string]$Root = (Split-Path $PSScriptRoot -Parent),
    [string]$PackagePath,
    [string]$ReportPath,
    [string]$Allowlist = (Join-Path (Split-Path $PSScriptRoot -Parent) 'docs/release/package-allowlist.txt')
)
# Reports locations and rule names only. Never print matched private content.
$ErrorActionPreference = 'Stop'
$Root = [IO.Path]::GetFullPath($Root)
$findings = New-Object 'System.Collections.Generic.List[object]'
$checked = 0
$rules = [ordered]@{
    'private-user-path' = '(?i)(?:[a-z]:[\\/]Users[\\/](?!Public\b|Default\b)[^\\/\s<>"'']+|/home/[^/\s<>]+)'
    'private-drive-path' = '(?i)\b[a-bd-z]:[\\/][^\r\n"<>]+'
    'email-review' = '\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b'
    'network-address-review' = '\b(?:\d{1,3}\.){3}\d{1,3}(?::\d{2,5})?\b'
    'secret-token' = '(?:gh[pousr]_[A-Za-z0-9_]{20,}|github_pat_[A-Za-z0-9_]{20,}|AKIA[A-Z0-9]{16}|-----BEGIN (?:RSA |OPENSSH |EC )?PRIVATE KEY-----)'
    'credential-assignment' = '(?i)\b(?:password|pairingCode|access_token|api_key)\s*[:=]\s*["''][^"'']{4,}["'']'
    'device-serial-review' = '(?i)(?:\bserial\s*[:=]\s*["''][a-z0-9]{6,}["'']|(?<![a-z0-9])(?=[a-f0-9]{0,7}[a-f])(?=[a-f0-9]{0,7}[0-9])[a-f0-9]{8}(?![a-z0-9]))'
}
function Add-Finding([string]$Path, [int]$Line, [string]$Rule, [string]$Revision) {
    $findings.Add([pscustomobject]@{ path=$Path; line=$Line; rule=$Rule; revision=$Revision })
}
function Scan-Bytes([string]$Path, [byte[]]$Bytes, [string]$Revision) {
    $script:checked++
    $extension = [IO.Path]::GetExtension($Path).ToLowerInvariant()
    if ($extension -in @('.jpg','.jpeg','.png','.gif','.mp4','.wav') -or $Path.EndsWith('.input.csv')) {
        Add-Finding $Path 0 'media-manual-review' $Revision
        # This is a review requirement, not a claim that EXIF has been inspected.
    }
    if ($Path -like '*scrcpy-server-*') { Add-Finding $Path 0 'binary-provenance-manual-review' $Revision; return }
    $texts = @([Text.Encoding]::UTF8.GetString($Bytes))
    if ($extension -in @('.exe','.dll')) { $texts += [Text.Encoding]::Unicode.GetString($Bytes) }
    foreach ($text in $texts) {
        if ($extension -in @('.exe','.dll')) {
            # Extract printable strings before matching; arbitrary decoded machine code
            # is not evidence of a path. Keep CJK characters for real localized paths.
            $text = [regex]::Replace($text,'[^\x20-\x7E\u4E00-\u9FFF]',"`n")
        }
        $lines = $text -split '\r?\n'
        for ($i=0; $i -lt $lines.Length; $i++) {
            foreach ($rule in $rules.Keys) {
                if ($lines[$i] -match $rules[$rule]) { Add-Finding $Path ($i+1) $rule $Revision }
            }
        }
    }
}
function Repo-Git([string[]]$Arguments) {
    $result = @(& git -c "safe.directory=$Root" -C $Root @Arguments 2>$null)
    if ($LASTEXITCODE -ne 0) { throw 'Git enumeration failed; scan is incomplete.' }
    return $result
}
try {
    if ($Scope -eq 'WorkingTree') {
        $paths = Repo-Git @('ls-files','--cached','--others','--exclude-standard')
        foreach ($path in ($paths | Sort-Object -Unique)) {
            $full = Join-Path $Root $path
            if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { throw 'Tracked input is missing.' }
            Scan-Bytes $path ([IO.File]::ReadAllBytes($full)) ''
        }
    }
    elseif ($Scope -eq 'History') {
        $seen = @{}
        foreach ($commit in (Repo-Git @('rev-list','--all'))) {
            $metadata = (Repo-Git @('show','-s','--format=%an%n%ae%n%cn%n%ce%n%B',$commit)) -join "`n"
            Scan-Bytes 'git-commit-metadata' ([Text.Encoding]::UTF8.GetBytes($metadata)) $commit
            foreach ($row in (Repo-Git @('ls-tree','-r',$commit))) {
                if ($row -notmatch '^\d+ blob ([0-9a-f]+)\t(.+)$') { continue }
                $blob = $Matches[1]; $path = $Matches[2]
                $key = $blob + ':' + $path
                if ($seen.ContainsKey($key)) { continue }; $seen[$key] = $true
                # Binary media still requires manual review. Text blobs are decoded by Git.
                if ([IO.Path]::GetExtension($path) -in @('.exe','.dll','.jpg','.jpeg','.png','.gif','.mp4','.wav') -or $path -like '*scrcpy-server-*') {
                    Add-Finding $path 0 'historical-binary-manual-review' $commit
                    $script:checked++; continue
                }
                $content = (Repo-Git @('show',($commit + ':' + $path))) -join "`n"
                Scan-Bytes $path ([Text.Encoding]::UTF8.GetBytes($content)) $commit
            }
        }
    }
    else {
        if (-not $PackagePath) { throw 'PackagePath is required.' }
        $allowed = @([IO.File]::ReadAllLines($Allowlist) | Where-Object { $_ -and -not $_.StartsWith('#') })
        $present = New-Object 'System.Collections.Generic.List[string]'
        if (Test-Path -LiteralPath $PackagePath -PathType Container) {
            $packageRoot = [IO.Path]::GetFullPath($PackagePath).TrimEnd('\','/')
            foreach ($file in (Get-ChildItem -LiteralPath $packageRoot -Recurse -File)) {
                $path = $file.FullName.Substring($packageRoot.Length+1).Replace('\','/')
                $present.Add($path)
                if ($allowed -cnotcontains $path) { Add-Finding $path 0 'package-not-allowlisted' '' }
                Scan-Bytes $path ([IO.File]::ReadAllBytes($file.FullName)) ''
            }
        }
        else {
            Add-Type -AssemblyName System.IO.Compression.FileSystem
            $zip = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($PackagePath))
            try {
                foreach ($entry in $zip.Entries) {
                    if ($entry.FullName.EndsWith('/')) { continue }
                    $path = $entry.FullName.Replace('\','/')
                    if ($present.Contains($path)) { Add-Finding $path 0 'duplicate-package-entry' '' }
                    $present.Add($path)
                    if ($allowed -cnotcontains $path) { Add-Finding $path 0 'package-not-allowlisted' '' }
                    $input = $entry.Open(); $memory = New-Object IO.MemoryStream
                    try { $input.CopyTo($memory); Scan-Bytes $path $memory.ToArray() '' }
                    finally { $input.Dispose(); $memory.Dispose() }
                }
            } finally { $zip.Dispose() }
        }
        foreach ($path in $allowed) { if (-not $present.Contains($path)) { Add-Finding $path 0 'required-package-file-missing' '' } }
    }
    $unique = @($findings | Sort-Object path,line,rule,revision -Unique)
    $result = [ordered]@{ schema=1; scope=$Scope; filesChecked=$checked; status=$(if($unique.Count){'review-required'}else{'pattern-scan-passed'}); findings=$unique; limitations=@('Heuristic scan cannot prove absence of secrets.','Media EXIF/content, brands, licensing and binary provenance require manual review.','Ignored local data is outside WorkingTree; scan final candidate separately.') }
    if ($ReportPath) {
        $fullReport = [IO.Path]::GetFullPath($ReportPath)
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($fullReport)) | Out-Null
        $temp = $fullReport + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
        try { [IO.File]::WriteAllText($temp,($result | ConvertTo-Json -Depth 6), (New-Object Text.UTF8Encoding $false)); Move-Item -LiteralPath $temp -Destination $fullReport -Force }
        finally { if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp } }
    }
    Write-Output ("Release scan: {0}; {1} inputs; {2} review locations." -f $Scope,$checked,$unique.Count)
    if ($unique.Count) { exit 1 }; exit 0
}
catch { Write-Output 'Release scan failed or incomplete. No matched content has been printed.'; exit 2 }
