using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using FrameDock.Core;
using FrameDock.Player;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Windowing;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace FrameDock;

public sealed partial class MainWindow : Window
{
    private enum TrimDragTarget { Playhead, Start, End }
    private enum CropDragHandle { TopLeft, Top, TopRight, Right, BottomRight, Bottom, BottomLeft, Left }

    private const int VirtualKeyControl = 0x11;
    private const int ClipboardCannotOpenHResult = unchecked((int)0x800401D0);
    private const double ControlsRevealDepth = 72;
    private static readonly TimeSpan ControlsHideDelay = TimeSpan.FromMilliseconds(360);
    private readonly IntPtr _windowHandle;
    private readonly string? _initialPath;
    private readonly PlayerSettings _settings;
    private readonly MediaExportService _exportService;
    private readonly SemaphoreSlim _openGate = new(1, 1);
    private MpvController? _player;
    private DispatcherQueueTimer? _statusTimer;
    private DispatcherQueueTimer? _compositionResizeTimer;
    private DispatcherQueueTimer? _settingsSaveTimer;
    private DispatcherQueueTimer? _noticeTimer;
    private DispatcherQueueTimer? _controlsHideTimer;
    private AppWindow? _appWindow;
    private MediaInfo? _mediaInfo;
    private string? _loadedPath;
    private string? _queuedOpenPath;
    private string? _outputDirectory;
    private CropRect? _crop;
    private CropRect? _cropDragOrigin;
    private CropDragHandle? _activeCropHandle;
    private Windows.Foundation.Point? _cropDragPointerOrigin;
    private Windows.Foundation.Point? _cropDragHandleOrigin;
    private CancellationTokenSource? _exportCancellation;
    private CancellationTokenSource? _thumbnailCancellation;
    private string? _thumbnailSourcePath;
    private int _thumbnailRotation = -1;
    private IReadOnlyList<BitmapImage> _thumbnailBitmaps = Array.Empty<BitmapImage>();
    private int _thumbnailFrameCount;
    private bool _isUpdatingTimeline;
    private bool _isTimelineDragging;
    private bool _isTrimTimelineDragging;
    private double _trimDragPointerOffset;
    private TrimDragTarget _trimDragTarget;
    private bool _isMuted;
    private bool _isFullscreen;
    private bool _isApplyingDisplayMode;
    private PlayerDisplayMode _displayMode = PlayerDisplayMode.MaximizedOverlay;
    private PlayerDisplayMode _modeBeforeFullscreen = PlayerDisplayMode.MaximizedOverlay;
    private bool _isCropMode;
    private int _additionalRotationDegreesClockwise;
    private double _editingPreviousSpeed = 1;
    private bool _editSessionStarted;
    private bool _isRestoringSettings;
    private bool _isClosing;
    private bool _isExporting;
    private bool _forceCompositionResize;
    private bool _initialCompositionResizePending;
    private bool _isCompositionFileLoaded;
    private int _postLoadCompositionResizeAttempts;
    private bool _isPointerOverControls;
    private bool _isPointerNearControls;
    private bool _isSettingsDialogOpen;
    private int _openFlyoutCount;
    private int _compositionWidth;
    private int _compositionHeight;

    public MainWindow(string? initialPath = null)
    {
        try
        {
            InitializeComponent();
        }
        catch (Exception ex)
        {
            App.WriteStartupLog(ex);
            throw;
        }
        TrimStartBox.ValueChanged += TrimBox_ValueChanged;
        TrimEndBox.ValueChanged += TrimBox_ValueChanged;
        _initialPath = string.IsNullOrWhiteSpace(initialPath) ? null : initialPath;
        _settings = PlayerSettings.Load();
        _exportService = new MediaExportService(new ExportOptions(
            Path.Combine(AppContext.BaseDirectory, "Media", "ffmpeg.exe"),
            Path.Combine(AppContext.BaseDirectory, "Media", "ffprobe.exe")));
        _windowHandle = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_windowHandle));
        _appWindow.Changed += AppWindow_Changed;
        _isMuted = _settings.Muted;
        _displayMode = _settings.DisplayMode;
        _modeBeforeFullscreen = _displayMode == PlayerDisplayMode.Fullscreen
            ? PlayerDisplayMode.MaximizedOverlay
            : _displayMode;

        _isRestoringSettings = true;
        TimelineSlider.ValueChanged += TimelineSlider_ValueChanged;
        VolumeSlider.ValueChanged += VolumeSlider_ValueChanged;
        VolumeSlider.Value = _settings.Volume;
        UpdateMuteButton();
        UpdateSkipLabels();
        UpdateSpeedMenu(_settings.Speed);
        UpdateEditSpeedControl(_settings.Speed);
        _isRestoringSettings = false;

        RootGrid.Loaded += RootGrid_Loaded;
        RootGrid.ActualThemeChanged += RootGrid_ActualThemeChanged;
        Closed += MainWindow_Closed;
        _statusTimer = DispatcherQueue.CreateTimer();
        _statusTimer.Interval = TimeSpan.FromMilliseconds(120);
        _statusTimer.Tick += (_, _) => RefreshStatus();
        _compositionResizeTimer = DispatcherQueue.CreateTimer();
        _compositionResizeTimer.Interval = TimeSpan.FromMilliseconds(140);
        _compositionResizeTimer.IsRepeating = false;
        _compositionResizeTimer.Tick += (_, _) => ApplyCompositionResize();
        _settingsSaveTimer = DispatcherQueue.CreateTimer();
        _settingsSaveTimer.Interval = TimeSpan.FromMilliseconds(450);
        _settingsSaveTimer.IsRepeating = false;
        _settingsSaveTimer.Tick += async (_, _) => await SaveSettingsAsync();
        _noticeTimer = DispatcherQueue.CreateTimer();
        _noticeTimer.Interval = TimeSpan.FromSeconds(6);
        _noticeTimer.IsRepeating = false;
        _noticeTimer.Tick += (_, _) =>
        {
            InlineNoticeText.Visibility = Visibility.Collapsed;
            InlineNoticeText.Text = string.Empty;
            ScheduleControlsHide();
        };
        _controlsHideTimer = DispatcherQueue.CreateTimer();
        _controlsHideTimer.Interval = ControlsHideDelay;
        _controlsHideTimer.IsRepeating = false;
        _controlsHideTimer.Tick += (_, _) => HideControlsIfIdle();
        if (OverflowButton.Flyout is { } overflowFlyout)
        {
            overflowFlyout.Opened += (_, _) => ControlsFlyoutOpened();
            overflowFlyout.Closed += (_, _) => ControlsFlyoutClosed();
        }
        NotificationBar.RegisterPropertyChangedCallback(InfoBar.IsOpenProperty, (_, _) =>
        {
            if (NotificationBar.IsOpen)
            {
                ShowControls();
            }
            else
            {
                ScheduleControlsHide();
            }
        });
        UpdateControlsBackground();
        UpdateDisplayModeMenu();
        UpdateTitleBarTheme();
    }

    private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyDisplayMode(_settings.DisplayMode, persist: false, revealControls: false);
        if (_initialPath is not null)
        {
            await OpenFileAsync(_initialPath);
        }
    }

    private void RootGrid_ActualThemeChanged(FrameworkElement sender, object args)
    {
        UpdateTitleBarTheme();
        UpdateControlsBackground();
    }

    private void RootGrid_PointerEntered(object sender, PointerRoutedEventArgs e) => UpdateControlsPointerState(e);

    private void RootGrid_PointerMoved(object sender, PointerRoutedEventArgs e) => UpdateControlsPointerState(e);

    private void RootGrid_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isPointerNearControls = false;
        _isPointerOverControls = false;
        ScheduleControlsHide();
    }

    private void ControlsBorder_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOverControls = true;
        ShowControls();
    }

    private void ControlsBorder_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOverControls = false;
        UpdateControlsPointerState(e);
    }

    private void ControlsBorder_GotFocus(object sender, RoutedEventArgs e) => ShowControls();

    private void ControlsBorder_LostFocus(object sender, RoutedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(ScheduleControlsHide);
    }

    private void UpdateControlsPointerState(PointerRoutedEventArgs e)
    {
        if (RootGrid.ActualHeight <= 0)
        {
            return;
        }

        var position = e.GetCurrentPoint(RootGrid).Position;
        var revealDepth = Math.Max(ControlsRevealDepth, ControlsBorder.ActualHeight + 18);
        _isPointerNearControls = position.Y >= RootGrid.ActualHeight - revealDepth;
        if (_isPointerNearControls)
        {
            ShowControls();
        }
        else
        {
            ScheduleControlsHide();
        }
    }

    private void ShowControls()
    {
        _controlsHideTimer?.Stop();
        if (EditorPanel.Visibility == Visibility.Visible)
        {
            ControlsBorder.Visibility = Visibility.Collapsed;
            return;
        }

        ControlsBorder.Visibility = Visibility.Visible;
    }

    private void ScheduleControlsHide()
    {
        if (_isClosing || _controlsHideTimer is null || ControlsBorder.Visibility != Visibility.Visible || AreControlsPinned())
        {
            return;
        }

        _controlsHideTimer.Stop();
        _controlsHideTimer.Start();
    }

    private void HideControlsIfIdle()
    {
        if (!_isClosing && _displayMode != PlayerDisplayMode.AlwaysVisible &&
            !_isPointerNearControls && !_isPointerOverControls && !AreControlsPinned())
        {
            ControlsBorder.Visibility = Visibility.Collapsed;
        }
    }

    private bool AreControlsPinned() =>
        EditorPanel.Visibility == Visibility.Visible ||
        NotificationBar.IsOpen ||
        InlineNoticeText.Visibility == Visibility.Visible ||
        _isSettingsDialogOpen ||
        _openFlyoutCount > 0 ||
        IsKeyboardFocusWithinControls();

    private bool IsKeyboardFocusWithinControls()
    {
        if (RootGrid.XamlRoot is not { } xamlRoot)
        {
            return false;
        }

        var focused = FocusManager.GetFocusedElement(xamlRoot) as DependencyObject;
        while (focused is not null)
        {
            if (ReferenceEquals(focused, ControlsBorder))
            {
                return false;
            }

            if (focused is Control control && control.FocusState == FocusState.Keyboard)
            {
                var ancestor = focused;
                while (ancestor is not null && !ReferenceEquals(ancestor, ControlsBorder))
                {
                    ancestor = VisualTreeHelper.GetParent(ancestor);
                }

                if (ReferenceEquals(ancestor, ControlsBorder))
                {
                    return true;
                }
            }

            focused = VisualTreeHelper.GetParent(focused);
        }

        return false;
    }

    private void ControlsFlyoutOpened()
    {
        _openFlyoutCount++;
        ShowControls();
    }

    private void ControlsFlyoutClosed()
    {
        _openFlyoutCount = Math.Max(0, _openFlyoutCount - 1);
        ScheduleControlsHide();
    }

    private void VideoRegion_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ScheduleCompositionResize();
        UpdateCropOverlay();
    }

    private (int Width, int Height) GetCompositionSize()
    {
        var scale = VideoRegion.XamlRoot?.RasterizationScale ?? 1;
        var width = Math.Clamp((int)Math.Round(VideoRegion.ActualWidth * scale), 1, 16384);
        var height = Math.Clamp((int)Math.Round(VideoRegion.ActualHeight * scale), 1, 16384);
        return (width, height);
    }

    private void ScheduleCompositionResize(bool force = false)
    {
        if (_isClosing || _player is null || _compositionResizeTimer is null)
        {
            return;
        }

        _forceCompositionResize |= force;
        _compositionResizeTimer.Stop();
        _compositionResizeTimer.Start();
    }

    private void SchedulePostLoadCompositionResize()
    {
        if (_initialCompositionResizePending && _isCompositionFileLoaded && _postLoadCompositionResizeAttempts < 2)
        {
            ScheduleCompositionResize(force: true);
        }
    }

    private void ApplyCompositionResize()
    {
        if (_isClosing || _player is not { } player)
        {
            return;
        }

        var force = _forceCompositionResize;
        _forceCompositionResize = false;
        if (force && _isCompositionFileLoaded)
        {
            _postLoadCompositionResizeAttempts++;
        }

        var (width, height) = GetCompositionSize();
        if (!force && width == _compositionWidth && height == _compositionHeight)
        {
            return;
        }

        try
        {
            var swapChain = player.ResizeComposition(width, height);
            _compositionWidth = width;
            _compositionHeight = height;
            if (swapChain != IntPtr.Zero)
            {
                SwapChainPanelInterop.Attach(MpvSwapChainPanel, swapChain);
                MpvSwapChainPanel.Visibility = Visibility.Visible;
                if (_isCompositionFileLoaded)
                {
                    _initialCompositionResizePending = false;
                }
            }
        }
        catch (Exception ex)
        {
            ShowError($"動画表示のサイズを更新できませんでした: {ex.Message}");
        }
    }

    private void UpdateTitleBarTheme()
    {
        if (_appWindow?.TitleBar is not { } titleBar || !AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        var dark = RootGrid.ActualTheme == ElementTheme.Dark;
        var background = dark ? MakeColor(32, 32, 32) : MakeColor(249, 249, 249);
        var foreground = dark ? MakeColor(245, 245, 245) : MakeColor(26, 26, 26);
        var hover = dark ? MakeColor(55, 55, 55) : MakeColor(230, 230, 230);
        var pressed = dark ? MakeColor(72, 72, 72) : MakeColor(215, 215, 215);
        titleBar.BackgroundColor = background;
        titleBar.ForegroundColor = foreground;
        titleBar.InactiveBackgroundColor = background;
        titleBar.InactiveForegroundColor = foreground;
        titleBar.ButtonBackgroundColor = background;
        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonHoverBackgroundColor = hover;
        titleBar.ButtonHoverForegroundColor = foreground;
        titleBar.ButtonPressedBackgroundColor = pressed;
        titleBar.ButtonPressedForegroundColor = foreground;
    }

    private void UpdateControlsBackground()
    {
        var dark = RootGrid.ActualTheme == ElementTheme.Dark;
        ControlsBorder.Background = new SolidColorBrush(dark
            ? MakeColor(31, 31, 31, 222)
            : MakeColor(249, 249, 249, 222));
    }

    private static Windows.UI.Color MakeColor(byte red, byte green, byte blue, byte alpha = 255) => new()
    {
        A = alpha,
        R = red,
        G = green,
        B = blue
    };

    private async void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, _windowHandle);
        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            await OpenFileAsync(file.Path);
        }
    }

    private void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
    }

    private async void RootGrid_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        var items = await e.DataView.GetStorageItemsAsync();
        var file = items.OfType<StorageFile>().FirstOrDefault();
        if (file is not null)
        {
            await OpenFileAsync(file.Path);
        }
    }

    private async Task OpenFileAsync(string path)
    {
        if (!await _openGate.WaitAsync(0))
        {
            _queuedOpenPath = path;
            return;
        }

        try
        {
            string? nextPath = path;
            while (nextPath is not null && !_isClosing)
            {
                _queuedOpenPath = null;
                await OpenFileCoreAsync(nextPath);
                nextPath = _queuedOpenPath;
            }
        }
        finally
        {
            _openGate.Release();
        }
    }

    private async Task OpenFileCoreAsync(string path)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
            {
                ShowError($"ファイルが見つかりません: {path}");
                return;
            }
        }
        catch (Exception ex)
        {
            ShowError($"ファイルを開けません: {ex.Message}");
            return;
        }

        try
        {
            NotificationBar.IsOpen = false;
            InlineNoticeText.Visibility = Visibility.Collapsed;
            InlineNoticeText.Text = string.Empty;
            _noticeTimer?.Stop();
            _statusTimer?.Stop();
            CancelExportForNewFile();
            var previousPlayer = _player;
            _player = null;
            SwapChainPanelInterop.Attach(MpvSwapChainPanel, IntPtr.Zero);
            MpvSwapChainPanel.Visibility = Visibility.Collapsed;
            previousPlayer?.Dispose();

            _loadedPath = fullPath;
            OutputContainerComboBox.SelectedIndex = 0;
            CancelThumbnailGeneration();
            TrimThumbnailStrip.Children.Clear();
            _thumbnailSourcePath = null;
            _thumbnailRotation = -1;
            _thumbnailBitmaps = Array.Empty<BitmapImage>();
            _thumbnailFrameCount = 0;
            _mediaInfo = null;
            _crop = null;
            _additionalRotationDegreesClockwise = 0;
            _editSessionStarted = false;
            _outputDirectory = Path.GetDirectoryName(fullPath);
            SetCropMode(false);
            EditorPanel.Visibility = Visibility.Collapsed;
            SetEditorLayout(false);
            ScheduleControlsHide();
            CropOverlayCanvas.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = Visibility.Collapsed;
            TimelineSlider.IsEnabled = false;
            OutputFolderText.Text = "元動画と同じフォルダー";
            ToolTipService.SetToolTip(OutputFolderText, "元動画と同じフォルダー");
            OutputNameBox.Text = $"{Path.GetFileNameWithoutExtension(fullPath)}-clip";
            CropInfoText.Text = "クロップなし";
            CropResetButton.IsEnabled = false;
            CropModeButton.IsEnabled = false;
            TrimStartBox.Value = 0;
            TrimEndBox.Value = 0;
            ExportButton.IsEnabled = false;
            HdrNoteText.Visibility = Visibility.Collapsed;

            var (width, height) = GetCompositionSize();
            var player = await Task.Run(() => new MpvController(_settings.Volume, message =>
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (!_isClosing && string.Equals(_loadedPath, fullPath, StringComparison.OrdinalIgnoreCase))
                    {
                        ShowError(message);
                    }
                }), compositionWidth: width, compositionHeight: height));
            if (_isClosing)
            {
                player.Dispose();
                return;
            }

            _player = player;
            _compositionWidth = width;
            _compositionHeight = height;
            _initialCompositionResizePending = true;
            _isCompositionFileLoaded = false;
            _postLoadCompositionResizeAttempts = 0;
            // A maximize or DPI layout change may have happened while libmpv was
            // being created. Those SizeChanged events ran before _player existed,
            // so schedule a resize against the now-settled video region. This
            // forced pass is repeated once the file and its swap chain are ready.
            ScheduleCompositionResize(force: true);
            player.SetSpeed(_settings.Speed);
            player.SetMuted(_isMuted);
            player.SwapChainChanged += swapChain => DispatcherQueue.TryEnqueue(() =>
            {
                if (_isClosing || !ReferenceEquals(_player, player))
                {
                    return;
                }

                try
                {
                    SwapChainPanelInterop.Attach(MpvSwapChainPanel, swapChain);
                    MpvSwapChainPanel.Visibility = Visibility.Visible;
                    EmptyState.Visibility = Visibility.Collapsed;
                    SchedulePostLoadCompositionResize();
                }
                catch (Exception ex)
                {
                    ShowError($"動画画面を初期化できません: {ex.Message}");
                }
            });
            player.FileLoaded += () => DispatcherQueue.TryEnqueue(() =>
            {
                if (_isClosing || !ReferenceEquals(_player, player))
                {
                    return;
                }

                _isCompositionFileLoaded = true;
                SchedulePostLoadCompositionResize();

                RefreshStatus();
            });
            player.LoadFile(fullPath);
            TimelineSlider.IsEnabled = true;
            _statusTimer?.Start();

            try
            {
                var inspected = await _exportService.InspectAsync(fullPath);
                if (_isClosing || !ReferenceEquals(_player, player))
                {
                    return;
                }

                _mediaInfo = inspected;
                TrimStartBox.Maximum = inspected.DurationSeconds;
                TrimEndBox.Maximum = inspected.DurationSeconds;
                TrimStartBox.Value = 0;
                TrimEndBox.Value = inspected.DurationSeconds;
                HdrNoteText.Visibility = inspected.IsHdr ? Visibility.Visible : Visibility.Collapsed;
                UpdateRotationControl();
                UpdateCropInfo();
                ValidateEditRange();
                UpdateApproximateCopyAvailability();
            }
            catch (ExportException ex)
            {
                ShowError(ex.UserMessage);
            }
            catch (Exception ex)
            {
                ShowError($"動画の編集情報を読み取れませんでした: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            var failedPlayer = _player;
            _player = null;
            SwapChainPanelInterop.Attach(MpvSwapChainPanel, IntPtr.Zero);
            MpvSwapChainPanel.Visibility = Visibility.Collapsed;
            failedPlayer?.Dispose();
            EmptyState.Visibility = Visibility.Visible;
            TimelineSlider.IsEnabled = false;
            _statusTimer?.Stop();
            ShowError(ex.Message);
        }

    }

    private void RefreshStatus()
    {
        if (_player is null)
        {
            return;
        }

        var position = _player.GetNumber("time-pos");
        var duration = _player.GetNumber("duration");
        if (position.HasValue && duration.HasValue && duration.Value > 0)
        {
            if (!_isTimelineDragging)
            {
                _isUpdatingTimeline = true;
                TimelineSlider.Maximum = duration.Value;
                TimelineSlider.Value = Math.Clamp(position.Value, 0, duration.Value);
                _isUpdatingTimeline = false;
            }

            TimeText.Text = $"{FormatTime(position.Value)} / {FormatTime(duration.Value)}";
            UpdateTrimTimeline(position.Value);
        }

        var isPaused = _player.GetFlag("pause");
        PlayIcon.Glyph = isPaused ? "\uE768" : "\uE769";
        EditPlayIcon.Glyph = isPaused ? "\uE768" : "\uE769";
        PlayButton.SetValue(Microsoft.UI.Xaml.Automation.AutomationProperties.NameProperty, isPaused ? "再生" : "一時停止");
        EditPlayButton.SetValue(Microsoft.UI.Xaml.Automation.AutomationProperties.NameProperty, isPaused ? "再生" : "一時停止");
    }

    private static string FormatTime(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0)
        {
            seconds = 0;
        }

        var time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : time.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    private static string FormatTimeFileSafe(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0)
        {
            seconds = 0;
        }

        var time = TimeSpan.FromSeconds(seconds);
        return $"{(int)time.TotalHours:00}-{time.Minutes:00}-{time.Seconds:00}-{time.Milliseconds:000}";
    }

    private void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _player?.TogglePause();
            RefreshStatus();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) => SeekRelative(-_settings.SkipSeconds);

    private void ForwardButton_Click(object sender, RoutedEventArgs e) => SeekRelative(_settings.SkipSeconds);

    private void PreviousFrameButton_Click(object sender, RoutedEventArgs e) => StepFrame(backwards: true);

    private void NextFrameButton_Click(object sender, RoutedEventArgs e) => StepFrame(backwards: false);

    private void StepFrame(bool backwards)
    {
        try
        {
            _player?.StepFrame(backwards);
            RefreshStatus();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void SeekRelative(double seconds)
    {
        try
        {
            _player?.SeekRelative(seconds);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void TimelineSlider_PointerPressed(object sender, PointerRoutedEventArgs e) => _isTimelineDragging = true;

    private void TimelineSlider_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _isTimelineDragging = false;
        SeekToTimelineValue();
        RefreshStatus();
    }

    private void TimelineSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_isUpdatingTimeline && _player is not null && TimelineSlider.IsEnabled)
        {
            SeekToTimelineValue();
        }
    }

    private void SeekToTimelineValue()
    {
        try
        {
            _player?.SeekAbsolute(TimelineSlider.Value);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void TrimTimelineCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateTrimTimeline();
        if (EditorPanel.Visibility == Visibility.Visible && TrimThumbnailStrip.Children.Count == 0 && _thumbnailCancellation is null)
        {
            _ = LoadTrimThumbnailsAsync();
        }
    }

    private void TrimTimelineCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_mediaInfo is null || !double.IsFinite(_mediaInfo.DurationSeconds) || _mediaInfo.DurationSeconds <= 0)
        {
            return;
        }

        var point = e.GetCurrentPoint(TrimTimelineCanvas).Position;
        var width = TrimTimelineCanvas.ActualWidth;
        var pad = 8d;
        var trackWidth = Math.Max(0, width - pad * 2);
        if (trackWidth <= 0)
        {
            return;
        }

        var duration = _mediaInfo.DurationSeconds;
        var startValue = double.IsFinite(TrimStartBox.Value) ? Math.Clamp(TrimStartBox.Value, 0, duration) : 0;
        var endValue = double.IsFinite(TrimEndBox.Value) ? Math.Clamp(TrimEndBox.Value, 0, duration) : duration;
        var startX = pad + startValue / duration * trackWidth;
        var endX = pad + endValue / duration * trackWidth;
        var currentPosition = _player?.GetNumber("time-pos") ?? 0;
        currentPosition = double.IsFinite(currentPosition) ? Math.Clamp(currentPosition, 0, duration) : 0;
        var playheadX = pad + currentPosition / duration * trackWidth;
        var startDistance = Math.Abs(point.X - startX);
        var endDistance = Math.Abs(point.X - endX);
        var playheadDistance = Math.Abs(point.X - playheadX);
        const double trimHandleHitRadius = 20;
        const double playheadHitRadius = 14;
        var onPlayheadKnob = point.Y <= 16 && playheadDistance <= playheadHitRadius;
        _trimDragTarget = onPlayheadKnob
            ? TrimDragTarget.Playhead
            : Math.Min(startDistance, endDistance) <= trimHandleHitRadius
                ? startDistance <= endDistance ? TrimDragTarget.Start : TrimDragTarget.End
                : TrimDragTarget.Playhead;
        var dragAnchorX = _trimDragTarget switch
        {
            TrimDragTarget.Start => startX,
            TrimDragTarget.End => endX,
            _ => playheadX
        };
        _trimDragPointerOffset = Math.Abs(point.X - dragAnchorX) <= (_trimDragTarget == TrimDragTarget.Playhead ? playheadHitRadius : trimHandleHitRadius)
            ? point.X - dragAnchorX
            : 0;
        _isTrimTimelineDragging = true;
        TrimTimelineCanvas.CapturePointer(e.Pointer);
        MoveTrimTimeline(point.X);
        e.Handled = true;
    }

    private void TrimTimelineCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isTrimTimelineDragging)
        {
            return;
        }

        MoveTrimTimeline(e.GetCurrentPoint(TrimTimelineCanvas).Position.X);
        e.Handled = true;
    }

    private void TrimTimelineCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isTrimTimelineDragging)
        {
            return;
        }

        MoveTrimTimeline(e.GetCurrentPoint(TrimTimelineCanvas).Position.X);
        TrimTimelineCanvas.ReleasePointerCapture(e.Pointer);
        _isTrimTimelineDragging = false;
        _trimDragPointerOffset = 0;
        e.Handled = true;
    }

    private void TrimTimelineCanvas_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        _isTrimTimelineDragging = false;
        _trimDragPointerOffset = 0;
        UpdateTrimTimeline();
    }

    private void MoveTrimTimeline(double x)
    {
        if (_mediaInfo is null || !double.IsFinite(_mediaInfo.DurationSeconds) || _mediaInfo.DurationSeconds <= 0)
        {
            return;
        }

        var width = TrimTimelineCanvas.ActualWidth;
        var pad = 8d;
        var trackWidth = Math.Max(0, width - pad * 2);
        if (trackWidth <= 0)
        {
            return;
        }

        var duration = _mediaInfo.DurationSeconds;
        var adjustedX = Math.Clamp(x - _trimDragPointerOffset, pad, pad + trackWidth);
        var seconds = Math.Clamp((adjustedX - pad) / trackWidth, 0, 1) * duration;
        switch (_trimDragTarget)
        {
            case TrimDragTarget.Start:
                var endValue = double.IsFinite(TrimEndBox.Value) ? TrimEndBox.Value : duration;
                var latestStart = Math.Floor(Math.Max(0, endValue - 0.01) * 100) / 100;
                TrimStartBox.Value = Math.Clamp(Math.Round(seconds, 2, MidpointRounding.AwayFromZero), 0, latestStart);
                break;
            case TrimDragTarget.End:
                var startValue = double.IsFinite(TrimStartBox.Value) ? TrimStartBox.Value : 0;
                var earliestEnd = Math.Ceiling(Math.Min(duration, startValue + 0.01) * 100) / 100;
                var latestEnd = Math.Floor(duration * 100) / 100;
                TrimEndBox.Value = Math.Clamp(Math.Round(seconds, 2, MidpointRounding.AwayFromZero), Math.Min(earliestEnd, latestEnd), latestEnd);
                break;
            default:
                var position = Math.Clamp(seconds, 0, duration);
                _isUpdatingTimeline = true;
                TimelineSlider.Value = position;
                _isUpdatingTimeline = false;
                SeekToTimelineValue();
                UpdateTrimTimeline(position);
                break;
        }
    }

    private void UpdateTrimTimeline(double? playheadSeconds = null)
    {
        var width = TrimTimelineCanvas.ActualWidth;
        const double pad = 8;
        const double trackY = 6;
        const double trackHeight = 52;
        var trackWidth = Math.Max(0, width - pad * 2);
        var duration = _mediaInfo?.DurationSeconds ?? 0;
        if (trackWidth <= 0 || !double.IsFinite(duration) || duration <= 0)
        {
            return;
        }

        var start = double.IsFinite(TrimStartBox.Value) ? Math.Clamp(TrimStartBox.Value, 0, duration) : 0;
        var end = double.IsFinite(TrimEndBox.Value) ? Math.Clamp(TrimEndBox.Value, 0, duration) : duration;
        var position = playheadSeconds ?? _player?.GetNumber("time-pos") ?? 0;
        position = Math.Clamp(position, 0, duration);
        var startX = pad + start / duration * trackWidth;
        var endX = pad + end / duration * trackWidth;
        var playheadX = pad + position / duration * trackWidth;

        SetTimelineElement(TrimTimelineBase, pad, trackY, trackWidth, trackHeight);
        SetTimelineElement(TrimTimelineBefore, pad, trackY, Math.Max(0, startX - pad), trackHeight);
        SetTimelineElement(TrimTimelineSelection, startX, trackY, Math.Max(0, endX - startX), trackHeight);
        SetTimelineElement(TrimTimelineAfter, endX, trackY, Math.Max(0, pad + trackWidth - endX), trackHeight);
        PositionThumbnailStrip(trackWidth);
        SetTimelineElement(TrimStartHandle, startX - TrimStartHandle.Width / 2, 8, TrimStartHandle.Width, TrimStartHandle.Height);
        SetTimelineElement(TrimEndHandle, endX - TrimEndHandle.Width / 2, 8, TrimEndHandle.Width, TrimEndHandle.Height);
        SetTimelineElement(TrimPlayheadLine, playheadX - TrimPlayheadLine.Width / 2, 1, TrimPlayheadLine.Width, TrimPlayheadLine.Height);
        SetTimelineElement(TrimPlayheadKnob, playheadX - TrimPlayheadKnob.Width / 2, 0, TrimPlayheadKnob.Width, TrimPlayheadKnob.Height);

        TrimStartReadout.Text = $"開始 {FormatTime(start)}";
        TrimPlayheadReadout.Text = $"再生位置 {FormatTime(position)}";
        TrimEndReadout.Text = $"終了 {FormatTime(end)}";
    }

    private void PositionThumbnailStrip(double width)
    {
        TrimThumbnailStrip.Width = width;
        TrimThumbnailStrip.Height = 52;
        if (_thumbnailBitmaps.Count == 0 || _thumbnailFrameCount <= 0 || width <= 0)
        {
            TrimThumbnailStrip.Children.Clear();
            return;
        }

        var tiles = TrimThumbnailStrip.Children.OfType<Image>().ToArray();
        if (tiles.Length != _thumbnailFrameCount)
        {
            TrimThumbnailStrip.Children.Clear();
            for (var index = 0; index < _thumbnailFrameCount; index++)
            {
                TrimThumbnailStrip.Children.Add(new Image
                {
                    Source = _thumbnailBitmaps[index],
                    Stretch = Stretch.UniformToFill,
                    IsHitTestVisible = false
                });
            }

            tiles = TrimThumbnailStrip.Children.OfType<Image>().ToArray();
        }

        var cellWidth = width / _thumbnailFrameCount;
        for (var index = 0; index < tiles.Length; index++)
        {
            tiles[index].Width = cellWidth;
            tiles[index].Height = 52;
            Canvas.SetLeft(tiles[index], index * cellWidth);
            Canvas.SetTop(tiles[index], 0);
        }
    }

    private async Task LoadTrimThumbnailsAsync(bool force = false)
    {
        if (_loadedPath is not { } sourcePath || _mediaInfo is not { } mediaInfo ||
            !double.IsFinite(mediaInfo.DurationSeconds) || mediaInfo.DurationSeconds <= 0)
        {
            return;
        }

        var stripWidth = Math.Max(0, TrimTimelineCanvas.ActualWidth - 16);
        if (stripWidth <= 0)
        {
            return;
        }

        var rotation = _additionalRotationDegreesClockwise;
        if (!force && string.Equals(_thumbnailSourcePath, sourcePath, StringComparison.OrdinalIgnoreCase) &&
            _thumbnailRotation == rotation && TrimThumbnailStrip.Children.Count > 0)
        {
            return;
        }

        CancelThumbnailGeneration();
        TrimThumbnailStrip.Children.Clear();
        _thumbnailBitmaps = Array.Empty<BitmapImage>();
        _thumbnailFrameCount = 0;
        var cancellation = new CancellationTokenSource();
        _thumbnailCancellation = cancellation;
        string[]? thumbnailPaths = null;
        try
        {
            var count = Math.Clamp((int)Math.Round(stripWidth / 72), 2, 12);
            count = Math.Min(count, Math.Max(1, (int)Math.Ceiling(mediaInfo.DurationSeconds * 24)));
            thumbnailPaths = await GenerateTrimThumbnailStripAsync(sourcePath, mediaInfo.DurationSeconds, rotation, count, cancellation.Token);
            if (thumbnailPaths is null || cancellation.IsCancellationRequested || _isClosing ||
                EditorPanel.Visibility != Visibility.Visible ||
                !string.Equals(_loadedPath, sourcePath, StringComparison.OrdinalIgnoreCase) ||
                _additionalRotationDegreesClockwise != rotation)
            {
                return;
            }

            var bitmaps = new List<BitmapImage>(thumbnailPaths.Length);
            foreach (var thumbnailPath in thumbnailPaths)
            {
                var file = await StorageFile.GetFileFromPathAsync(thumbnailPath);
                var bitmap = new BitmapImage();
                using (var stream = await file.OpenReadAsync())
                {
                    await bitmap.SetSourceAsync(stream);
                }

                bitmaps.Add(bitmap);
            }

            if (cancellation.IsCancellationRequested || _isClosing || EditorPanel.Visibility != Visibility.Visible ||
                !string.Equals(_loadedPath, sourcePath, StringComparison.OrdinalIgnoreCase) ||
                _additionalRotationDegreesClockwise != rotation)
            {
                return;
            }

            _thumbnailBitmaps = bitmaps;
            _thumbnailFrameCount = bitmaps.Count;
            _thumbnailSourcePath = sourcePath;
            _thumbnailRotation = rotation;
            PositionThumbnailStrip(Math.Max(0, TrimTimelineCanvas.ActualWidth - 16));
        }
        catch (OperationCanceledException)
        {
            // Loading a new video, rotation, or editor state supersedes this strip.
        }
        catch
        {
            // Keep the real timeline rail available if ffmpeg cannot decode a still frame.
        }
        finally
        {
            if (thumbnailPaths is not null)
            {
                foreach (var thumbnailPath in thumbnailPaths)
                {
                    try
                    {
                        File.Delete(thumbnailPath);
                    }
                    catch
                    {
                        // A leftover temp image must not affect editing.
                    }
                }
            }

            if (ReferenceEquals(_thumbnailCancellation, cancellation))
            {
                _thumbnailCancellation = null;
                cancellation.Dispose();
            }
        }
    }

    private async Task<string[]?> GenerateTrimThumbnailStripAsync(string sourcePath, double durationSeconds, int rotation, int count, CancellationToken cancellationToken)
    {
        var ffmpegPath = Path.Combine(AppContext.BaseDirectory, "Media", "ffmpeg.exe");
        if (!File.Exists(ffmpegPath))
        {
            return null;
        }

        var outputPaths = Enumerable.Range(0, count)
            .Select(_ => Path.Combine(Path.GetTempPath(), $"FrameDock-trim-{Guid.NewGuid():N}.png"))
            .ToArray();
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-nostdin");
        startInfo.ArgumentList.Add("-nostats");

        for (var index = 0; index < count; index++)
        {
            var timestamp = durationSeconds * (index + 0.5) / count;
            startInfo.ArgumentList.Add("-ss");
            startInfo.ArgumentList.Add(timestamp.ToString("0.###", CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("-threads");
            startInfo.ArgumentList.Add("1");
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(sourcePath);
        }

        var filters = new List<string>();
        for (var index = 0; index < count; index++)
        {
            var frameFilters = new List<string>();
            switch (rotation)
            {
                case 90:
                    frameFilters.Add("transpose=clock");
                    break;
                case 180:
                    frameFilters.Add("hflip,vflip");
                    break;
                case 270:
                    frameFilters.Add("transpose=cclock");
                    break;
            }

            frameFilters.Add("scale=128:72:force_original_aspect_ratio=decrease");
            frameFilters.Add("pad=128:72:(ow-iw)/2:(oh-ih)/2");
            frameFilters.Add("setsar=1");
            filters.Add($"[{index}:v:0]{string.Join(',', frameFilters)}[thumb{index}]");
        }

        startInfo.ArgumentList.Add("-filter_complex");
        startInfo.ArgumentList.Add(string.Join(';', filters));
        for (var index = 0; index < count; index++)
        {
            startInfo.ArgumentList.Add("-map");
            startInfo.ArgumentList.Add($"[thumb{index}]");
            startInfo.ArgumentList.Add("-frames:v");
            startInfo.ArgumentList.Add("1");
            startInfo.ArgumentList.Add("-y");
            startInfo.ArgumentList.Add(outputPaths[index]);
        }

        try
        {
            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                return null;
            }

            using var cancellationRegistration = cancellationToken.Register(() =>
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
            });

            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            _ = await errorTask;
            cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode != 0 || outputPaths.Any(path => !File.Exists(path)))
            {
                DeleteThumbnailFiles(outputPaths);
                return null;
            }

            return outputPaths;
        }
        catch
        {
            DeleteThumbnailFiles(outputPaths);
            throw;
        }
    }

    private static void DeleteThumbnailFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
                // A leftover temp image must not affect editing.
            }
        }
    }

    private void CancelThumbnailGeneration()
    {
        var cancellation = _thumbnailCancellation;
        _thumbnailCancellation = null;
        if (cancellation is not null)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }
    }

    private static void SetTimelineElement(FrameworkElement element, double x, double y, double width, double height)
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        element.Width = Math.Max(0, width);
        element.Height = Math.Max(0, height);
    }

    private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_isRestoringSettings)
        {
            return;
        }

        _settings.Volume = e.NewValue;
        try
        {
            _player?.SetVolume(e.NewValue);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }

        ScheduleSettingsSave();
    }

    private void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        _isMuted = !_isMuted;
        _settings.Muted = _isMuted;
        try
        {
            _player?.SetMuted(_isMuted);
            UpdateMuteButton();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }

        ScheduleSettingsSave();
    }

    private void UpdateMuteButton()
    {
        MuteIcon.Glyph = _isMuted ? "\uE74F" : "\uE767";
        MuteButton.SetValue(Microsoft.UI.Xaml.Automation.AutomationProperties.NameProperty, _isMuted ? "ミュートを解除" : "ミュート");
    }

    private void SpeedMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleMenuFlyoutItem item ||
            !double.TryParse(item.Tag?.ToString(), CultureInfo.InvariantCulture, out var speed))
        {
            return;
        }

        _settings.Speed = speed;
        UpdateSpeedMenu(speed);
        try
        {
            _player?.SetSpeed(speed);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }

        UpdateApproximateCopyAvailability();
        ValidateEditRange();
        ScheduleSettingsSave();
    }

    private void UpdateSpeedMenu(double speed)
    {
        foreach (var item in SpeedMenuItem.Items.OfType<ToggleMenuFlyoutItem>())
        {
            item.IsChecked = double.TryParse(item.Tag?.ToString(), CultureInfo.InvariantCulture, out var itemSpeed) &&
                Math.Abs(itemSpeed - speed) < 0.001;
        }

        SpeedMenuItem.Text = $"再生速度: {speed.ToString("0.##", CultureInfo.InvariantCulture)}×";
        UpdateEditSpeedControl(speed);
    }

    private void UpdateEditSpeedControl(double speed)
    {
        var label = $"{speed.ToString("0.##", CultureInfo.InvariantCulture)}×";
        EditSpeedText.Text = label;
        ToolTipService.SetToolTip(EditSpeedButton, $"プレビューと書き出しの速度: {label}");
        EditSpeedButton.SetValue(Microsoft.UI.Xaml.Automation.AutomationProperties.NameProperty, $"編集速度 {label}");
        if (EditSpeedButton.Flyout is MenuFlyout flyout)
        {
            foreach (var item in flyout.Items.OfType<ToggleMenuFlyoutItem>())
            {
                item.IsChecked = double.TryParse(item.Tag?.ToString(), CultureInfo.InvariantCulture, out var itemSpeed) &&
                    Math.Abs(itemSpeed - speed) < 0.001;
            }
        }
    }

    private void UpdateSkipLabels()
    {
        var seconds = _settings.SkipSeconds.ToString("0.##", CultureInfo.InvariantCulture);
        BackSkipText.Text = seconds;
        ForwardSkipText.Text = seconds;
        var backLabel = $"{seconds}秒戻る";
        var forwardLabel = $"{seconds}秒進む";
        ToolTipService.SetToolTip(BackButton, $"{backLabel} (←)");
        ToolTipService.SetToolTip(ForwardButton, $"{forwardLabel} (→)");
        BackButton.SetValue(Microsoft.UI.Xaml.Automation.AutomationProperties.NameProperty, backLabel);
        ForwardButton.SetValue(Microsoft.UI.Xaml.Automation.AutomationProperties.NameProperty, forwardLabel);
    }

    private void ScheduleSettingsSave()
    {
        if (_isClosing || _settingsSaveTimer is null)
        {
            return;
        }

        _settingsSaveTimer.Stop();
        _settingsSaveTimer.Start();
    }

    private async Task SaveSettingsAsync()
    {
        try
        {
            await _settings.SaveAsync();
        }
        catch (Exception ex)
        {
            if (!_isClosing)
            {
                ShowError($"設定を保存できませんでした: {ex.Message}");
            }
        }
    }

    private async void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var skipBox = new NumberBox
        {
            Header = "左右に移動する秒数",
            Minimum = 1,
            Maximum = 600,
            Value = _settings.SkipSeconds,
            SmallChange = 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact
        };
        var dialog = new ContentDialog
        {
            Title = "再生設定",
            Content = skipBox,
            PrimaryButtonText = "保存",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot
        };
        _isSettingsDialogOpen = true;
        ShowControls();
        try
        {
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                if (!double.IsFinite(skipBox.Value) || skipBox.Value < 1 || skipBox.Value > 600)
                {
                    ShowError("移動する秒数は 1〜600 の範囲で指定してください。");
                    return;
                }

                _settings.SkipSeconds = skipBox.Value;
                UpdateSkipLabels();
                await SaveSettingsAsync();
            }
        }
        finally
        {
            _isSettingsDialogOpen = false;
            ScheduleControlsHide();
        }
    }

    private void FullscreenButton_Click(object sender, RoutedEventArgs e)
    {
        if (_displayMode == PlayerDisplayMode.Fullscreen)
        {
            ApplyDisplayMode(_modeBeforeFullscreen, persist: true, revealControls: true);
        }
        else
        {
            ApplyDisplayMode(PlayerDisplayMode.Fullscreen, persist: true, revealControls: true);
        }
    }

    private void DisplayModeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleMenuFlyoutItem item ||
            !Enum.TryParse<PlayerDisplayMode>(item.Tag?.ToString(), out var mode))
        {
            return;
        }

        ApplyDisplayMode(mode, persist: true, revealControls: true);
    }

    private void ApplyDisplayMode(PlayerDisplayMode mode, bool persist, bool revealControls)
    {
        if (mode == PlayerDisplayMode.Fullscreen && _displayMode != PlayerDisplayMode.Fullscreen)
        {
            _modeBeforeFullscreen = _displayMode;
        }

        _displayMode = mode;
        _isFullscreen = mode == PlayerDisplayMode.Fullscreen;

        _isApplyingDisplayMode = true;
        try
        {
            if (_appWindow is not null)
            {
                if (_isFullscreen)
                {
                    _appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
                }
                else
                {
                    _appWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
                    if (_appWindow.Presenter is OverlappedPresenter presenter)
                    {
                        if (mode == PlayerDisplayMode.MaximizedOverlay)
                        {
                            presenter.Maximize();
                        }
                        else
                        {
                            presenter.Restore();
                        }
                    }
                }
            }
        }
        finally
        {
            _isApplyingDisplayMode = false;
        }

        var controlsAreReserved = mode == PlayerDisplayMode.AlwaysVisible;
        ControlsBorder.SetValue(Grid.RowProperty, controlsAreReserved ? 2 : 0);
        ControlsBorder.VerticalAlignment = controlsAreReserved ? VerticalAlignment.Stretch : VerticalAlignment.Bottom;

        if (controlsAreReserved || revealControls)
        {
            ShowControls();
            if (controlsAreReserved)
            {
                _controlsHideTimer?.Stop();
            }
            else
            {
                ScheduleControlsHide();
            }
        }
        else
        {
            _controlsHideTimer?.Stop();
            ControlsBorder.Visibility = Visibility.Collapsed;
        }

        FullscreenIcon.Glyph = _isFullscreen ? "\uE73F" : "\uE740";
        var fullscreenLabel = _isFullscreen ? "全画面を終了 (Esc)" : "全画面表示 (F)";
        ToolTipService.SetToolTip(FullscreenButton, fullscreenLabel);
        FullscreenButton.SetValue(Microsoft.UI.Xaml.Automation.AutomationProperties.NameProperty, fullscreenLabel);
        UpdateDisplayModeMenu();
        ScheduleCompositionResize();

        if (persist)
        {
            _settings.DisplayMode = mode;
            ScheduleSettingsSave();
        }
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (_isClosing || _isApplyingDisplayMode || (!args.DidPresenterChange && !args.DidSizeChange) ||
            _appWindow?.Presenter is not OverlappedPresenter presenter)
        {
            return;
        }

        var nativeMode = presenter.State switch
        {
            OverlappedPresenterState.Maximized => PlayerDisplayMode.MaximizedOverlay,
            OverlappedPresenterState.Restored => PlayerDisplayMode.AlwaysVisible,
            _ => (PlayerDisplayMode?)null
        };
        if (nativeMode.HasValue && nativeMode.Value != _displayMode)
        {
            ApplyDisplayMode(nativeMode.Value, persist: true, revealControls: true);
        }
    }

    private void UpdateDisplayModeMenu()
    {
        AlwaysVisibleModeItem.IsChecked = _displayMode == PlayerDisplayMode.AlwaysVisible;
        MaximizedOverlayModeItem.IsChecked = _displayMode == PlayerDisplayMode.MaximizedOverlay;
        FullscreenModeItem.IsChecked = _displayMode == PlayerDisplayMode.Fullscreen;
        DisplayModeMenuItem.Text = _displayMode switch
        {
            PlayerDisplayMode.AlwaysVisible => "表示モード: 常時表示",
            PlayerDisplayMode.MaximizedOverlay => "表示モード: 最大化（タスクバー表示）",
            PlayerDisplayMode.Fullscreen => "表示モード: 全画面表示",
            _ => "表示モード"
        };
    }

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!e.Handled)
        {
            ShowControls();
            ScheduleControlsHide();
        }

        if (e.Handled || IsTextEntryFocused())
        {
            return;
        }

        var control = (GetKeyState(VirtualKeyControl) & 0x8000) != 0;
        if (control && e.Key == Windows.System.VirtualKey.O)
        {
            OpenButton_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (control && e.Key == Windows.System.VirtualKey.S)
        {
            SaveFrameButton_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if ((int)e.Key == 0xBC)
        {
            StepFrame(backwards: true);
            e.Handled = true;
            return;
        }

        if ((int)e.Key == 0xBE)
        {
            StepFrame(backwards: false);
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Windows.System.VirtualKey.Space:
                PlayButton_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Left:
                SeekRelative(-_settings.SkipSeconds);
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Right:
                SeekRelative(_settings.SkipSeconds);
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.F:
                FullscreenButton_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Escape when _isCropMode:
                SetCropMode(false);
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Escape when _isFullscreen:
                FullscreenButton_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
        }
    }

    private bool IsTextEntryFocused()
    {
        var focused = FocusManager.GetFocusedElement(RootGrid.XamlRoot) as DependencyObject;
        while (focused is not null)
        {
            if (focused is TextBox or PasswordBox or ComboBox or Slider or NumberBox)
            {
                return true;
            }

            focused = VisualTreeHelper.GetParent(focused);
        }

        return false;
    }

    private void EditButton_Click(object sender, RoutedEventArgs e)
    {
        if (_mediaInfo is null)
        {
            ShowError("編集できる動画を開いてください。");
            return;
        }

        var showEditor = EditorPanel.Visibility != Visibility.Visible;
        EditorPanel.Visibility = showEditor ? Visibility.Visible : Visibility.Collapsed;
        SetEditorLayout(showEditor);
        if (showEditor)
        {
            if (!_editSessionStarted)
            {
                _editingPreviousSpeed = _settings.Speed;
                _editSessionStarted = true;
            }

            ShowControls();
            ValidateEditRange();
            UpdateTrimTimeline();
            UpdateApproximateCopyAvailability();
            _ = LoadTrimThumbnailsAsync();
        }
        else
        {
            SetCropMode(false);
            CancelThumbnailGeneration();
            ShowControls();
            ScheduleControlsHide();
        }

        UpdateCropOverlay();
    }

    private void SetEditorLayout(bool editing)
    {
        if (RootGrid.RowDefinitions.Count < 2)
        {
            return;
        }

        RootGrid.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
        RootGrid.RowDefinitions[1].Height = GridLength.Auto;
        UpdateCropOverlay();
    }

    private void CancelEditButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isExporting || _mediaInfo is null)
        {
            return;
        }

        SetCropMode(false);
        _crop = null;
        _cropDragOrigin = null;
        _activeCropHandle = null;
        _cropDragPointerOrigin = null;
        _cropDragHandleOrigin = null;
        _additionalRotationDegreesClockwise = 0;
        _isTrimTimelineDragging = false;
        TrimStartBox.Value = 0;
        TrimEndBox.Value = _mediaInfo?.DurationSeconds ?? 0;
        ApproximateCopyMenuItem.IsChecked = false;
        OutputContainerComboBox.SelectedIndex = 0;
        _outputDirectory = _loadedPath is null ? null : Path.GetDirectoryName(_loadedPath);
        OutputFolderText.Text = "元動画と同じフォルダー";
        ToolTipService.SetToolTip(OutputFolderText, "元動画と同じフォルダー");
        OutputNameBox.Text = _loadedPath is null ? string.Empty : $"{Path.GetFileNameWithoutExtension(_loadedPath)}-clip";

        try
        {
            _player?.SetVideoRotation(0);
            _player?.SetSpeed(_editingPreviousSpeed);
        }
        catch (Exception ex)
        {
            ShowError($"プレビュー設定を戻せませんでした: {ex.Message}");
        }

        var speedChanged = Math.Abs(_settings.Speed - _editingPreviousSpeed) > 0.001;
        _settings.Speed = _editingPreviousSpeed;
        UpdateSpeedMenu(_settings.Speed);
        UpdateCropInfo();
        UpdateRotationControl();
        UpdateApproximateCopyAvailability();
        ValidateEditRange();
        UpdateCropOverlay();
        _editSessionStarted = false;
        EditorPanel.Visibility = Visibility.Collapsed;
        SetEditorLayout(false);
        CancelThumbnailGeneration();
        ShowControls();
        ScheduleControlsHide();
        if (speedChanged)
        {
            ScheduleSettingsSave();
        }
    }

    private void VideoRegion_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (_player is null)
        {
            return;
        }

        var menu = new MenuFlyout();
        menu.Opened += (_, _) => ControlsFlyoutOpened();
        menu.Closed += (_, _) => ControlsFlyoutClosed();
        var copyItem = new MenuFlyoutItem { Text = "表示中のフレームをコピー" };
        copyItem.Icon = new FontIcon { Glyph = "\uE8C8" };
        copyItem.Click += CopyFrameButton_Click;
        menu.Items.Add(copyItem);
        menu.ShowAt(VideoRegion, e.GetPosition(VideoRegion));
        e.Handled = true;
    }

    private void TrimBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => ValidateEditRange();

    private void MarkTrimStartButton_Click(object sender, RoutedEventArgs e)
    {
        var position = _player?.GetNumber("time-pos");
        if (position.HasValue && double.IsFinite(position.Value) && _mediaInfo is { DurationSeconds: > 0 } mediaInfo)
        {
            var end = double.IsFinite(TrimEndBox.Value) ? TrimEndBox.Value : mediaInfo.DurationSeconds;
            var latestStart = Math.Floor(Math.Max(0, end - 0.01) * 100) / 100;
            var roundedPosition = Math.Round(Math.Clamp(position.Value, 0, mediaInfo.DurationSeconds), 2, MidpointRounding.AwayFromZero);
            TrimStartBox.Value = Math.Clamp(roundedPosition, 0, latestStart);
        }
    }

    private void MarkTrimEndButton_Click(object sender, RoutedEventArgs e)
    {
        var position = _player?.GetNumber("time-pos");
        if (position.HasValue && double.IsFinite(position.Value) && _mediaInfo is { DurationSeconds: > 0 } mediaInfo)
        {
            var start = double.IsFinite(TrimStartBox.Value) ? TrimStartBox.Value : 0;
            var earliestEnd = Math.Ceiling(Math.Min(mediaInfo.DurationSeconds, start + 0.01) * 100) / 100;
            var latestEnd = Math.Floor(mediaInfo.DurationSeconds * 100) / 100;
            var roundedPosition = Math.Round(Math.Clamp(position.Value, 0, mediaInfo.DurationSeconds), 2, MidpointRounding.AwayFromZero);
            TrimEndBox.Value = Math.Clamp(roundedPosition, Math.Min(earliestEnd, latestEnd), latestEnd);
        }
    }

    private void ValidateEditRange()
    {
        if (_mediaInfo is null)
        {
            ExportButton.IsEnabled = false;
            EditRangeText.Text = "開始と終了を確認してください";
            UpdateTrimTimeline();
            return;
        }

        var start = TrimStartBox.Value;
        var end = TrimEndBox.Value;
        var valid = double.IsFinite(start) && double.IsFinite(end) && start >= 0 && end > start && end <= _mediaInfo.DurationSeconds;
        ExportButton.IsEnabled = valid && !_isExporting;
        if (valid)
        {
            EditRangeText.Text = $"{FormatTime(start)} から {FormatTime(end)} まで";
        }
        else
        {
            EditRangeText.Text = "開始は 0 以上、終了は開始より後にしてください";
        }

        UpdateTrimTimeline();
    }

    private void CropModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_mediaInfo is null)
        {
            return;
        }

        if (!_isCropMode && _additionalRotationDegreesClockwise != 0)
        {
            ShowNotice("クロップ範囲を編集するには、回転を 0° に戻してください。", InfoBarSeverity.Informational);
            return;
        }

        if (!_isCropMode && _crop is null)
        {
            var evenWidth = _mediaInfo.DisplayWidth & ~1;
            var evenHeight = _mediaInfo.DisplayHeight & ~1;
            if (evenWidth < 2 || evenHeight < 2)
            {
                ShowError("この動画の表示サイズではクロップできません。");
                return;
            }

            _crop = new CropRect(0, 0, evenWidth, evenHeight);
            UpdateCropInfo();
            UpdateApproximateCopyAvailability();
        }

        SetCropMode(!_isCropMode);
    }

    private void SetCropMode(bool enabled)
    {
        _isCropMode = enabled && _mediaInfo is not null;
        if (!_isCropMode)
        {
            _activeCropHandle = null;
            _cropDragOrigin = null;
            _cropDragPointerOrigin = null;
            _cropDragHandleOrigin = null;
        }
        CropModeButtonText.Text = _isCropMode ? "完了" : "クロップ";
        CropModeButton.SetValue(Microsoft.UI.Xaml.Automation.AutomationProperties.NameProperty, _isCropMode ? "クロップ編集を完了" : "クロップ範囲を調整");
        ToolTipService.SetToolTip(CropModeButton, _isCropMode
            ? "8つのハンドルをドラッグして範囲を調整。もう一度押すと確定"
            : "四隅と四辺の8つのハンドルで範囲を調整");
        CropOverlayCanvas.IsHitTestVisible = _isCropMode;
        CropOverlayCanvas.Visibility = _isCropMode || (_crop.HasValue && EditorPanel.Visibility == Visibility.Visible)
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (_isCropMode)
        {
            CropOverlayCanvas.SizeChanged += CropOverlayCanvas_SizeChanged;
            UpdateCropOverlay();
        }
        else
        {
            CropOverlayCanvas.SizeChanged -= CropOverlayCanvas_SizeChanged;
        }

        UpdateCropOverlay();
    }

    private void CropOverlayCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateCropOverlay();

    private void CropResetButton_Click(object sender, RoutedEventArgs e)
    {
        SetCropMode(false);
        _crop = null;
        UpdateCropInfo();
        UpdateApproximateCopyAvailability();
        UpdateCropOverlay();
    }

    private void RotateButton_Click(object sender, RoutedEventArgs e)
    {
        var previous = _additionalRotationDegreesClockwise;
        _additionalRotationDegreesClockwise = (_additionalRotationDegreesClockwise + 90) % 360;
        SetCropMode(false);
        try
        {
            _player?.SetVideoRotation(_additionalRotationDegreesClockwise);
        }
        catch (Exception ex)
        {
            _additionalRotationDegreesClockwise = previous;
            try
            {
                _player?.SetVideoRotation(previous);
            }
            catch
            {
                // Keep the original error visible; restoring the previous preview is best effort.
            }

            ShowError($"プレビューを回転できませんでした: {ex.Message}");
        }

        UpdateRotationControl();
        UpdateCropOverlay();
        UpdateApproximateCopyAvailability();
        ValidateEditRange();
        _ = LoadTrimThumbnailsAsync(force: true);
    }

    private void UpdateRotationControl()
    {
        RotateButtonText.Text = $"{_additionalRotationDegreesClockwise}°";
        RotateButton.SetValue(Microsoft.UI.Xaml.Automation.AutomationProperties.NameProperty, $"時計回りに回転: {_additionalRotationDegreesClockwise} 度");
        ToolTipService.SetToolTip(RotateButton, $"プレビューと書き出しを時計回りに回転（現在 {_additionalRotationDegreesClockwise}°）");
        CropModeButton.IsEnabled = _mediaInfo is not null && _additionalRotationDegreesClockwise == 0;
        ToolTipService.SetToolTip(CropModeButton, _additionalRotationDegreesClockwise == 0
            ? "四隅と四辺の8つのハンドルで範囲を調整"
            : "クロップを編集するには、回転を 0° に戻してください");
    }

    private void UpdateApproximateCopyAvailability()
    {
        var hasTransforms = _crop.HasValue || _additionalRotationDegreesClockwise != 0 || Math.Abs(_settings.Speed - 1) > 0.001;
        if (hasTransforms && ApproximateCopyMenuItem.IsChecked)
        {
            ApproximateCopyMenuItem.IsChecked = false;
        }

        ApproximateCopyMenuItem.IsEnabled = _mediaInfo is not null && !hasTransforms && !_isExporting;
        ExportOptionsButton.IsEnabled = !_isExporting;
        CancelEditButton.IsEnabled = !_isExporting;
    }

    private void CropOverlayCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_isCropMode || _mediaInfo is null || _crop is not { } crop || _additionalRotationDegreesClockwise != 0)
        {
            return;
        }

        var point = e.GetCurrentPoint(CropOverlayCanvas).Position;
        var handles = GetInsetCropHandlePoints(crop);
        var nearest = handles
            .Select(item => (item.Handle, DistanceSquared: Math.Pow(item.Position.X - point.X, 2) + Math.Pow(item.Position.Y - point.Y, 2)))
            .OrderBy(item => item.DistanceSquared)
            .First();
        if (nearest.DistanceSquared > 22 * 22)
        {
            return;
        }

        CropOverlayCanvas.CapturePointer(e.Pointer);
        _activeCropHandle = nearest.Handle;
        _cropDragOrigin = crop;
        _cropDragPointerOrigin = point;
        _cropDragHandleOrigin = GetCropHandlePoints(crop).First(item => item.Handle == nearest.Handle).Position;
        e.Handled = true;
    }

    private void CropOverlayCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_activeCropHandle is not { } handle || _cropDragOrigin is not { } origin || _mediaInfo is null)
        {
            return;
        }

        ApplyCropHandleDrag(handle, origin, GetCropDragPoint(e.GetCurrentPoint(CropOverlayCanvas).Position));
        e.Handled = true;
    }

    private void CropOverlayCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_activeCropHandle is not { } handle || _cropDragOrigin is not { } origin)
        {
            return;
        }

        var point = GetCropDragPoint(e.GetCurrentPoint(CropOverlayCanvas).Position);
        ApplyCropHandleDrag(handle, origin, point);
        CropOverlayCanvas.ReleasePointerCapture(e.Pointer);
        _activeCropHandle = null;
        _cropDragOrigin = null;
        _cropDragPointerOrigin = null;
        _cropDragHandleOrigin = null;
        e.Handled = true;
    }

    private void CropOverlayCanvas_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (_cropDragOrigin is { } originalCrop)
        {
            _crop = originalCrop;
        }

        _activeCropHandle = null;
        _cropDragOrigin = null;
        _cropDragPointerOrigin = null;
        _cropDragHandleOrigin = null;
        UpdateCropInfo();
        UpdateApproximateCopyAvailability();
        UpdateCropOverlay();
    }

    private Windows.Foundation.Rect GetVideoContentBounds()
    {
        if (_mediaInfo is null || CropOverlayCanvas.ActualWidth <= 0 || CropOverlayCanvas.ActualHeight <= 0)
        {
            return new Windows.Foundation.Rect(0, 0, CropOverlayCanvas.ActualWidth, CropOverlayCanvas.ActualHeight);
        }

        var (previewWidth, previewHeight) = GetPreviewDimensions();
        var contentAspect = (double)previewWidth / previewHeight;
        var panelAspect = CropOverlayCanvas.ActualWidth / CropOverlayCanvas.ActualHeight;
        if (panelAspect > contentAspect)
        {
            var width = CropOverlayCanvas.ActualHeight * contentAspect;
            var left = (CropOverlayCanvas.ActualWidth - width) / 2;
            return new Windows.Foundation.Rect(left, 0, width, CropOverlayCanvas.ActualHeight);
        }

        var height = CropOverlayCanvas.ActualWidth / contentAspect;
        var top = (CropOverlayCanvas.ActualHeight - height) / 2;
        return new Windows.Foundation.Rect(0, top, CropOverlayCanvas.ActualWidth, height);
    }

    private (int Width, int Height) GetPreviewDimensions()
    {
        if (_mediaInfo is null || _additionalRotationDegreesClockwise is 90 or 270)
        {
            return (_mediaInfo?.DisplayHeight ?? 1, _mediaInfo?.DisplayWidth ?? 1);
        }

        return (_mediaInfo?.DisplayWidth ?? 1, _mediaInfo?.DisplayHeight ?? 1);
    }

    private CropRect GetPreviewCrop(CropRect crop)
    {
        if (_mediaInfo is null)
        {
            return crop;
        }

        var width = _mediaInfo.DisplayWidth;
        var height = _mediaInfo.DisplayHeight;
        return _additionalRotationDegreesClockwise switch
        {
            90 => new CropRect(height - crop.Y - crop.Height, crop.X, crop.Height, crop.Width),
            180 => new CropRect(width - crop.X - crop.Width, height - crop.Y - crop.Height, crop.Width, crop.Height),
            270 => new CropRect(crop.Y, width - crop.X - crop.Width, crop.Height, crop.Width),
            _ => crop
        };
    }

    private (CropDragHandle Handle, Windows.Foundation.Point Position)[] GetCropHandlePoints(CropRect crop)
    {
        var bounds = GetVideoContentBounds();
        var previewCrop = GetPreviewCrop(crop);
        var (previewWidth, previewHeight) = GetPreviewDimensions();
        var x = bounds.Left + previewCrop.X * bounds.Width / previewWidth;
        var y = bounds.Top + previewCrop.Y * bounds.Height / previewHeight;
        var width = previewCrop.Width * bounds.Width / previewWidth;
        var height = previewCrop.Height * bounds.Height / previewHeight;
        return new[]
        {
            (CropDragHandle.TopLeft, new Windows.Foundation.Point(x, y)),
            (CropDragHandle.Top, new Windows.Foundation.Point(x + width / 2, y)),
            (CropDragHandle.TopRight, new Windows.Foundation.Point(x + width, y)),
            (CropDragHandle.Right, new Windows.Foundation.Point(x + width, y + height / 2)),
            (CropDragHandle.BottomRight, new Windows.Foundation.Point(x + width, y + height)),
            (CropDragHandle.Bottom, new Windows.Foundation.Point(x + width / 2, y + height)),
            (CropDragHandle.BottomLeft, new Windows.Foundation.Point(x, y + height)),
            (CropDragHandle.Left, new Windows.Foundation.Point(x, y + height / 2))
        };
    }

    private (CropDragHandle Handle, Windows.Foundation.Point Position)[] GetInsetCropHandlePoints(CropRect crop)
    {
        const double inset = 10;
        var minX = Math.Min(inset, CropOverlayCanvas.ActualWidth / 2);
        var maxX = Math.Max(minX, CropOverlayCanvas.ActualWidth - inset);
        var minY = Math.Min(inset, CropOverlayCanvas.ActualHeight / 2);
        var maxY = Math.Max(minY, CropOverlayCanvas.ActualHeight - inset);
        return GetCropHandlePoints(crop)
            .Select(item => (item.Handle, new Windows.Foundation.Point(
                Math.Clamp(item.Position.X, minX, maxX),
                Math.Clamp(item.Position.Y, minY, maxY))))
            .ToArray();
    }

    private Windows.Foundation.Point GetCropDragPoint(Windows.Foundation.Point pointer)
    {
        if (_cropDragPointerOrigin is not { } pointerOrigin || _cropDragHandleOrigin is not { } handleOrigin)
        {
            return pointer;
        }

        return new Windows.Foundation.Point(
            handleOrigin.X + pointer.X - pointerOrigin.X,
            handleOrigin.Y + pointer.Y - pointerOrigin.Y);
    }

    private void ApplyCropHandleDrag(CropDragHandle handle, CropRect origin, Windows.Foundation.Point point)
    {
        if (_mediaInfo is null)
        {
            return;
        }

        var bounds = GetVideoContentBounds();
        var maxWidth = _mediaInfo.DisplayWidth & ~1;
        var maxHeight = _mediaInfo.DisplayHeight & ~1;
        if (bounds.Width <= 0 || bounds.Height <= 0 || maxWidth < 2 || maxHeight < 2)
        {
            return;
        }

        var pixelX = Math.Clamp((point.X - bounds.Left) / bounds.Width * _mediaInfo.DisplayWidth, 0, maxWidth);
        var pixelY = Math.Clamp((point.Y - bounds.Top) / bounds.Height * _mediaInfo.DisplayHeight, 0, maxHeight);
        var left = origin.X;
        var top = origin.Y;
        var right = origin.X + origin.Width;
        var bottom = origin.Y + origin.Height;
        if (handle is CropDragHandle.TopLeft or CropDragHandle.Left or CropDragHandle.BottomLeft)
        {
            left = Math.Clamp(EvenFloor(pixelX), 0, right - 2);
        }

        if (handle is CropDragHandle.TopRight or CropDragHandle.Right or CropDragHandle.BottomRight)
        {
            right = Math.Clamp(EvenCeiling(pixelX), left + 2, maxWidth);
        }

        if (handle is CropDragHandle.TopLeft or CropDragHandle.Top or CropDragHandle.TopRight)
        {
            top = Math.Clamp(EvenFloor(pixelY), 0, bottom - 2);
        }

        if (handle is CropDragHandle.BottomLeft or CropDragHandle.Bottom or CropDragHandle.BottomRight)
        {
            bottom = Math.Clamp(EvenCeiling(pixelY), top + 2, maxHeight);
        }

        var resized = new CropRect(left, top, right - left, bottom - top);
        try
        {
            MediaGeometry.ValidateCrop(resized, _mediaInfo.DisplayWidth, _mediaInfo.DisplayHeight);
            _crop = resized;
            UpdateCropInfo();
            UpdateApproximateCopyAvailability();
            UpdateCropOverlay();
        }
        catch (ExportValidationException ex)
        {
            ShowError(ex.UserMessage);
        }
    }

    private static int EvenFloor(double value) => Math.Max(0, (int)Math.Floor(value / 2) * 2);

    private static int EvenCeiling(double value) => checked((int)Math.Ceiling(value / 2) * 2);

    private void UpdateCropOverlay()
    {
        if (_mediaInfo is null || CropOverlayCanvas.ActualWidth <= 0 || CropOverlayCanvas.ActualHeight <= 0)
        {
            return;
        }

        var bounds = GetVideoContentBounds();
        if (_crop is not { } crop)
        {
            CropSelectionRectangle.Visibility = Visibility.Collapsed;
            CropMaskLeft.Visibility = Visibility.Collapsed;
            CropMaskTop.Visibility = Visibility.Collapsed;
            CropMaskRight.Visibility = Visibility.Collapsed;
            CropMaskBottom.Visibility = Visibility.Collapsed;
            foreach (var handle in GetCropHandleElements())
            {
                handle.Visibility = Visibility.Collapsed;
            }
            return;
        }

        var (previewWidth, previewHeight) = GetPreviewDimensions();
        var previewCrop = GetPreviewCrop(crop);
        var scaleX = bounds.Width / previewWidth;
        var scaleY = bounds.Height / previewHeight;
        var x = bounds.Left + previewCrop.X * scaleX;
        var y = bounds.Top + previewCrop.Y * scaleY;
        var width = previewCrop.Width * scaleX;
        var height = previewCrop.Height * scaleY;
        Canvas.SetLeft(CropSelectionRectangle, x);
        Canvas.SetTop(CropSelectionRectangle, y);
        CropSelectionRectangle.Width = width;
        CropSelectionRectangle.Height = height;
        CropSelectionRectangle.Visibility = Visibility.Visible;

        SetMask(CropMaskLeft, 0, 0, x, CropOverlayCanvas.ActualHeight);
        SetMask(CropMaskTop, x, 0, width, y);
        SetMask(CropMaskRight, x + width, 0, Math.Max(0, CropOverlayCanvas.ActualWidth - x - width), CropOverlayCanvas.ActualHeight);
        SetMask(CropMaskBottom, x, y + height, width, Math.Max(0, CropOverlayCanvas.ActualHeight - y - height));

        var showHandles = _isCropMode && _additionalRotationDegreesClockwise == 0;
        foreach (var item in GetInsetCropHandlePoints(crop).Zip(GetCropHandleElements()))
        {
            var border = item.Second;
            border.Visibility = showHandles ? Visibility.Visible : Visibility.Collapsed;
            if (showHandles)
            {
                Canvas.SetLeft(border, item.First.Position.X - border.Width / 2);
                Canvas.SetTop(border, item.First.Position.Y - border.Height / 2);
            }
        }
    }

    private Border[] GetCropHandleElements() =>
    [
        CropHandleTopLeft,
        CropHandleTop,
        CropHandleTopRight,
        CropHandleRight,
        CropHandleBottomRight,
        CropHandleBottom,
        CropHandleBottomLeft,
        CropHandleLeft
    ];

    private static void SetMask(Microsoft.UI.Xaml.Shapes.Rectangle rectangle, double x, double y, double width, double height)
    {
        Canvas.SetLeft(rectangle, x);
        Canvas.SetTop(rectangle, y);
        rectangle.Width = Math.Max(0, width);
        rectangle.Height = Math.Max(0, height);
        rectangle.Visibility = width > 0 && height > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateCropInfo()
    {
        CropResetButton.IsEnabled = _crop.HasValue;
        if (_crop is not { } crop)
        {
            CropInfoText.Text = _mediaInfo is null
                ? "クロップなし"
                : $"クロップなし · 表示 {_mediaInfo.DisplayWidth} × {_mediaInfo.DisplayHeight} px";
            return;
        }

        var aspect = (double)crop.Width / crop.Height;
        CropInfoText.Text = $"{crop.Width} × {crop.Height} px · {aspect.ToString("0.##", CultureInfo.InvariantCulture)}:1";
    }

    private async void OutputFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, _windowHandle);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            _outputDirectory = folder.Path;
            OutputFolderText.Text = folder.Path;
            ToolTipService.SetToolTip(OutputFolderText, folder.Path);
        }
    }

    private ExportContainer GetSelectedExportContainer() => OutputContainerComboBox.SelectedIndex switch
    {
        1 => ExportContainer.Mkv,
        2 => ExportContainer.Mov,
        _ => ExportContainer.Mp4
    };

    private static string GetExportExtension(ExportContainer container) => container switch
    {
        ExportContainer.Mkv => ".mkv",
        ExportContainer.Mov => ".mov",
        _ => ".mp4"
    };

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_mediaInfo is null || _loadedPath is null)
        {
            ShowError("編集できる動画を開いてください。");
            return;
        }

        var start = TrimStartBox.Value;
        var end = TrimEndBox.Value;
        if (!double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end <= start || end > _mediaInfo.DurationSeconds)
        {
            ShowError("開始時刻と終了時刻を確認してください。");
            return;
        }

        var mode = ApproximateCopyMenuItem.IsChecked ? ExportMode.StreamCopyApproximate : ExportMode.AccurateReencode;
        if (mode == ExportMode.AccurateReencode && _mediaInfo.IsHdr)
        {
            ShowError("HDR 動画の正確な再エンコードには対応していません。書き出しの詳細から「無変換で切り出し」を選ぶと元の色を保てます。");
            return;
        }

        if (mode == ExportMode.StreamCopyApproximate && _crop is not null)
        {
            ShowError("クロップと「無変換で切り出し」は併用できません。クロップを解除するか、通常の書き出しを選んでください。");
            return;
        }

        if (mode == ExportMode.StreamCopyApproximate &&
            (_additionalRotationDegreesClockwise != 0 || Math.Abs(_settings.Speed - 1) > 0.001))
        {
            ShowError("回転または再生速度の変更には正確な再エンコードが必要です。");
            return;
        }

        var fileStem = OutputNameBox.Text.Trim();
        foreach (var extension in new[] { ".mp4", ".mkv", ".mov" })
        {
            if (fileStem.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                fileStem = fileStem[..^extension.Length];
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(fileStem) || fileStem is "." or ".." || Path.GetFileName(fileStem) != fileStem || fileStem.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            ShowError("出力ファイル名には、ファイル名として使える文字を入力してください。");
            return;
        }

        var directory = _outputDirectory ?? Path.GetDirectoryName(_loadedPath)!;
        var outputContainer = GetSelectedExportContainer();
        var destination = Path.Combine(directory, fileStem + GetExportExtension(outputContainer));
        if (File.Exists(destination))
        {
            ShowError("同じ名前のファイルが保存先にあります。別の名前を指定してください。");
            return;
        }

        var request = new ExportRequest(
            _loadedPath,
            destination,
            start,
            end,
            _crop,
            mode,
            AdditionalRotationDegreesClockwise: _additionalRotationDegreesClockwise,
            PlaybackSpeed: _settings.Speed,
            OutputContainer: outputContainer);
        var exportCancellation = new CancellationTokenSource();
        _exportCancellation = exportCancellation;
        var token = exportCancellation.Token;
        _isExporting = true;
        UpdateApproximateCopyAvailability();
        NotificationBar.IsOpen = false;
        ExportProgressBar.Value = 0;
        ExportProgressBar.Visibility = Visibility.Visible;
        CancelExportButton.Visibility = Visibility.Visible;
        ExportButton.Content = "書き出し中…";
        OutputContainerComboBox.IsEnabled = false;
        ExportOptionsButton.IsEnabled = false;
        OutputFolderButton.IsEnabled = false;
        OutputNameBox.IsEnabled = false;
        ValidateEditRange();

        var progress = new Progress<ExportProgress>(item =>
        {
            if (_isClosing || token.IsCancellationRequested)
            {
                return;
            }

            ExportProgressBar.IsIndeterminate = item.Phase is ExportProgressPhase.Preparing or ExportProgressPhase.Verifying;
            ExportProgressBar.Value = Math.Clamp(item.Fraction, 0, 1);
            EditRangeText.Text = item.Message;
        });

        try
        {
            var result = await _exportService.ExportAsync(request, progress, token);
            if (!_isClosing && ReferenceEquals(_exportCancellation, exportCancellation))
            {
                var successMessage = $"書き出しました: {result.DestinationPath}";
                if (result.BoundariesAreApproximate)
                {
                    successMessage += " (キーフレーム位置に合わせたため、境界は指定時刻と異なります)";
                }

                ShowNotice(successMessage, InfoBarSeverity.Success);
            }
        }
        catch (OperationCanceledException)
        {
            if (!_isClosing && ReferenceEquals(_exportCancellation, exportCancellation))
            {
                ShowNotice("書き出しをキャンセルしました。", InfoBarSeverity.Informational);
            }
        }
        catch (ExportException ex)
        {
            if (!_isClosing && ReferenceEquals(_exportCancellation, exportCancellation))
            {
                ShowError(ex.UserMessage);
            }
        }
        catch (Exception ex)
        {
            if (!_isClosing && ReferenceEquals(_exportCancellation, exportCancellation))
            {
                ShowError($"動画を書き出せませんでした: {ex.Message}");
            }
        }
        finally
        {
            exportCancellation.Dispose();
            if (ReferenceEquals(_exportCancellation, exportCancellation))
            {
                _exportCancellation = null;
                _isExporting = false;
                if (!_isClosing)
                {
                    ExportProgressBar.IsIndeterminate = false;
                    ExportProgressBar.Visibility = Visibility.Collapsed;
                    CancelExportButton.Visibility = Visibility.Collapsed;
                    ExportButton.Content = "書き出し";
                    CancelEditButton.IsEnabled = true;
                    UpdateApproximateCopyAvailability();
                    OutputContainerComboBox.IsEnabled = true;
                    ExportOptionsButton.IsEnabled = true;
                    OutputFolderButton.IsEnabled = true;
                    OutputNameBox.IsEnabled = true;
                    ValidateEditRange();
                }
            }
        }
    }

    private void CancelExportButton_Click(object sender, RoutedEventArgs e) => _exportCancellation?.Cancel();

    private void CancelExportForNewFile()
    {
        var cancellation = _exportCancellation;
        _exportCancellation = null;
        cancellation?.Cancel();
        _isExporting = false;
        ExportProgressBar.IsIndeterminate = false;
        ExportProgressBar.Visibility = Visibility.Collapsed;
        CancelExportButton.Visibility = Visibility.Collapsed;
        ExportButton.Content = "書き出し";
        OutputContainerComboBox.IsEnabled = true;
        ExportOptionsButton.IsEnabled = true;
        OutputFolderButton.IsEnabled = true;
        OutputNameBox.IsEnabled = true;
    }

    private async void SaveFrameButton_Click(object sender, RoutedEventArgs e)
    {
        var player = _player;
        if (player is null)
        {
            ShowError("先に動画を開いてください。");
            return;
        }

        string? temporary = null;
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "FrameDock");
            Directory.CreateDirectory(directory);
            temporary = Path.Combine(directory, $".FrameDock-{Guid.NewGuid():N}.png");
            await player.SaveScreenshotAsync(temporary);
            var destination = MoveScreenshotToAvailablePath(temporary, directory, BuildScreenshotName());
            temporary = null;
            ShowNotice($"フレーム画像を保存しました: {destination}", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowError($"Pictures\\FrameDock へのフレーム画像の保存に失敗しました: {ex.Message}");
        }
        finally
        {
            try
            {
                if (temporary is not null && File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            catch
            {
                // The user has already received the save result or error; ignore temp cleanup failures.
            }
        }
    }

    private string BuildScreenshotName()
    {
        var stem = _loadedPath is null ? "FrameDock" : Path.GetFileNameWithoutExtension(_loadedPath);
        var position = _player?.GetNumber("time-pos") ?? 0;
        return $"{stem}-{FormatTimeFileSafe(position)}";
    }

    private static string MoveScreenshotToAvailablePath(string temporaryPath, string directory, string baseName)
    {
        for (var collision = 0; ; collision++)
        {
            var suffix = collision == 0 ? string.Empty : $" ({collision + 1})";
            var destination = Path.Combine(directory, $"{baseName}{suffix}.png");
            try
            {
                File.Move(temporaryPath, destination, overwrite: false);
                return destination;
            }
            catch (IOException) when (File.Exists(destination))
            {
                // Keep the existing image and try the next available name.
            }
        }
    }

    private async void CopyFrameButton_Click(object sender, RoutedEventArgs e)
    {
        var player = _player;
        if (player is null)
        {
            ShowError("先に動画を開いてください。");
            return;
        }

        var phase = "表示中のフレームを取得";
        string? path = null;
        var clipboardDataSet = false;
        var clipboardFlushed = false;
        try
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FrameDock",
                "clipboard-frames");
            Directory.CreateDirectory(folder);
            path = Path.Combine(folder, $"frame-{Guid.NewGuid():N}.png");
            await player.SaveScreenshotAsync(path);
            phase = "保存した画像を開く";
            var file = await StorageFile.GetFileFromPathAsync(path);
            phase = "クリップボードへ転送";
            await SetClipboardImageWithRetryAsync(file, () => clipboardDataSet = true);
            clipboardFlushed = true;
            ShowNotice("表示中のフレームを画像としてコピーしました。", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            var cause = ex.InnerException ?? ex;
            ShowError($"画像をコピーできませんでした（{phase}、{cause.GetType().Name} 0x{cause.HResult:X8}）: {ex.Message}");
        }
        finally
        {
            // A successful Flush makes the clipboard independent from this source file.
            // If the clipboard accepted data but Flush failed, keep the file for deferred rendering.
            if (path is not null && (!clipboardDataSet || clipboardFlushed))
            {
                TryDeleteClipboardFrame(path);
            }
        }
    }

    private static async Task SetClipboardImageWithRetryAsync(StorageFile file, Action onClipboardDataSet)
    {
        const int maxAttempts = 5;
        Exception? lastBusyError = null;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                // SetContentWithOptions reports a busy clipboard as false instead of throwing.
                // Flush persists the data after FrameDock closes, so retry both operations together.
                var package = new DataPackage();
                package.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
                package.RequestedOperation = DataPackageOperation.Copy;
                if (Clipboard.SetContentWithOptions(package, null))
                {
                    onClipboardDataSet();
                    Clipboard.Flush();
                    return;
                }

                lastBusyError = new COMException("クリップボードは他のアプリで使用中です。", ClipboardCannotOpenHResult);
            }
            catch (COMException ex) when (ex.HResult == ClipboardCannotOpenHResult)
            {
                lastBusyError = ex;
            }

            if (attempt + 1 < maxAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)));
            }
        }

        throw new IOException("Windowsのクリップボードを使用中です。しばらくしてからもう一度お試しください。", lastBusyError);
    }

    private static void TryDeleteClipboardFrame(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Cleanup must not change the result of copying the frame.
        }
    }

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int key);

    private void ShowError(string message) => ShowNotice(message, InfoBarSeverity.Error);

    private void ShowNotice(string message, InfoBarSeverity severity)
    {
        if (_isClosing)
        {
            return;
        }

        _noticeTimer?.Stop();
        ShowControls();
        if (severity is InfoBarSeverity.Success or InfoBarSeverity.Informational)
        {
            NotificationBar.IsOpen = false;
            InlineNoticeText.Text = message;
            ToolTipService.SetToolTip(InlineNoticeText, message);
            InlineNoticeText.SetValue(Microsoft.UI.Xaml.Automation.AutomationProperties.NameProperty, message);
            InlineNoticeText.Visibility = Visibility.Visible;
            _noticeTimer?.Start();
            return;
        }

        InlineNoticeText.Visibility = Visibility.Collapsed;
        InlineNoticeText.Text = string.Empty;
        NotificationBar.Severity = severity;
        NotificationBar.Title = severity switch
        {
            InfoBarSeverity.Error => "エラー",
            InfoBarSeverity.Warning => "確認",
            InfoBarSeverity.Success => "完了",
            _ => "FrameDock"
        };
        NotificationBar.Message = message;
        NotificationBar.IsOpen = true;
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _isClosing = true;
        _settingsSaveTimer?.Stop();
        _noticeTimer?.Stop();
        _controlsHideTimer?.Stop();
        _statusTimer?.Stop();
        _compositionResizeTimer?.Stop();
        _exportCancellation?.Cancel();
        CancelThumbnailGeneration();
        var player = _player;
        _player = null;
        SwapChainPanelInterop.Attach(MpvSwapChainPanel, IntPtr.Zero);
        player?.Dispose();
        _ = SaveSettingsOnCloseAsync();
    }

    private async Task SaveSettingsOnCloseAsync()
    {
        try
        {
            await _settings.SaveAsync();
        }
        catch
        {
            // The window is already closing; a settings write failure has no safe UI surface.
        }
    }
}
