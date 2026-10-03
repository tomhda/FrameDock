using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using FrameDock.Core;

namespace FrameDock.Player;

internal sealed class MpvController : IDisposable
{
    private readonly IntPtr _handle;
    private readonly CancellationTokenSource _eventCancellation = new();
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<int>> _pendingCommands = new();
    private readonly Task _eventPump;
    private readonly Action<string>? _onError;
    private long _nextReplyId;
    private bool _disposed;

    public event Action? FileLoaded;
    public event Action<IntPtr>? SwapChainChanged;

    public MpvController(double initialVolume, Action<string>? onError = null, bool headless = false, int compositionWidth = 1280, int compositionHeight = 720)
    {
        _onError = onError;
        _handle = MpvNative.mpv_create();
        if (_handle == IntPtr.Zero)
        {
            _eventCancellation.Dispose();
            throw new InvalidOperationException("libmpv could not create a player instance.");
        }

        try
        {
            var options = new List<(string Name, string Value)>
            {
                ("vo", headless ? "null" : "gpu"),
                ("osc", "no"),
                ("osd-level", "0"),
                ("osd-bar", "no"),
                ("osd-on-seek", "no"),
                ("terminal", "no"),
                ("input-default-bindings", "no"),
                ("keep-open", "yes"),
                ("save-position-on-quit", "no"),
                ("volume", initialVolume.ToString("0.##", CultureInfo.InvariantCulture)),
            };
            if (headless)
            {
                options.Add(("ao", "null"));
                options.Add(("hwdec", "no"));
            }
            else
            {
                options.Add(("gpu-api", "d3d11"));
                options.Add(("gpu-context", "d3d11"));
                options.Add(("d3d11-output-mode", "composition"));
                options.Add(("d3d11-composition-size", $"{Math.Clamp(compositionWidth, 1, 16384)}x{Math.Clamp(compositionHeight, 1, 16384)}"));
                options.Add(("hwdec", "auto-safe"));
            }

            if (Environment.GetEnvironmentVariable("FRAMEDOCK_MPV_LOG") == "1")
            {
                options.Add(("log-file", Path.Combine(Path.GetTempPath(), "FrameDock-mpv.log")));
            }

            foreach (var (name, value) in options)
            {
                EnsureSuccess(MpvNative.mpv_set_option_string(_handle, name, value), $"Setting mpv option '{name}'");
            }

            EnsureSuccess(MpvNative.mpv_initialize(_handle), "Initializing libmpv");
            _eventPump = Task.Run(PumpEvents);
        }
        catch
        {
            MpvNative.mpv_terminate_destroy(_handle);
            _eventCancellation.Dispose();
            throw;
        }
    }

    public void LoadFile(string path)
    {
        ThrowIfDisposed();
        Execute("loadfile", Path.GetFullPath(path), "replace");
        SetProperty("pause", "no");
    }

    public void TogglePause()
    {
        ThrowIfDisposed();
        var isPaused = GetFlag("pause");
        SetProperty("pause", isPaused ? "no" : "yes");
    }

    public void SetPaused(bool paused) => SetProperty("pause", paused ? "yes" : "no");

    public void SeekRelative(double seconds)
    {
        ThrowIfDisposed();
        if (!double.IsFinite(seconds) || Math.Abs(seconds) > 3600)
        {
            throw new ArgumentOutOfRangeException(nameof(seconds));
        }

        Execute("seek", seconds.ToString("0.###", CultureInfo.InvariantCulture), "relative+exact");
    }

    public void SeekAbsolute(double seconds)
    {
        ThrowIfDisposed();
        if (!double.IsFinite(seconds) || seconds < 0 || seconds > 86_400_000)
        {
            throw new ArgumentOutOfRangeException(nameof(seconds));
        }

        Execute("seek", seconds.ToString("0.###", CultureInfo.InvariantCulture), "absolute+exact");
    }

    public void StepFrame(bool backwards)
    {
        ThrowIfDisposed();
        SetPaused(true);
        Execute(backwards ? "frame-back-step" : "frame-step");
    }

    public void SetVolume(double volume)
    {
        ThrowIfDisposed();
        if (!double.IsFinite(volume) || volume is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(volume));
        }

        SetProperty("volume", volume.ToString("0.##", CultureInfo.InvariantCulture));
    }

    public void SetSpeed(double speed)
    {
        ThrowIfDisposed();
        if (!double.IsFinite(speed) || speed is < 0.25 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(speed));
        }

        SetProperty("speed", speed.ToString("0.##", CultureInfo.InvariantCulture));
    }

    public void SetVideoRotation(int degreesClockwise)
    {
        ThrowIfDisposed();
        var normalized = ((degreesClockwise % 360) + 360) % 360;
        if (normalized % 90 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(degreesClockwise), "Video rotation must be a multiple of 90 degrees.");
        }

        SetProperty("video-rotate", normalized.ToString(CultureInfo.InvariantCulture));
    }

    public void SetVideoCrop(CropRect? codedCrop)
    {
        ThrowIfDisposed();
        if (codedCrop is not { } crop)
        {
            SetProperty("video-crop", string.Empty);
            return;
        }

        if (crop.X < 0 || crop.Y < 0 || crop.Width <= 0 || crop.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(codedCrop));
        }

        SetProperty("video-crop", string.Create(CultureInfo.InvariantCulture, $"{crop.Width}x{crop.Height}+{crop.X}+{crop.Y}"));
    }

    public void SetVideoAspectOverride(double? aspectRatio)
    {
        ThrowIfDisposed();
        if (aspectRatio is null)
        {
            SetProperty("video-aspect-override", "-1");
            return;
        }

        if (!double.IsFinite(aspectRatio.Value) || aspectRatio.Value <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(aspectRatio), "The aspect ratio must be finite and positive.");
        }

        SetProperty("video-aspect-override", aspectRatio.Value.ToString("0.##########", CultureInfo.InvariantCulture));
    }

    public void SetMuted(bool muted)
    {
        ThrowIfDisposed();
        SetProperty("mute", muted ? "yes" : "no");
    }

    public bool GetFlag(string name)
    {
        ThrowIfDisposed();
        var value = 0;
        return MpvNative.mpv_get_property(_handle, name, MpvNative.FormatFlag, ref value) >= 0 && value != 0;
    }

    public double? GetNumber(string name)
    {
        ThrowIfDisposed();
        var value = 0d;
        return MpvNative.mpv_get_property(_handle, name, MpvNative.FormatDouble, ref value) >= 0 && double.IsFinite(value)
            ? value
            : null;
    }

    public string? GetString(string name)
    {
        ThrowIfDisposed();
        var value = MpvNative.mpv_get_property_string(_handle, name);
        if (value == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUTF8(value);
        }
        finally
        {
            MpvNative.mpv_free(value);
        }
    }

    public IntPtr GetDisplaySwapChain()
    {
        ThrowIfDisposed();
        long address = 0;
        var result = MpvNative.mpv_get_property(_handle, "display-swapchain", MpvNative.FormatInt64, ref address);
        return result >= 0 && address != 0 ? new IntPtr(address) : IntPtr.Zero;
    }

    public IntPtr ResizeComposition(int width, int height)
    {
        ThrowIfDisposed();
        if (width is < 1 or > 16384 || height is < 1 or > 16384)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Composition dimensions must be between 1 and 16384 pixels.");
        }

        SetProperty("options/d3d11-composition-size", $"{width}x{height}");
        return GetDisplaySwapChain();
    }

    public async Task<string> SaveScreenshotAsync(string path, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var fullPath = Path.GetFullPath(path);
        if (File.Exists(fullPath))
        {
            throw new IOException("The screenshot destination already exists.");
        }

        await ExecuteAsync(cancellationToken, "screenshot-to-file", fullPath, "video");
        var info = new FileInfo(fullPath);
        if (!info.Exists || info.Length == 0)
        {
            throw new IOException("mpv completed the screenshot command but did not create an image.");
        }

        return fullPath;
    }

    private void SetProperty(string name, string value)
    {
        ThrowIfDisposed();
        var result = MpvNative.mpv_set_property_string(_handle, name, value);
        EnsureSuccess(result, $"Setting mpv property '{name}'");
    }

    private void Execute(params string[] arguments)
    {
        var pointers = AllocateArguments(arguments);
        try
        {
            var result = MpvNative.mpv_command(_handle, pointers.Array);
            EnsureSuccess(result, $"Running mpv command '{arguments[0]}'");
        }
        finally
        {
            pointers.Dispose();
        }
    }

    private async Task ExecuteAsync(CancellationToken cancellationToken, params string[] arguments)
    {
        var replyId = unchecked((ulong)Interlocked.Increment(ref _nextReplyId));
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingCommands.TryAdd(replyId, completion))
        {
            throw new InvalidOperationException("Could not track the mpv command reply.");
        }

        var pointers = AllocateArguments(arguments);
        try
        {
            var result = MpvNative.mpv_command_async(_handle, replyId, pointers.Array);
            EnsureSuccess(result, $"Starting mpv command '{arguments[0]}'");
        }
        catch
        {
            _pendingCommands.TryRemove(replyId, out _);
            throw;
        }
        finally
        {
            pointers.Dispose();
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _eventCancellation.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var error = await completion.Task.WaitAsync(timeout.Token);
            EnsureSuccess(error, $"Completing mpv command '{arguments[0]}'");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_eventCancellation.IsCancellationRequested)
        {
            throw new TimeoutException($"mpv did not finish '{arguments[0]}' within 30 seconds.");
        }
        finally
        {
            _pendingCommands.TryRemove(replyId, out _);
        }
    }

    private static NativeArguments AllocateArguments(string[] arguments)
    {
        var pointers = new IntPtr[arguments.Length + 1];
        try
        {
            for (var i = 0; i < arguments.Length; i++)
            {
                var bytes = Encoding.UTF8.GetBytes(arguments[i] + "\0");
                pointers[i] = Marshal.AllocHGlobal(bytes.Length);
                Marshal.Copy(bytes, 0, pointers[i], bytes.Length);
            }

            var array = Marshal.AllocHGlobal(IntPtr.Size * pointers.Length);
            Marshal.Copy(pointers, 0, array, pointers.Length);
            return new NativeArguments(array, pointers);
        }
        catch
        {
            foreach (var pointer in pointers)
            {
                if (pointer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(pointer);
                }
            }

            throw;
        }
    }

    private void PumpEvents()
    {
        while (!_eventCancellation.IsCancellationRequested)
        {
            var eventPointer = MpvNative.mpv_wait_event(_handle, -1);
            if (eventPointer == IntPtr.Zero)
            {
                continue;
            }

            var mpvEvent = Marshal.PtrToStructure<MpvEvent>(eventPointer);
            if (mpvEvent.EventId == MpvNative.EventShutdown || _eventCancellation.IsCancellationRequested)
            {
                return;
            }

            if (mpvEvent.EventId == MpvNative.EventFileLoaded)
            {
                FileLoaded?.Invoke();
            }
            else if (mpvEvent.EventId == MpvNative.EventVideoReconfig)
            {
                var swapChain = GetDisplaySwapChain();
                if (swapChain != IntPtr.Zero)
                {
                    SwapChainChanged?.Invoke(swapChain);
                }
            }
            else if (mpvEvent.EventId == MpvNative.EventCommandReply && _pendingCommands.TryRemove(mpvEvent.ReplyUserData, out var completion))
            {
                completion.TrySetResult(mpvEvent.Error);
            }
            else if (mpvEvent.EventId == MpvNative.EventEndFile && mpvEvent.Data != IntPtr.Zero)
            {
                var endFile = Marshal.PtrToStructure<MpvEventEndFile>(mpvEvent.Data);
                if (endFile.Error < 0)
                {
                    _onError?.Invoke(Localization.Strings.Get("Error_PlaybackFailed"));
                }
            }
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static void EnsureSuccess(int error, string operation)
    {
        if (error >= 0)
        {
            return;
        }

        throw new InvalidOperationException($"{operation} failed: {MpvNative.ErrorString(error)}");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _eventCancellation.Cancel();
        MpvNative.mpv_wakeup(_handle);
        _eventPump.GetAwaiter().GetResult();
        foreach (var command in _pendingCommands.Values)
        {
            command.TrySetCanceled(_eventCancellation.Token);
        }

        _pendingCommands.Clear();
        MpvNative.mpv_terminate_destroy(_handle);
        _eventCancellation.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeArguments(IntPtr array, IntPtr[] values) : IDisposable
    {
        public IntPtr Array { get; } = array;

        public void Dispose()
        {
            Marshal.FreeHGlobal(Array);
            foreach (var pointer in values)
            {
                if (pointer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(pointer);
                }
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MpvEvent
    {
        public int EventId;
        public int Error;
        public ulong ReplyUserData;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MpvEventEndFile
    {
        public int Reason;
        public int Error;
        public long PlaylistEntryId;
        public long PlaylistInsertId;
        public int PlaylistInsertNumEntries;
    }
}

internal static class MpvNative
{
    internal const int FormatFlag = 3;
    internal const int FormatInt64 = 4;
    internal const int FormatDouble = 5;
    internal const int EventShutdown = 1;
    internal const int EventCommandReply = 5;
    internal const int EventEndFile = 7;
    internal const int EventFileLoaded = 8;
    internal const int EventVideoReconfig = 17;

    [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr mpv_create();

    [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_set_option_string(IntPtr handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_initialize(IntPtr handle);

    [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_command(IntPtr handle, IntPtr arguments);

    [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_command_async(IntPtr handle, ulong replyUserData, IntPtr arguments);

    [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_set_property_string(IntPtr handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_get_property(IntPtr handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        int format,
        ref double value);

    [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_get_property(IntPtr handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        int format,
        ref int value);

    [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_get_property(IntPtr handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        int format,
        ref long value);

    [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr mpv_get_property_string(IntPtr handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr mpv_wait_event(IntPtr handle, double timeout);

    [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void mpv_wakeup(IntPtr handle);

    [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void mpv_free(IntPtr data);

    [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void mpv_terminate_destroy(IntPtr handle);

    [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr mpv_error_string(int error);

    internal static string ErrorString(int error) => Marshal.PtrToStringUTF8(mpv_error_string(error)) ?? $"error {error}";
}
