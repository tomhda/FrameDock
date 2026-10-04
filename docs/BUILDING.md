# Building FrameDock

[日本語](BUILDING.ja.md)

## Requirements

- Windows 10 version 2004 (build 19041) or later, or Windows 11, x64
- .NET 8 SDK
- An internet connection for the first dependency download, and `tar.exe` (included in Windows 11)
- PowerShell 5.1 or later

## Build and run a Release

From the repository root:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\build-release.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\run.ps1
```

`build-release.ps1` downloads the pinned dependency archives, verifies their SHA-256 hashes and those of the license texts, and stages them in `vendor\`. It then cleans the Release intermediates, publishes to `artifacts\release\win-x64`, and checks that the output contains the WinUI resources `App.xbf`, `MainWindow.xbf`, and `FrameDock.pri`. `libmpv-2.dll`, `Media\ffmpeg.exe`, and `Media\ffprobe.exe` are placed next to `FrameDock.exe`, and the licenses and build information go into `ThirdPartyNotices`.

Dependencies live in `vendor\`. Downloaded archives and temporary files live in `tools\.cache\`. Both are generated and managed by the bootstrap script.

- `-SkipDependencyBootstrap` skips the download when the dependencies are already staged and there is no network. The hashes of every binary and license text in `vendor\` are still checked.
- `-OutputDirectory` changes the output location. It must be inside the repository.

To stage the dependencies somewhere else, or to use archives you already have, call the bootstrap script directly. Archives that do not match the pinned SHA-256 are rejected.

```powershell
$bootstrapScript = Join-Path (Join-Path $PWD 'tools') 'bootstrap-dependencies.ps1'
& $bootstrapScript -InstallRoot 'D:/FrameDock-stage' -CacheRoot 'D:/FrameDock-cache' -MpvArchivePath 'D:/archives/mpv-dev-lgpl-x86_64-20260925-git-35af06172b.7z' -FfmpegArchivePath 'D:/archives/ffmpeg-9.0.2-essentials_build.7z'
```

## Build the installer

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\build-installer.ps1
```

This builds a Release and then one offline setup executable, `artifacts\installer\FrameDock-Setup-<version>-win-x64.exe`. The current app version is `1.0.0`.

The first run downloads the pinned Inno Setup 7.1.0 from its official GitHub release, checks its SHA-256 and Authenticode signature, and extracts it into `tools\.cache\` only. Inno Setup is used at build time and is not installed with FrameDock. For commercial use, see the [Inno Setup license information](https://jrsoftware.org/isorder.php).

The setup contains the Release app, the WinUI resources, libmpv, FFmpeg, and the license notices, so nothing is downloaded at install time. It installs per user into `%LOCALAPPDATA%\Programs\FrameDock` and creates a Start menu entry and an uninstaller. The desktop icon and the "Open with" registration for common video formats are optional tasks. Windows default apps are not changed. The setup is not code-signed.

`-SkipReleaseBuild` reuses an existing Release.

## Source bundle for a release

FFmpeg is distributed under GPLv3 and libmpv under LGPL, so every release is published together with the source of the bundled binaries.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\build-source-bundle.ps1
```

This downloads the FFmpeg, x264, and mpv source archives at the exact commits of the bundled binaries, checks their SHA-256 hashes, and writes them to `artifacts\sources` with `SOURCES.md`, the FFmpeg build's `README.txt`, and `SHA256SUMS.txt`. Attach every file in that folder to the release next to the setup executable. [SOURCES.md](SOURCES.md) describes the contents.

After publishing a release, update the version in the download links of `README.md` and `README.ja.md` (at the top and in the install section).

When the pinned FFmpeg or mpv build changes, update the commits and hashes in `tools\build-source-bundle.ps1` and in `docs\SOURCES.md`.

## Tests

Build the Core library and run the geometry checks, which do not need FFmpeg:

```powershell
dotnet build .\FrameDock.Core\FrameDock.Core.csproj
dotnet run --project .\FrameDock.Core.Tests\FrameDock.Core.Tests.csproj
```

To also run the encode and ffprobe integration checks with the bundled FFmpeg, set these environment variables first:

```powershell
$env:FRAMEDOCK_TEST_FFMPEG = (Resolve-Path .\vendor\ffmpeg\ffmpeg.exe).Path
$env:FRAMEDOCK_TEST_FFPROBE = (Resolve-Path .\vendor\ffmpeg\ffprobe.exe).Path
dotnet run --project .\FrameDock.Core.Tests\FrameDock.Core.Tests.csproj
```

Without both variables, the checks that need the external tools print `SKIP`. The test items and the coordinate and time contracts of Core are described in [EXPORT-NOTES.md](../EXPORT-NOTES.md).

## Interface text

UI text is in `FrameDock\Strings\<language>\Resources.resw`, and the messages of the Core library are in `FrameDock.Core\Resources\Messages.resx` (English) and `Messages.<language>.resx`. After changing or adding text, check that every language has the same keys and the same number of placeholders:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\check-strings.ps1
```

## Environment variables for testing

| Variable | Effect |
|---|---|
| `FRAMEDOCK_LANGUAGE` | `ja-JP` or `en-US`. Overrides the display language. |
| `FRAMEDOCK_SETTINGS_PATH` | Path of a settings file to use instead of `%LOCALAPPDATA%\FrameDock\settings.json`. |
| `FRAMEDOCK_OPEN_EDITOR` | `1` opens the editor as soon as a video has loaded. |
| `FRAMEDOCK_MPV_LOG` | `1` writes the mpv log to `%TEMP%\FrameDock-mpv.log`. |
