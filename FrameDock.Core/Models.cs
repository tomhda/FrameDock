namespace FrameDock.Core;

/// <summary>A positive pixel/sample aspect ratio.</summary>
public readonly record struct AspectRatio(int Numerator, int Denominator)
{
    public static AspectRatio Square { get; } = new(1, 1);

    public double Value => (double)Numerator / Denominator;

    public override string ToString() => $"{Numerator}:{Denominator}";
}

/// <summary>
/// Media details from ffprobe. Start/end values used by ExportRequest are relative
/// to the displayed beginning of the clip, even when a container has a nonzero
/// presentation start timestamp. DisplayWidth/DisplayHeight describe a
/// square-pixel image after sample-aspect correction and right-angle rotation.
/// </summary>
public sealed record MediaInfo
{
    public required string SourcePath { get; init; }
    public required double DurationSeconds { get; init; }
    public required double StartTimeSeconds { get; init; }
    public required int CodedWidth { get; init; }
    public required int CodedHeight { get; init; }
    public required AspectRatio SampleAspectRatio { get; init; }
    public required int RotationDegreesClockwise { get; init; }
    public required int DisplayWidth { get; init; }
    public required int DisplayHeight { get; init; }
    public required bool HasAudio { get; init; }
    public required string VideoCodec { get; init; }
    public string? AudioCodec { get; init; }
    public string? PixelFormat { get; init; }
    public string? ColorTransfer { get; init; }
    public string? ColorPrimaries { get; init; }
    public bool IsHdr { get; init; }
    public string? HdrDescription { get; init; }
}

/// <summary>
/// Integer rectangle in MediaInfo's square-pixel oriented display space,
/// measured from the top-left corner.
/// </summary>
public readonly record struct CropRect(int X, int Y, int Width, int Height);

public enum ExportMode
{
    AccurateReencode,
    StreamCopyApproximate
}

public sealed record ExportRequest(
    string SourcePath,
    string DestinationPath,
    double StartSeconds,
    double EndSeconds,
    CropRect? Crop = null,
    ExportMode Mode = ExportMode.AccurateReencode,
    int AdditionalRotationDegreesClockwise = 0,
    double PlaybackSpeed = 1.0);

public enum ExportProgressPhase
{
    Preparing,
    Encoding,
    Verifying,
    Completed
}

public sealed record ExportProgress(
    ExportProgressPhase Phase,
    double Fraction,
    double ProcessedSeconds,
    double ExpectedSeconds,
    string Message);

public sealed record ExportResult(
    string DestinationPath,
    double RequestedDurationSeconds,
    MediaInfo OutputMediaInfo,
    ExportMode Mode,
    bool BoundariesAreApproximate);

public enum H264Preset
{
    Ultrafast,
    Superfast,
    Veryfast,
    Faster,
    Fast,
    Medium,
    Slow,
    Slower,
    Veryslow
}

/// <summary>CPU software encoder used for accurate MP4 output.</summary>
public enum VideoEncoder
{
    H264,
    H265
}

/// <summary>Paths and quality options for the externally supplied FFmpeg tools.</summary>
public sealed class ExportOptions
{
    public ExportOptions(string ffmpegPath, string ffprobePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpegPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(ffprobePath);
        FfmpegPath = Path.GetFullPath(ffmpegPath);
        FfprobePath = Path.GetFullPath(ffprobePath);
    }

    public string FfmpegPath { get; }
    public string FfprobePath { get; }
    public VideoEncoder Encoder { get; init; } = VideoEncoder.H264;
    public H264Preset Preset { get; init; } = H264Preset.Medium;
    public int ConstantRateFactor { get; init; } = 18;
}

/// <summary>An error that can be shown directly in the Japanese UI.</summary>
public class ExportException : Exception
{
    public ExportException(string userMessage, string? diagnostic = null, Exception? innerException = null)
        : base(userMessage, innerException)
    {
        UserMessage = userMessage;
        Diagnostic = diagnostic;
    }

    public string UserMessage { get; }
    public string? Diagnostic { get; }
}

public sealed class ExportValidationException : ExportException
{
    public ExportValidationException(string userMessage)
        : base(userMessage)
    {
    }
}
