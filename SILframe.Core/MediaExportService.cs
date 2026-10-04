using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SILframe.Core;

/// <summary>Inspects local media and safely exports trims/crops with FFmpeg.</summary>
public sealed class MediaExportService
{
    private const int MaxDiagnosticCharacters = 12_000;
    private readonly ExportOptions _options;

    public MediaExportService(ExportOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<MediaInfo> InspectAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizeExistingFile(sourcePath, Messages.Get("Error_InputFileNotFound"));
        EnsureToolExists(_options.FfprobePath, "ffprobe");

        var result = await ProcessExecution.RunAsync(
            _options.FfprobePath,
            ["-v", "error", "-print_format", "json", "-show_streams", "-show_format", normalizedPath],
            cancellationToken,
            maxDiagnosticCharacters: MaxDiagnosticCharacters);

        if (result.ExitCode != 0)
        {
            throw new ExportException(
                Messages.Get("Error_InspectFailed"),
                FormatDiagnostic("ffprobe", result.StandardError));
        }

        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            return ParseMediaInfo(document.RootElement, normalizedPath);
        }
        catch (ExportException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new ExportException(
                Messages.Get("Error_InspectParseFailed"),
                exception.Message,
                exception);
        }
    }

    public async Task<ExportResult> ExportAsync(
        ExportRequest request,
        IProgress<ExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        Report(progress, ExportProgressPhase.Preparing, 0, 0, 0, Messages.Get("Progress_Preparing"));

        var sourcePath = NormalizeExistingFile(request.SourcePath, Messages.Get("Error_InputFileNotFound"));
        var destinationPath = NormalizeDestination(request.DestinationPath);

        if (string.Equals(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ExportValidationException(Messages.Get("Error_SameAsSource"));
        }

        var outputExtension = GetExtension(request.OutputContainer);
        if (!string.Equals(Path.GetExtension(destinationPath), outputExtension, StringComparison.OrdinalIgnoreCase))
        {
            throw new ExportValidationException(Messages.Format("Error_ExtensionMismatch", outputExtension));
        }

        if (File.Exists(destinationPath))
        {
            throw new ExportValidationException(Messages.Get("Error_DestinationExists"));
        }

        var destinationDirectory = Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrEmpty(destinationDirectory) || !Directory.Exists(destinationDirectory))
        {
            throw new ExportValidationException(Messages.Get("Error_DestinationFolderMissing"));
        }

        ValidateEncoderOptions();
        EnsureToolExists(_options.FfmpegPath, "FFmpeg");
        EnsureToolExists(_options.FfprobePath, "ffprobe");

        var media = await InspectAsync(sourcePath, cancellationToken);
        if (request.Segments is { Count: > 0 })
        {
            return await ExportMultiSegmentAsync(request, request.Segments, media, sourcePath, destinationPath, destinationDirectory, outputExtension, progress, cancellationToken);
        }
        ValidateRequest(request, media);
        if (request.Mode == ExportMode.AccurateReencode && media.IsHdr)
        {
            throw new ExportValidationException(
                Messages.Get("Error_HdrAccurateNotSupported"));
        }

        var temporaryPath = CreateTemporaryPath(destinationDirectory, Path.GetFileNameWithoutExtension(destinationPath), outputExtension);
        try
        {
            var requestedDuration = request.EndSeconds - request.StartSeconds;
            var expectedDuration = request.Mode == ExportMode.AccurateReencode
                ? requestedDuration / request.PlaybackSpeed
                : requestedDuration;
            var arguments = BuildExportArguments(media, request, sourcePath, temporaryPath);
            var lastProgress = 0d;
            void OnProgressLine(string line)
            {
                if (!TryReadProcessedSeconds(line, out var processedSeconds))
                {
                    return;
                }

                var fraction = Math.Clamp(processedSeconds / expectedDuration, 0, 0.995);
                lastProgress = Math.Max(lastProgress, fraction);
                Report(progress, ExportProgressPhase.Encoding, lastProgress, processedSeconds, expectedDuration, Messages.Get("Progress_Encoding"));
            }

            var processResult = await ProcessExecution.RunAsync(
                _options.FfmpegPath,
                arguments,
                cancellationToken,
                onStandardOutputLine: OnProgressLine,
                maxDiagnosticCharacters: MaxDiagnosticCharacters);

            if (processResult.ExitCode != 0)
            {
                throw new ExportException(
                    request.Mode == ExportMode.StreamCopyApproximate
                        ? Messages.Get("Error_StreamCopyFailed")
                        : Messages.Get("Error_ExportFailed"),
                    FormatDiagnostic("FFmpeg", processResult.StandardError));
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(temporaryPath) || new FileInfo(temporaryPath).Length <= 0)
            {
                throw new ExportException(Messages.Get("Error_ExportEmpty"));
            }

            Report(progress, ExportProgressPhase.Verifying, Math.Max(lastProgress, 0.995), expectedDuration, expectedDuration, Messages.Get("Progress_Verifying"));
            MediaInfo outputInfo;
            try
            {
                outputInfo = await InspectAsync(temporaryPath, cancellationToken);
            }
            catch (ExportException exception) when (request.Mode == ExportMode.StreamCopyApproximate)
            {
                throw new ExportException(
                    Messages.Get("Error_StreamCopyNoKeyframe"),
                    exception.Diagnostic ?? exception.UserMessage,
                    exception);
            }

            ValidateOutputMedia(outputInfo, media, request, _options.Encoder);

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // File.Move without overwrite is atomic on the same volume and
                // also closes the race with another file appearing at the target.
                File.Move(temporaryPath, destinationPath, overwrite: false);
            }
            catch (IOException) when (File.Exists(destinationPath))
            {
                throw new ExportValidationException(Messages.Get("Error_DestinationExists"));
            }

            Report(progress, ExportProgressPhase.Completed, 1, expectedDuration, expectedDuration, Messages.Get("Progress_Completed"));
            return new ExportResult(
                destinationPath,
                requestedDuration,
                outputInfo,
                request.Mode,
                request.Mode == ExportMode.StreamCopyApproximate);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ExportException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new ExportException(Messages.Get("Error_ExportWriteFailed"), exception.Message, exception);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    public async Task<string> ExportFrameAsync(
        string sourcePath,
        string destinationPath,
        double timeSeconds,
        CropRect? crop,
        int additionalRotationDegreesClockwise,
        double zoomFactor,
        double zoomFocusX,
        double zoomFocusY,
        CancellationToken cancellationToken = default)
    {
        var source = NormalizeExistingFile(sourcePath, Messages.Get("Error_InputFileNotFound"));
        var destination = NormalizeDestination(destinationPath);
        if (File.Exists(destination))
        {
            throw new ExportValidationException(Messages.Get("Error_DestinationExists"));
        }

        var destinationDirectory = Path.GetDirectoryName(destination);
        if (string.IsNullOrEmpty(destinationDirectory) || !Directory.Exists(destinationDirectory))
        {
            throw new ExportValidationException(Messages.Get("Error_DestinationFolderMissing"));
        }

        if (!double.IsFinite(timeSeconds))
        {
            throw new ExportValidationException(Messages.Get("Error_TimeMustBeFinite"));
        }

        if (timeSeconds < 0)
        {
            throw new ExportValidationException(Messages.Get("Error_StartNonNegative"));
        }

        ValidateEncoderOptions();
        EnsureToolExists(_options.FfmpegPath, "FFmpeg");
        EnsureToolExists(_options.FfprobePath, "ffprobe");

        var media = await InspectAsync(source, cancellationToken);
        MediaGeometry.ValidateAdditionalRotation(additionalRotationDegreesClockwise);
        MediaGeometry.ValidateZoom(zoomFactor, zoomFocusX, zoomFocusY);
        if (crop is { } rectangle)
        {
            MediaGeometry.ValidateCrop(rectangle, media.DisplayWidth, media.DisplayHeight);
        }

        var clampedTime = Math.Clamp(timeSeconds, 0, Math.Max(0, media.DurationSeconds));
        // Uses the same video filter as accurate export so the saved pixels
        // match the export output (SAR, rotation, crop, zoom, extra rotation).
        var filter = MediaGeometry.BuildVideoFilter(
            media,
            crop,
            additionalRotationDegreesClockwise,
            1.0,
            zoomFactor,
            zoomFocusX,
            zoomFocusY);
        var temporaryPath = CreateTemporaryPath(
            destinationDirectory,
            Path.GetFileNameWithoutExtension(destination),
            ".png");
        try
        {
            // "-update 1" writes one image to the literal path; without it a "%"
            // in the file name would be expanded as a sequence pattern.
            string[] output = ["-map", "0:v:0", "-an", "-sn", "-dn", "-vf", filter, "-c:v", "png", "-update", "1", "-f", "image2", temporaryPath];
            var arguments = new List<string>
            {
                "-hide_banner", "-loglevel", "error", "-nostats",
                "-n"
            };
            if (Math.Abs(media.StartTimeSeconds) <= 0.001)
            {
                arguments.AddRange(["-ss", FormatSeconds(clampedTime), "-noautorotate", "-i", source]);
            }
            else if (clampedTime > 15)
            {
                // Same two-stage seek as accurate export, so a late frame does
                // not decode the whole file from the start.
                arguments.AddRange(["-ss", FormatSeconds(clampedTime - 10), "-noautorotate", "-i", source, "-ss", "10"]);
            }
            else
            {
                arguments.AddRange(["-noautorotate", "-i", source, "-ss", FormatSeconds(clampedTime)]);
            }

            arguments.AddRange(["-frames:v", "1"]);
            arguments.AddRange(output);
            var processResult = await ProcessExecution.RunAsync(
                _options.FfmpegPath,
                arguments,
                cancellationToken,
                maxDiagnosticCharacters: MaxDiagnosticCharacters);
            cancellationToken.ThrowIfCancellationRequested();
            // The position can be past the last video frame (audio can run
            // longer, and a paused player rests at the very end). Step back and
            // keep the final frame of that stretch, as the player shows it.
            foreach (var stepBackSeconds in new[] { 2d, 10d, 60d })
            {
                if (processResult.ExitCode != 0 || (File.Exists(temporaryPath) && new FileInfo(temporaryPath).Length > 0))
                {
                    break;
                }

                TryDelete(temporaryPath);
                List<string> tailArguments =
                [
                    "-hide_banner", "-loglevel", "error", "-nostats", "-n",
                    "-ss", FormatSeconds(Math.Max(0, clampedTime - stepBackSeconds)), "-noautorotate", "-i", source
                ];
                tailArguments.AddRange(output);
                processResult = await ProcessExecution.RunAsync(
                    _options.FfmpegPath,
                    tailArguments,
                    cancellationToken,
                    maxDiagnosticCharacters: MaxDiagnosticCharacters);
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (processResult.ExitCode != 0)
            {
                throw new ExportException(
                    Messages.Get("Error_ExportFailed"),
                    FormatDiagnostic("FFmpeg", processResult.StandardError));
            }

            if (!File.Exists(temporaryPath) || new FileInfo(temporaryPath).Length <= 0)
            {
                throw new ExportException(Messages.Get("Error_ExportEmpty"));
            }

            try
            {
                File.Move(temporaryPath, destination, overwrite: false);
            }
            catch (IOException) when (File.Exists(destination))
            {
                throw new ExportValidationException(Messages.Get("Error_DestinationExists"));
            }

            return destination;
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static void ValidateMultiSegmentRequest(ExportRequest request, MediaInfo media)
    {
        if (request.Mode is not ExportMode.AccurateReencode and not ExportMode.StreamCopyApproximate)
        {
            throw new ExportValidationException(Messages.Get("Error_ExportModeRequired"));
        }

        if (!Enum.IsDefined(request.OutputContainer))
        {
            throw new ExportValidationException(Messages.Get("Error_ExportContainerRequired"));
        }

        var segments = request.Segments!;
        if (request.Mode == ExportMode.StreamCopyApproximate && segments.Count >= 2)
        {
            throw new ExportValidationException(Messages.Get("Error_MultiSegmentStreamCopyNotSupported"));
        }

        foreach (var segment in segments)
        {
            if (!double.IsFinite(segment.StartSeconds) || !double.IsFinite(segment.EndSeconds))
            {
                throw new ExportValidationException(Messages.Get("Error_TimeMustBeFinite"));
            }

            if (segment.StartSeconds < 0)
            {
                throw new ExportValidationException(Messages.Get("Error_StartNonNegative"));
            }

            if (segment.EndSeconds <= segment.StartSeconds)
            {
                throw new ExportValidationException(Messages.Get("Error_EndAfterStart"));
            }

            if (segment.EndSeconds > media.DurationSeconds)
            {
                throw new ExportValidationException(Messages.Get("Error_EndBeyondDuration"));
            }

            MediaGeometry.ValidateAdditionalRotation(segment.AdditionalRotationDegreesClockwise);
            MediaGeometry.ValidatePlaybackSpeed(segment.PlaybackSpeed);
            MediaGeometry.ValidateZoom(segment.ZoomFactor, segment.ZoomFocusX, segment.ZoomFocusY);
            if (segment.Crop is { } crop)
            {
                MediaGeometry.ValidateCrop(crop, media.DisplayWidth, media.DisplayHeight);
            }
        }

        if (request.Mode == ExportMode.StreamCopyApproximate && segments.Count == 1)
        {
            var only = segments[0];
            if (only.Crop is not null || only.AdditionalRotationDegreesClockwise != 0 || only.PlaybackSpeed != 1.0 || only.ZoomFactor > 1.0)
            {
                throw new ExportValidationException(Messages.Get("Error_StreamCopyTransformNotSupported"));
            }
        }

        for (var i = 1; i < segments.Count; i++)
        {
            if (!(segments[i].StartSeconds >= segments[i - 1].EndSeconds))
            {
                throw new ExportValidationException(Messages.Get("Error_SegmentOrderOverlap"));
            }
        }
    }

    private async Task<ExportResult> ExportMultiSegmentAsync(
        ExportRequest request,
        IReadOnlyList<ExportSegment> segments,
        MediaInfo media,
        string sourcePath,
        string destinationPath,
        string? destinationDirectory,
        string outputExtension,
        IProgress<ExportProgress>? progress,
        CancellationToken cancellationToken)
    {
        ValidateMultiSegmentRequest(request, media);
        if (request.Mode == ExportMode.AccurateReencode && media.IsHdr)
        {
            throw new ExportValidationException(
                Messages.Get("Error_HdrAccurateNotSupported"));
        }

        if (request.Mode == ExportMode.StreamCopyApproximate)
        {
            var only = segments[0];
            var single = new ExportRequest(
                request.SourcePath,
                request.DestinationPath,
                only.StartSeconds,
                only.EndSeconds,
                only.Crop,
                request.Mode,
                only.AdditionalRotationDegreesClockwise,
                only.PlaybackSpeed,
                request.OutputContainer,
                only.ZoomFactor,
                only.ZoomFocusX,
                only.ZoomFocusY);
            return await ExportAsync(single, progress, cancellationToken);
        }

        var canvas = MediaGeometry.ComputeExportDimensions(media, segments[0].Crop, segments[0].AdditionalRotationDegreesClockwise);
        var requestedDuration = 0d;
        var expectedDuration = 0d;
        foreach (var segment in segments)
        {
            var length = segment.EndSeconds - segment.StartSeconds;
            requestedDuration += length;
            expectedDuration += length / segment.PlaybackSpeed;
        }

        var temporaryPath = CreateTemporaryPath(destinationDirectory!, Path.GetFileNameWithoutExtension(destinationPath), outputExtension);
        try
        {
            var arguments = BuildMultiSegmentArguments(media, request, segments, sourcePath, temporaryPath, canvas.Item1, canvas.Item2, expectedDuration);
            var lastProgress = 0d;
            void OnProgressLine(string line)
            {
                if (!TryReadProcessedSeconds(line, out var processedSeconds))
                {
                    return;
                }

                var fraction = Math.Clamp(processedSeconds / expectedDuration, 0, 0.995);
                lastProgress = Math.Max(lastProgress, fraction);
                Report(progress, ExportProgressPhase.Encoding, lastProgress, processedSeconds, expectedDuration, Messages.Get("Progress_Encoding"));
            }

            var processResult = await ProcessExecution.RunAsync(
                _options.FfmpegPath,
                arguments,
                cancellationToken,
                onStandardOutputLine: OnProgressLine,
                maxDiagnosticCharacters: MaxDiagnosticCharacters);

            if (processResult.ExitCode != 0)
            {
                throw new ExportException(
                    Messages.Get("Error_ExportFailed"),
                    FormatDiagnostic("FFmpeg", processResult.StandardError));
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(temporaryPath) || new FileInfo(temporaryPath).Length <= 0)
            {
                throw new ExportException(Messages.Get("Error_ExportEmpty"));
            }

            Report(progress, ExportProgressPhase.Verifying, Math.Max(lastProgress, 0.995), expectedDuration, expectedDuration, Messages.Get("Progress_Verifying"));
            var outputInfo = await InspectAsync(temporaryPath, cancellationToken);
            ValidateMultiSegmentOutputMedia(outputInfo, media, request, segments, canvas.Item1, canvas.Item2, _options.Encoder);

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Move(temporaryPath, destinationPath, overwrite: false);
            }
            catch (IOException) when (File.Exists(destinationPath))
            {
                throw new ExportValidationException(Messages.Get("Error_DestinationExists"));
            }

            Report(progress, ExportProgressPhase.Completed, 1, expectedDuration, expectedDuration, Messages.Get("Progress_Completed"));
            return new ExportResult(
                destinationPath,
                requestedDuration,
                outputInfo,
                request.Mode,
                false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ExportException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new ExportException(Messages.Get("Error_ExportWriteFailed"), exception.Message, exception);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private IReadOnlyList<string> BuildMultiSegmentArguments(
        MediaInfo media,
        ExportRequest request,
        IReadOnlyList<ExportSegment> segments,
        string sourcePath,
        string temporaryPath,
        int canvasWidth,
        int canvasHeight,
        double expectedDuration)
    {
        var arguments = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostats", "-progress", "pipe:1",
            "-n"
        };

        foreach (var segment in segments)
        {
            arguments.AddRange(["-ss", FormatSeconds(segment.StartSeconds), "-noautorotate", "-i", sourcePath]);
        }

        var graph = new StringBuilder();
        var videoLabels = new List<string>(segments.Count);
        var audioLabels = new List<string>(segments.Count);
        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            var sourceLength = segment.EndSeconds - segment.StartSeconds;
            var trimEnd = FormatSeconds(sourceLength);
            var filters = new List<string>
            {
                "setpts=PTS-STARTPTS",
                $"trim=start=0:end={trimEnd}",
                "setpts=PTS-STARTPTS",
                $"scale={media.DisplayWidthBeforeRotation()}:{media.DisplayHeightBeforeRotation()}:flags=lanczos",
                "setsar=1"
            };

            switch (media.RotationDegreesClockwise)
            {
                case 90:
                    filters.Add("transpose=clock");
                    break;
                case 180:
                    filters.Add("hflip,vflip");
                    break;
                case 270:
                    filters.Add("transpose=cclock");
                    break;
            }

            if (segment.Crop is { } rectangle)
            {
                filters.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"crop={rectangle.Width}:{rectangle.Height}:{rectangle.X}:{rectangle.Y}"));
            }

            if (segment.ZoomFactor > 1.0)
            {
                var viewport = segment.Crop ?? new CropRect(0, 0, media.DisplayWidth, media.DisplayHeight);
                var sample = MediaGeometry.ComputeZoomSampleRect(viewport, segment.ZoomFactor, segment.ZoomFocusX, segment.ZoomFocusY);
                filters.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"crop={sample.Width}:{sample.Height}:{sample.X - viewport.X}:{sample.Y - viewport.Y}"));
                filters.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"scale={viewport.Width}:{viewport.Height}:flags=lanczos"));
            }

            switch (segment.AdditionalRotationDegreesClockwise)
            {
                case 90:
                    filters.Add("transpose=clock");
                    break;
                case 180:
                    filters.Add("hflip,vflip");
                    break;
                case 270:
                    filters.Add("transpose=cclock");
                    break;
            }

            if (segment.PlaybackSpeed != 1.0)
            {
                filters.Add($"setpts=PTS/{FormatSeconds(segment.PlaybackSpeed)}");
                filters.Add("setpts=PTS-STARTPTS");
            }

            filters.Add("pad=ceil(iw/2)*2:ceil(ih/2)*2");
            var edited = MediaGeometry.ComputeExportDimensions(media, segment.Crop, segment.AdditionalRotationDegreesClockwise);
            if (edited != (canvasWidth, canvasHeight))
            {
                filters.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"scale={canvasWidth}:{canvasHeight}:force_original_aspect_ratio=decrease:flags=lanczos"));
                filters.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"pad={canvasWidth}:{canvasHeight}:(ow-iw)/2:(oh-ih)/2:black"));
            }

            filters.Add("setsar=1");
            filters.Add("sidedata=mode=delete:type=DISPLAYMATRIX");
            var videoOut = $"[v{index}]";
            graph.Append($"[{index}:v:0]{string.Join(',', filters)}{videoOut};");
            videoLabels.Add(videoOut);

            if (media.HasAudio)
            {
                var audioFilters = new List<string>
                {
                    "asetpts=PTS-STARTPTS",
                    $"atrim=start=0:end={trimEnd}",
                    "asetpts=PTS-STARTPTS"
                };
                if (segment.PlaybackSpeed != 1.0)
                {
                    audioFilters.Add(BuildAtempoFilter(segment.PlaybackSpeed));
                }

                audioFilters.Add("asetpts=PTS-STARTPTS");
                var audioOut = $"[a{index}]";
                graph.Append($"[{index}:a:0]{string.Join(',', audioFilters)}{audioOut};");
                audioLabels.Add(audioOut);
            }
        }

        graph.Append($"{string.Concat(videoLabels)}concat=n={segments.Count}:v=1:a=0[vout]");
        if (media.HasAudio)
        {
            graph.Append(';');
            graph.Append($"{string.Concat(audioLabels)}concat=n={segments.Count}:v=0:a=1[aout]");
        }

        arguments.AddRange(["-filter_complex", graph.ToString()]);
        arguments.AddRange(["-map", "[vout]"]);
        if (media.HasAudio)
        {
            arguments.AddRange(["-map", "[aout]"]);
        }

        arguments.AddRange(["-sn", "-dn"]);
        arguments.AddRange(["-t", FormatSeconds(expectedDuration)]);
        arguments.AddRange(
        [
            "-map_metadata", "-1", "-map_metadata:s:v:0", "-1",
            "-c:v", _options.Encoder == VideoEncoder.H264 ? "libx264" : "libx265", "-preset", ExportPresetName(),
            "-crf", _options.ConstantRateFactor.ToString(CultureInfo.InvariantCulture),
            "-pix_fmt", "yuv420p"
        ]);

        if (media.HasAudio)
        {
            arguments.AddRange(["-c:a", "aac", "-b:a", "192k"]);
        }

        if (request.OutputContainer is ExportContainer.Mp4 or ExportContainer.Mov)
        {
            arguments.AddRange(["-movflags", "+faststart"]);
        }

        if (_options.Encoder == VideoEncoder.H265 && request.OutputContainer is ExportContainer.Mp4 or ExportContainer.Mov)
        {
            arguments.AddRange(["-tag:v", "hvc1"]);
        }

        arguments.AddRange(["-f", GetMuxer(request.OutputContainer), temporaryPath]);
        return arguments;
    }

    private static void ValidateMultiSegmentOutputMedia(
        MediaInfo output,
        MediaInfo source,
        ExportRequest request,
        IReadOnlyList<ExportSegment> segments,
        int canvasWidth,
        int canvasHeight,
        VideoEncoder encoder)
    {
        ValidateOutputContainer(output, request.OutputContainer);
        if (output.RotationDegreesClockwise != 0)
        {
            throw new ExportException(Messages.Get("Error_OutputRotationMismatch"));
        }

        if (output.CodedWidth != canvasWidth || output.CodedHeight != canvasHeight)
        {
            throw new ExportException(Messages.Get("Error_OutputSizeMismatch"));
        }

        if (output.HasAudio != source.HasAudio)
        {
            throw new ExportException(Messages.Get("Error_OutputAudioMismatch"));
        }

        var expectedDuration = 0d;
        foreach (var segment in segments)
        {
            expectedDuration += (segment.EndSeconds - segment.StartSeconds) / segment.PlaybackSpeed;
        }

        var baseTolerance = Math.Max(0.15, Math.Min(1, expectedDuration * 0.02));
        var durationTolerance = baseTolerance + (0.1 * (segments.Count - 1));
        var expectedVideoCodec = encoder == VideoEncoder.H264 ? "h264" : "hevc";
        if (!output.VideoCodec.Equals(expectedVideoCodec, StringComparison.OrdinalIgnoreCase) ||
            (source.HasAudio && !string.Equals(output.AudioCodec, "aac", StringComparison.OrdinalIgnoreCase)) ||
            Math.Abs(output.DurationSeconds - expectedDuration) > durationTolerance)
        {
            throw new ExportException(Messages.Get("Error_OutputFormatMismatch"));
        }

        if (!double.IsFinite(output.DurationSeconds) || output.DurationSeconds <= 0)
        {
            throw new ExportException(Messages.Get("Error_OutputDurationUnknown"));
        }
    }

    private static MediaInfo ParseMediaInfo(JsonElement root, string sourcePath)
    {
        if (!root.TryGetProperty("streams", out var streamsElement) || streamsElement.ValueKind != JsonValueKind.Array)
        {
            throw new ExportException(Messages.Get("Error_NoVideoInfo"));
        }

        var streams = streamsElement.EnumerateArray().ToArray();
        var video = streams.FirstOrDefault(IsVideoStream);
        if (video.ValueKind == JsonValueKind.Undefined)
        {
            throw new ExportException(Messages.Get("Error_NoVideoStream"));
        }

        var width = ReadInt(video, "width");
        var height = ReadInt(video, "height");
        var sar = ParseAspectRatio(ReadString(video, "sample_aspect_ratio"));
        var rotation = ParseClockwiseRotation(video);
        var dimensions = MediaGeometry.ComputeDisplayDimensions(width, height, sar, rotation);
        var duration = ReadNumber(root, "format", "duration") ?? double.NaN;
        if (!double.IsFinite(duration) || duration <= 0)
        {
            duration = streams
                .Select(stream => ReadNumber(stream, "duration"))
                .Where(value => value.HasValue && value.Value > 0 && double.IsFinite(value.Value))
                .Select(value => value!.Value)
                .DefaultIfEmpty(double.NaN)
                .Max();
        }
        if (!double.IsFinite(duration) || duration <= 0)
        {
            throw new ExportException(Messages.Get("Error_NoDuration"));
        }

        var startTime = ReadNumber(video, "start_time") ?? ReadNumber(root, "format", "start_time") ?? 0;
        if (!double.IsFinite(startTime))
        {
            startTime = 0;
        }

        var audio = streams.FirstOrDefault(stream => string.Equals(ReadString(stream, "codec_type"), "audio", StringComparison.OrdinalIgnoreCase));
        var audioExists = audio.ValueKind != JsonValueKind.Undefined;
        var transfer = ReadString(video, "color_transfer");
        var primaries = ReadString(video, "color_primaries");
        var hdrReason = DetectHdr(video, transfer, primaries);
        var formatElement = root.TryGetProperty("format", out var foundFormat) ? foundFormat : default;
        var audioStreamCount = streams.Count(stream => string.Equals(ReadString(stream, "codec_type"), "audio", StringComparison.OrdinalIgnoreCase));
        var subtitleStreamCount = streams.Count(stream => string.Equals(ReadString(stream, "codec_type"), "subtitle", StringComparison.OrdinalIgnoreCase));
        var frameRate = ParseFrameRate(ReadString(video, "avg_frame_rate")) ?? ParseFrameRate(ReadString(video, "r_frame_rate"));
        var videoBitRate = ReadLong(video, "bit_rate");
        var formatObject = formatElement.ValueKind == JsonValueKind.Object ? formatElement : (JsonElement?)null;

        return new MediaInfo
        {
            SourcePath = sourcePath,
            DurationSeconds = duration,
            StartTimeSeconds = startTime,
            CodedWidth = width,
            CodedHeight = height,
            SampleAspectRatio = sar,
            RotationDegreesClockwise = rotation,
            DisplayWidth = dimensions.Width,
            DisplayHeight = dimensions.Height,
            HasAudio = audioExists,
            VideoCodec = ReadString(video, "codec_name") ?? "unknown",
            AudioCodec = audioExists ? ReadString(audio, "codec_name") : null,
            PixelFormat = ReadString(video, "pix_fmt"),
            ColorTransfer = transfer,
            ColorPrimaries = primaries,
            ContainerFormatNames = formatElement.ValueKind == JsonValueKind.Object ? ReadString(formatElement, "format_name") : null,
            ContainerMajorBrand = formatElement.ValueKind == JsonValueKind.Object ? ReadTag(formatElement, "major_brand") : null,
            IsHdr = hdrReason is not null,
            HdrDescription = hdrReason,
            FrameRate = frameRate,
            VideoBitRate = videoBitRate,
            AudioChannels = audioExists ? ReadIntNullable(audio, "channels") : null,
            AudioChannelLayout = audioExists ? ReadString(audio, "channel_layout") : null,
            AudioSampleRate = audioExists ? ReadIntNullable(audio, "sample_rate") : null,
            AudioBitRate = audioExists ? ReadLong(audio, "bit_rate") : null,
            AudioStreamCount = audioStreamCount,
            SubtitleStreamCount = subtitleStreamCount,
            OverallBitRate = formatObject.HasValue ? ReadLong(formatObject.Value, "bit_rate") : null,
            FileSizeBytes = formatObject.HasValue ? ReadLong(formatObject.Value, "size") : null,
            ContainerLongName = formatObject.HasValue ? ReadString(formatObject.Value, "format_long_name") : null
        };
    }

    private static bool IsVideoStream(JsonElement stream)
    {
        if (!string.Equals(ReadString(stream, "codec_type"), "video", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !stream.TryGetProperty("disposition", out var disposition) ||
            !disposition.TryGetProperty("attached_pic", out var attachedPicture) ||
            attachedPicture.ValueKind != JsonValueKind.Number || attachedPicture.GetInt32() == 0;
    }

    private static string? DetectHdr(JsonElement video, string? transfer, string? primaries)
    {
        if (string.Equals(transfer, "smpte2084", StringComparison.OrdinalIgnoreCase))
        {
            return "PQ (SMPTE ST 2084)";
        }

        if (string.Equals(transfer, "arib-std-b67", StringComparison.OrdinalIgnoreCase))
        {
            return "HLG (ARIB STD-B67)";
        }

        if (video.TryGetProperty("side_data_list", out var sideData) && sideData.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in sideData.EnumerateArray())
            {
                var type = ReadString(item, "side_data_type") ?? string.Empty;
                if (type.Contains("mastering display", StringComparison.OrdinalIgnoreCase) ||
                    type.Contains("content light level", StringComparison.OrdinalIgnoreCase) ||
                    type.Contains("dolby vision", StringComparison.OrdinalIgnoreCase) ||
                    type.Contains("dovi", StringComparison.OrdinalIgnoreCase) ||
                    type.Contains("dynamic hdr", StringComparison.OrdinalIgnoreCase) ||
                    type.Contains("HDR10+", StringComparison.OrdinalIgnoreCase))
                {
                    return type;
                }
            }
        }

        if (string.Equals(primaries, "bt2020", StringComparison.OrdinalIgnoreCase) &&
            (transfer is null || transfer.Equals("unknown", StringComparison.OrdinalIgnoreCase)))
        {
            return Messages.Get("Hdr_Bt2020Possible");
        }

        return null;
    }

    private static int ParseClockwiseRotation(JsonElement video)
    {
        if (video.TryGetProperty("side_data_list", out var sideData) && sideData.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in sideData.EnumerateArray())
            {
                if (ReadNumber(item, "rotation") is { } counterClockwiseAngle)
                {
                    // ffprobe's Display Matrix rotation is counter-clockwise.
                    return AngleToRightAngle(-counterClockwiseAngle);
                }
            }
        }

        var legacyTag = ReadTag(video, "rotate");
        if (TryParseFiniteNumber(legacyTag, out var clockwiseAngle))
        {
            // The old container rotate tag is conventionally clockwise.
            return AngleToRightAngle(clockwiseAngle);
        }

        return 0;
    }

    private static int AngleToRightAngle(double angle)
    {
        if (!double.IsFinite(angle))
        {
            return 0;
        }

        var nearest = Math.Round(angle / 90, MidpointRounding.AwayFromZero) * 90;
        if (Math.Abs(nearest - angle) > 0.1)
        {
            throw new ExportValidationException(Messages.Get("Error_RotationNotRightAngle"));
        }

        return ((checked((int)nearest) % 360) + 360) % 360;
    }

    private static AspectRatio ParseAspectRatio(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Equals("N/A", StringComparison.OrdinalIgnoreCase))
        {
            return AspectRatio.Square;
        }

        var parts = text.Split([':', '/'], StringSplitOptions.TrimEntries);
        if (parts.Length == 2 &&
            int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var numerator) &&
            int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var denominator) &&
            numerator > 0 && denominator > 0)
        {
            return new AspectRatio(numerator, denominator);
        }

        return AspectRatio.Square;
    }

    private static string? ReadTag(JsonElement stream, string tagName)
    {
        if (!stream.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in tags.EnumerateObject())
        {
            if (property.Name.Equals(tagName, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : property.Value.ToString();
            }
        }

        return null;
    }

    private static string? ReadString(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static double? ParseFrameRate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var parts = text.Split('/');
        if (parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator) &&
            double.IsFinite(numerator) && double.IsFinite(denominator) && denominator != 0)
        {
            var rate = numerator / denominator;
            return double.IsFinite(rate) && rate > 0 ? rate : null;
        }

        return TryParseFiniteNumber(text, out var direct) && direct > 0 ? direct : null;
    }

    private static long? ReadLong(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) && result >= 0
            ? result
            : null;
    }

    private static int? ReadIntNullable(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) && result >= 0
            ? result
            : null;
    }

    private static int ReadInt(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var value) || !value.TryGetInt32(out var result))
        {
            throw new ExportException(Messages.Get("Error_VideoSizeUnknown"));
        }

        return result;
    }

    private static double? ReadNumber(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return TryParseFiniteNumber(value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString(), out var number)
            ? number
            : null;
    }

    private static double? ReadNumber(JsonElement root, string parentName, string propertyName)
    {
        return root.TryGetProperty(parentName, out var parent) ? ReadNumber(parent, propertyName) : null;
    }

    private static bool TryParseFiniteNumber(string? text, out double value)
    {
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value))
        {
            return true;
        }

        value = 0;
        return false;
    }

    private static void ValidateRequest(ExportRequest request, MediaInfo media)
    {
        if (!double.IsFinite(request.StartSeconds) || !double.IsFinite(request.EndSeconds))
        {
            throw new ExportValidationException(Messages.Get("Error_TimeMustBeFinite"));
        }

        if (request.StartSeconds < 0)
        {
            throw new ExportValidationException(Messages.Get("Error_StartNonNegative"));
        }

        if (request.EndSeconds <= request.StartSeconds)
        {
            throw new ExportValidationException(Messages.Get("Error_EndAfterStart"));
        }

        if (request.EndSeconds > media.DurationSeconds)
        {
            throw new ExportValidationException(Messages.Get("Error_EndBeyondDuration"));
        }

        if (request.Mode is not ExportMode.AccurateReencode and not ExportMode.StreamCopyApproximate)
        {
            throw new ExportValidationException(Messages.Get("Error_ExportModeRequired"));
        }

        if (!Enum.IsDefined(request.OutputContainer))
        {
            throw new ExportValidationException(Messages.Get("Error_ExportContainerRequired"));
        }

        MediaGeometry.ValidateAdditionalRotation(request.AdditionalRotationDegreesClockwise);
        MediaGeometry.ValidatePlaybackSpeed(request.PlaybackSpeed);
        MediaGeometry.ValidateZoom(request.ZoomFactor, request.ZoomFocusX, request.ZoomFocusY);

        if (request.Mode == ExportMode.StreamCopyApproximate &&
            (request.Crop is not null || request.AdditionalRotationDegreesClockwise != 0 || request.PlaybackSpeed != 1.0 || request.ZoomFactor > 1.0))
        {
            throw new ExportValidationException(Messages.Get("Error_StreamCopyTransformNotSupported"));
        }

        if (request.Crop is { } crop)
        {
            MediaGeometry.ValidateCrop(crop, media.DisplayWidth, media.DisplayHeight);
        }
    }

    private void ValidateEncoderOptions()
    {
        if (_options.ConstantRateFactor is < 0 or > 51)
        {
            throw new ExportValidationException(Messages.Get("Error_QualityOutOfRange"));
        }

        if (!Enum.IsDefined(_options.Preset) || !Enum.IsDefined(_options.Encoder))
        {
            throw new ExportValidationException(Messages.Get("Error_EncoderInvalid"));
        }
    }

    private IReadOnlyList<string> BuildExportArguments(
        MediaInfo media,
        ExportRequest request,
        string sourcePath,
        string temporaryPath)
    {
        var duration = request.Mode == ExportMode.AccurateReencode
            ? FormatSeconds((request.EndSeconds - request.StartSeconds) / request.PlaybackSpeed)
            : FormatSeconds(request.EndSeconds - request.StartSeconds);
        var arguments = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostats", "-progress", "pipe:1",
            "-n"
        };

        if (request.Mode == ExportMode.AccurateReencode && request.StartSeconds > 15 && Math.Abs(media.StartTimeSeconds) > 0.001)
        {
            // Two-stage seek for late trims on nonzero-start inputs: coarse
            // input-side seek is fast, then output-side seek runs on the
            // filtered timeline after setpts/atempo change playback speed.
            arguments.AddRange(["-ss", FormatSeconds(request.StartSeconds - 10), "-noautorotate", "-i", sourcePath, "-ss", FormatSeconds(10 / request.PlaybackSpeed)]);
        }
        else if (Math.Abs(media.StartTimeSeconds) > 0.001)
        {
            // Some MPEG-TS demuxers expose their first video timestamp as a
            // positive start_time but still treat input-side -ss as an absolute
            // timestamp. Output-side seeking decodes the preroll, but runs on
            // the filtered timeline, after setpts/atempo change playback speed.
            var outputSeekSeconds = request.Mode == ExportMode.AccurateReencode
                ? request.StartSeconds / request.PlaybackSpeed
                : request.StartSeconds;
            arguments.AddRange(["-noautorotate", "-i", sourcePath, "-ss", FormatSeconds(outputSeekSeconds)]);
        }
        else
        {
            // Input-side seek is fast and still frame-accurate when transcoding;
            // FFmpeg decodes and discards preroll before the requested point.
            arguments.AddRange(["-ss", FormatSeconds(request.StartSeconds), "-noautorotate", "-i", sourcePath]);
        }

        arguments.AddRange(["-t", duration, "-map", "0:v:0", "-map", "0:a:0?", "-sn", "-dn"]);

        if (request.Mode == ExportMode.StreamCopyApproximate)
        {
            arguments.AddRange(["-c", "copy", "-avoid_negative_ts", "make_zero"]);
        }
        else
        {
            arguments.AddRange(
            [
                "-map_metadata", "-1", "-map_metadata:s:v:0", "-1",
                "-vf", MediaGeometry.BuildVideoFilter(
                    media,
                    request.Crop,
                    request.AdditionalRotationDegreesClockwise,
                    request.PlaybackSpeed,
                    request.ZoomFactor,
                    request.ZoomFocusX,
                    request.ZoomFocusY),
                "-c:v", _options.Encoder == VideoEncoder.H264 ? "libx264" : "libx265", "-preset", ExportPresetName(),
                "-crf", _options.ConstantRateFactor.ToString(CultureInfo.InvariantCulture),
                "-pix_fmt", "yuv420p"
            ]);

            if (media.HasAudio)
            {
                arguments.AddRange(["-c:a", "aac", "-b:a", "192k"]);
                if (request.PlaybackSpeed != 1.0)
                {
                    arguments.AddRange(["-af", BuildAtempoFilter(request.PlaybackSpeed)]);
                }
            }
        }

        if (request.OutputContainer is ExportContainer.Mp4 or ExportContainer.Mov)
        {
            arguments.AddRange(["-movflags", "+faststart"]);
        }

        if (_options.Encoder == VideoEncoder.H265 && request.OutputContainer is ExportContainer.Mp4 or ExportContainer.Mov)
        {
            arguments.AddRange(["-tag:v", "hvc1"]);
        }

        arguments.AddRange(["-f", GetMuxer(request.OutputContainer), temporaryPath]);
        return arguments;
    }

    private static string BuildAtempoFilter(double playbackSpeed)
    {
        MediaGeometry.ValidatePlaybackSpeed(playbackSpeed);
        var factors = new List<double>();
        var remaining = playbackSpeed;
        while (remaining < 0.5)
        {
            factors.Add(0.5);
            remaining /= 0.5;
        }

        while (remaining > 2.0)
        {
            factors.Add(2.0);
            remaining /= 2.0;
        }

        factors.Add(remaining);
        return string.Join(',', factors.Select(factor => $"atempo={FormatSeconds(factor)}"));
    }

    private string ExportPresetName() => _options.Preset.ToString().ToLowerInvariant();

    private static void ValidateOutputMedia(MediaInfo output, MediaInfo source, ExportRequest request, VideoEncoder encoder)
    {
        ValidateOutputContainer(output, request.OutputContainer);

        var expected = request.Mode == ExportMode.StreamCopyApproximate
            ? (source.CodedWidth, source.CodedHeight)
            : MediaGeometry.ComputeExportDimensions(source, request.Crop, request.AdditionalRotationDegreesClockwise);

        var expectedRotation = request.Mode == ExportMode.StreamCopyApproximate ? source.RotationDegreesClockwise : 0;
        if (output.RotationDegreesClockwise != expectedRotation)
        {
            throw new ExportException(Messages.Get("Error_OutputRotationMismatch"));
        }

        if (output.CodedWidth != expected.Item1 || output.CodedHeight != expected.Item2)
        {
            throw new ExportException(Messages.Get("Error_OutputSizeMismatch"));
        }

        if (output.HasAudio != source.HasAudio)
        {
            throw new ExportException(Messages.Get("Error_OutputAudioMismatch"));
        }

        if (request.Mode == ExportMode.StreamCopyApproximate)
        {
            if (!output.VideoCodec.Equals(source.VideoCodec, StringComparison.OrdinalIgnoreCase) ||
                output.SampleAspectRatio != source.SampleAspectRatio ||
                output.IsHdr != source.IsHdr ||
                (source.HasAudio && !string.Equals(output.AudioCodec, source.AudioCodec, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ExportException(Messages.Get("Error_StreamCopyVerifyMismatch"));
            }

            if (output.DurationSeconds > source.DurationSeconds + 1)
            {
                throw new ExportException(Messages.Get("Error_StreamCopyTooLong"));
            }
        }
        else
        {
            var expectedDuration = (request.EndSeconds - request.StartSeconds) / request.PlaybackSpeed;
            var durationTolerance = Math.Max(0.15, Math.Min(1, expectedDuration * 0.02));
            var expectedVideoCodec = encoder == VideoEncoder.H264 ? "h264" : "hevc";
            if (!output.VideoCodec.Equals(expectedVideoCodec, StringComparison.OrdinalIgnoreCase) ||
                (source.HasAudio && !string.Equals(output.AudioCodec, "aac", StringComparison.OrdinalIgnoreCase)) ||
                Math.Abs(output.DurationSeconds - expectedDuration) > durationTolerance)
            {
                throw new ExportException(Messages.Get("Error_OutputFormatMismatch"));
            }
        }

        if (!double.IsFinite(output.DurationSeconds) || output.DurationSeconds <= 0)
        {
            throw new ExportException(Messages.Get("Error_OutputDurationUnknown"));
        }
    }

    private static void ValidateOutputContainer(MediaInfo output, ExportContainer expectedContainer)
    {
        var formats = (output.ContainerFormatNames ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var majorBrand = output.ContainerMajorBrand?.Trim();
        var matches = expectedContainer switch
        {
            ExportContainer.Mp4 => formats.Contains("mp4", StringComparer.OrdinalIgnoreCase) &&
                                   !string.Equals(majorBrand, "qt", StringComparison.OrdinalIgnoreCase),
            ExportContainer.Mkv => formats.Contains("matroska", StringComparer.OrdinalIgnoreCase),
            ExportContainer.Mov => formats.Contains("mov", StringComparer.OrdinalIgnoreCase) &&
                                   string.Equals(majorBrand, "qt", StringComparison.OrdinalIgnoreCase),
            _ => false
        };

        if (!matches)
        {
            throw new ExportException(Messages.Get("Error_OutputContainerMismatch"));
        }
    }

    private static string GetExtension(ExportContainer container) => container switch
    {
        ExportContainer.Mp4 => ".mp4",
        ExportContainer.Mkv => ".mkv",
        ExportContainer.Mov => ".mov",
        _ => throw new ExportValidationException(Messages.Get("Error_ExportContainerRequired"))
    };

    private static string GetMuxer(ExportContainer container) => container switch
    {
        ExportContainer.Mp4 => "mp4",
        ExportContainer.Mkv => "matroska",
        ExportContainer.Mov => "mov",
        _ => throw new ExportValidationException(Messages.Get("Error_ExportContainerRequired"))
    };

    private static string NormalizeExistingFile(string? path, string errorMessage)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ExportValidationException(errorMessage);
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ExportValidationException(errorMessage);
        }

        if (!File.Exists(fullPath))
        {
            throw new ExportValidationException(errorMessage);
        }

        return fullPath;
    }

    private static string NormalizeDestination(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ExportValidationException(Messages.Get("Error_DestinationNameRequired"));
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ExportValidationException(Messages.Get("Error_DestinationNameInvalid"));
        }
    }

    private static string CreateTemporaryPath(string directory, string destinationName, string extension)
    {
        string candidate;
        do
        {
            candidate = Path.Combine(directory, $".{destinationName}.{Guid.NewGuid():N}.partial{extension}");
        }
        while (File.Exists(candidate));

        return candidate;
    }

    private static void EnsureToolExists(string path, string displayName)
    {
        if (!File.Exists(path))
        {
            throw new ExportException(Messages.Format("Error_ToolMissing", displayName));
        }
    }

    private static bool TryReadProcessedSeconds(string line, out double seconds)
    {
        const string MicrosecondsPrefix = "out_time_us=";
        const string TimePrefix = "out_time=";
        if (line.StartsWith(MicrosecondsPrefix, StringComparison.Ordinal) &&
            long.TryParse(line.AsSpan(MicrosecondsPrefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds))
        {
            seconds = Math.Max(0, microseconds / 1_000_000d);
            return true;
        }

        if (line.StartsWith(TimePrefix, StringComparison.Ordinal) &&
            TimeSpan.TryParse(line.AsSpan(TimePrefix.Length), CultureInfo.InvariantCulture, out var time))
        {
            seconds = Math.Max(0, time.TotalSeconds);
            return true;
        }

        seconds = 0;
        return false;
    }

    private static string FormatSeconds(double seconds) => seconds.ToString("0.#########", CultureInfo.InvariantCulture);

    private static string FormatDiagnostic(string toolName, string diagnostic)
    {
        var clean = new string(diagnostic.Where(character => !char.IsControl(character) || character is '\r' or '\n' or '\t').ToArray()).Trim();
        return clean.Length == 0 ? Messages.Format("Diagnostic_NoDetail", toolName) : Messages.Format("Diagnostic_Detail", toolName, clean);
    }

    private static void Report(
        IProgress<ExportProgress>? progress,
        ExportProgressPhase phase,
        double fraction,
        double processedSeconds,
        double expectedSeconds,
        string message)
    {
        try
        {
            progress?.Report(new ExportProgress(phase, Math.Clamp(fraction, 0, 1), processedSeconds, expectedSeconds, message));
        }
        catch
        {
            // A UI progress handler should never interrupt the FFmpeg process.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Preserve the original export error or cancellation result.
        }
    }
}

internal sealed record ToolProcessResult(int ExitCode, string StandardOutput, string StandardError);

internal static class ProcessExecution
{
    public static async Task<ToolProcessResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        Action<string>? onStandardOutputLine = null,
        int maxDiagnosticCharacters = 12_000)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false),
            RedirectStandardError = true,
            RedirectStandardInput = false
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new ExportException(Messages.Get("Error_ToolLaunchFailed"));
            }
        }
        catch (ExportException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new ExportException(Messages.Get("Error_ToolLaunchFailedCheckFiles"), exception.Message, exception);
        }

        var stderrTail = new BoundedTailBuffer(maxDiagnosticCharacters);
        var stderrTask = ReadTailAsync(process.StandardError, stderrTail);
        Task<string> stdoutTask;
        if (onStandardOutputLine is null)
        {
            stdoutTask = process.StandardOutput.ReadToEndAsync();
        }
        else
        {
            stdoutTask = ReadLinesAsync(process.StandardOutput, onStandardOutputLine);
        }

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKillTree(process);
            try
            {
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // The process may already have exited between cancellation and kill.
            }

            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            throw;
        }

        var standardOutput = await stdoutTask.ConfigureAwait(false);
        await stderrTask.ConfigureAwait(false);
        return new ToolProcessResult(process.ExitCode, standardOutput, stderrTail.ToString());
    }

    private static async Task<string> ReadLinesAsync(StreamReader reader, Action<string> callback)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            try
            {
                callback(line);
            }
            catch
            {
                // Progress callbacks must not stop draining stdout.
            }
        }

        return string.Empty;
    }

    private static async Task ReadTailAsync(StreamReader reader, BoundedTailBuffer tail)
    {
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
        {
            tail.Append(buffer.AsSpan(0, count));
        }
    }

    private static void TryKillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }
}

internal sealed class BoundedTailBuffer
{
    private readonly int _capacity;
    private readonly StringBuilder _content = new();

    public BoundedTailBuffer(int capacity)
    {
        _capacity = Math.Max(1, capacity);
    }

    public void Append(ReadOnlySpan<char> text)
    {
        _content.Append(text);
        if (_content.Length > _capacity)
        {
            _content.Remove(0, _content.Length - _capacity);
        }
    }

    public override string ToString() => _content.ToString();
}
