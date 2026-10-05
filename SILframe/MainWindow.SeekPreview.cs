using System.Diagnostics;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;

namespace SILframe;

// Thumbnail and time shown above the seek bar under the pointer. The
// thumbnails are made in the background after playback has started, so
// opening a video is not slowed down; until one is ready only the time shows.
public sealed partial class MainWindow
{
    private const int SeekPreviewWidth = 192;
    private const int SeekPreviewHeight = 108;
    private const int SeekPreviewBatchSize = 8;
    private const double SeekPreviewThumbSize = 20;

    private CancellationTokenSource? _seekPreviewCancellation;
    private double[] _seekPreviewTimes = Array.Empty<double>();
    private string?[] _seekPreviewPaths = Array.Empty<string?>();
    private BitmapImage?[] _seekPreviewBitmaps = Array.Empty<BitmapImage?>();
    private int _seekPreviewShownIndex = -1;

    private void InitializeSeekPreview()
    {
        TimelineSlider.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(TimelineSlider_PointerMovedForPreview), true);
        TimelineSlider.AddHandler(UIElement.PointerExitedEvent, new PointerEventHandler((_, _) => HideSeekPreview()), true);
        ApplySeekPreviewSetting();
        if (double.TryParse(Environment.GetEnvironmentVariable("SILFRAME_SEEK_PREVIEW_AT"), NumberStyles.Float, CultureInfo.InvariantCulture, out var ratio))
        {
            // UI automation hook: show the preview at this fraction of the seek
            // bar without pointer input, refreshed as thumbnails arrive.
            var timer = DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(1);
            timer.Tick += (_, _) =>
            {
                if (_settings.ShowSeekPreview && TimelineSlider.IsEnabled && ControlsBorder.Visibility == Visibility.Visible)
                {
                    ShowSeekPreviewAt(TimelineSlider.ActualWidth * Math.Clamp(ratio, 0, 1));
                }
            };
            timer.Start();
        }
    }

    private void ApplySeekPreviewSetting()
    {
        // The preview box shows the time itself, so the slider's own tooltip
        // would repeat it while dragging.
        TimelineSlider.IsThumbToolTipEnabled = !_settings.ShowSeekPreview;
        if (!_settings.ShowSeekPreview)
        {
            HideSeekPreview();
            StopSeekPreview();
        }
        else if (_seekPreviewTimes.Length == 0 && _mediaInfo is { } media && _loadedPath is { } path)
        {
            StartSeekPreview(path, media.DurationSeconds);
        }
    }

    private void StopSeekPreview()
    {
        var cancellation = _seekPreviewCancellation;
        _seekPreviewCancellation = null;
        if (cancellation is not null)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }

        var paths = _seekPreviewPaths;
        _seekPreviewTimes = Array.Empty<double>();
        _seekPreviewPaths = Array.Empty<string?>();
        _seekPreviewBitmaps = Array.Empty<BitmapImage?>();
        _seekPreviewShownIndex = -1;
        SeekPreviewImage.Source = null;
        SeekPreviewImage.Visibility = Visibility.Collapsed;
        DeleteThumbnailFiles(paths.OfType<string>());
    }

    private async void StartSeekPreview(string sourcePath, double durationSeconds)
    {
        StopSeekPreview();
        if (!_settings.ShowSeekPreview || !double.IsFinite(durationSeconds) || durationSeconds < 2)
        {
            return;
        }

        _ = Task.Run(DeleteStaleSeekPreviewFiles);
        // About one thumbnail every five seconds, within sensible bounds.
        var count = (int)Math.Clamp(Math.Ceiling(durationSeconds / 5), 10, 160);
        var times = new double[count];
        for (var i = 0; i < count; i++)
        {
            times[i] = durationSeconds * (i + 0.5) / count;
        }

        var paths = new string?[count];
        _seekPreviewTimes = times;
        _seekPreviewPaths = paths;
        _seekPreviewBitmaps = new BitmapImage?[count];
        var cancellation = new CancellationTokenSource();
        _seekPreviewCancellation = cancellation;
        var token = cancellation.Token;
        try
        {
            // Let playback start first.
            await Task.Delay(1500, token);
            foreach (var batch in OrderCoarseToFine(count).Chunk(SeekPreviewBatchSize))
            {
                var batchPaths = await GenerateSeekPreviewBatchAsync(sourcePath, batch.Select(i => times[i]).ToArray(), token);
                if (token.IsCancellationRequested || !ReferenceEquals(_seekPreviewPaths, paths))
                {
                    DeleteThumbnailFiles(batchPaths ?? Array.Empty<string>());
                    return;
                }

                if (batchPaths is null)
                {
                    // This video cannot be decoded here; stop instead of retrying every batch.
                    return;
                }

                for (var i = 0; i < batch.Length; i++)
                {
                    paths[batch[i]] = batchPaths[i];
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            App.WriteErrorLog(ex);
        }
    }

    // Spreads the first thumbnails over the whole video, then fills the gaps,
    // so that hovering anywhere soon shows something close.
    private static int[] OrderCoarseToFine(int count)
    {
        var order = new List<int>(count);
        var seen = new bool[count];
        for (var step = 16; step >= 1; step /= 2)
        {
            for (var i = step / 2; i < count; i += step)
            {
                if (!seen[i])
                {
                    seen[i] = true;
                    order.Add(i);
                }
            }
        }

        for (var i = 0; i < count; i++)
        {
            if (!seen[i])
            {
                order.Add(i);
            }
        }

        return order.ToArray();
    }

    private static async Task<string[]?> GenerateSeekPreviewBatchAsync(string sourcePath, double[] timestamps, CancellationToken cancellationToken)
    {
        var ffmpegPath = Path.Combine(AppContext.BaseDirectory, "Media", "ffmpeg.exe");
        if (!File.Exists(ffmpegPath))
        {
            return null;
        }

        var outputPaths = timestamps
            .Select(_ => Path.Combine(Path.GetTempPath(), $"SILframe-seek-{Guid.NewGuid():N}.jpg"))
            .ToArray();
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        void Add(params string[] arguments)
        {
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }

        Add("-hide_banner", "-loglevel", "error", "-nostdin", "-nostats");
        foreach (var timestamp in timestamps)
        {
            Add("-ss", timestamp.ToString("0.###", CultureInfo.InvariantCulture), "-threads", "1", "-i", sourcePath);
        }

        var scale = $"scale={SeekPreviewWidth}:{SeekPreviewHeight}:force_original_aspect_ratio=decrease,pad={SeekPreviewWidth}:{SeekPreviewHeight}:(ow-iw)/2:(oh-ih)/2,setsar=1";
        Add("-filter_complex", string.Join(';', timestamps.Select((_, i) => $"[{i}:v:0]{scale}[thumb{i}]")));
        for (var i = 0; i < timestamps.Length; i++)
        {
            Add("-map", $"[thumb{i}]", "-frames:v", "1", "-q:v", "5", "-update", "1", "-y", outputPaths[i]);
        }

        try
        {
            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                return null;
            }

            try
            {
                // Playback and the interface come first.
                process.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch
            {
                // The process may already have exited.
            }

            var errorTask = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(60));
            try
            {
                await process.WaitForExitAsync(deadline.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    // The process may have exited between the check and Kill.
                }

                await process.WaitForExitAsync();
            }

            await errorTask;
            if (cancellationToken.IsCancellationRequested || deadline.IsCancellationRequested ||
                process.ExitCode != 0 || outputPaths.Any(path => !File.Exists(path)))
            {
                DeleteThumbnailFiles(outputPaths);
                return null;
            }

            return outputPaths;
        }
        catch
        {
            DeleteThumbnailFiles(outputPaths);
            return null;
        }
    }

    private void TimelineSlider_PointerMovedForPreview(object sender, PointerRoutedEventArgs e)
    {
        if (!_settings.ShowSeekPreview || !TimelineSlider.IsEnabled || TimelineSlider.Maximum <= 0 ||
            ControlsBorder.Visibility != Visibility.Visible)
        {
            HideSeekPreview();
            return;
        }

        ShowSeekPreviewAt(e.GetCurrentPoint(TimelineSlider).Position.X);
    }

    private void ShowSeekPreviewAt(double x)
    {
        var trackWidth = TimelineSlider.ActualWidth - SeekPreviewThumbSize;
        if (trackWidth <= 0)
        {
            return;
        }

        var ratio = Math.Clamp((x - SeekPreviewThumbSize / 2) / trackWidth, 0, 1);
        var seconds = ratio * TimelineSlider.Maximum;
        SeekPreviewTime.Text = FormatTime(seconds);

        var index = FindNearestSeekPreview(seconds);
        if (index != _seekPreviewShownIndex)
        {
            _seekPreviewShownIndex = index;
            if (index < 0)
            {
                SeekPreviewImage.Source = null;
                SeekPreviewImage.Visibility = Visibility.Collapsed;
            }
            else
            {
                SeekPreviewImage.Source = _seekPreviewBitmaps[index] ??= new BitmapImage(new Uri(_seekPreviewPaths[index]!));
                SeekPreviewImage.Visibility = Visibility.Visible;
            }
        }

        SeekPreviewBox.Visibility = Visibility.Visible;
        SeekPreviewBox.UpdateLayout();
        var origin = TimelineSlider.TransformToVisual(SeekPreviewLayer).TransformPoint(new Windows.Foundation.Point(0, 0));
        var boxWidth = SeekPreviewBox.ActualWidth;
        var left = Math.Clamp(origin.X + x - boxWidth / 2, 4, Math.Max(4, SeekPreviewLayer.ActualWidth - boxWidth - 4));
        Canvas.SetLeft(SeekPreviewBox, left);
        Canvas.SetTop(SeekPreviewBox, Math.Max(0, origin.Y - SeekPreviewBox.ActualHeight - 2));
    }

    // The closest finished thumbnail, as long as it is not misleadingly far
    // from the pointer's time.
    private int FindNearestSeekPreview(double seconds)
    {
        var times = _seekPreviewTimes;
        if (times.Length == 0)
        {
            return -1;
        }

        var spacing = times.Length > 1 ? times[1] - times[0] : double.MaxValue;
        var center = Math.Clamp((int)(seconds / (spacing * times.Length) * times.Length), 0, times.Length - 1);
        for (var offset = 0; offset < times.Length; offset++)
        {
            foreach (var candidate in new[] { center - offset, center + offset })
            {
                if (candidate >= 0 && candidate < times.Length && _seekPreviewPaths[candidate] is not null)
                {
                    return Math.Abs(times[candidate] - seconds) <= Math.Max(spacing * 4, 20) ? candidate : -1;
                }
            }
        }

        return -1;
    }

    // Thumbnails are deleted when the video changes or the window closes. If
    // the process was ended abruptly they stay behind, so old ones are removed.
    private static void DeleteStaleSeekPreviewFiles()
    {
        try
        {
            var limit = DateTime.UtcNow.AddHours(-12);
            foreach (var file in Directory.EnumerateFiles(Path.GetTempPath(), "SILframe-seek-*.jpg"))
            {
                if (File.GetLastWriteTimeUtc(file) < limit)
                {
                    File.Delete(file);
                }
            }
        }
        catch
        {
            // Leftover temporary images are harmless.
        }
    }

    private void HideSeekPreview()
    {
        SeekPreviewBox.Visibility = Visibility.Collapsed;
    }
}
