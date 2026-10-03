# Third-party software and notices

FrameDock bundles the following executable dependencies. The Release script copies the notices and upstream license texts into `ThirdPartyNotices` beside the application.

## FFmpeg

- Package: FFmpeg 9.0.2 essentials, Windows x64 static build by Gyan Doshi (`www.gyan.dev`). This build includes `libx264` and `libx265` and is marked **GPL v3** in the package README.
- Archive: [`ffmpeg-9.0.2-essentials_build.7z`](https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-9.0.2-essentials_build.7z)
- Archive SHA-256: `4705843ccaaf54257c16ad90f3e952ece33c17df964ecf7bfdbb0f49c7171077`
- Binaries: `ffmpeg.exe` SHA-256 `3256173f3f8bffd7df12227c68adf68025edb1832273a9530688a7bb1ed8edec`; `ffprobe.exe` SHA-256 `f0d36ecbbdd3bcfac3efa078c96c7271c2e68b3810595552ac3b7f17e9a65c52`.
- License text: [GNU GPL version 3](https://www.gnu.org/licenses/gpl-3.0.txt), copied from the pinned archive as `vendor/ffmpeg/LICENSE.txt` and into the Release notices.
- Source: [FFmpeg source commit `946fcce07b`](https://github.com/FFmpeg/FFmpeg/commit/946fcce07b), [Gyan's official Windows build page](https://www.gyan.dev/ffmpeg/builds/), and the [GyanD/codexffmpeg build/release repository](https://github.com/GyanD/codexffmpeg). The pinned archive's `README.txt` records its release version, build configuration, enabled external libraries, and source commit. That build README is included in `ThirdPartyNotices`.

This FFmpeg binary is GPLv3. FrameDock itself is licensed under GPLv3 (see [`LICENSE`](LICENSE)).

## libmpv

- Package: `mpv-dev-lgpl` x86_64, built by [zhongfly/mpv-winbuild](https://github.com/zhongfly/mpv-winbuild) from mpv commit [`35af06172be5199212e0406878bd0fde532d080d`](https://github.com/mpv-player/mpv/commit/35af06172be5199212e0406878bd0fde532d080d). The builder describes this variant as LGPLv2.1+ and says it excludes LGPL-incompatible packages. Its release notes also link to the [build workflow](https://github.com/zhongfly/mpv-winbuild/actions/runs/36199293941).
- Archive: [`mpv-dev-lgpl-x86_64-20260925-git-35af06172b.7z`](https://github.com/zhongfly/mpv-winbuild/releases/download/2026-09-25-35af06172b/mpv-dev-lgpl-x86_64-20260925-git-35af06172b.7z)
- Archive SHA-256: `f86c2c942fa911aa0c6bc678fe5d17ae8e66e0f2aa17f8db88a03907c77ca498`
- DLL: `libmpv-2.dll` SHA-256 `675f8a46972bbc1ff969e54ae155f613e2f73b0190b7ed2781319d80677aeb4e`.
- License text and notices: `vendor/mpv/LICENSE.LGPL.txt` is `LICENSE.LGPL` from the exact mpv commit above; `vendor/mpv/Copyright.txt` is that commit's upstream component-license notice. Both are copied to `ThirdPartyNotices`.
- Source: [mpv source commit](https://github.com/mpv-player/mpv/tree/35af06172be5199212e0406878bd0fde532d080d) and [mpv-winbuild project](https://github.com/zhongfly/mpv-winbuild). The release archive contains the development headers/import library and DLL, but no source tree; consult the linked build workflow and repository for its build recipe and source references.

## Microsoft Windows App SDK

- NuGet package `Microsoft.WindowsAppSDK` version `2.5.1`, declared by `FrameDock/FrameDock.csproj` and included in the self-contained Release output.
- Project: [Microsoft Windows App SDK](https://github.com/microsoft/windowsappsdk).
- The package's `license.txt` is copied from the restored NuGet package into `ThirdPartyNotices/Microsoft-Windows-App-SDK-LICENSE.txt` by `tools/build-release.ps1`. Follow those Microsoft Software License Terms for the redistributable package files.

## Dependency verification

`tools/bootstrap-dependencies.ps1` pins and verifies the complete archive hashes before extraction, checks archive paths for rooted or parent-traversal entries, extracts only named files, verifies each staged file hash, and rejects mismatching existing files instead of silently replacing them. It also fetches the two mpv notice files from the immutable upstream commit URL and verifies their hashes. `tools/build-release.ps1` clean-builds Release, copies and hash-checks generated WinUI XAML/PRI resources that `dotnet publish` omits, rechecks the staged native hashes, and checks that all expected files landed in the Release output.
