# Source code for the bundled binaries

SILframe's installer contains `ffmpeg.exe` and `ffprobe.exe` (a GPLv3 build of FFmpeg) and `libmpv-2.dll` (LGPLv2.1 or later). This file accompanies the source archives that are published with each SILframe release. SILframe's own source is the repository the release is tagged in.

## Archives in the source bundle

`tools\build-source-bundle.ps1` downloads these archives, checks their SHA-256 hashes, and places them in `artifacts\sources` together with this file and the FFmpeg build's own `README.txt`.

| File | Contents | Upstream |
|---|---|---|
| `ffmpeg-946fcce07b-source.tar.gz` | FFmpeg at the commit the bundled build was made from | <https://github.com/FFmpeg/FFmpeg/commit/946fcce07b6dcd0331c8cc609192aeff5e1924f8> |
| `x264-0480cb05-source.tar.gz` | x264 v0.165.3223, the H.264 encoder SILframe uses for exports | <https://code.videolan.org/videolan/x264/-/commit/0480cb05fa188d37ae87e8f4fd8f1aea3711f7ee> |
| `mpv-35af06172b-source.tar.gz` | mpv at the commit `libmpv-2.dll` was built from | <https://github.com/mpv-player/mpv/commit/35af06172be5199212e0406878bd0fde532d080d> |
| `FFmpeg-BUILD-README.txt` | The build configuration of the bundled FFmpeg, and the versions of every external library linked into it | <https://www.gyan.dev/ffmpeg/builds/> |

## Other libraries linked into the FFmpeg build

The bundled FFmpeg is the "essentials" build 9.0.2 by Gyan Doshi. It is statically linked with the external libraries listed, with their versions, at the end of `FFmpeg-BUILD-README.txt`. Their sources are available from their upstream projects at those versions. The build recipe is published at <https://github.com/GyanD/codexffmpeg>.

x265 is listed there as `4.3-45-g116b875`. That commit is not present in the public x265 repository (<https://bitbucket.org/multicoreware/x265_git>), so no x265 archive is included in this bundle. SILframe's interface does not use the x265 encoder.

If you need the source of any of these libraries and cannot obtain it from its upstream project, open an issue in the SILframe repository and it will be provided.

## libmpv

`libmpv-2.dll` is the `mpv-dev-lgpl` x86_64 build by [zhongfly/mpv-winbuild](https://github.com/zhongfly/mpv-winbuild), which excludes LGPL-incompatible components. SILframe loads it as a separate DLL, so it can be replaced with another build of the same API version. The build workflow and its library references are linked from the release notes of that project.

Hashes of the bundled binaries and the license texts are listed in [THIRD-PARTY-LICENSES.md](../THIRD-PARTY-LICENSES.md).
