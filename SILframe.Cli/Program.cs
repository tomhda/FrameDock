using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using SILframe.Core;

namespace SILframe.Cli;

// Command-line access to the same inspection, frame, and export code the app
// uses. Every command prints one JSON object on stdout and nothing else there,
// so scripts and AI agents can parse the result. Progress goes to stderr.
internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitFailed = 1;
    private const int ExitUsage = 2;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static async Task<int> Main(string[] args)
    {
        // Messages stay in English whatever the Windows display language is,
        // so the output does not change from one machine to another.
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.Out.WriteLine(HelpText);
            return args.Length == 0 ? ExitUsage : ExitOk;
        }

        if (args[0] is "--version" or "version")
        {
            return Print(new { ok = true, version = GetVersion() });
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            var command = args[0];
            var arguments = Arguments.Parse(args.Skip(1).ToArray());
            return command switch
            {
                "info" => await InfoAsync(arguments, cancellation.Token),
                "frame" => await FrameAsync(arguments, cancellation.Token),
                "export" => await ExportAsync(arguments, cancellation.Token),
                _ => throw new UsageException($"Unknown command '{command}'. Commands: info, frame, export.")
            };
        }
        catch (UsageException exception)
        {
            return Fail("usage", exception.Message, null, ExitUsage);
        }
        catch (OperationCanceledException)
        {
            return Fail("canceled", "Canceled.", null, ExitFailed);
        }
        catch (ExportValidationException exception)
        {
            return Fail("invalid_request", exception.UserMessage, exception.Diagnostic, ExitFailed);
        }
        catch (ExportException exception)
        {
            return Fail("failed", exception.UserMessage, exception.Diagnostic, ExitFailed);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Fail("io_error", exception.Message, null, ExitFailed);
        }
    }

    private static async Task<int> InfoAsync(Arguments arguments, CancellationToken cancellationToken)
    {
        arguments.AllowOnly();
        var media = await CreateService().InspectAsync(arguments.RequireInput(), cancellationToken);
        return Print(new { ok = true, media });
    }

    private static async Task<int> FrameAsync(Arguments arguments, CancellationToken cancellationToken)
    {
        arguments.AllowOnly("time", "out", "crop", "rotate", "zoom", "overwrite");
        var input = arguments.RequireInput();
        var time = ParseTime(arguments.Require("time"), "--time");
        var output = Path.GetFullPath(arguments.Get("out") ??
            Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(input))!,
                $"{Path.GetFileNameWithoutExtension(input)}-{FormatTimeForFileName(time)}.png"));
        if (!string.Equals(Path.GetExtension(output), ".png", StringComparison.OrdinalIgnoreCase))
        {
            throw new UsageException("--out must end in .png.");
        }

        var (zoom, focusX, focusY) = ParseZoom(arguments.Get("zoom"));
        var service = CreateService();
        // The app shows the last frame for a position past the end; a script
        // asking for such a time has more likely made a mistake.
        var duration = (await service.InspectAsync(input, cancellationToken)).DurationSeconds;
        if (time > duration)
        {
            throw new ExportValidationException(
                $"--time {time.ToString("0.###", CultureInfo.InvariantCulture)} is past the end of the video ({duration.ToString("0.###", CultureInfo.InvariantCulture)} seconds).");
        }

        PrepareDestination(input, output, arguments.Has("overwrite"));
        var saved = await service.ExportFrameAsync(
            input,
            output,
            time,
            ParseCrop(arguments.Get("crop")),
            ParseRotation(arguments.Get("rotate")),
            zoom,
            focusX,
            focusY,
            cancellationToken);
        return Print(new { ok = true, output = saved, timeSeconds = time });
    }

    private static async Task<int> ExportAsync(Arguments arguments, CancellationToken cancellationToken)
    {
        arguments.AllowOnly("out", "start", "end", "segment", "crop", "rotate", "speed", "zoom", "copy", "overwrite", "quiet");
        var input = arguments.RequireInput();
        var output = Path.GetFullPath(arguments.Require("out"));
        var container = Path.GetExtension(output).ToLowerInvariant() switch
        {
            ".mp4" => ExportContainer.Mp4,
            ".mkv" => ExportContainer.Mkv,
            ".mov" => ExportContainer.Mov,
            _ => throw new UsageException("--out must end in .mp4, .mkv, or .mov.")
        };

        var service = CreateService();
        var crop = ParseCrop(arguments.Get("crop"));
        var rotation = ParseRotation(arguments.Get("rotate"));
        var speed = arguments.Get("speed") is { } speedText ? ParseNumber(speedText, "--speed") : 1.0;
        var (zoom, focusX, focusY) = ParseZoom(arguments.Get("zoom"));
        var mode = arguments.Has("copy") ? ExportMode.StreamCopyApproximate : ExportMode.AccurateReencode;

        var segmentTexts = arguments.GetAll("segment");
        double start;
        double end;
        List<ExportSegment>? segments = null;
        if (segmentTexts.Count > 0)
        {
            if (arguments.Has("start") || arguments.Has("end"))
            {
                throw new UsageException("Use either --segment or --start/--end, not both.");
            }

            segments = new List<ExportSegment>();
            foreach (var text in segmentTexts)
            {
                var (segmentStart, segmentEnd) = ParseRange(text);
                segments.Add(new ExportSegment(segmentStart, segmentEnd, crop, rotation, speed, zoom, focusX, focusY));
            }

            start = segments[0].StartSeconds;
            end = segments[^1].EndSeconds;
            if (segments.Count == 1)
            {
                segments = null;
            }
        }
        else
        {
            start = arguments.Get("start") is { } startText ? ParseTime(startText, "--start") : 0;
            end = arguments.Get("end") is { } endText
                ? ParseTime(endText, "--end")
                : (await service.InspectAsync(input, cancellationToken)).DurationSeconds;
        }

        PrepareDestination(input, output, arguments.Has("overwrite"));
        var request = new ExportRequest(
            input,
            output,
            start,
            end,
            crop,
            mode,
            AdditionalRotationDegreesClockwise: rotation,
            PlaybackSpeed: speed,
            OutputContainer: container,
            ZoomFactor: zoom,
            ZoomFocusX: focusX,
            ZoomFocusY: focusY,
            Segments: segments);

        var lastPercent = -1;
        var progress = arguments.Has("quiet")
            ? null
            : new SynchronousProgress(update =>
            {
                var percent = (int)Math.Floor(update.Fraction * 100);
                if (update.Phase == ExportProgressPhase.Encoding && percent == lastPercent)
                {
                    return;
                }

                lastPercent = percent;
                Console.Error.WriteLine($"{update.Phase.ToString().ToLowerInvariant()} {percent}%");
            });
        var result = await service.ExportAsync(request, progress, cancellationToken);
        return Print(new
        {
            ok = true,
            output = result.DestinationPath,
            mode = result.Mode == ExportMode.StreamCopyApproximate ? "copy" : "reencode",
            boundariesAreApproximate = result.BoundariesAreApproximate,
            requestedDurationSeconds = result.RequestedDurationSeconds,
            media = result.OutputMediaInfo with { SourcePath = result.DestinationPath }
        });
    }

    private static MediaExportService CreateService()
    {
        var media = Path.Combine(AppContext.BaseDirectory, "Media");
        return new MediaExportService(new ExportOptions(
            Path.Combine(media, "ffmpeg.exe"),
            Path.Combine(media, "ffprobe.exe")));
    }

    // The export code refuses to replace a file. --overwrite removes the old
    // one first, but never the input itself.
    private static void PrepareDestination(string input, string output, bool overwrite)
    {
        if (string.Equals(Path.GetFullPath(input), output, StringComparison.OrdinalIgnoreCase))
        {
            throw new UsageException("--out is the input file.");
        }

        if (!File.Exists(output))
        {
            return;
        }

        if (!overwrite)
        {
            throw new ExportValidationException($"'{output}' already exists. Add --overwrite to replace it.");
        }

        File.Delete(output);
    }

    private static double ParseTime(string text, string option)
    {
        var parts = text.Split(':');
        if (parts.Length is >= 1 and <= 3 && parts.All(part => part.Length > 0))
        {
            double total = 0;
            var valid = true;
            for (var i = 0; i < parts.Length; i++)
            {
                var isLast = i == parts.Length - 1;
                var style = isLast ? NumberStyles.AllowDecimalPoint : NumberStyles.None;
                if (!double.TryParse(parts[i], style, CultureInfo.InvariantCulture, out var value) ||
                    (parts.Length > 1 && i > 0 && value >= 60))
                {
                    valid = false;
                    break;
                }

                total = total * 60 + value;
            }

            if (valid && double.IsFinite(total))
            {
                return total;
            }
        }

        throw new UsageException($"{option}: '{text}' is not a time. Use seconds (12.5), m:ss (1:02.5), or h:mm:ss.");
    }

    private static (double Start, double End) ParseRange(string text)
    {
        var separator = text.IndexOf('-');
        if (separator <= 0 || separator == text.Length - 1)
        {
            throw new UsageException($"--segment: '{text}' is not a range. Use START-END, for example 0:10-0:25.");
        }

        return (ParseTime(text[..separator], "--segment"), ParseTime(text[(separator + 1)..], "--segment"));
    }

    private static double ParseNumber(string text, string option) =>
        double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
            ? value
            : throw new UsageException($"{option}: '{text}' is not a number.");

    private static CropRect? ParseCrop(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var parts = text.Split(',');
        if (parts.Length == 4 &&
            parts.Select(part => int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : -1).ToArray() is var values &&
            values.All(value => value >= 0))
        {
            return new CropRect(values[0], values[1], values[2], values[3]);
        }

        throw new UsageException($"--crop: '{text}' is not a rectangle. Use X,Y,WIDTH,HEIGHT in pixels of the displayed picture.");
    }

    private static int ParseRotation(string? text) => text switch
    {
        null or "0" => 0,
        "90" => 90,
        "180" => 180,
        "270" => 270,
        _ => throw new UsageException("--rotate must be 0, 90, 180, or 270 (clockwise).")
    };

    private static (double Zoom, double FocusX, double FocusY) ParseZoom(string? text)
    {
        if (text is null)
        {
            return (1, 0.5, 0.5);
        }

        var parts = text.Split(',');
        if (parts.Length is 1 or 3)
        {
            var zoom = ParseNumber(parts[0], "--zoom");
            return parts.Length == 1
                ? (zoom, 0.5, 0.5)
                : (zoom, ParseNumber(parts[1], "--zoom"), ParseNumber(parts[2], "--zoom"));
        }

        throw new UsageException("--zoom: use FACTOR or FACTOR,FOCUS_X,FOCUS_Y (focus from 0 to 1).");
    }

    private static string FormatTimeForFileName(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)time.TotalHours:00}h{time.Minutes:00}m{time.Seconds:00}s{time.Milliseconds:000}";
    }

    private static string GetVersion()
    {
        var version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var plus = version.IndexOf('+');
        return plus < 0 ? version : version[..plus];
    }

    private static int Print(object value)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(value, JsonOptions));
        return ExitOk;
    }

    private static int Fail(string code, string message, string? detail, int exitCode)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(new { ok = false, error = new { code, message, detail } }, JsonOptions));
        return exitCode;
    }

    private const string HelpText = """
        silframe-cli - inspect videos, save frames, and export clips without opening the SILframe window.

        Usage:
          silframe-cli info <video>
          silframe-cli frame <video> --time <time> [--out <file.png>] [options]
          silframe-cli export <video> --out <file.mp4|.mkv|.mov> [options]
          silframe-cli --version

        Every command prints one JSON object on stdout: {"ok": true, ...} on success,
        {"ok": false, "error": {"code", "message", "detail"}} on failure.
        Exit code: 0 success, 1 failed, 2 wrong usage. Export progress goes to stderr.

        Times are seconds (12.5), m:ss (1:02.5), or h:mm:ss, measured from the start of the video.

        info
          Prints duration, size, frame rate, codecs, and track counts.

        frame
          --time <time>          Position of the frame. Required.
          --out <file.png>       Default: next to the video, named after it and the time.
          --crop X,Y,W,H         Rectangle in pixels of the displayed picture.
          --rotate 90|180|270    Clockwise.
          --zoom F[,FX,FY]       Magnify 1 to 4 times around a focus point (0 to 1, default 0.5,0.5).
          --overwrite            Replace --out if it exists.

        export
          --out <file>           Required. The extension selects MP4, MKV, or MOV.
          --start <time>         Default: 0.
          --end <time>           Default: the end of the video.
          --segment START-END    Repeat to join several ranges into one file. Replaces --start/--end.
          --crop, --rotate, --zoom   As for frame. Applied to every segment.
          --speed <factor>       0.5 to 4. Default 1.
          --copy                 Cut without re-encoding. Fast and lossless, but the cut points move
                                 to nearby keyframes. Not with crop, rotate, zoom, speed, or several segments.
          --overwrite            Replace --out if it exists.
          --quiet                No progress on stderr.

        Re-encoded output is H.264 video with AAC audio.

        Examples:
          silframe-cli info talk.mp4
          silframe-cli frame talk.mp4 --time 1:23.5 --out slide.png
          silframe-cli export talk.mp4 --start 0:10 --end 0:40 --out clip.mp4
          silframe-cli export talk.mp4 --segment 0:10-0:20 --segment 1:00-1:15 --out highlights.mp4
          silframe-cli export talk.mp4 --start 5:00 --end 9:30 --copy --out part.mkv
        """;

    // IProgress<T> implementations normally post to a synchronization context;
    // a console tool wants the line written as soon as it is reported.
    private sealed class SynchronousProgress(Action<ExportProgress> handler) : IProgress<ExportProgress>
    {
        public void Report(ExportProgress value) => handler(value);
    }
}

internal sealed class UsageException(string message) : Exception(message);

internal sealed class Arguments
{
    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal) { "copy", "overwrite", "quiet" };
    private readonly Dictionary<string, List<string>> _options = new(StringComparer.Ordinal);
    private readonly List<string> _positional = new();

    public static Arguments Parse(string[] args)
    {
        var result = new Arguments();
        for (var i = 0; i < args.Length; i++)
        {
            var argument = args[i];
            if (!argument.StartsWith("--", StringComparison.Ordinal) || argument.Length == 2)
            {
                result._positional.Add(argument);
                continue;
            }

            var name = argument[2..];
            string value;
            var equals = name.IndexOf('=');
            if (equals > 0)
            {
                value = name[(equals + 1)..];
                name = name[..equals];
            }
            else if (Flags.Contains(name))
            {
                value = "";
            }
            else if (i + 1 < args.Length)
            {
                value = args[++i];
            }
            else
            {
                throw new UsageException($"--{name} needs a value.");
            }

            if (!result._options.TryGetValue(name, out var values))
            {
                result._options[name] = values = new List<string>();
            }

            values.Add(value);
        }

        return result;
    }

    public void AllowOnly(params string[] names)
    {
        foreach (var name in _options.Keys)
        {
            if (!names.Contains(name, StringComparer.Ordinal))
            {
                throw new UsageException($"Unknown option --{name}.");
            }
        }

        foreach (var (name, values) in _options)
        {
            if (values.Count > 1 && name != "segment")
            {
                throw new UsageException($"--{name} is given more than once.");
            }
        }
    }

    public string RequireInput() => _positional.Count switch
    {
        1 => _positional[0],
        0 => throw new UsageException("The video file is missing."),
        _ => throw new UsageException($"Unexpected argument '{_positional[1]}'. Put file names with spaces in quotes.")
    };

    public bool Has(string name) => _options.ContainsKey(name);

    public string? Get(string name) => _options.TryGetValue(name, out var values) ? values[0] : null;

    public IReadOnlyList<string> GetAll(string name) => _options.TryGetValue(name, out var values) ? values : Array.Empty<string>();

    public string Require(string name) => Get(name) ?? throw new UsageException($"--{name} is required.");
}
