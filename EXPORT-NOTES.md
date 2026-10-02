# FrameDock export core

The `FrameDock.Core` library inspects local media with the injected `ffprobe.exe` and exports MP4, Matroska, or QuickTime MOV clips with the injected `ffmpeg.exe`. It does not locate, download, or install either binary; the app supplies paths to its pinned dependency bundle.

## UI contract

Construct `MediaExportService` with `new ExportOptions(ffmpegPath, ffprobePath)`. `InspectAsync(path, cancellationToken)` returns `MediaInfo`; `ExportAsync(request, progress, cancellationToken)` returns `ExportResult`. Progress phases are `Preparing`, `Encoding`, `Verifying`, and `Completed`. `ExportException.UserMessage` is Japanese UI copy, and `Diagnostic` carries bounded FFmpeg or ffprobe detail.

`MediaInfo.CodedWidth` and `CodedHeight` are the stored pixel dimensions. `DisplayWidth` and `DisplayHeight` are the crop editor's square-pixel, oriented frame dimensions. Core computes the unrotated display width by rounding `CodedWidth * SampleAspectRatio` to the nearest integer (half values round up), retains coded height, then swaps dimensions for 90° or 270° rotation. FFprobe display-matrix rotation is reported as counter-clockwise, so Core normalizes it to `RotationDegreesClockwise` for the UI.

`CropRect` is an integer rectangle from the top-left of that oriented display frame. Core scales for SAR, applies the right-angle rotation, then crops. Its x, y, width, and height must fit inside `DisplayWidth`/`DisplayHeight` and each must be even for the yuv420 H.264 output. With no crop, an odd display edge is padded by at most one pixel on the right or bottom so the video encoder receives even dimensions.

`ExportRequest.StartSeconds` and `EndSeconds` are seconds from the beginning of the presented clip, including containers whose first timestamp is nonzero. Core probes `start_time` and uses output-side seek with decoding for those inputs; accurate export divides the seek position by `PlaybackSpeed` because output-side seeking runs after the video and audio speed filters. When accurate export starts more than 15 seconds into such an input, Core first seeks the input to 10 seconds before the start and then applies the remaining 10 seconds (divided by `PlaybackSpeed`) as the output-side seek, so late trims do not decode the whole preroll. Ordinary zero-start media uses FFmpeg's accurate input seek. Time values must be finite, start must be nonnegative, end must be greater than start, and end must not exceed the probed duration.

## Export behavior and limits

- `ExportRequest.OutputContainer` selects MP4, Matroska, or MOV, and the destination extension must match it (`.mp4`, `.mkv`, `.mov`). Core writes to a unique temporary file beside the destination, validates it with ffprobe, then commits with a no-overwrite file move. Existing destinations and source path aliases are rejected.
- `AccurateReencode` is the default. It uses the CPU `libx264` encoder, medium preset, CRF 18, AAC audio at 192 kbit/s, and square-pixel output. `ExportOptions.Encoder` can select `VideoEncoder.H264` or `VideoEncoder.H265`; `Preset` and `ConstantRateFactor` are configurable. The pinned build must contain the selected software encoder. H.265 output in MP4 or MOV is tagged `hvc1` so Apple players accept it. Both encoder choices are covered by the opt-in integration harness.
- Accurate HDR re-encoding is rejected with a Japanese message to avoid silently changing HDR colors. `StreamCopyApproximate` preserves the encoded streams and HDR signaling, allows no crop, and sets `ExportResult.BoundariesAreApproximate` to `true`. Its trim points are constrained by keyframes; the UI should label this mode as approximate. If the requested interval has no usable keyframe, the error recommends accurate re-encoding.
- Stream-copy output retains the source coded dimensions, sample aspect ratio, rotation, and codecs. Accurate output bakes rotation into pixels and removes the display-matrix side data so players do not rotate it again.

## Build and integration checks

From the repository root:

```powershell
dotnet build .\FrameDock.Core\FrameDock.Core.csproj
$env:FRAMEDOCK_TEST_FFMPEG = "$PWD\vendor\ffmpeg\ffmpeg.exe"
$env:FRAMEDOCK_TEST_FFPROBE = "$PWD\vendor\ffmpeg\ffprobe.exe"
dotnet run --project .\FrameDock.Core.Tests\FrameDock.Core.Tests.csproj
```

The test harness uses no test-framework NuGet packages. Without both environment variables it prints an explicit `SKIP` for executable integration checks while still running the geometry tests. With them set, it creates short local fixtures and checks ffprobe properties, SAR plus rotation using a colored-quadrant crop, Unicode and shell-special filename handling, accurate duration and crop bounds, no-audio input, HDR rejection/preservation, approximate stream-copy behavior, nonzero MPEG-TS `start_time`, case-insensitive source aliases, existing output preservation, and active-process cancellation cleanup/source preservation.
