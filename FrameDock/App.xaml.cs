using Microsoft.UI.Xaml;

namespace FrameDock;

public partial class App : Application
{
    private Window? _window;
    private static readonly string StartupLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FrameDock",
        "startup-error.log");

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, args) => WriteStartupLog(args.Exception);
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
            WriteStartupLog(ex);
            throw;
        }
    }

    internal static void WriteStartupLog(Exception exception)
    {
        try
        {
            var directory = Path.GetDirectoryName(StartupLogPath)!;
            Directory.CreateDirectory(directory);
            File.AppendAllText(StartupLogPath, $"{DateTimeOffset.Now:O}\r\n{exception}\r\n---\r\n");
        }
        catch
        {
            // Crash diagnostics must never create a second startup failure.
        }
    }
}
