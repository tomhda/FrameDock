using System.Diagnostics;
using System.Globalization;
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
        Run("SAR and rotation produce oriented square-pixel dimensions", TestDisplayDimensions);
        Run("crop bounds and H.264 chroma alignment are enforced", TestCropValidation);
        Run("rotation accepts only right angles", TestRotationValidation);

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
            Pass("ffprobe reads dimensions, SAR, rotation, duration, codecs, and audio");

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
            Pass("time ranges, crop, rotation, and speed validation reject invalid values");

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
            Pass("stream-copy preserves display properties, reports approximate boundaries, and refuses transforms");

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
