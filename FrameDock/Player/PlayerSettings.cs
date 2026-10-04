using System.Text.Json;

namespace FrameDock.Player;

internal enum PlayerDisplayMode
{
    AlwaysVisible,
    MaximizedOverlay,
    Fullscreen
}

internal enum FrameSaveLocation
{
    Pictures,
    CustomFolder,
    VideoFolder,
    VideoSubfolder
}

internal sealed class PlayerSettings
{
    internal const string DefaultFrameSaveSubfolder = "FrameDock";

    private static readonly SemaphoreSlim SaveGate = new(1, 1);

    public double SkipSeconds { get; set; } = 10;
    public double Volume { get; set; } = 75;
    public double Speed { get; set; } = 1;
    public bool Repeat { get; set; }
    public bool Muted { get; set; }
    public PlayerDisplayMode DisplayMode { get; set; } = PlayerDisplayMode.MaximizedOverlay;
    public string Language { get; set; } = "";
    public FrameSaveLocation FrameSaveLocation { get; set; } = FrameSaveLocation.Pictures;
    public string FrameSaveFolder { get; set; } = "";
    public string FrameSaveSubfolder { get; set; } = DefaultFrameSaveSubfolder;
    public bool ShowRepeatButton { get; set; } = true;
    public bool ShowSpeedButton { get; set; } = true;
    public bool ShowTracksButton { get; set; } = true;
    public bool ShowCopyFrameButton { get; set; }

    internal static bool IsValidFolderName(string? name)
    {
        var trimmed = name?.Trim();
        return !string.IsNullOrEmpty(trimmed) && trimmed is not ("." or "..") &&
            trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !trimmed.EndsWith('.');
    }

    // FRAMEDOCK_SETTINGS_PATH lets UI automation run against a scratch file
    // instead of the user's own settings.
    public static string SettingsPath => Environment.GetEnvironmentVariable("FRAMEDOCK_SETTINGS_PATH") is { Length: > 0 } overridePath
        ? overridePath
        : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FrameDock",
            "settings.json");

    public static PlayerSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new PlayerSettings();
            }

            var loaded = JsonSerializer.Deserialize<PlayerSettings>(File.ReadAllText(SettingsPath)) ?? new PlayerSettings();
            loaded.SkipSeconds = double.IsFinite(loaded.SkipSeconds) ? Math.Clamp(loaded.SkipSeconds, 1, 600) : 10;
            loaded.Volume = double.IsFinite(loaded.Volume) ? Math.Clamp(loaded.Volume, 0, 100) : 75;
            loaded.Speed = double.IsFinite(loaded.Speed) ? Math.Clamp(loaded.Speed, 0.25, 4) : 1;
            loaded.FrameSaveLocation = Enum.IsDefined(loaded.FrameSaveLocation) ? loaded.FrameSaveLocation : FrameSaveLocation.Pictures;
            loaded.FrameSaveFolder ??= "";
            loaded.FrameSaveSubfolder = IsValidFolderName(loaded.FrameSaveSubfolder) ? loaded.FrameSaveSubfolder.Trim() : DefaultFrameSaveSubfolder;
            if (!Enum.IsDefined(loaded.DisplayMode))
            {
                loaded.DisplayMode = PlayerDisplayMode.MaximizedOverlay;
            }

            loaded.Language = NormalizeLanguage(loaded.Language);
            return loaded;
        }
        catch
        {
            return new PlayerSettings();
        }
    }

    private static string NormalizeLanguage(string? value) => value switch
    {
        "ja-JP" => "ja-JP",
        "en-US" => "en-US",
        _ => "",
    };

    // Reads only the display language without full validation so App can
    // decide resources before any window exists. Reads the file once.
    public static string ReadLanguageOnly()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return "";
            }

            using var document = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            if (document.RootElement.TryGetProperty("Language", out var element) &&
                element.ValueKind == JsonValueKind.String)
            {
                return NormalizeLanguage(element.GetString());
            }

            return "";
        }
        catch
        {
            return "";
        }
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        await SaveGate.WaitAsync(cancellationToken);
        try
        {
            var destination = SettingsPath;
            var directory = Path.GetDirectoryName(destination)!;
            Directory.CreateDirectory(directory);
            var temporary = destination + ".tmp";
            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, this, cancellationToken: cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            SaveGate.Release();
        }
    }
}
