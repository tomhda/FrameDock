[CmdletBinding()]
param(
    [string]$ReleaseDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $ReleaseDirectory) { $ReleaseDirectory = Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\release\win-x64' }

$releasePath = [System.IO.Path]::GetFullPath($ReleaseDirectory)
$executablePath = Join-Path $releasePath 'SILframe.exe'
if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
    throw "Release 実行ファイルがありません。先に .\tools\build-release.ps1 を実行してください: $executablePath"
}

Start-Process -FilePath $executablePath -WorkingDirectory $releasePath
