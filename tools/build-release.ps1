[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [switch]$SkipDependencyBootstrap
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repositoryRoot 'artifacts\release\win-x64' }
$outputPath = [System.IO.Path]::GetFullPath($OutputDirectory)
$requiredOutputPrefix = $repositoryRoot.TrimEnd('\') + '\'
if (-not $outputPath.StartsWith($requiredOutputPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Release 出力先はリポジトリ内に指定してください: $outputPath"
}

function Assert-NoReparsePath {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { return }
    $pending = New-Object 'System.Collections.Generic.Stack[string]'
    $pending.Push($Path)
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        $item = Get-Item -LiteralPath $directory -Force
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Release 出力先にジャンクションまたはシンボリックリンクがあります: $directory"
        }
        foreach ($child in Get-ChildItem -LiteralPath $directory -Force) {
            if (($child.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Release 出力先にジャンクションまたはシンボリックリンクがあります: $($child.FullName)"
            }
            if ($child.PSIsContainer) { $pending.Push($child.FullName) }
        }
    }
}

$currentPath = $outputPath
while ($currentPath) {
    if (Test-Path -LiteralPath $currentPath) {
        $item = Get-Item -LiteralPath $currentPath -Force
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Release 出力先の親パスにジャンクションまたはシンボリックリンクがあります: $currentPath"
        }
    }
    $parentPath = Split-Path -Parent $currentPath
    if (-not $parentPath -or $parentPath -eq $currentPath) { break }
    $currentPath = $parentPath
}

$projectPath = Join-Path $repositoryRoot 'FrameDock\FrameDock.csproj'
$bootstrapPath = Join-Path $PSScriptRoot 'bootstrap-dependencies.ps1'
[xml]$projectDocument = Get-Content -LiteralPath $projectPath -Raw
$targetFrameworkValues = @($projectDocument.Project.PropertyGroup | ForEach-Object { [string]$_.TargetFramework } | Where-Object { $_ })
if ($targetFrameworkValues.Count -eq 0) {
    throw "TargetFramework を読み取れませんでした: $projectPath"
}
$binaryOutputRoot = Join-Path (Split-Path -Parent $projectPath) ("bin\Release\{0}\win-x64" -f $targetFrameworkValues[0])
$expectedFiles = @(
    [pscustomobject]@{ Path = 'vendor\mpv\libmpv-2.dll'; Sha256 = '675f8a46972bbc1ff969e54ae155f613e2f73b0190b7ed2781319d80677aeb4e' },
    [pscustomobject]@{ Path = 'vendor\ffmpeg\ffmpeg.exe'; Sha256 = '3256173f3f8bffd7df12227c68adf68025edb1832273a9530688a7bb1ed8edec' },
    [pscustomobject]@{ Path = 'vendor\ffmpeg\ffprobe.exe'; Sha256 = 'f0d36ecbbdd3bcfac3efa078c96c7271c2e68b3810595552ac3b7f17e9a65c52' },
    [pscustomobject]@{ Path = 'vendor\ffmpeg\LICENSE.txt'; Sha256 = '8ceb4b9ee5adedde47b31e975c1d90c73ad27b6b165a1dcd80c7c545eb65b903' },
    [pscustomobject]@{ Path = 'vendor\ffmpeg\README.txt'; Sha256 = '0342de6bb39dd421cbec2c00d89ad274b640d75ad3f2cab09ffd647ce2d8f949' },
    [pscustomobject]@{ Path = 'vendor\mpv\LICENSE.LGPL.txt'; Sha256 = '72b672113d642cbb8ef5dcc76938db801983c56e50b1400ab930f1a64d6dc8d9' },
    [pscustomobject]@{ Path = 'vendor\mpv\Copyright.txt'; Sha256 = 'bfe9ee4cceabcb8ecbfadf208d04156f73d801e6a57369a5606bb8341e204a23' }
)

if (-not $SkipDependencyBootstrap) {
    & $bootstrapPath
}

foreach ($expected in $expectedFiles) {
    $path = Join-Path $repositoryRoot $expected.Path
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "必要な依存ファイルがありません。 .\tools\bootstrap-dependencies.ps1 を実行してください: $path"
    }
    $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expected.Sha256) {
        throw "依存ファイルの SHA-256 が一致しません。再取得後に再実行してください: $path"
    }
}

$dotnet = Get-Command 'dotnet.exe' -ErrorAction Stop
$sdkText = (& $dotnet.Source --version | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $sdkText -notmatch '^8\.') {
    throw ".NET 8 SDK が必要です。検出した SDK: $sdkText"
}

New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
Assert-NoReparsePath -Path $outputPath
Write-Host 'Release の古い中間生成物を clean します。'
& $dotnet.Source clean $projectPath -c Release --verbosity quiet
if ($LASTEXITCODE -ne 0) {
    throw "dotnet clean が失敗しました (exit=$LASTEXITCODE)"
}
Write-Host "Release x64 を発行します: $outputPath"
& $dotnet.Source publish $projectPath -c Release -r win-x64 --self-contained true -o $outputPath
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish が失敗しました (exit=$LASTEXITCODE)"
}

$xamlResources = @()
foreach ($requiredResource in @('App.xbf', 'MainWindow.xbf')) {
    $resourcePath = Join-Path $binaryOutputRoot $requiredResource
    if (-not (Test-Path -LiteralPath $resourcePath -PathType Leaf)) {
        throw "Release bin に必要な WinUI XAML resource がありません: $resourcePath"
    }
    $xamlResources += Get-Item -LiteralPath $resourcePath
}
$priPath = Join-Path $binaryOutputRoot 'FrameDock.pri'
if (-not (Test-Path -LiteralPath $priPath -PathType Leaf)) {
    throw "Release bin に必要な PRI resource がありません: $priPath"
}
$xamlResources += Get-Item -LiteralPath $priPath

foreach ($resource in $xamlResources) {
    $destination = Join-Path $outputPath $resource.Name
    Copy-Item -LiteralPath $resource.FullName -Destination $destination -Force
    $sourceHash = (Get-FileHash -LiteralPath $resource.FullName -Algorithm SHA256).Hash
    $destinationHash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
    if ($sourceHash -ne $destinationHash) {
        throw "WinUI resource のコピー検証に失敗しました: $destination"
    }
    Write-Host "WinUI resource を確認しました: $($resource.Name)"
}

$noticesPath = Join-Path $outputPath 'ThirdPartyNotices'
New-Item -ItemType Directory -Path $noticesPath -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'THIRD-PARTY-LICENSES.md') -Destination (Join-Path $noticesPath 'THIRD-PARTY-LICENSES.md') -Force
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'vendor\ffmpeg\LICENSE.txt') -Destination (Join-Path $noticesPath 'FFmpeg-LICENSE.txt') -Force
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'vendor\ffmpeg\README.txt') -Destination (Join-Path $noticesPath 'FFmpeg-BUILD-README.txt') -Force
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'vendor\mpv\LICENSE.LGPL.txt') -Destination (Join-Path $noticesPath 'mpv-LICENSE.LGPL.txt') -Force
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'vendor\mpv\Copyright.txt') -Destination (Join-Path $noticesPath 'mpv-Copyright.txt') -Force
$nugetLocations = (& $dotnet.Source nuget locals global-packages --list | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $nugetLocations -notmatch 'global-packages:\s*(.+)') {
    throw 'NuGet の global-packages ディレクトリを特定できませんでした。'
}
$nugetRoot = $Matches[1].Trim()
$windowsAppSdkLicense = Join-Path $nugetRoot 'microsoft.windowsappsdk\2.5.1\license.txt'
if (-not (Test-Path -LiteralPath $windowsAppSdkLicense -PathType Leaf)) {
    throw "Windows App SDK のライセンス文書がありません: $windowsAppSdkLicense"
}
Copy-Item -LiteralPath $windowsAppSdkLicense -Destination (Join-Path $noticesPath 'Microsoft-Windows-App-SDK-LICENSE.txt') -Force

$publishedFiles = @(
    [pscustomobject]@{ Path = 'FrameDock.exe'; Sha256 = $null },
    [pscustomobject]@{ Path = 'libmpv-2.dll'; Sha256 = '675f8a46972bbc1ff969e54ae155f613e2f73b0190b7ed2781319d80677aeb4e' },
    [pscustomobject]@{ Path = 'Media\ffmpeg.exe'; Sha256 = '3256173f3f8bffd7df12227c68adf68025edb1832273a9530688a7bb1ed8edec' },
    [pscustomobject]@{ Path = 'Media\ffprobe.exe'; Sha256 = 'f0d36ecbbdd3bcfac3efa078c96c7271c2e68b3810595552ac3b7f17e9a65c52' }
)
foreach ($published in $publishedFiles) {
    $path = Join-Path $outputPath $published.Path
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "発行出力が不足しています: $path"
    }
    if ($published.Sha256) {
        $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $published.Sha256) {
            throw "発行出力の依存バイナリが想定と異なります: $path"
        }
    }
}

Write-Host 'Release 出力と同梱ライセンス文書を確認しました。'
Write-Host "実行ファイル: $(Join-Path $outputPath 'FrameDock.exe')"
