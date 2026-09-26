using System.Text.Json;

namespace FrameDock.Player;

internal enum PlayerDisplayMode
{
    AlwaysVisible,
    MaximizedOverlay,
    Fullscreen
}

internal sealed class PlayerSettings
{
    public double SkipSeconds { get; set; } = 10;
    public double Volume { get; set; } = 75;
    public double Speed { get; set; } = 1;
    public bool Muted { get; set; }
    public PlayerDisplayMode DisplayMode { get; set; } = PlayerDisplayMode.MaximizedOverlay;

    public static string SettingsPath => Path.Combine(
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
            if (!Enum.IsDefined(loaded.DisplayMode))
            {
                loaded.DisplayMode = PlayerDisplayMode.MaximizedOverlay;
            }

            return loaded;
        }
        catch
        {
            return new PlayerSettings();
        }
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
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
}
