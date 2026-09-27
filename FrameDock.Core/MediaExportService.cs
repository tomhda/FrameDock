using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace FrameDock.Core;

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
        var normalizedPath = NormalizeExistingFile(sourcePath, "入力動画が見つかりません。ファイルの場所を確認してください。");
        EnsureToolExists(_options.FfprobePath, "ffprobe");

        var result = await ProcessExecution.RunAsync(
            _options.FfprobePath,
            ["-v", "error", "-print_format", "json", "-show_streams", "-show_format", normalizedPath],
            cancellationToken,
            maxDiagnosticCharacters: MaxDiagnosticCharacters);

        if (result.ExitCode != 0)
        {
            throw new ExportException(
                "動画の情報を読み取れませんでした。対応している動画ファイルか確認してください。",
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
                "動画の情報を読み取れませんでした。ffprobe の出力を解析できません。",
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
        Report(progress, ExportProgressPhase.Preparing, 0, 0, 0, "書き出しの準備中…");

        var sourcePath = NormalizeExistingFile(request.SourcePath, "入力動画が見つかりません。ファイルの場所を確認してください。");
        var destinationPath = NormalizeDestination(request.DestinationPath);

        if (string.Equals(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ExportValidationException("入力動画と同じ場所には書き出せません。別の保存先を指定してください。");
        }

        var outputExtension = GetExtension(request.OutputContainer);
        if (!string.Equals(Path.GetExtension(destinationPath), outputExtension, StringComparison.OrdinalIgnoreCase))
        {
            throw new ExportValidationException($"選択した形式の拡張子は {outputExtension} です。保存先の名前を確認してください。");
        }

        if (File.Exists(destinationPath))
        {
            throw new ExportValidationException("出力先には既にファイルがあります。別の名前を指定してください。");
        }

        var destinationDirectory = Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrEmpty(destinationDirectory) || !Directory.Exists(destinationDirectory))
        {
            throw new ExportValidationException("保存先フォルダーが見つかりません。フォルダーを確認してください。");
        }

        ValidateEncoderOptions();
        EnsureToolExists(_options.FfmpegPath, "FFmpeg");
        EnsureToolExists(_options.FfprobePath, "ffprobe");

        var media = await InspectAsync(sourcePath, cancellationToken);
        ValidateRequest(request, media);
        if (request.Mode == ExportMode.AccurateReencode && media.IsHdr)
        {
            throw new ExportValidationException(
                "HDR 動画の正確なトリム／クロップ書き出しには現在対応していません。色の変化を避けるため、書き出しを停止しました。");
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
                Report(progress, ExportProgressPhase.Encoding, lastProgress, processedSeconds, expectedDuration, "動画を書き出し中…");
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
                        ? "ストリームコピーに失敗しました。指定範囲に利用できるキーフレームがないか、選択した形式に入力の映像・音声を格納できない可能性があります。正確な再エンコードを試してください。"
                        : "動画を書き出せませんでした。保存先の空き容量と FFmpeg の対応形式を確認してください。",
                    FormatDiagnostic("FFmpeg", processResult.StandardError));
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(temporaryPath) || new FileInfo(temporaryPath).Length <= 0)
            {
                throw new ExportException("書き出し結果が空でした。FFmpeg のログを確認してください。");
            }

            Report(progress, ExportProgressPhase.Verifying, Math.Max(lastProgress, 0.995), expectedDuration, expectedDuration, "書き出した動画を確認中…");
            MediaInfo outputInfo;
            try
            {
                outputInfo = await InspectAsync(temporaryPath, cancellationToken);
            }
            catch (ExportException exception) when (request.Mode == ExportMode.StreamCopyApproximate)
            {
                throw new ExportException(
                    "ストリームコピーの範囲に利用できるキーフレームがありません。正確な再エンコードを試してください。",
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
                throw new ExportValidationException("出力先には既にファイルがあります。別の名前を指定してください。");
            }

            Report(progress, ExportProgressPhase.Completed, 1, expectedDuration, expectedDuration, "書き出しが完了しました。");
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
            throw new ExportException("動画を書き出せませんでした。保存先とアクセス権を確認してください。", exception.Message, exception);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static MediaInfo ParseMediaInfo(JsonElement root, string sourcePath)
    {
        if (!root.TryGetProperty("streams", out var streamsElement) || streamsElement.ValueKind != JsonValueKind.Array)
        {
            throw new ExportException("動画の映像情報が見つかりませんでした。");
        }

        var streams = streamsElement.EnumerateArray().ToArray();
        var video = streams.FirstOrDefault(IsVideoStream);
        if (video.ValueKind == JsonValueKind.Undefined)
        {
            throw new ExportException("映像ストリームが見つかりません。動画ファイルを選択してください。");
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
            throw new ExportException("動画の再生時間を読み取れませんでした。");
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
            HdrDescription = hdrReason
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
            return "BT.2020 色域（HDR の可能性あり）";
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
            throw new ExportValidationException("この動画の回転情報は 90 度単位ではないため、クロップ位置を計算できません。");
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

    private static int ReadInt(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var value) || !value.TryGetInt32(out var result))
        {
            throw new ExportException("動画の映像サイズを読み取れませんでした。");
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
            throw new ExportValidationException("開始時刻と終了時刻には有限の数値を指定してください。");
        }

        if (request.StartSeconds < 0)
        {
            throw new ExportValidationException("開始時刻は 0 秒以上にしてください。");
        }

        if (request.EndSeconds <= request.StartSeconds)
        {
            throw new ExportValidationException("終了時刻は開始時刻より後にしてください。");
        }

        if (request.EndSeconds > media.DurationSeconds)
        {
            throw new ExportValidationException("終了時刻が動画の再生時間を超えています。");
        }

        if (request.Mode is not ExportMode.AccurateReencode and not ExportMode.StreamCopyApproximate)
        {
            throw new ExportValidationException("書き出し方法を選択してください。");
        }

        if (!Enum.IsDefined(request.OutputContainer))
        {
            throw new ExportValidationException("書き出し形式を選択してください。");
        }

        MediaGeometry.ValidateAdditionalRotation(request.AdditionalRotationDegreesClockwise);
        MediaGeometry.ValidatePlaybackSpeed(request.PlaybackSpeed);

        if (request.Mode == ExportMode.StreamCopyApproximate &&
            (request.Crop is not null || request.AdditionalRotationDegreesClockwise != 0 || request.PlaybackSpeed != 1.0))
        {
            throw new ExportValidationException("ストリームコピーではクロップ、追加回転、速度変更はできません。正確な書き出しを選択してください。");
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
            throw new ExportValidationException("動画品質設定が範囲外です。");
        }

        if (!Enum.IsDefined(_options.Preset) || !Enum.IsDefined(_options.Encoder))
        {
            throw new ExportValidationException("エンコード設定が正しくありません。");
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

        if (Math.Abs(media.StartTimeSeconds) > 0.001)
        {
            // Some MPEG-TS demuxers expose their first video timestamp as a
            // positive start_time but still treat input-side -ss as an absolute
            // timestamp. Output-side seeking is relative to the clip start and
            // decodes the preroll, preserving the UI's zero-based timeline.
            arguments.AddRange(["-noautorotate", "-i", sourcePath, "-ss", FormatSeconds(request.StartSeconds)]);
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
                    request.PlaybackSpeed),
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
            throw new ExportException("書き出した動画の回転情報が元動画と一致しませんでした。");
        }

        if (output.CodedWidth != expected.Item1 || output.CodedHeight != expected.Item2)
        {
            throw new ExportException("書き出した動画のサイズが指定した範囲と一致しませんでした。");
        }

        if (output.HasAudio != source.HasAudio)
        {
            throw new ExportException("書き出した動画の音声ストリームが元動画と一致しませんでした。");
        }

        if (request.Mode == ExportMode.StreamCopyApproximate)
        {
            if (!output.VideoCodec.Equals(source.VideoCodec, StringComparison.OrdinalIgnoreCase) ||
                output.SampleAspectRatio != source.SampleAspectRatio ||
                output.IsHdr != source.IsHdr ||
                (source.HasAudio && !string.Equals(output.AudioCodec, source.AudioCodec, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ExportException("ストリームコピー後の映像形式または表示情報が元動画と一致しませんでした。");
            }

            if (output.DurationSeconds > source.DurationSeconds + 1)
            {
                throw new ExportException("ストリームコピーの出力時間が動画全体の長さを超えています。");
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
                throw new ExportException("書き出した動画の形式または再生時間が指定内容と一致しませんでした。");
            }
        }

        if (!double.IsFinite(output.DurationSeconds) || output.DurationSeconds <= 0)
        {
            throw new ExportException("書き出した動画の再生時間を確認できませんでした。");
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
            throw new ExportException("書き出した動画のコンテナが選択した形式と一致しませんでした。");
        }
    }

    private static string GetExtension(ExportContainer container) => container switch
    {
        ExportContainer.Mp4 => ".mp4",
        ExportContainer.Mkv => ".mkv",
        ExportContainer.Mov => ".mov",
        _ => throw new ExportValidationException("書き出し形式を選択してください。")
    };

    private static string GetMuxer(ExportContainer container) => container switch
    {
        ExportContainer.Mp4 => "mp4",
        ExportContainer.Mkv => "matroska",
        ExportContainer.Mov => "mov",
        _ => throw new ExportValidationException("書き出し形式を選択してください。")
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
            throw new ExportValidationException("保存先のファイル名を指定してください。");
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ExportValidationException("保存先のファイル名を確認してください。");
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
            throw new ExportException($"{displayName} が見つかりません。アプリの依存ファイルを確認してください。");
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
        return clean.Length == 0 ? $"{toolName} から詳細なエラーは返されませんでした。" : $"{toolName} の詳細:\n{clean}";
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
                throw new ExportException("動画処理ツールを起動できませんでした。");
            }
        }
        catch (ExportException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new ExportException("動画処理ツールを起動できませんでした。アプリの依存ファイルを確認してください。", exception.Message, exception);
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
