using System.Diagnostics;
using System.Globalization;
using System.Resources;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FrameDock.Core;

namespace FrameDock.Core.Tests;

internal static class Program
{
    private static int _passed;

    private static async Task<int> Main()
    {
        var japaneseCulture = CultureInfo.GetCultureInfo("ja-JP");
        CultureInfo.CurrentUICulture = japaneseCulture;
        CultureInfo.DefaultThreadCurrentUICulture = japaneseCulture;
        Run("SAR and rotation produce oriented square-pixel dimensions", TestDisplayDimensions);
        Run("crop bounds and H.264 chroma alignment are enforced", TestCropValidation);
        Run("zoom sample rectangles and display-to-coded mapping honor focus, SAR, and rotation", TestZoomGeometry);
        Run("rotation accepts only right angles", TestRotationValidation);
        Run("localized messages follow CurrentUICulture and resx keys match", TestLocalization);

        var ffmpegPath = Environment.GetEnvironmentVariable("FRAMEDOCK_TEST_FFMPEG");
        var ffprobePath = Environment.GetEnvironmentVariable("FRAMEDOCK_TEST_FFPROBE");
        if (string.IsNullOrWhiteSpace(ffmpegPath) || string.IsNullOrWhiteSpace(ffprobePath) ||
            !File.Exists(ffmpegPath) || !File.Exists(ffprobePath))
        {
            Console.WriteLine("SKIP: FFmpeg integration checks require FRAMEDOCK_TEST_FFMPEG and FRAMEDOCK_TEST_FFPROBE to point to local executables.");
            Console.WriteLine($"PASS: {_passed} unit checks; integration checks skipped.");
            return 0;
        }

        try
        {
            await RunIntegrationAsync(ffmpegPath, ffprobePath);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FAIL: {exception}");
            return 1;
        }

        Console.WriteLine($"PASS: {_passed} checks, including FFmpeg/ffprobe integration.");
        return 0;
    }

    private static async Task RunIntegrationAsync(string ffmpegPath, string ffprobePath)
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"framedock-core-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testRoot);
        try
        {
            var service = new MediaExportService(new ExportOptions(ffmpegPath, ffprobePath)
            {
                Preset = H264Preset.Ultrafast,
                ConstantRateFactor = 18
            });
            var source = await CreateRotatedSarFixtureAsync(ffmpegPath, testRoot);

            var info = await service.InspectAsync(source);
            Assert(info.CodedWidth == 160 && info.CodedHeight == 96, "coded dimensions should be read from ffprobe");
            Assert(info.SampleAspectRatio == new AspectRatio(4, 3), $"SAR should be 4:3, got {info.SampleAspectRatio}");
            Assert(info.RotationDegreesClockwise == 270, $"ffprobe's 90 degree counter-clockwise display matrix should normalize to 270 clockwise, got {info.RotationDegreesClockwise}");
            Assert(info.DisplayWidth == 96 && info.DisplayHeight == 213, $"display dimensions should be 96x213, got {info.DisplayWidth}x{info.DisplayHeight}");
            Assert(info.HasAudio && string.Equals(info.AudioCodec, "aac", StringComparison.OrdinalIgnoreCase), "audio stream should be detected");
            Assert(string.Equals(info.VideoCodec, "h264", StringComparison.OrdinalIgnoreCase), "video codec should be detected");
            AssertContainer(info, ExportContainer.Mp4, "fixture");
            Pass("ffprobe reads dimensions, SAR, rotation, duration, codecs, and audio");
            Assert(info.FrameRate.HasValue && Math.Abs(info.FrameRate.Value - 30) < 0.01, $"frame rate should be 30 fps, got {info.FrameRate}");
            Assert(info.AudioChannels == 1, $"audio channels should be 1, got {info.AudioChannels}");
            Assert(info.AudioSampleRate == 48000, $"audio sample rate should be 48000, got {info.AudioSampleRate}");
            Assert(info.AudioStreamCount == 1, $"audio stream count should be 1, got {info.AudioStreamCount}");
            Assert(info.SubtitleStreamCount == 0, $"subtitle stream count should be 0, got {info.SubtitleStreamCount}");
            Assert(info.FileSizeBytes.HasValue && info.FileSizeBytes.Value == new FileInfo(source).Length, $"file size should match the fixture bytes, got {info.FileSizeBytes}");
            Pass("ffprobe reads frame rate, audio channels, sample rate, stream counts, and file size");
            var editorFrame = Path.Combine(testRoot, "editor-frame.png");
            await service.ExportFrameAsync(source, editorFrame, 2.0, new CropRect(0, 0, 48, 64), 90, 1.0, 0.5, 0.5);
            Assert(File.Exists(editorFrame), "editor frame save should create a PNG");
            var editorFrameSize = await ReadImageDimensionsAsync(ffprobePath, editorFrame);
            Assert(editorFrameSize == (64, 48), $"a 48x64 display-space crop with an extra 90 degree rotation should save 64x48 pixels, got {editorFrameSize}");
            var editorFramePixel = await ReadFirstFramePixelAsync(ffmpegPath, editorFrame, 32, 24, testRoot);
            Assert(editorFramePixel.G > editorFramePixel.R * 1.5 && editorFramePixel.G > editorFramePixel.B * 1.5,
                $"the crop-then-rotate frame should keep the oriented green quadrant, got RGB {editorFramePixel.R},{editorFramePixel.G},{editorFramePixel.B}");
            var zoomFrame = Path.Combine(testRoot, "zoom-frame.png");
            await service.ExportFrameAsync(source, zoomFrame, 2.0, null, 0, 2.0, 0.5, 0.5);
            var zoomFrameSize = await ReadImageDimensionsAsync(ffprobePath, zoomFrame);
            Assert(zoomFrameSize == (96, 214), $"a 2x zoom frame should keep the 96x214 export canvas, got {zoomFrameSize}");
            var zoomFramePixel = await ReadFirstFramePixelAsync(ffmpegPath, zoomFrame, 20, 20, testRoot);
            Assert(zoomFramePixel.G > zoomFramePixel.R * 1.5 && zoomFramePixel.G > zoomFramePixel.B * 1.5,
                $"the zoomed frame should magnify the oriented green quadrant, got RGB {zoomFramePixel.R},{zoomFramePixel.G},{zoomFramePixel.B}");
            Pass("editor frame save matches the export filter for crop, rotation, and zoom sizes and pixels");

            var specialOutput = Path.Combine(testRoot, "clip 日本 & ; ' first.mp4");
            var sourceHash = await HashFileAsync(source);
            var cropResult = await service.ExportAsync(new ExportRequest(
                source,
                specialOutput,
                StartSeconds: 1,
                EndSeconds: 3,
                Crop: new CropRect(0, 0, 48, 48)));
            Assert(File.Exists(specialOutput), "accurate export should create the output");
            Assert(cropResult.OutputMediaInfo.CodedWidth == 48 && cropResult.OutputMediaInfo.CodedHeight == 48, "crop output dimensions should match the requested rectangle");
            Assert(cropResult.OutputMediaInfo.RotationDegreesClockwise == 0, "accurate output must bake in orientation and clear display rotation");
            Assert(cropResult.OutputMediaInfo.DurationSeconds is > 1.8 and < 2.2, $"accurate output duration should be close to 2 seconds, got {cropResult.OutputMediaInfo.DurationSeconds}");
            Assert(sourceHash == await HashFileAsync(source), "accurate export must preserve the input bytes");
            var cornerPixel = await ReadFirstFramePixelAsync(ffmpegPath, specialOutput, x: 24, y: 24, testRoot);
            Assert(cornerPixel.G > cornerPixel.R * 1.5 && cornerPixel.G > cornerPixel.B * 1.5,
                $"the oriented top-left crop should be green (input's top-right quadrant), got RGB {cornerPixel.R},{cornerPixel.G},{cornerPixel.B}");
            Pass("accurate export bakes rotation, crops the displayed green quadrant, and preserves source bytes");

            var rotatedCropOutput = Path.Combine(testRoot, "crop-then-rotate.mp4");
            var rotatedCropResult = await service.ExportAsync(new ExportRequest(
                source,
                rotatedCropOutput,
                StartSeconds: 1,
                EndSeconds: 3,
                Crop: new CropRect(0, 0, 48, 64),
                AdditionalRotationDegreesClockwise: 90));
            Assert(rotatedCropResult.OutputMediaInfo.CodedWidth == 64 && rotatedCropResult.OutputMediaInfo.CodedHeight == 48,
                "an additional 90 degree rotation after a 48x64 display-space crop should produce 64x48 pixels");
            Assert(rotatedCropResult.OutputMediaInfo.RotationDegreesClockwise == 0,
                "additional rotation should be baked into the pixels without display metadata");
            var rotatedCornerPixel = await ReadFirstFramePixelAsync(ffmpegPath, rotatedCropOutput, x: 32, y: 24, testRoot);
            Assert(rotatedCornerPixel.G > rotatedCornerPixel.R * 1.5 && rotatedCornerPixel.G > rotatedCornerPixel.B * 1.5,
                $"crop-then-clockwise-rotate should keep the expected source quadrant, got RGB {rotatedCornerPixel.R},{rotatedCornerPixel.G},{rotatedCornerPixel.B}");
            Pass("additional rotation follows display-space crop and rotates actual SAR-corrected pixels clockwise");

            var defaultZoomOutput = Path.Combine(testRoot, "zoom-default.mp4");
            var explicitOneZoomOutput = Path.Combine(testRoot, "zoom-one.mp4");
            await service.ExportAsync(new ExportRequest(source, defaultZoomOutput, 1, 3));
            await service.ExportAsync(new ExportRequest(source, explicitOneZoomOutput, 1, 3, ZoomFactor: 1.0));
            foreach (var (x, y) in new[] { (20, 20), (80, 180), (48, 106) })
            {
                Assert(await ReadFirstFramePixelAsync(ffmpegPath, defaultZoomOutput, x, y, testRoot) ==
                       await ReadFirstFramePixelAsync(ffmpegPath, explicitOneZoomOutput, x, y, testRoot),
                    "default zoom and explicit 1x zoom should preserve the same decoded pixels");
            }
            Pass("default zoom is pixel-identical to explicit 1x export");

            var centerZoomOutput = Path.Combine(testRoot, "zoom-center.mp4");
            var centerZoomResult = await service.ExportAsync(new ExportRequest(
                source,
                centerZoomOutput,
                StartSeconds: 1,
                EndSeconds: 3,
                ZoomFactor: 2.0));
            Assert(centerZoomResult.OutputMediaInfo.CodedWidth == 96 && centerZoomResult.OutputMediaInfo.CodedHeight == 214,
                "zoom should preserve the base display canvas and only pad its odd height to the encoder's even dimension");
            var centerZoomPixel = await ReadFirstFramePixelAsync(ffmpegPath, centerZoomOutput, 20, 20, testRoot);
            Assert(centerZoomPixel.G > centerZoomPixel.R * 1.5 && centerZoomPixel.G > centerZoomPixel.B * 1.5,
                $"center zoom should magnify the source's oriented top-left green quadrant, got RGB {centerZoomPixel.R},{centerZoomPixel.G},{centerZoomPixel.B}");

            var offCenterZoomOutput = Path.Combine(testRoot, "zoom-off-center.mp4");
            var offCenterZoomResult = await service.ExportAsync(new ExportRequest(
                source,
                offCenterZoomOutput,
                StartSeconds: 1,
                EndSeconds: 3,
                ZoomFactor: 2.0,
                ZoomFocusX: 1.0,
                ZoomFocusY: 1.0));
            Assert(offCenterZoomResult.OutputMediaInfo.CodedWidth == 96 && offCenterZoomResult.OutputMediaInfo.CodedHeight == 214,
                "off-center zoom must preserve the same output canvas as centered zoom");
            var offCenterZoomPixel = await ReadFirstFramePixelAsync(ffmpegPath, offCenterZoomOutput, 20, 180, testRoot);
            Assert(offCenterZoomPixel.B > offCenterZoomPixel.R * 1.5 && offCenterZoomPixel.B > offCenterZoomPixel.G * 1.5,
                $"bottom-right focus should magnify the oriented bottom-right blue quadrant, got RGB {offCenterZoomPixel.R},{offCenterZoomPixel.G},{offCenterZoomPixel.B}");

            var zoomCropRotateOutput = Path.Combine(testRoot, "zoom-crop-rotate.mp4");
            var zoomCropRotateResult = await service.ExportAsync(new ExportRequest(
                source,
                zoomCropRotateOutput,
                StartSeconds: 1,
                EndSeconds: 3,
                Crop: new CropRect(0, 0, 48, 64),
                AdditionalRotationDegreesClockwise: 90,
                ZoomFactor: 2.0,
                ZoomFocusX: 0.0,
                ZoomFocusY: 0.0));
            Assert(zoomCropRotateResult.OutputMediaInfo.CodedWidth == 64 && zoomCropRotateResult.OutputMediaInfo.CodedHeight == 48,
                "zoom must scale back to the original crop viewport before additional rotation");
            var zoomCropRotatePixel = await ReadFirstFramePixelAsync(ffmpegPath, zoomCropRotateOutput, 40, 24, testRoot);
            Assert(zoomCropRotatePixel.G > zoomCropRotatePixel.R * 1.5 && zoomCropRotatePixel.G > zoomCropRotatePixel.B * 1.5,
                "crop, zoom, and extra rotation should retain the oriented SAR-corrected green crop");
            Pass("center/off-center zoom exports real pixels, keeps canvas size, and composes after SAR/rotation/crop");

            foreach (var (container, extension) in new[]
            {
                (ExportContainer.Mp4, ".mp4"),
                (ExportContainer.Mkv, ".mkv"),
                (ExportContainer.Mov, ".mov")
            })
            {
                var accuratePath = Path.Combine(testRoot, $"container-accurate-{container}{extension}");
                var accurateResult = await service.ExportAsync(new ExportRequest(
                    source,
                    accuratePath,
                    StartSeconds: 1,
                    EndSeconds: 3,
                    OutputContainer: container));
                Assert(File.Exists(accuratePath), $"accurate {container} export should create the selected extension");
                AssertContainer(accurateResult.OutputMediaInfo, container, $"accurate {container} export");

                var copyPath = Path.Combine(testRoot, $"container-copy-{container}{extension}");
                var copyContainerResult = await service.ExportAsync(new ExportRequest(
                    source,
                    copyPath,
                    StartSeconds: 1,
                    EndSeconds: 3,
                    Mode: ExportMode.StreamCopyApproximate,
                    OutputContainer: container));
                Assert(copyContainerResult.BoundariesAreApproximate,
                    $"stream-copy {container} export should retain approximate-boundary semantics");
                AssertContainer(copyContainerResult.OutputMediaInfo, container, $"stream-copy {container} export");
                Assert(copyContainerResult.OutputMediaInfo.VideoCodec.Equals(info.VideoCodec, StringComparison.OrdinalIgnoreCase),
                    $"stream-copy {container} should preserve the source video codec");
            }
            await AssertExportRejectedAsync(service, new ExportRequest(
                source,
                Path.Combine(testRoot, "wrong-extension.mp4"),
                1,
                2,
                OutputContainer: ExportContainer.Mkv), "container-extension mismatch");
            Pass("MP4, Matroska, and QuickTime MOV muxers work for accurate and stream-copy exports and are verified by ffprobe");

            foreach (var (speed, expectedDuration) in new[] { (0.25, 8.0), (0.5, 4.0), (2.0, 1.0), (4.0, 0.5) })
            {
                var speedOutput = Path.Combine(testRoot, $"speed-{speed.ToString(CultureInfo.InvariantCulture)}.mp4");
                ExportProgress? latestEncodingProgress = null;
                var speedResult = await service.ExportAsync(new ExportRequest(
                    source,
                    speedOutput,
                    StartSeconds: 1,
                    EndSeconds: 3,
                    PlaybackSpeed: speed),
                    new ImmediateProgress<ExportProgress>(item =>
                    {
                        if (item.Phase == ExportProgressPhase.Encoding)
                        {
                            latestEncodingProgress = item;
                        }
                    }));
                Assert(Math.Abs(speedResult.RequestedDurationSeconds - 2.0) < 0.001,
                    "requested duration should remain the source interval even when output playback speed changes");
                Assert(Math.Abs(speedResult.OutputMediaInfo.DurationSeconds - expectedDuration) < 0.2,
                    $"{speed}x output should last about {expectedDuration}s, got {speedResult.OutputMediaInfo.DurationSeconds}s");
                var audioDuration = await ReadStreamDurationAsync(ffprobePath, speedOutput, "a:0");
                Assert(Math.Abs(audioDuration - expectedDuration) < 0.25,
                    $"{speed}x audio should also last about {expectedDuration}s, got {audioDuration}s");
                Assert(latestEncodingProgress is not null && Math.Abs(latestEncodingProgress.ExpectedSeconds - expectedDuration) < 0.001,
                    "encoding progress should use the speed-adjusted output duration");
            }
            Pass("accurate speed changes video and audio duration, progress, and atempo chains from 0.25x through 4x");

            await AssertExportRejectedAsync(service, new ExportRequest(source, Path.Combine(testRoot, "nan.mp4"), double.NaN, 2), "finite");
            await AssertExportRejectedAsync(service, new ExportRequest(source, Path.Combine(testRoot, "negative.mp4"), -1, 2), "nonnegative");
            await AssertExportRejectedAsync(service, new ExportRequest(source, Path.Combine(testRoot, "empty.mp4"), 2, 2), "ordered");
            await AssertExportRejectedAsync(service, new ExportRequest(source, Path.Combine(testRoot, "past-end.mp4"), 1, 9), "duration");
            await AssertExportRejectedAsync(service, new ExportRequest(source, Path.Combine(testRoot, "odd-crop.mp4"), 1, 2, new CropRect(1, 0, 48, 48)), "chroma alignment");
            await AssertExportRejectedAsync(service, new ExportRequest(source, Path.Combine(testRoot, "outside.mp4"), 1, 2, new CropRect(96, 0, 2, 2)), "crop bounds");
            await AssertExportRejectedAsync(service, new ExportRequest(source, Path.Combine(testRoot, "invalid-rotation.mp4"), 1, 2, AdditionalRotationDegreesClockwise: 45), "invalid additional rotation");
            await AssertExportRejectedAsync(service, new ExportRequest(source, Path.Combine(testRoot, "invalid-speed-nan.mp4"), 1, 2, PlaybackSpeed: double.NaN), "non-finite playback speed");
            await AssertExportRejectedAsync(service, new ExportRequest(source, Path.Combine(testRoot, "invalid-speed-low.mp4"), 1, 2, PlaybackSpeed: 0.2), "playback speed below minimum");
            await AssertExportRejectedAsync(service, new ExportRequest(source, Path.Combine(testRoot, "invalid-speed-high.mp4"), 1, 2, PlaybackSpeed: 4.1), "playback speed above maximum");
            await AssertExportRejectedAsync(service, new ExportRequest(source, Path.Combine(testRoot, "invalid-zoom-nan.mp4"), 1, 2, ZoomFactor: double.NaN), "non-finite zoom factor");
            await AssertExportRejectedAsync(service, new ExportRequest(source, Path.Combine(testRoot, "invalid-zoom-low.mp4"), 1, 2, ZoomFactor: 0.9), "zoom factor below minimum");
            await AssertExportRejectedAsync(service, new ExportRequest(source, Path.Combine(testRoot, "invalid-zoom-high.mp4"), 1, 2, ZoomFactor: 4.1), "zoom factor above maximum");
            await AssertExportRejectedAsync(service, new ExportRequest(source, Path.Combine(testRoot, "invalid-zoom-focus.mp4"), 1, 2, ZoomFocusX: double.NaN), "non-finite zoom focus");
            await AssertExportRejectedAsync(service, new ExportRequest(source, Path.Combine(testRoot, "invalid-zoom-focus-range.mp4"), 1, 2, ZoomFocusY: 1.1), "zoom focus outside range");
            Pass("time ranges, crop, rotation, speed, and zoom validation reject invalid values");

            var sourceAlias = source.ToUpperInvariant();
            await AssertExportRejectedAsync(service, new ExportRequest(source, sourceAlias, 0, 1), "case-insensitive source path alias");
            var existingOutput = Path.Combine(testRoot, "existing.mp4");
            await File.WriteAllTextAsync(existingOutput, "keep this file");
            await AssertExportRejectedAsync(service, new ExportRequest(source, existingOutput, 0, 1), "existing destination");
            Assert(await File.ReadAllTextAsync(existingOutput) == "keep this file", "existing destination contents must remain intact");
            Pass("source aliases and existing destinations are rejected without overwriting files");

            var copyOutput = Path.Combine(testRoot, "copy.mp4");
            var copyResult = await service.ExportAsync(new ExportRequest(
                source,
                copyOutput,
                StartSeconds: 1,
                EndSeconds: 3,
                Mode: ExportMode.StreamCopyApproximate));
            Assert(copyResult.BoundariesAreApproximate, "stream-copy output must identify approximate boundaries");
            Assert(copyResult.OutputMediaInfo.CodedWidth == info.CodedWidth && copyResult.OutputMediaInfo.CodedHeight == info.CodedHeight, "stream-copy must preserve coded dimensions");
            Assert(copyResult.OutputMediaInfo.RotationDegreesClockwise == info.RotationDegreesClockwise, "stream-copy should retain source display rotation metadata");
            Assert(copyResult.OutputMediaInfo.SampleAspectRatio == info.SampleAspectRatio, "stream-copy should retain source SAR");
            await AssertExportRejectedAsync(service, new ExportRequest(
                source,
                Path.Combine(testRoot, "copy-crop.mp4"),
                1,
                3,
                new CropRect(0, 0, 48, 48),
                ExportMode.StreamCopyApproximate), "stream-copy crop");
            await AssertExportRejectedAsync(service, new ExportRequest(source,
                Path.Combine(testRoot, "copy-rotate.mp4"),
                1,
                3,
                Mode: ExportMode.StreamCopyApproximate,
                AdditionalRotationDegreesClockwise: 90), "stream-copy additional rotation");
            await AssertExportRejectedAsync(service, new ExportRequest(source,
                Path.Combine(testRoot, "copy-speed.mp4"),
                1,
                3,
                Mode: ExportMode.StreamCopyApproximate,
                PlaybackSpeed: 2), "stream-copy speed change");
            await AssertExportRejectedAsync(service, new ExportRequest(source,
                Path.Combine(testRoot, "copy-zoom.mp4"),
                1,
                3,
                Mode: ExportMode.StreamCopyApproximate,
                ZoomFactor: 2), "stream-copy zoom");
            Pass("stream-copy preserves display properties, reports approximate boundaries, and refuses transforms including zoom");

            var silentSource = Path.Combine(testRoot, "silent.mp4");
            await RunFfmpegAsync(ffmpegPath,
            [
                "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=160x96:rate=30:duration=2",
                "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-y", silentSource
            ]);
            var silentInfo = await service.InspectAsync(silentSource);
            Assert(!silentInfo.HasAudio && silentInfo.AudioCodec is null, "silent fixture should report no audio");
            var silentOutput = Path.Combine(testRoot, "silent-output.mp4");
            var silentResult = await service.ExportAsync(new ExportRequest(silentSource, silentOutput, 0, 1));
            Assert(!silentResult.OutputMediaInfo.HasAudio, "accurate export should succeed without an audio stream");
            var silentSpeedResult = await service.ExportAsync(new ExportRequest(
                silentSource,
                Path.Combine(testRoot, "silent-speed.mp4"),
                0,
                1,
                PlaybackSpeed: 2));
            Assert(!silentSpeedResult.OutputMediaInfo.HasAudio && Math.Abs(silentSpeedResult.OutputMediaInfo.DurationSeconds - 0.5) < 0.1,
                "speed export should re-time video-only sources without inventing an audio stream");
            Pass("accurate export handles silent video both at normal speed and with speed adjustment");

            var h265Service = new MediaExportService(new ExportOptions(ffmpegPath, ffprobePath)
            {
                Encoder = VideoEncoder.H265,
                Preset = H264Preset.Ultrafast,
                ConstantRateFactor = 28
            });
            var h265Result = await h265Service.ExportAsync(new ExportRequest(
                silentSource,
                Path.Combine(testRoot, "h265.mp4"),
                0,
                1));
            Assert(string.Equals(h265Result.OutputMediaInfo.VideoCodec, "hevc", StringComparison.OrdinalIgnoreCase), "encoder option should select the CPU H.265 encoder");
            var h265Tag = await ReadVideoCodecTagAsync(ffprobePath, Path.Combine(testRoot, "h265.mp4"));
            Assert(string.Equals(h265Tag, "hvc1", StringComparison.OrdinalIgnoreCase), $"H.265 MP4 should use hvc1 tag, got {h265Tag}");
            Pass("encoder selection can switch accurate MP4 output to H.265");

            var hdrSource = Path.Combine(testRoot, "hdr-signal.mp4");
            await RunFfmpegAsync(ffmpegPath,
            [
                "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=160x96:rate=30:duration=2",
                "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
                "-x264-params", "colorprim=bt2020:transfer=smpte2084:colormatrix=bt2020nc", "-y", hdrSource
            ]);
            var hdrInfo = await service.InspectAsync(hdrSource);
            Assert(hdrInfo.IsHdr, "PQ transfer metadata should classify the fixture as HDR");
            await AssertExportRejectedAsync(service, new ExportRequest(hdrSource, Path.Combine(testRoot, "hdr-output.mp4"), 0, 1), "HDR accurate export");
            var hdrCopy = await service.ExportAsync(new ExportRequest(
                hdrSource,
                Path.Combine(testRoot, "hdr-copy.mp4"),
                0,
                1,
                Mode: ExportMode.StreamCopyApproximate));
            Assert(hdrCopy.OutputMediaInfo.IsHdr, "stream-copy should retain HDR signaling");
            Pass("HDR accurate re-encode is rejected while stream-copy preserves HDR signaling");

            var tsSource = Path.Combine(testRoot, "offset-start.ts");
            await RunFfmpegAsync(ffmpegPath,
            ["-hide_banner", "-loglevel", "error", "-i", source, "-c", "copy", "-output_ts_offset", "1.4", "-f", "mpegts", "-y", tsSource]);
            var tsInfo = await service.InspectAsync(tsSource);
            Assert(tsInfo.StartTimeSeconds > 1, $"MPEG-TS fixture should retain its nonzero start time, got {tsInfo.StartTimeSeconds}");
            var tsOutput = Path.Combine(testRoot, "offset-trim.mp4");
            var tsResult = await service.ExportAsync(new ExportRequest(tsSource, tsOutput, 1, 3));
            Assert(tsResult.OutputMediaInfo.DurationSeconds is > 1.7 and < 2.3, $"relative trim on offset MPEG-TS should be near 2 seconds, got {tsResult.OutputMediaInfo.DurationSeconds}");
            Pass("nonzero MPEG-TS start timestamps are handled as clip-relative trim times");

            await CheckOffsetSpeedTrimAsync(ffmpegPath, ffprobePath, service, testRoot);
            Pass("speed changes on offset-timestamp video preserve the selected frames and audio duration");
            await CheckLateStartSeekAsync(ffmpegPath, ffprobePath, service, testRoot);
            Pass("late trims past 20 seconds use two-stage seek and preserve the selected frames");
            await CheckMultiSegmentEditsAsync(service, ffmpegPath, testRoot);
            Pass("multi-segment edits share the first canvas, fit later segments with black bars, and match total time");
            await CheckMultiSegmentGapAsync(service, ffmpegPath, testRoot);
            Pass("multi-segment gaps join end-adjacent frames without decoding deleted ranges");
            await CheckMultiSegmentSpeedsAsync(service, ffmpegPath, ffprobePath, testRoot);
            Pass("multi-segment speeds sum video and audio to the speed-adjusted total with progress");
            await CheckMultiSegmentSilentAsync(service, ffmpegPath, testRoot);
            Pass("multi-segment export handles silent video without inventing audio");
            await CheckMultiSegmentOffsetStartAsync(service, ffmpegPath, testRoot);
            Pass("multi-segment trims on nonzero-start MPEG-TS use clip-relative times including past 20 seconds");
            await CheckMultiSegmentValidationAsync(service, ffmpegPath, testRoot);
            Pass("multi-segment validation rejects overlap, order, stream-copy, and range errors with fixed copy");
            await CheckMultiSegmentCancellationAsync(service, ffmpegPath, testRoot);
            Pass("multi-segment cancellation removes temporaries and preserves the input");

            var cancelSource = await CreateCancellationFixtureAsync(ffmpegPath, testRoot);
            var cancelHash = await HashFileAsync(cancelSource);
            var cancelDestination = Path.Combine(testRoot, "cancelled-output.mp4");
            using var cancellation = new CancellationTokenSource();
            var cancellationProgress = new ImmediateProgress<ExportProgress>(item =>
            {
                if (item.Phase == ExportProgressPhase.Encoding)
                {
                    cancellation.Cancel();
                }
            });
            var cancellationThrown = false;
            try
            {
                await service.ExportAsync(new ExportRequest(cancelSource, cancelDestination, 0, 60), cancellationProgress, cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                cancellationThrown = true;
            }

            Assert(cancellationThrown, "cancel request should cancel the export");
            Assert(!File.Exists(cancelDestination), "cancelled export must not commit the final output");
            Assert(!Directory.EnumerateFiles(testRoot, "*.partial.mp4").Any(), "cancelled export must remove its own temporary file");
            Assert(cancelHash == await HashFileAsync(cancelSource), "cancelled export must preserve the input bytes");
            Pass("cancellation kills active FFmpeg export, removes the temporary, and preserves the input");
        }
        finally
        {
            try
            {
                Directory.Delete(testRoot, recursive: true);
            }
            catch
            {
                Console.Error.WriteLine($"WARN: temporary test files remain at {testRoot}");
            }
        }
    }

    private static void TestLocalization()
    {
        var japanese = CultureInfo.GetCultureInfo("ja-JP");
        var english = CultureInfo.GetCultureInfo("en-US");
        try
        {
            CultureInfo.CurrentUICulture = japanese;
            Assert(ReadValidationMessage(() => MediaGeometry.ValidateCrop(new CropRect(96, 0, 2, 2), 96, 213)) == "クロップ範囲が動画の表示領域を超えています。", "crop bounds message should be Japanese");
            Assert(ReadValidationMessage(() => MediaGeometry.ValidateZoom(4.1, 0.5, 0.5)) == "ズーム倍率は 1 倍から 4 倍の範囲で指定してください。", "zoom range message should be Japanese");
            CultureInfo.CurrentUICulture = english;
            Assert(ReadValidationMessage(() => MediaGeometry.ValidateCrop(new CropRect(96, 0, 2, 2), 96, 213)) == "The crop area extends beyond the video.", "crop bounds message should be English");
            Assert(ReadValidationMessage(() => MediaGeometry.ValidateZoom(4.1, 0.5, 0.5)) == "The zoom level must be between 1× and 4×.", "zoom range message should be English");
            Assert(ReadValidationMessage(() => MediaGeometry.NormalizeRightAngleRotation(45)) == "This video's rotation isn't a multiple of 90 degrees, so the crop position can't be calculated.", "rotation message should be English");
            var manager = new ResourceManager("FrameDock.Core.Messages", typeof(MediaInfo).Assembly);
            using var neutralSet = manager.GetResourceSet(CultureInfo.InvariantCulture, true, true);
            using var japaneseSet = manager.GetResourceSet(japanese, true, true);
            Assert(neutralSet is not null && japaneseSet is not null, "both resx resource sets should load");
            var neutralKeys = new HashSet<string>();
            foreach (System.Collections.DictionaryEntry entry in neutralSet!)
            {
                neutralKeys.Add((string)entry.Key);
            }
            var japaneseKeys = new HashSet<string>();
            foreach (System.Collections.DictionaryEntry entry in japaneseSet!)
            {
                japaneseKeys.Add((string)entry.Key);
            }
            Assert(neutralKeys.SetEquals(japaneseKeys), "resx keys should match between English and Japanese");
            foreach (var key in neutralKeys)
            {
                var neutralPlaceholders = CountPlaceholders(manager.GetString(key, CultureInfo.InvariantCulture));
                var japanesePlaceholders = CountPlaceholders(manager.GetString(key, japanese));
                Assert(neutralPlaceholders == japanesePlaceholders, "placeholder count should match for " + key);
            }
        }
        finally
        {
            CultureInfo.CurrentUICulture = japanese;
        }
    }

    private static string ReadValidationMessage(Action action)
    {
        try
        {
            action();
        }
        catch (ExportValidationException exception)
        {
            return exception.UserMessage;
        }

        throw new InvalidOperationException("expected ExportValidationException");
    }

    private static int CountPlaceholders(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var count = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '{' && index + 1 < text.Length && char.IsDigit(text[index + 1]))
            {
                count++;
            }
        }

        return count;
    }

    private static void TestDisplayDimensions()
    {
        var squareLandscape = MediaGeometry.ComputeDisplayDimensions(1920, 1080, AspectRatio.Square, 0);
        Assert(squareLandscape == (1920, 1080), "square pixel dimensions should remain unchanged");
        var anamorphic = MediaGeometry.ComputeDisplayDimensions(720, 480, new AspectRatio(8, 9), 0);
        Assert(anamorphic == (640, 480), "8:9 SAR should correct 720x480 to 640x480");
        var portrait = MediaGeometry.ComputeDisplayDimensions(720, 480, new AspectRatio(8, 9), 90);
        Assert(portrait == (480, 640), "90 degree rotation should swap SAR-corrected dimensions");
        var oddRound = MediaGeometry.ComputeDisplayDimensions(160, 96, new AspectRatio(4, 3), 0);
        Assert(oddRound == (213, 96), "SAR width should round to nearest integer pixel");
    }

    private static void TestCropValidation()
    {
        MediaGeometry.ValidateCrop(new CropRect(0, 0, 48, 48), 96, 213);
        Expect<ExportValidationException>(() => MediaGeometry.ValidateCrop(new CropRect(96, 0, 2, 2), 96, 213));
        Expect<ExportValidationException>(() => MediaGeometry.ValidateCrop(new CropRect(0, 0, 0, 2), 96, 213));
        Expect<ExportValidationException>(() => MediaGeometry.ValidateCrop(new CropRect(2, 2, 49, 48), 96, 213));
        Expect<ExportValidationException>(() => MediaGeometry.ValidateCrop(new CropRect(int.MaxValue, 0, 2, 2), 96, 213));
    }

    private static void TestZoomGeometry()
    {
        var viewport = new CropRect(20, 10, 100, 80);
        Assert(MediaGeometry.ComputeZoomSampleRect(viewport, 1.0, 0.5, 0.5) == viewport,
            "1x zoom should return the unmodified display viewport");
        Assert(MediaGeometry.ComputeZoomSampleRect(viewport, 2.0, 0.5, 0.5) == new CropRect(46, 30, 50, 40),
            "center focus should select an even-aligned inner rectangle around the viewport center");
        Assert(MediaGeometry.ComputeZoomSampleRect(viewport, 2.0, 0.0, 1.0) == new CropRect(20, 50, 50, 40),
            "edge focus should clamp the sample to the requested lower-left viewport edge");
        Assert(MediaGeometry.ComputeBaseViewportDimensions(CreateSyntheticMediaInfo(270)) == (96, 213),
            "base viewport dimensions should use oriented display dimensions before extra rotation");
        Assert(MediaGeometry.ComputeBaseViewportDimensions(CreateSyntheticMediaInfo(270), new CropRect(0, 0, 48, 64)) == (48, 64),
            "explicit crop dimensions define the base viewport");
        Assert(MediaGeometry.ComputeBaseViewportDimensions(CreateSyntheticMediaInfo(270), new CropRect(0, 0, 47, 63)) == (47, 63),
            "preview aspect lookup should tolerate a transient crop that has not yet snapped to encoder chroma alignment");

        var rotatedSarMedia = CreateSyntheticMediaInfo(270);
        Assert(MediaGeometry.MapDisplayRectToCodedRect(rotatedSarMedia, new CropRect(0, 0, 48, 106)) == new CropRect(80, 0, 80, 48),
            "display top-left on a counter-clockwise rotated anamorphic source should map to coded top-right");
        Assert(MediaGeometry.MapDisplayRectToCodedRect(rotatedSarMedia, new CropRect(48, 106, 48, 106)) == new CropRect(0, 48, 81, 48),
            "display bottom-right should map back through both rotation and SAR scale with covering edge rounding");
        Assert(MediaGeometry.MapDisplayRectToCodedRect(CreateSyntheticMediaInfo(90), new CropRect(0, 0, 48, 106)) == new CropRect(0, 48, 80, 48),
            "clockwise source rotation should be inverted when mapping display rectangles to coded coordinates");
        Assert(MediaGeometry.MapDisplayRectToCodedRect(CreateSyntheticMediaInfo(0), new CropRect(0, 0, 106, 48)) == new CropRect(0, 0, 80, 48),
            "unrotated SAR mapping should scale display edges back to coded pixel boundaries");
        Assert(MediaGeometry.MapDisplayRectToCodedRect(CreateSyntheticMediaInfo(180), new CropRect(0, 0, 106, 48)) == new CropRect(80, 48, 80, 48),
            "180 degree source rotation should invert both display axes before SAR mapping");

        Expect<ExportValidationException>(() => MediaGeometry.ComputeZoomSampleRect(viewport, double.NaN, 0.5, 0.5));
        Expect<ExportValidationException>(() => MediaGeometry.ComputeZoomSampleRect(viewport, 2.0, double.PositiveInfinity, 0.5));
        Expect<ExportValidationException>(() => MediaGeometry.ComputeZoomSampleRect(viewport, 4.1, 0.5, 0.5));
        Expect<ExportValidationException>(() => MediaGeometry.ComputeZoomSampleRect(new CropRect(0, 0, 6, 8), 4.0, 0.5, 0.5));
        Expect<ExportValidationException>(() => MediaGeometry.MapDisplayRectToCodedRect(rotatedSarMedia, new CropRect(95, 212, 2, 2)));
    }

    private static MediaInfo CreateSyntheticMediaInfo(int rotation)
    {
        var dimensions = MediaGeometry.ComputeDisplayDimensions(160, 96, new AspectRatio(4, 3), rotation);
        return new MediaInfo
        {
            SourcePath = "synthetic.mp4",
            DurationSeconds = 8,
            StartTimeSeconds = 0,
            CodedWidth = 160,
            CodedHeight = 96,
            SampleAspectRatio = new AspectRatio(4, 3),
            RotationDegreesClockwise = rotation,
            DisplayWidth = dimensions.Width,
            DisplayHeight = dimensions.Height,
            HasAudio = false,
            VideoCodec = "h264"
        };
    }

    private static void TestRotationValidation()
    {
        Assert(MediaGeometry.NormalizeRightAngleRotation(-90) == 270, "negative right angles should normalize");
        Assert(MediaGeometry.NormalizeRightAngleRotation(450) == 90, "angles over 360 should normalize");
        Expect<ExportValidationException>(() => MediaGeometry.NormalizeRightAngleRotation(45));
    }

    private static async Task<string> CreateRotatedSarFixtureAsync(string ffmpegPath, string testRoot)
    {
        var imagePath = Path.Combine(testRoot, "quad 日本 & ; ' chart.ppm");
        await WriteQuadrantPpmAsync(imagePath, 160, 96);
        var basePath = Path.Combine(testRoot, "base 日本 & ; ' chart.mp4");
        await RunFfmpegAsync(ffmpegPath,
        [
            "-hide_banner", "-loglevel", "error", "-framerate", "30", "-loop", "1", "-i", imagePath,
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=8", "-t", "8",
            "-vf", "setsar=4/3", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "0", "-pix_fmt", "yuv444p",
            "-c:a", "aac", "-b:a", "64k", "-shortest", "-y", basePath
        ]);

        var rotatedPath = Path.Combine(testRoot, "source 日本 & ; ' clip.mp4");
        await RunFfmpegAsync(ffmpegPath,
        ["-hide_banner", "-loglevel", "error", "-display_rotation:v:0", "90", "-i", basePath, "-c", "copy", "-y", rotatedPath]);
        return rotatedPath;
    }

    private static async Task CheckOffsetSpeedTrimAsync(
        string ffmpegPath, string ffprobePath, MediaExportService service, string testRoot)
    {
        var source = Path.Combine(testRoot, "timed-colors.mp4");
        await RunFfmpegAsync(ffmpegPath,
        [
            "-hide_banner", "-loglevel", "error",
            "-f", "lavfi", "-i", "color=red:s=64x48:r=30:d=4",
            "-f", "lavfi", "-i", "color=green:s=64x48:r=30:d=4",
            "-f", "lavfi", "-i", "color=blue:s=64x48:r=30:d=4",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=12",
            "-filter_complex", "[0:v][1:v][2:v]concat=n=3:v=1:a=0[v]",
            "-map", "[v]", "-map", "3:a", "-c:v", "libx264", "-preset", "ultrafast",
            "-g", "30", "-pix_fmt", "yuv420p", "-c:a", "aac", "-y", source
        ]);
        var offsetSource = Path.Combine(testRoot, "timed-colors-offset.ts");
        await RunFfmpegAsync(ffmpegPath,
        ["-hide_banner", "-loglevel", "error", "-i", source, "-c", "copy", "-output_ts_offset", "1.4", "-f", "mpegts", "-y", offsetSource]);
        Assert((await service.InspectAsync(offsetSource)).StartTimeSeconds > 1,
            "the timed fixture must exercise the offset-timestamp seek path");

        foreach (var input in new[] { source, offsetSource })
        foreach (var (start, speed, expectGreen) in new[]
        {
            (2.0, 2.0, false), (2.0, 4.0, false),
            (6.0, 0.5, true), (6.0, 0.25, true), (6.0, 1.0, true)
        })
        {
            var output = Path.Combine(testRoot, $"timed-{Guid.NewGuid():N}.mp4");
            var result = await service.ExportAsync(new ExportRequest(input, output, start, start + 2, PlaybackSpeed: speed));
            var pixel = await ReadFirstFramePixelAsync(ffmpegPath, output, 32, 24, testRoot);
            Assert(expectGreen
                    ? pixel.G > 80 && pixel.R < 30 && pixel.B < 30
                    : pixel.R > 200 && pixel.G < 30 && pixel.B < 30,
                $"{Path.GetExtension(input)} {speed}x trim starting at {start}s selected the wrong source frames: {pixel}");
            var expectedDuration = 2 / speed;
            Assert(Math.Abs(result.OutputMediaInfo.DurationSeconds - expectedDuration) < 0.2,
                "the selected interval must have the speed-adjusted duration");
            Assert(Math.Abs(await ReadStreamDurationAsync(ffprobePath, output, "a:0") - expectedDuration) < 0.25,
                "audio must cover the same speed-adjusted interval as video");
        }
    }

    private static async Task CheckLateStartSeekAsync(
        string ffmpegPath, string ffprobePath, MediaExportService service, string testRoot)
    {
        var source = Path.Combine(testRoot, "timed-colors-30.mp4");
        await RunFfmpegAsync(ffmpegPath,
        [
            "-hide_banner", "-loglevel", "error",
            "-f", "lavfi", "-i", "color=red:s=64x48:r=30:d=10",
            "-f", "lavfi", "-i", "color=green:s=64x48:r=30:d=10",
            "-f", "lavfi", "-i", "color=blue:s=64x48:r=30:d=10",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=30",
            "-filter_complex", "[0:v][1:v][2:v]concat=n=3:v=1:a=0[v]",
            "-map", "[v]", "-map", "3:a", "-c:v", "libx264", "-preset", "ultrafast",
            "-g", "30", "-pix_fmt", "yuv420p", "-c:a", "aac", "-y", source
        ]);
        var offsetSource = Path.Combine(testRoot, "timed-colors-30-offset.ts");
        await RunFfmpegAsync(ffmpegPath,
        ["-hide_banner", "-loglevel", "error", "-i", source, "-c", "copy", "-output_ts_offset", "1.4", "-f", "mpegts", "-y", offsetSource]);
        Assert((await service.InspectAsync(offsetSource)).StartTimeSeconds > 1,
            "the late fixture must exercise the offset-timestamp seek path");

        foreach (var input in new[] { source, offsetSource })
        foreach (var speed in new[] { 1.0, 2.0 })
        {
            var output = Path.Combine(testRoot, $"timed-late-{Guid.NewGuid():N}.mp4");
            var result = await service.ExportAsync(new ExportRequest(input, output, 20, 22, PlaybackSpeed: speed));
            var pixel = await ReadFirstFramePixelAsync(ffmpegPath, output, 32, 24, testRoot);
            Assert(pixel.B > 80 && pixel.B > pixel.R * 1.5 && pixel.B > pixel.G * 1.5,
                $"{Path.GetExtension(input)} {speed}x late trim starting at 20s selected the wrong source frames: {pixel}");
            var expectedDuration = 2 / speed;
            Assert(Math.Abs(result.OutputMediaInfo.DurationSeconds - expectedDuration) < 0.2,
                "the late interval must have the speed-adjusted duration");
            Assert(Math.Abs(await ReadStreamDurationAsync(ffprobePath, output, "a:0") - expectedDuration) < 0.25,
                "late audio must cover the same speed-adjusted interval as video");
        }
    }

    private static async Task<string> CreateTimed12FixtureAsync(string ffmpegPath, string testRoot)
    {
        var source = Path.Combine(testRoot, $"timed12-{Guid.NewGuid():N}.mp4");
        await RunFfmpegAsync(ffmpegPath,
        [
            "-hide_banner", "-loglevel", "error",
            "-f", "lavfi", "-i", "color=red:s=64x48:r=30:d=4",
            "-f", "lavfi", "-i", "color=green:s=64x48:r=30:d=4",
            "-f", "lavfi", "-i", "color=blue:s=64x48:r=30:d=4",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=12",
            "-filter_complex", "[0:v][1:v][2:v]concat=n=3:v=1:a=0[v]",
            "-map", "[v]", "-map", "3:a", "-c:v", "libx264", "-preset", "ultrafast",
            "-g", "30", "-pix_fmt", "yuv420p", "-c:a", "aac", "-y", source
        ]);
        return source;
    }

    private static async Task CheckMultiSegmentEditsAsync(MediaExportService service, string ffmpegPath, string testRoot)
    {
        var source = await CreateTimed12FixtureAsync(ffmpegPath, testRoot);
        var segments = new List<ExportSegment>
        {
            new(0, 2),
            new(4, 6, AdditionalRotationDegreesClockwise: 90),
            new(8, 10, Crop: new CropRect(0, 0, 32, 32), ZoomFactor: 2.0)
        };
        var output = Path.Combine(testRoot, $"multi-edits-{Guid.NewGuid():N}.mp4");
        var result = await service.ExportAsync(new ExportRequest(source, output, 0, 1, Segments: segments));
        Assert(result.OutputMediaInfo.CodedWidth == 64 && result.OutputMediaInfo.CodedHeight == 48, "canvas should follow the first segment at 64x48");
        Assert(Math.Abs(result.RequestedDurationSeconds - 6.0) < 0.001, "requested duration should sum source lengths before speed");
        Assert(Math.Abs(result.OutputMediaInfo.DurationSeconds - 6.0) < 0.3, $"edited output should last about 6s, got {result.OutputMediaInfo.DurationSeconds}s");
        var first = await ReadFramePixelAtAsync(ffmpegPath, output, 1.0, 32, 24, testRoot);
        Assert(first.R > 200 && first.G < 30 && first.B < 30, $"first segment should stay red, got RGB {first.R},{first.G},{first.B}");
        var secondCenter = await ReadFramePixelAtAsync(ffmpegPath, output, 3.0, 32, 24, testRoot);
        Assert(secondCenter.G > 80 && secondCenter.R < 30 && secondCenter.B < 30, $"rotated second segment should stay green at the center, got RGB {secondCenter.R},{secondCenter.G},{secondCenter.B}");
        var secondEdge = await ReadFramePixelAtAsync(ffmpegPath, output, 3.0, 3, 24, testRoot);
        Assert(secondEdge.R < 30 && secondEdge.G < 30 && secondEdge.B < 30, $"rotated segment fitted to the canvas should pad the sides black, got RGB {secondEdge.R},{secondEdge.G},{secondEdge.B}");
        var thirdCenter = await ReadFramePixelAtAsync(ffmpegPath, output, 5.0, 32, 24, testRoot);
        Assert(thirdCenter.B > 80 && thirdCenter.R < 30 && thirdCenter.G < 30, $"cropped and zoomed third segment should stay blue at the center, got RGB {thirdCenter.R},{thirdCenter.G},{thirdCenter.B}");
        var thirdEdge = await ReadFramePixelAtAsync(ffmpegPath, output, 5.0, 2, 24, testRoot);
        Assert(thirdEdge.R < 30 && thirdEdge.G < 30 && thirdEdge.B < 30, $"cropped segment fitted to the canvas should pad the sides black, got RGB {thirdEdge.R},{thirdEdge.G},{thirdEdge.B}");
    }

    private static async Task CheckMultiSegmentGapAsync(MediaExportService service, string ffmpegPath, string testRoot)
    {
        var source = await CreateTimed12FixtureAsync(ffmpegPath, testRoot);
        var segments = new List<ExportSegment> { new(1, 2), new(5, 6) };
        var output = Path.Combine(testRoot, $"multi-gap-{Guid.NewGuid():N}.mp4");
        var result = await service.ExportAsync(new ExportRequest(source, output, 0, 1, Segments: segments));
        Assert(Math.Abs(result.OutputMediaInfo.DurationSeconds - 2.0) < 0.3, $"gapped output should last about 2s, got {result.OutputMediaInfo.DurationSeconds}s");
        var beforeJoint = await ReadFramePixelAtAsync(ffmpegPath, output, 0.9, 32, 24, testRoot);
        Assert(beforeJoint.R > 200 && beforeJoint.G < 30 && beforeJoint.B < 30, $"frame before the joint should be the first segment tail (red), got RGB {beforeJoint.R},{beforeJoint.G},{beforeJoint.B}");
        var afterJoint = await ReadFramePixelAtAsync(ffmpegPath, output, 1.1, 32, 24, testRoot);
        Assert(afterJoint.G > 80 && afterJoint.R < 30 && afterJoint.B < 30, $"frame after the joint should be the second segment head (green), got RGB {afterJoint.R},{afterJoint.G},{afterJoint.B}");
    }

    private static async Task CheckMultiSegmentSpeedsAsync(MediaExportService service, string ffmpegPath, string ffprobePath, string testRoot)
    {
        var source = await CreateTimed12FixtureAsync(ffmpegPath, testRoot);
        var segments = new List<ExportSegment> { new(0, 2, PlaybackSpeed: 1.0), new(4, 6, PlaybackSpeed: 2.0) };
        var output = Path.Combine(testRoot, $"multi-speed-{Guid.NewGuid():N}.mp4");
        ExportProgress? latestEncodingProgress = null;
        var result = await service.ExportAsync(
            new ExportRequest(source, output, 0, 1, Segments: segments),
            new ImmediateProgress<ExportProgress>(item =>
            {
                if (item.Phase == ExportProgressPhase.Encoding)
                {
                    latestEncodingProgress = item;
                }
            }));
        Assert(Math.Abs(result.RequestedDurationSeconds - 4.0) < 0.001, "requested duration should sum lengths before speed");
        Assert(Math.Abs(result.OutputMediaInfo.DurationSeconds - 3.0) < 0.3, $"speed output should last about 3s, got {result.OutputMediaInfo.DurationSeconds}s");
        var audioDuration = await ReadStreamDurationAsync(ffprobePath, output, "a:0");
        Assert(Math.Abs(audioDuration - 3.0) < 0.3, $"speed audio should also last about 3s, got {audioDuration}s");
        Assert(latestEncodingProgress is not null && Math.Abs(latestEncodingProgress.ExpectedSeconds - 3.0) < 0.001, "encoding progress should use the summed speed-adjusted duration");
    }

    private static async Task CheckMultiSegmentSilentAsync(MediaExportService service, string ffmpegPath, string testRoot)
    {
        var silentSource = Path.Combine(testRoot, $"multi-silent-{Guid.NewGuid():N}.mp4");
        await RunFfmpegAsync(ffmpegPath,
        [
            "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=64x48:rate=30:duration=4",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-y", silentSource
        ]);
        Assert(!(await service.InspectAsync(silentSource)).HasAudio, "silent multi fixture should report no audio");
        var segments = new List<ExportSegment> { new(0, 1), new(2, 3) };
        var output = Path.Combine(testRoot, $"multi-silent-out-{Guid.NewGuid():N}.mp4");
        var result = await service.ExportAsync(new ExportRequest(silentSource, output, 0, 1, Segments: segments));
        Assert(!result.OutputMediaInfo.HasAudio, "silent multi export should stay silent");
        Assert(Math.Abs(result.OutputMediaInfo.DurationSeconds - 2.0) < 0.3, $"silent multi output should last about 2s, got {result.OutputMediaInfo.DurationSeconds}s");
    }

    private static async Task CheckMultiSegmentOffsetStartAsync(MediaExportService service, string ffmpegPath, string testRoot)
    {
        var source = Path.Combine(testRoot, $"multi-timed30-{Guid.NewGuid():N}.mp4");
        await RunFfmpegAsync(ffmpegPath,
        [
            "-hide_banner", "-loglevel", "error",
            "-f", "lavfi", "-i", "color=red:s=64x48:r=30:d=10",
            "-f", "lavfi", "-i", "color=green:s=64x48:r=30:d=10",
            "-f", "lavfi", "-i", "color=blue:s=64x48:r=30:d=10",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=30",
            "-filter_complex", "[0:v][1:v][2:v]concat=n=3:v=1:a=0[v]",
            "-map", "[v]", "-map", "3:a", "-c:v", "libx264", "-preset", "ultrafast",
            "-g", "30", "-pix_fmt", "yuv420p", "-c:a", "aac", "-y", source
        ]);
        var offsetSource = Path.Combine(testRoot, $"multi-timed30-offset-{Guid.NewGuid():N}.ts");
        await RunFfmpegAsync(ffmpegPath,
        ["-hide_banner", "-loglevel", "error", "-i", source, "-c", "copy", "-output_ts_offset", "1.4", "-f", "mpegts", "-y", offsetSource]);
        Assert((await service.InspectAsync(offsetSource)).StartTimeSeconds > 1, "offset fixture must exercise nonzero start timestamps");
        var segments = new List<ExportSegment> { new(1, 3), new(21, 23) };
        var output = Path.Combine(testRoot, $"multi-offset-{Guid.NewGuid():N}.mp4");
        var result = await service.ExportAsync(new ExportRequest(offsetSource, output, 0, 1, Segments: segments));
        Assert(Math.Abs(result.OutputMediaInfo.DurationSeconds - 4.0) < 0.4, $"offset multi output should last about 4s, got {result.OutputMediaInfo.DurationSeconds}s");
        var head = await ReadFramePixelAtAsync(ffmpegPath, output, 0.5, 32, 24, testRoot);
        Assert(head.R > 200 && head.G < 30 && head.B < 30, $"offset multi head should be red, got RGB {head.R},{head.G},{head.B}");
        var tail = await ReadFramePixelAtAsync(ffmpegPath, output, 3.0, 32, 24, testRoot);
        Assert(tail.B > 80 && tail.R < 30 && tail.G < 30, $"offset multi tail past 20s should be blue, got RGB {tail.R},{tail.G},{tail.B}");
    }

    private static async Task CheckMultiSegmentValidationAsync(MediaExportService service, string ffmpegPath, string testRoot)
    {
        var source = await CreateTimed12FixtureAsync(ffmpegPath, testRoot);
        await AssertExportRejectedWithMessageAsync(
            service,
            new ExportRequest(source, Path.Combine(testRoot, "overlap.mp4"), 0, 1, Segments: new List<ExportSegment> { new(1, 3), new(2, 4) }),
            "区間の順序が正しくないか、区間どうしが重なっています。",
            "overlapping segments");
        await AssertExportRejectedWithMessageAsync(
            service,
            new ExportRequest(source, Path.Combine(testRoot, "order.mp4"), 0, 1, Segments: new List<ExportSegment> { new(5, 6), new(1, 2) }),
            "区間の順序が正しくないか、区間どうしが重なっています。",
            "out-of-order segments");
        await AssertExportRejectedWithMessageAsync(
            service,
            new ExportRequest(source, Path.Combine(testRoot, "copy-multi.mp4"), 0, 1, Mode: ExportMode.StreamCopyApproximate, Segments: new List<ExportSegment> { new(1, 2), new(3, 4) }),
            "分割した動画は「高速切り出し（画質維持）」では書き出せません。",
            "multi-segment stream-copy");
        await AssertExportRejectedAsync(
            service,
            new ExportRequest(source, Path.Combine(testRoot, "past-end.mp4"), 0, 1, Segments: new List<ExportSegment> { new(1, 20) }),
            "segment past duration");
        await AssertExportRejectedAsync(
            service,
            new ExportRequest(source, Path.Combine(testRoot, "copy-one-crop.mp4"), 0, 1, Mode: ExportMode.StreamCopyApproximate, Segments: new List<ExportSegment> { new(1, 2, Crop: new CropRect(0, 0, 32, 32)) }),
            "single-segment stream-copy crop");
        var adjacentOutput = Path.Combine(testRoot, $"multi-adjacent-{Guid.NewGuid():N}.mp4");
        var adjacentResult = await service.ExportAsync(new ExportRequest(
            source, adjacentOutput, 0, 1, Segments: new List<ExportSegment> { new(1, 2), new(2, 3) }));
        Assert(Math.Abs(adjacentResult.OutputMediaInfo.DurationSeconds - 2.0) < 0.3, "adjacent segments sharing a boundary should concatenate");
    }

    private static async Task CheckMultiSegmentCancellationAsync(MediaExportService service, string ffmpegPath, string testRoot)
    {
        var cancelSource = await CreateCancellationFixtureAsync(ffmpegPath, testRoot);
        var cancelHash = await HashFileAsync(cancelSource);
        var cancelDestination = Path.Combine(testRoot, "multi-cancelled-output.mp4");
        using var cancellation = new CancellationTokenSource();
        var cancellationProgress = new ImmediateProgress<ExportProgress>(item =>
        {
            if (item.Phase == ExportProgressPhase.Encoding)
            {
                cancellation.Cancel();
            }
        });
        var segments = new List<ExportSegment> { new(0, 20), new(20, 40), new(40, 60) };
        var cancellationThrown = false;
        try
        {
            await service.ExportAsync(new ExportRequest(cancelSource, cancelDestination, 0, 1, Segments: segments), cancellationProgress, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            cancellationThrown = true;
        }

        Assert(cancellationThrown, "cancel request should cancel the multi-segment export");
        Assert(!File.Exists(cancelDestination), "cancelled multi export must not commit the final output");
        Assert(!Directory.EnumerateFiles(testRoot, "*.partial.mp4").Any(), "cancelled multi export must remove its own temporary file");
        Assert(cancelHash == await HashFileAsync(cancelSource), "cancelled multi export must preserve the input bytes");
    }

    private static async Task<(byte R, byte G, byte B)> ReadFramePixelAtAsync(
        string ffmpegPath,
        string videoPath,
        double seconds,
        int x,
        int y,
        string testRoot)
    {
        var imagePath = Path.Combine(testRoot, $"sample-{Guid.NewGuid():N}.ppm");
        await RunFfmpegAsync(ffmpegPath,
        ["-hide_banner", "-loglevel", "error", "-ss", seconds.ToString(CultureInfo.InvariantCulture), "-i", videoPath, "-frames:v", "1", "-f", "image2", "-vcodec", "ppm", "-y", imagePath]);
        var bytes = await File.ReadAllBytesAsync(imagePath);
        var offset = FindPpmPixelOffset(bytes, out var width, out var height);
        if (x < 0 || y < 0 || x >= width || y >= height)
        {
            throw new InvalidOperationException($"sample pixel {x},{y} exceeds output image {width}x{height}");
        }

        offset += (y * width + x) * 3;
        return (bytes[offset], bytes[offset + 1], bytes[offset + 2]);
    }

    private static async Task AssertExportRejectedWithMessageAsync(MediaExportService service, ExportRequest request, string expectedMessage, string assertionName)
    {
        try
        {
            await service.ExportAsync(request);
        }
        catch (ExportValidationException exception)
        {
            Assert(exception.UserMessage == expectedMessage, $"expected fixed copy for {assertionName}, got '{exception.UserMessage}'");
            return;
        }

        throw new InvalidOperationException($"expected validation rejection for {assertionName}");
    }

    private static async Task<string> CreateCancellationFixtureAsync(string ffmpegPath, string testRoot)
    {
        var source = Path.Combine(testRoot, "long-cancel-source.mp4");
        await RunFfmpegAsync(ffmpegPath,
        [
            "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=60:duration=60",
            "-c:v", "libx264", "-preset", "ultrafast", "-crf", "38", "-g", "60", "-pix_fmt", "yuv420p", "-y", source
        ]);
        return source;
    }

    private static async Task WriteQuadrantPpmAsync(string path, int width, int height)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var header = Encoding.ASCII.GetBytes($"P6\n{width} {height}\n255\n");
        await stream.WriteAsync(header);
        var pixels = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var top = y < height / 2;
                var left = x < width / 2;
                var color = (top, left) switch
                {
                    (true, true) => (R: (byte)255, G: (byte)0, B: (byte)0),
                    (true, false) => (R: (byte)0, G: (byte)255, B: (byte)0),
                    (false, true) => (R: (byte)0, G: (byte)0, B: (byte)255),
                    _ => (R: (byte)255, G: (byte)255, B: (byte)0)
                };
                var offset = (y * width + x) * 3;
                pixels[offset] = color.R;
                pixels[offset + 1] = color.G;
                pixels[offset + 2] = color.B;
            }
        }

        await stream.WriteAsync(pixels);
    }

    private static async Task<(byte R, byte G, byte B)> ReadFirstFramePixelAsync(
        string ffmpegPath,
        string videoPath,
        int x,
        int y,
        string testRoot)
    {
        var imagePath = Path.Combine(testRoot, $"sample-{Guid.NewGuid():N}.ppm");
        await RunFfmpegAsync(ffmpegPath,
        ["-hide_banner", "-loglevel", "error", "-i", videoPath, "-frames:v", "1", "-f", "image2", "-vcodec", "ppm", "-y", imagePath]);
        var bytes = await File.ReadAllBytesAsync(imagePath);
        var offset = FindPpmPixelOffset(bytes, out var width, out var height);
        if (x < 0 || y < 0 || x >= width || y >= height)
        {
            throw new InvalidOperationException($"sample pixel {x},{y} exceeds output image {width}x{height}");
        }

        offset += (y * width + x) * 3;
        return (bytes[offset], bytes[offset + 1], bytes[offset + 2]);
    }

    private static async Task<(int Width, int Height)> ReadImageDimensionsAsync(string ffprobePath, string imagePath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffprobePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[]
        {
            "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height", "-of", "json", imagePath
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("could not start ffprobe");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffprobe exited {process.ExitCode}: {error}");
        }

        using var document = JsonDocument.Parse(output);
        var stream = document.RootElement.GetProperty("streams").EnumerateArray().First();
        return (stream.GetProperty("width").GetInt32(), stream.GetProperty("height").GetInt32());
    }

    private static async Task<double> ReadStreamDurationAsync(string ffprobePath, string videoPath, string selector)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffprobePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[]
        {
            "-v", "error", "-select_streams", selector, "-show_entries", "stream=duration", "-of", "json", videoPath
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("could not start ffprobe");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffprobe exited {process.ExitCode}: {error}");
        }

        using var document = JsonDocument.Parse(output);
        var stream = document.RootElement.GetProperty("streams").EnumerateArray().First();
        var durationValue = stream.GetProperty("duration");
        var durationText = durationValue.ValueKind == JsonValueKind.String ? durationValue.GetString() : durationValue.ToString();
        return double.Parse(durationText!, CultureInfo.InvariantCulture);
    }

    private static async Task<string?> ReadVideoCodecTagAsync(string ffprobePath, string videoPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffprobePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[]
        {
            "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=codec_tag_string", "-of", "json", videoPath
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("could not start ffprobe");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffprobe exited {process.ExitCode}: {error}");
        }

        using var document = JsonDocument.Parse(output);
        var stream = document.RootElement.GetProperty("streams").EnumerateArray().First();
        return stream.TryGetProperty("codec_tag_string", out var tag) ? tag.GetString() : null;
    }

    private static void AssertContainer(MediaInfo media, ExportContainer expected, string context)
    {
        var formats = (media.ContainerFormatNames ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var majorBrand = media.ContainerMajorBrand?.Trim();
        var matches = expected switch
        {
            ExportContainer.Mp4 => formats.Contains("mp4", StringComparer.OrdinalIgnoreCase) &&
                                   !string.Equals(majorBrand, "qt", StringComparison.OrdinalIgnoreCase),
            ExportContainer.Mkv => formats.Contains("matroska", StringComparer.OrdinalIgnoreCase),
            ExportContainer.Mov => formats.Contains("mov", StringComparer.OrdinalIgnoreCase) &&
                                   string.Equals(majorBrand, "qt", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
        Assert(matches,
            $"{context} should probe as {expected}, got format '{media.ContainerFormatNames}' and major brand '{media.ContainerMajorBrand}'");
    }

    private static int FindPpmPixelOffset(byte[] bytes, out int width, out int height)
    {
        var cursor = 0;
        string ReadToken()
        {
            while (cursor < bytes.Length && char.IsWhiteSpace((char)bytes[cursor]))
            {
                cursor++;
            }

            var start = cursor;
            while (cursor < bytes.Length && !char.IsWhiteSpace((char)bytes[cursor]))
            {
                cursor++;
            }

            return Encoding.ASCII.GetString(bytes, start, cursor - start);
        }

        if (ReadToken() != "P6")
        {
            throw new InvalidOperationException("ffmpeg did not produce a P6 PPM frame");
        }

        width = int.Parse(ReadToken(), CultureInfo.InvariantCulture);
        height = int.Parse(ReadToken(), CultureInfo.InvariantCulture);
        if (ReadToken() != "255")
        {
            throw new InvalidOperationException("unexpected PPM maximum color value");
        }

        // The final header token stops before its newline; consume exactly the
        // single delimiter so a binary pixel byte that happens to be whitespace
        // remains intact.
        if (cursor < bytes.Length && bytes[cursor] == (byte)'\r') cursor++;
        if (cursor < bytes.Length && bytes[cursor] == (byte)'\n') cursor++;
        return cursor;
    }

    private static async Task AssertExportRejectedAsync(MediaExportService service, ExportRequest request, string assertionName)
    {
        try
        {
            await service.ExportAsync(request);
        }
        catch (ExportValidationException)
        {
            return;
        }

        throw new InvalidOperationException($"expected validation rejection for {assertionName}");
    }

    private static async Task RunFfmpegAsync(string ffmpegPath, IReadOnlyList<string> arguments)
    {
        await RunToolAsync(ffmpegPath, arguments);
    }

    private static async Task RunToolAsync(string executablePath, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"could not start {executablePath}");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await stdout;
        var error = await stderr;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"tool exited {process.ExitCode}: {error}\n{output}");
        }
    }

    private static async Task<string> HashFileAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash);
    }

    private static void Run(string name, Action test)
    {
        test();
        Pass(name);
    }

    private static void Pass(string name)
    {
        _passed++;
        Console.WriteLine($"PASS: {name}");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Expect<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"expected {typeof(TException).Name}");
    }

    private sealed class ImmediateProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
