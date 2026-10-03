# Checks UI string resources: en-US/ja-JP resw key parity, placeholder parity,
# code key references, and x:Uid coverage. Exits 1 on any mismatch.
# Compatible with Windows PowerShell 5.1.
$ErrorActionPreference = 'Stop'

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$jaPath = Join-Path $repositoryRoot 'FrameDock\Strings\ja-JP\Resources.resw'
$enPath = Join-Path $repositoryRoot 'FrameDock\Strings\en-US\Resources.resw'
$failures = @()

function Get-ReswTable {
    param([Parameter(Mandatory = $true)][string]$Path)
    $document = New-Object xml
    $document.Load($Path)
    $table = @{}
    foreach ($entry in $document.root.data) {
        $table[$entry.name] = $entry.value
    }
    return $table
}

function Get-PlaceholderCount {
    param([string]$Text)
    if ([string]::IsNullOrEmpty($Text)) { return 0 }
    return ([regex]::Matches($Text, '\{\d+')).Count
}

$ja = Get-ReswTable -Path $jaPath
$en = Get-ReswTable -Path $enPath

foreach ($key in $ja.Keys) {
    if (-not $en.ContainsKey($key)) {
        $failures += "missing in en-US: $key"
    }
}
foreach ($key in $en.Keys) {
    if (-not $ja.ContainsKey($key)) {
        $failures += "missing in ja-JP: $key"
    }
}
foreach ($key in $ja.Keys) {
    if ($en.ContainsKey($key)) {
        $expected = Get-PlaceholderCount -Text $ja[$key]
        $actual = Get-PlaceholderCount -Text $en[$key]
        if ($expected -ne $actual) {
            $failures += "placeholder count differs for ${key}: ja-JP=${expected} en-US=${actual}"
        }
    }
}

$codeFiles = Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'FrameDock') -Recurse -Include '*.cs' -File |
    Where-Object { ($_.FullName -notmatch '\\(bin|obj)\\') }
$codePattern = 'Strings\.(Get|Format)\("([^"]+)"'
foreach ($file in $codeFiles) {
    $text = [System.IO.File]::ReadAllText($file.FullName)
    foreach ($match in [regex]::Matches($text, $codePattern)) {
        $key = $match.Groups[2].Value
        if (-not $ja.ContainsKey($key)) {
            $failures += "code key missing in ja-JP resw: $key ($($file.Name))"
        }
        if (-not $en.ContainsKey($key)) {
            $failures += "code key missing in en-US resw: $key ($($file.Name))"
        }
    }
}

$xamlFiles = Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'FrameDock') -Filter '*.xaml' -File
foreach ($file in $xamlFiles) {
    $text = [System.IO.File]::ReadAllText($file.FullName)
    foreach ($match in [regex]::Matches($text, 'x:Uid="([^"]+)"')) {
        $uid = $match.Groups[1].Value
        $prefix = "$uid."
        $jaHit = 0
        $enHit = 0
        foreach ($key in $ja.Keys) {
            if ($key.StartsWith($prefix, [System.StringComparison]::Ordinal)) { $jaHit++ }
        }
        foreach ($key in $en.Keys) {
            if ($key.StartsWith($prefix, [System.StringComparison]::Ordinal)) { $enHit++ }
        }
        if ($jaHit -eq 0) {
            $failures += "x:Uid without ja-JP key: $uid ($($file.Name))"
        }
        if ($enHit -eq 0) {
            $failures += "x:Uid without en-US key: $uid ($($file.Name))"
        }
    }
}

if ($failures.Count -gt 0) {
    foreach ($failure in $failures) {
        Write-Host "check-strings: $failure"
    }
    exit 1
}

Write-Host ("check-strings: OK ({0} keys, {1} code files, {2} xaml files)" -f $ja.Count, $codeFiles.Count, $xamlFiles.Count)
exit 0
