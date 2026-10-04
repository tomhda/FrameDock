using System.Globalization;
using SILframe.Player;
using Microsoft.UI.Xaml;
using Microsoft.Windows.Globalization;

namespace SILframe;

public partial class App : Application
{
    private Window? _window;
    private static readonly string ErrorLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SILframe",
        "error.log");

    public App()
    {
        ApplyLanguageOverride();
        InitializeComponent();
        UnhandledException += (_, args) => WriteErrorLog(args.Exception);
    }

    // Decides the UI language before any window exists: SILFRAME_LANGUAGE
    // first, then the settings file. An explicit language also drives
    // CurrentUICulture so Core picks the same messages. Without either, the
    // UI follows Windows (falling back to en-US) and Core is aligned with
    // whichever of Japanese or English the UI actually resolved to.
    // Number and time formatting keep using InvariantCulture; CurrentCulture
    // is never changed here.
    private static void ApplyLanguageOverride()
    {
        var requested = NormalizeLanguage(Environment.GetEnvironmentVariable("SILFRAME_LANGUAGE"));
        if (requested is null)
        {
            var saved = PlayerSettings.ReadLanguageOnly();
            requested = string.IsNullOrEmpty(saved) ? null : saved;
        }

        if (requested is not null)
        {
            ApplicationLanguages.PrimaryLanguageOverride = requested;
            var culture = new CultureInfo(requested);
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            CultureInfo.CurrentUICulture = culture;
            return;
        }

        var isJapanese = CultureInfo.CurrentUICulture.Name.StartsWith("ja", StringComparison.OrdinalIgnoreCase);
        var fallback = new CultureInfo(isJapanese ? "ja-JP" : "en-US");
        CultureInfo.DefaultThreadCurrentUICulture = fallback;
        CultureInfo.CurrentUICulture = fallback;
    }

    private static string? NormalizeLanguage(string? value)
    {
        if (string.Equals(value, "ja-JP", StringComparison.OrdinalIgnoreCase))
        {
            return "ja-JP";
        }

        if (string.Equals(value, "en-US", StringComparison.OrdinalIgnoreCase))
        {
            return "en-US";
        }

        return null;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            var requestedPath = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault();
            _window = new MainWindow(requestedPath);
            _window.Activate();
        }
        catch (Exception ex)
        {
            WriteErrorLog(ex);
            throw;
        }
    }

    internal static void WriteErrorLog(Exception exception)
    {
        try
        {
            var directory = Path.GetDirectoryName(ErrorLogPath)!;
            Directory.CreateDirectory(directory);
            File.AppendAllText(ErrorLogPath, $"{DateTimeOffset.Now:O}\r\n{exception}\r\n---\r\n");
        }
        catch
        {
            // Crash diagnostics must never create a second startup failure.
        }
    }
}
