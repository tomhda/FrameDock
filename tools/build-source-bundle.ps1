[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [string]$CacheRoot
)

# Collects the source archives that accompany a FrameDock release: FFmpeg and
# x264 (GPL) and mpv (LGPL) at the exact commits of the bundled binaries.

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$scriptDirectory = [System.IO.Path]::GetFullPath($PSScriptRoot)
$repositoryRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $scriptDirectory))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repositoryRoot 'artifacts\sources' }
if (-not $CacheRoot) { $CacheRoot = Join-Path $scriptDirectory '.cache\sources' }
$outputPath = [System.IO.Path]::GetFullPath($OutputDirectory)
$cachePath = [System.IO.Path]::GetFullPath($CacheRoot)
$requiredOutputPrefix = $repositoryRoot.TrimEnd('\') + '\'
if (-not $outputPath.StartsWith($requiredOutputPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "ソース一式の出力先はリポジトリ内に指定してください: $outputPath"
}

$archives = @(
    [pscustomobject]@{
        Name = 'ffmpeg-946fcce07b-source.tar.gz'
        Url = 'https://github.com/FFmpeg/FFmpeg/archive/946fcce07b6dcd0331c8cc609192aeff5e1924f8.tar.gz'
        Sha256 = '0aa2b1de2a5698b20a23e93d539a9a8e82ca0117496c5bdf05d198805f42bb3b'
    },
    [pscustomobject]@{
        Name = 'x264-0480cb05-source.tar.gz'
        Url = 'https://code.videolan.org/videolan/x264/-/archive/0480cb05fa188d37ae87e8f4fd8f1aea3711f7ee/x264-0480cb05fa188d37ae87e8f4fd8f1aea3711f7ee.tar.gz'
        Sha256 = 'd0967a1348c85dfde363bb52610403be898171493100561efa0dd05d5fd1ae50'
    },
    [pscustomobject]@{
        Name = 'mpv-35af06172b-source.tar.gz'
        Url = 'https://github.com/mpv-player/mpv/archive/35af06172be5199212e0406878bd0fde532d080d.tar.gz'
        Sha256 = '542536b057860ac0b1dae9e11b47120f97b3a13318f8746655b0d035b01a7a11'
    }
)

function Get-Sha256 {
    param([Parameter(Mandatory = $true)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

New-Item -ItemType Directory -Force -Path $cachePath | Out-Null
New-Item -ItemType Directory -Force -Path $outputPath | Out-Null

foreach ($archive in $archives) {
    $cached = Join-Path $cachePath $archive.Name
    if ((Test-Path -LiteralPath $cached -PathType Leaf) -and (Get-Sha256 $cached) -ne $archive.Sha256) {
        Remove-Item -LiteralPath $cached -Force
    }

    if (-not (Test-Path -LiteralPath $cached -PathType Leaf)) {
        Write-Host "ソースを取得します: $($archive.Name)"
        $partial = "$cached.partial"
        # code.videolan.org answers browser-like user agents with a bot check page.
        Invoke-WebRequest -Uri $archive.Url -OutFile $partial -UseBasicParsing -UserAgent 'FrameDock-build-source-bundle'
        $actual = Get-Sha256 $partial
        if ($actual -ne $archive.Sha256) {
            Remove-Item -LiteralPath $partial -Force
            throw "SHA-256 が一致しません: $($archive.Name) (expected $($archive.Sha256), actual $actual)"
        }

        Move-Item -LiteralPath $partial -Destination $cached -Force
    }

    Copy-Item -LiteralPath $cached -Destination (Join-Path $outputPath $archive.Name) -Force
    Write-Host "確認しました: $($archive.Name)"
}

$buildReadme = Join-Path $repositoryRoot 'vendor\ffmpeg\README.txt'
if (-not (Test-Path -LiteralPath $buildReadme -PathType Leaf)) {
    throw "vendor\ffmpeg\README.txt がありません。先に tools\bootstrap-dependencies.ps1 を実行してください。"
}

Copy-Item -LiteralPath $buildReadme -Destination (Join-Path $outputPath 'FFmpeg-BUILD-README.txt') -Force
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'docs\SOURCES.md') -Destination (Join-Path $outputPath 'SOURCES.md') -Force

$sumLines = Get-ChildItem -LiteralPath $outputPath -File |
    Where-Object { $_.Name -ne 'SHA256SUMS.txt' } |
    Sort-Object Name |
    ForEach-Object { '{0}  {1}' -f (Get-Sha256 $_.FullName), $_.Name }
# LF line endings, so that 'sha256sum -c' also works outside Windows.
[System.IO.File]::WriteAllText((Join-Path $outputPath 'SHA256SUMS.txt'), (($sumLines -join "`n") + "`n"), (New-Object System.Text.UTF8Encoding($false)))

Write-Host "ソース一式を作成しました: $outputPath"
Get-ChildItem -LiteralPath $outputPath -File | Sort-Object Name | ForEach-Object {
    Write-Host ('  {0}  {1:N1} MB' -f $_.Name, ($_.Length / 1MB))
}
