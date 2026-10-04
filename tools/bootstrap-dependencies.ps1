[CmdletBinding()]
param(
    [string]$InstallRoot,
    [string]$CacheRoot,
    [string]$MpvArchivePath,
    [string]$FfmpegArchivePath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $InstallRoot) { $InstallRoot = Split-Path -Parent $PSScriptRoot }
if (-not $CacheRoot) { $CacheRoot = Join-Path $PSScriptRoot '.cache' }

$dependencies = @(
    [pscustomobject]@{
        Name = 'mpv'
        ArchiveName = 'mpv-dev-lgpl-x86_64-20260925-git-35af06172b.7z'
        ArchiveUrl = 'https://github.com/zhongfly/mpv-winbuild/releases/download/2026-09-25-35af06172b/mpv-dev-lgpl-x86_64-20260925-git-35af06172b.7z'
        ArchiveSha256 = 'f86c2c942fa911aa0c6bc678fe5d17ae8e66e0f2aa17f8db88a03907c77ca498'
        Members = @('libmpv-2.dll')
        Payloads = @(
            [pscustomobject]@{
                Member = 'libmpv-2.dll'
                Destination = 'vendor\mpv\libmpv-2.dll'
                Sha256 = '675f8a46972bbc1ff969e54ae155f613e2f73b0190b7ed2781319d80677aeb4e'
            }
        )
    },
    [pscustomobject]@{
        Name = 'ffmpeg'
        ArchiveName = 'ffmpeg-9.0.2-essentials_build.7z'
        ArchiveUrl = 'https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-9.0.2-essentials_build.7z'
        ArchiveSha256 = '4705843ccaaf54257c16ad90f3e952ece33c17df964ecf7bfdbb0f49c7171077'
        Members = @(
            'ffmpeg-9.0.2-essentials_build/bin/ffmpeg.exe',
            'ffmpeg-9.0.2-essentials_build/bin/ffprobe.exe',
            'ffmpeg-9.0.2-essentials_build/LICENSE',
            'ffmpeg-9.0.2-essentials_build/README.txt'
        )
        Payloads = @(
            [pscustomobject]@{
                Member = 'ffmpeg-9.0.2-essentials_build/bin/ffmpeg.exe'
                Destination = 'vendor\ffmpeg\ffmpeg.exe'
                Sha256 = '3256173f3f8bffd7df12227c68adf68025edb1832273a9530688a7bb1ed8edec'
            },
            [pscustomobject]@{
                Member = 'ffmpeg-9.0.2-essentials_build/bin/ffprobe.exe'
                Destination = 'vendor\ffmpeg\ffprobe.exe'
                Sha256 = 'f0d36ecbbdd3bcfac3efa078c96c7271c2e68b3810595552ac3b7f17e9a65c52'
            },
            [pscustomobject]@{
                Member = 'ffmpeg-9.0.2-essentials_build/LICENSE'
                Destination = 'vendor\ffmpeg\LICENSE.txt'
                Sha256 = '8ceb4b9ee5adedde47b31e975c1d90c73ad27b6b165a1dcd80c7c545eb65b903'
            },
            [pscustomobject]@{
                Member = 'ffmpeg-9.0.2-essentials_build/README.txt'
                Destination = 'vendor\ffmpeg\README.txt'
                Sha256 = '0342de6bb39dd421cbec2c00d89ad274b640d75ad3f2cab09ffd647ce2d8f949'
            }
        )
    }
)

$mpvSourceFiles = @(
    [pscustomobject]@{
        Name = 'mpv LGPL license text'
        Url = 'https://raw.githubusercontent.com/mpv-player/mpv/35af06172be5199212e0406878bd0fde532d080d/LICENSE.LGPL'
        CacheName = 'mpv-LICENSE.LGPL'
        Destination = 'vendor\mpv\LICENSE.LGPL.txt'
        Sha256 = '72b672113d642cbb8ef5dcc76938db801983c56e50b1400ab930f1a64d6dc8d9'
    },
    [pscustomobject]@{
        Name = 'mpv copyright and component license notes'
        Url = 'https://raw.githubusercontent.com/mpv-player/mpv/35af06172be5199212e0406878bd0fde532d080d/Copyright'
        CacheName = 'mpv-Copyright'
        Destination = 'vendor\mpv\Copyright.txt'
        Sha256 = 'bfe9ee4cceabcb8ecbfadf208d04156f73d801e6a57369a5606bb8341e204a23'
    }
)

function Get-FullPath {
    param([Parameter(Mandatory = $true)][string]$Path)
    return [System.IO.Path]::GetFullPath($Path)
}

function Assert-Hash {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Expected,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Label がありません: $Path"
    }

    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $Expected) {
        throw "$Label の SHA-256 が一致しません。期待値=$Expected 実際=$actual ファイル=$Path"
    }
}

function Get-VerifiedDownload {
    param(
        [Parameter(Mandatory = $true)][string]$Url,
        [Parameter(Mandatory = $true)][string]$CachePath,
        [Parameter(Mandatory = $true)][string]$ExpectedSha256,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if (Test-Path -LiteralPath $CachePath -PathType Leaf) {
        $cachedHash = (Get-FileHash -LiteralPath $CachePath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($cachedHash -eq $ExpectedSha256) {
            Write-Host "キャッシュを検証しました: $Label"
            return $CachePath
        }
        Write-Warning "キャッシュのハッシュが異なるため、検証後に置き換えます: $CachePath"
    }

    $downloadPath = Join-Path (Split-Path -Parent $CachePath) ('download-' + [Guid]::NewGuid().ToString('N') + '.part')
    Write-Host "ダウンロード中: $Label"
    Invoke-WebRequest -Uri $Url -OutFile $downloadPath -UseBasicParsing -TimeoutSec 600
    Assert-Hash -Path $downloadPath -Expected $ExpectedSha256 -Label $Label
    Move-Item -LiteralPath $downloadPath -Destination $CachePath -Force
    return $CachePath
}

function Assert-ArchiveEntriesSafe {
    param(
        [Parameter(Mandatory = $true)][string[]]$Entries,
        [Parameter(Mandatory = $true)][string]$ArchiveName
    )

    foreach ($entry in $Entries) {
        $name = ([string]$entry).Trim().Replace('\', '/')
        if (-not $name) { continue }
        if ([System.IO.Path]::IsPathRooted($name) -or $name.StartsWith('/') -or $name -match '^[A-Za-z]:' -or $name -match '(^|/)\.\.(/|$)') {
            throw "アーカイブに危険なパスが含まれています: $ArchiveName : $entry"
        }
    }
}

function Invoke-Tar {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    $output = & $script:TarPath @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        $detail = (($output | ForEach-Object { [string]$_ }) -join [Environment]::NewLine)
        throw "Windows 標準 tar.exe による展開に失敗しました (exit=$exitCode): $detail"
    }
    return $output
}

function Get-VerifiedArchive {
    param(
        [Parameter(Mandatory = $true)]$Dependency,
        [string]$ProvidedPath
    )

    if ($ProvidedPath) {
        $resolvedPath = (Resolve-Path -LiteralPath $ProvidedPath).Path
        Assert-Hash -Path $resolvedPath -Expected $Dependency.ArchiveSha256 -Label "$($Dependency.Name) archive"
        return $resolvedPath
    }

    $cachePath = Join-Path $script:CacheRoot $Dependency.ArchiveName
    return Get-VerifiedDownload -Url $Dependency.ArchiveUrl -CachePath $cachePath -ExpectedSha256 $Dependency.ArchiveSha256 -Label "$($Dependency.Name) archive"
}

function Expand-VerifiedMembers {
    param(
        [Parameter(Mandatory = $true)]$Dependency,
        [Parameter(Mandatory = $true)][string]$ArchivePath,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    $listing = @(Invoke-Tar -Arguments @('-tf', $ArchivePath))
    Assert-ArchiveEntriesSafe -Entries $listing -ArchiveName $Dependency.ArchiveName

    $normalizedListing = @($listing | ForEach-Object { ([string]$_).Trim().Replace('\', '/') })
    foreach ($member in $Dependency.Members) {
        if ($normalizedListing -notcontains $member) {
            throw "固定アーカイブ内に必要なファイルがありません: $($Dependency.ArchiveName) : $member"
        }
    }

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    foreach ($member in $Dependency.Members) {
        $arguments = @('-xf', $ArchivePath, '-C', $Destination, '--', $member)
        Invoke-Tar -Arguments $arguments | Out-Null
        $memberPath = Join-Path $Destination ($member.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
        if (-not (Test-Path -LiteralPath $memberPath -PathType Leaf)) {
            throw "展開後のファイルがありません: $memberPath"
        }
    }
}

function Assert-NoReparsePoints {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { return }
    $pending = New-Object 'System.Collections.Generic.Stack[string]'
    $pending.Push($Path)
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        $item = Get-Item -LiteralPath $directory -Force
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "展開キャッシュにジャンクションまたはシンボリックリンクがあります: $directory"
        }
        foreach ($child in Get-ChildItem -LiteralPath $directory -Force) {
            if (($child.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "展開キャッシュにジャンクションまたはシンボリックリンクがあります: $($child.FullName)"
            }
            if ($child.PSIsContainer) { $pending.Push($child.FullName) }
        }
    }
}

function Assert-NoReparseAncestors {
    param([Parameter(Mandatory = $true)][string]$Path)

    $current = Get-FullPath $Path
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "配置先のパスにジャンクションまたはシンボリックリンクがあります: $current"
            }
        }
        $parent = Split-Path -Parent $current
        if (-not $parent -or $parent -eq $current) { break }
        $current = $parent
    }
}

$installRoot = Get-FullPath $InstallRoot
$script:CacheRoot = Get-FullPath $CacheRoot
Assert-NoReparseAncestors -Path $installRoot
New-Item -ItemType Directory -Path $installRoot -Force | Out-Null
New-Item -ItemType Directory -Path $script:CacheRoot -Force | Out-Null
Assert-NoReparsePoints -Path $script:CacheRoot
$script:TarPath = (Get-Command 'tar.exe' -ErrorAction Stop).Source

$staging = Join-Path $script:CacheRoot 'extract-silframe-pinned-v1'
New-Item -ItemType Directory -Path $staging -Force | Out-Null
Assert-NoReparsePoints -Path $staging

$archivePaths = @{
    mpv = Get-VerifiedArchive -Dependency $dependencies[0] -ProvidedPath $MpvArchivePath
    ffmpeg = Get-VerifiedArchive -Dependency $dependencies[1] -ProvidedPath $FfmpegArchivePath
}

foreach ($dependency in $dependencies) {
    $extractPath = Join-Path $staging $dependency.Name
    Expand-VerifiedMembers -Dependency $dependency -ArchivePath $archivePaths[$dependency.Name] -Destination $extractPath
    foreach ($payload in $dependency.Payloads) {
        $source = Join-Path $extractPath ($payload.Member.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
        Assert-Hash -Path $source -Expected $payload.Sha256 -Label "$($dependency.Name) payload $($payload.Member)"
    }
}
Assert-NoReparsePoints -Path $staging

$verifiedLicensePaths = @{}
foreach ($sourceFile in $mpvSourceFiles) {
    $licensePath = Join-Path $script:CacheRoot $sourceFile.CacheName
    $verifiedLicensePaths[$sourceFile.Destination] = Get-VerifiedDownload -Url $sourceFile.Url -CachePath $licensePath -ExpectedSha256 $sourceFile.Sha256 -Label $sourceFile.Name
}

$stagedFiles = @()
foreach ($dependency in $dependencies) {
    $extractPath = Join-Path $staging $dependency.Name
    foreach ($payload in $dependency.Payloads) {
        $source = Join-Path $extractPath ($payload.Member.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
        $destination = Join-Path $installRoot $payload.Destination
        $stagedFiles += [pscustomobject]@{ Source = $source; Destination = $destination; Sha256 = $payload.Sha256; Label = $payload.Destination }
    }
}
foreach ($sourceFile in $mpvSourceFiles) {
    $stagedFiles += [pscustomobject]@{ Source = $verifiedLicensePaths[$sourceFile.Destination]; Destination = (Join-Path $installRoot $sourceFile.Destination); Sha256 = $sourceFile.Sha256; Label = $sourceFile.Destination }
}

foreach ($file in $stagedFiles) {
    $parent = Split-Path -Parent $file.Destination
    Assert-NoReparseAncestors -Path $parent
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    if (Test-Path -LiteralPath $file.Destination) {
        if (-not (Test-Path -LiteralPath $file.Destination -PathType Leaf)) {
            throw "依存ファイルの配置先にファイル以外があります: $($file.Destination)"
        }
        Assert-Hash -Path $file.Destination -Expected $file.Sha256 -Label "既存ファイル $($file.Label)"
    }
}

foreach ($file in $stagedFiles) {
    if (-not (Test-Path -LiteralPath $file.Destination -PathType Leaf)) {
        Copy-Item -LiteralPath $file.Source -Destination $file.Destination
    }
    Assert-Hash -Path $file.Destination -Expected $file.Sha256 -Label "配置済みファイル $($file.Label)"
    Write-Host "配置を確認しました: $($file.Label)"
}

Write-Host '依存ファイルの取得、アーカイブパス検査、SHA-256 検証、配置が完了しました。'
Write-Host "インストール先: $installRoot"
