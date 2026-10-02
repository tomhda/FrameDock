using Microsoft.UI.Xaml;

namespace FrameDock;

public partial class App : Application
{
    private Window? _window;
    private static readonly string ErrorLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FrameDock",
        "error.log");

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, args) => WriteErrorLog(args.Exception);
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
