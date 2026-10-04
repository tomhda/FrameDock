using SILframe.Player;

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: SILframe.RuntimeSmoke.exe <video-file> [...]");
    return 2;
}

var failed = false;
foreach (var path in args)
{
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"FAIL {path}: file not found");
        failed = true;
        continue;
    }

    using var controller = new MpvController(0, message => Console.Error.WriteLine($"mpv: {message}"), headless: true);
    var fileLoaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    controller.FileLoaded += () => fileLoaded.TrySetResult();

    try
    {
        controller.LoadFile(path);
        await fileLoaded.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var advanced = false;
        var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < timeout)
        {
            var position = controller.GetNumber("time-pos");
            if (position is > 0.2)
            {
                advanced = true;
                break;
            }

            await Task.Delay(100);
        }

        if (!advanced)
        {
            Console.Error.WriteLine($"FAIL {path}: libmpv loaded the file but video time did not advance");
            failed = true;
        }
        else
        {
            Console.WriteLine($"PASS {Path.GetFileName(path)}: libmpv loaded and decoded playback advanced");
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"FAIL {path}: {ex.Message}");
        failed = true;
    }
}

return failed ? 1 : 0;
