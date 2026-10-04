[CmdletBinding()]
param(
    [string]$ReleaseDirectory,
    [string]$OutputDirectory,
    [switch]$SkipReleaseBuild,
    [switch]$SkipDependencyBootstrap
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$scriptDirectory = [System.IO.Path]::GetFullPath($PSScriptRoot)
$repositoryRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $scriptDirectory))
if (-not $ReleaseDirectory) { $ReleaseDirectory = Join-Path $repositoryRoot 'artifacts\release\win-x64' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repositoryRoot 'artifacts\installer' }
$releasePath = [System.IO.Path]::GetFullPath($ReleaseDirectory)
$outputPath = [System.IO.Path]::GetFullPath($OutputDirectory)
$requiredOutputPrefix = $repositoryRoot.TrimEnd('\') + '\'
foreach ($path in @($releasePath, $outputPath)) {
    if (-not $path.StartsWith($requiredOutputPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Installer 入出力先はリポジトリ内に指定してください: $path"
    }
}

function Assert-NoReparsePath {
    param([Parameter(Mandatory = $true)][string]$Path)

    $currentPath = $Path
    while ($currentPath) {
        if (Test-Path -LiteralPath $currentPath) {
            $item = Get-Item -LiteralPath $currentPath -Force
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Installer パスにジャンクションまたはシンボリックリンクがあります: $currentPath"
            }
        }
        $parentPath = Split-Path -Parent $currentPath
        if (-not $parentPath -or $parentPath -eq $currentPath) { break }
        $currentPath = $parentPath
    }

    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { return }
    $pending = New-Object 'System.Collections.Generic.Stack[string]'
    $pending.Push($Path)
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        foreach ($child in Get-ChildItem -LiteralPath $directory -Force) {
            if (($child.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Installer 入出力にジャンクションまたはシンボリックリンクがあります: $($child.FullName)"
            }
            if ($child.PSIsContainer) { $pending.Push($child.FullName) }
        }
    }
}

Assert-NoReparsePath -Path $releasePath
Assert-NoReparsePath -Path $outputPath

if (-not $SkipReleaseBuild) {
    $releaseScript = Join-Path $scriptDirectory 'build-release.ps1'
    $releaseArguments = @{ OutputDirectory = $releasePath }
    if ($SkipDependencyBootstrap) { $releaseArguments.SkipDependencyBootstrap = $true }
    & $releaseScript @releaseArguments
    if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) {
        throw "Release build が失敗しました (exit=$LASTEXITCODE)"
    }
}

$requiredFiles = @(
    'SILframe.exe',
    'SILframe.dll',
    'SILframe.deps.json',
    'SILframe.runtimeconfig.json',
    'SILframe.pri',
    'App.xbf',
    'MainWindow.xbf',
    'libmpv-2.dll',
    'Media\ffmpeg.exe',
    'Media\ffprobe.exe',
    'ThirdPartyNotices\THIRD-PARTY-LICENSES.md',
    'ThirdPartyNotices\FFmpeg-LICENSE.txt',
    'ThirdPartyNotices\FFmpeg-BUILD-README.txt',
    'ThirdPartyNotices\mpv-LICENSE.LGPL.txt',
    'ThirdPartyNotices\mpv-Copyright.txt',
    'ThirdPartyNotices\Microsoft-Windows-App-SDK-LICENSE.txt'
)
foreach ($relativePath in $requiredFiles) {
    $requiredPath = Join-Path $releasePath $relativePath
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Release 出力にインストーラーの必須ファイルがありません: $requiredPath"
    }
}

$pinnedFiles = @(
    [pscustomobject]@{ Path = 'libmpv-2.dll'; Sha256 = '675f8a46972bbc1ff969e54ae155f613e2f73b0190b7ed2781319d80677aeb4e' },
    [pscustomobject]@{ Path = 'Media\ffmpeg.exe'; Sha256 = '3256173f3f8bffd7df12227c68adf68025edb1832273a9530688a7bb1ed8edec' },
    [pscustomobject]@{ Path = 'Media\ffprobe.exe'; Sha256 = 'f0d36ecbbdd3bcfac3efa078c96c7271c2e68b3810595552ac3b7f17e9a65c52' }
)
foreach ($expected in $pinnedFiles) {
    $filePath = Join-Path $releasePath $expected.Path
    $actual = (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expected.Sha256) {
        throw "Release 出力のネイティブ依存ファイルの SHA-256 が一致しません: $filePath"
    }
}

$productVersion = (Get-Item -LiteralPath (Join-Path $releasePath 'SILframe.exe')).VersionInfo.ProductVersion
if ($productVersion -notmatch '^(\d+\.\d+\.\d+)(?:\.\d+)?(?:\+.*)?$') {
    throw "SILframe.exe からセットアップのバージョンを取得できません: $productVersion"
}
$appVersion = $Matches[1]

$compilerVersion = '7.1.0'
$compilerCache = Join-Path $scriptDirectory ('.cache\innosetup-' + $compilerVersion)
$compilerHome = Join-Path $compilerCache 'portable'
$compilerPath = Join-Path $compilerHome 'ISCC.exe'
$bootstrapInstaller = Join-Path $compilerCache ('innosetup-' + $compilerVersion + '-x64.exe')
$expectedCompilerHash = '0362a383ed217d4c4239b5933866dd96d3eb2102737da92f80f6057a4b40df2f'
$expectedCompilerExeHash = 'd06ebd38f38e3cee60a3c50cc45bd449d77e0bc6a5cabc607ea9886808e4de1a'
$compilerDownloadUrl = 'https://github.com/jrsoftware/issrc/releases/download/is-7_1_0/innosetup-7.1.0-x64.exe'

New-Item -ItemType Directory -Path $compilerCache -Force | Out-Null
Assert-NoReparsePath -Path $compilerCache
if (-not (Test-Path -LiteralPath $compilerPath -PathType Leaf)) {
    if (-not (Test-Path -LiteralPath $bootstrapInstaller -PathType Leaf)) {
        Write-Host "Inno Setup $compilerVersion を公式リリースから取得します。"
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri $compilerDownloadUrl -OutFile $bootstrapInstaller -UseBasicParsing
    }

    $actualCompilerHash = (Get-FileHash -LiteralPath $bootstrapInstaller -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualCompilerHash -ne $expectedCompilerHash) {
        throw "Inno Setup installer の SHA-256 が一致しません: $bootstrapInstaller"
    }
    $signature = Get-AuthenticodeSignature -FilePath $bootstrapInstaller
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
        -not $signature.SignerCertificate -or
        $signature.SignerCertificate.GetNameInfo([System.Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false) -ne 'Pyrsys B.V.') {
        throw "Inno Setup installer の Authenticode 署名を確認できません: $($signature.StatusMessage)"
    }

    New-Item -ItemType Directory -Path $compilerHome -Force | Out-Null
    $arguments = '/PORTABLE=1 /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /DIR="' + $compilerHome + '"'
    $process = Start-Process -FilePath $bootstrapInstaller -ArgumentList $arguments -Wait -PassThru
    if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $compilerPath -PathType Leaf)) {
        throw "Inno Setup のローカル展開に失敗しました (exit=$($process.ExitCode))"
    }
}

$versionOutput = (& $compilerPath --version | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $versionOutput -ne $compilerVersion) {
    throw "Inno Setup compiler のバージョンが想定と異なります: $versionOutput"
}
$actualCompilerExeHash = (Get-FileHash -LiteralPath $compilerPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualCompilerExeHash -ne $expectedCompilerExeHash) {
    throw "Inno Setup compiler の SHA-256 が一致しません: $compilerPath"
}
$compilerSignature = Get-AuthenticodeSignature -FilePath $compilerPath
if ($compilerSignature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
    -not $compilerSignature.SignerCertificate -or
    $compilerSignature.SignerCertificate.GetNameInfo([System.Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false) -ne 'Pyrsys B.V.') {
    throw "Inno Setup compiler の Authenticode 署名を確認できません: $($compilerSignature.StatusMessage)"
}

New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
Assert-NoReparsePath -Path $outputPath
$installerScript = Join-Path $repositoryRoot 'installer\SILframe.iss'
$previousAppVersion = $env:SILFRAME_APP_VERSION
$previousPublishDir = $env:SILFRAME_PUBLISH_DIR
try {
    $env:SILFRAME_APP_VERSION = $appVersion
    $env:SILFRAME_PUBLISH_DIR = $releasePath
    Write-Host "SILframe Setup を作成します (version $appVersion)。"
    & $compilerPath --quiet-progress ("--output-dir=$outputPath") $installerScript
    if ($LASTEXITCODE -ne 0) {
        throw "Inno Setup compiler が失敗しました (exit=$LASTEXITCODE)"
    }
}
finally {
    if ($null -eq $previousAppVersion) { Remove-Item Env:\SILFRAME_APP_VERSION -ErrorAction SilentlyContinue }
    else { $env:SILFRAME_APP_VERSION = $previousAppVersion }
    if ($null -eq $previousPublishDir) { Remove-Item Env:\SILFRAME_PUBLISH_DIR -ErrorAction SilentlyContinue }
    else { $env:SILFRAME_PUBLISH_DIR = $previousPublishDir }
}

$installerPath = Join-Path $outputPath "SILframe-Setup-$appVersion-win-x64.exe"
if (-not (Test-Path -LiteralPath $installerPath -PathType Leaf)) {
    throw "Inno Setup がセットアップファイルを出力しませんでした: $installerPath"
}
$installer = Get-Item -LiteralPath $installerPath
Write-Host "セットアップを作成しました: $($installer.FullName)"
Write-Host ('サイズ: {0:N1} MB' -f ($installer.Length / 1MB))
