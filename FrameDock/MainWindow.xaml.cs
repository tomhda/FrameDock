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
using Microsoft.UI.Windowing;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace FrameDock;

public sealed partial class MainWindow : Window
{
    private const int VirtualKeyControl = 0x11;
    private const int ClipboardCannotOpenHResult = unchecked((int)0x800401D0);
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
    private AppWindow? _appWindow;
    private MediaInfo? _mediaInfo;
    private string? _loadedPath;
    private string? _queuedOpenPath;
    private string? _outputDirectory;
    private CropRect? _crop;
    private Windows.Foundation.Point? _cropDragStart;
    private CropRect? _cropDragPreview;
    private CancellationTokenSource? _exportCancellation;
    private bool _isUpdatingTimeline;
    private bool _isTimelineDragging;
    private bool _isMuted;
    private bool _isFullscreen;
    private bool _isCropMode;
    private bool _isRestoringSettings;
    private bool _isClosing;
    private bool _isExporting;
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
        _isMuted = _settings.Muted;

        _isRestoringSettings = true;
        TimelineSlider.ValueChanged += TimelineSlider_ValueChanged;
        VolumeSlider.ValueChanged += VolumeSlider_ValueChanged;
        VolumeSlider.Value = _settings.Volume;
        UpdateMuteButton();
        UpdateSkipLabels();
        UpdateSpeedMenu(_settings.Speed);
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
        };
        UpdateTitleBarTheme();
    }

    private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initialPath is not null)
        {
            await OpenFileAsync(_initialPath);
        }
    }

    private void RootGrid_ActualThemeChanged(FrameworkElement sender, object args) => UpdateTitleBarTheme();

    private void VideoRegion_SizeChanged(object sender, SizeChangedEventArgs e) => ScheduleCompositionResize();

    private void ScheduleCompositionResize()
    {
        if (_isClosing || _player is null || _compositionResizeTimer is null)
        {
            return;
        }

        _compositionResizeTimer.Stop();
        _compositionResizeTimer.Start();
    }

    private void ApplyCompositionResize()
    {
        if (_isClosing || _player is not { } player)
        {
            return;
        }

        var scale = VideoRegion.XamlRoot?.RasterizationScale ?? 1;
        var width = Math.Clamp((int)Math.Round(VideoRegion.ActualWidth * scale), 1, 16384);
        var height = Math.Clamp((int)Math.Round(VideoRegion.ActualHeight * scale), 1, 16384);
        if (width == _compositionWidth && height == _compositionHeight)
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

    private static Windows.UI.Color MakeColor(byte red, byte green, byte blue) => new()
    {
        A = 255,
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
            _mediaInfo = null;
            _crop = null;
            _outputDirectory = Path.GetDirectoryName(fullPath);
            EditorPanel.Visibility = Visibility.Collapsed;
            CropOverlayCanvas.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = Visibility.Collapsed;
            TimelineSlider.IsEnabled = false;
            OutputFolderText.Text = "元動画と同じフォルダー";
            OutputNameBox.Text = $"{Path.GetFileNameWithoutExtension(fullPath)}-clip";
            CropInfoText.Text = "クロップなし";
            CropResetButton.IsEnabled = false;
            TrimStartBox.Value = 0;
            TrimEndBox.Value = 0;
            ExportButton.IsEnabled = false;
            HdrNoteText.Visibility = Visibility.Collapsed;

            var scale = VideoRegion.XamlRoot?.RasterizationScale ?? 1;
            var width = Math.Max(1, (int)Math.Round(VideoRegion.ActualWidth * scale));
            var height = Math.Max(1, (int)Math.Round(VideoRegion.ActualHeight * scale));
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
                CropModeButton.IsEnabled = true;
                UpdateCropInfo();
                ValidateEditRange();
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
        }

        var isPaused = _player.GetFlag("pause");
        PlayIcon.Glyph = isPaused ? "\uE768" : "\uE769";
        PlayButton.SetValue(Microsoft.UI.Xaml.Automation.AutomationProperties.NameProperty, isPaused ? "再生" : "一時停止");
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
    }

    private void UpdateSkipLabels()
    {
        var seconds = _settings.SkipSeconds.ToString("0.##", CultureInfo.InvariantCulture);
        BackSkipText.Text = $"−{seconds}";
        ForwardSkipText.Text = $"+{seconds}";
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

    private void FullscreenButton_Click(object sender, RoutedEventArgs e)
    {
        if (_appWindow is null)
        {
            return;
        }

        _isFullscreen = !_isFullscreen;
        _appWindow.SetPresenter(_isFullscreen ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped);
        FullscreenIcon.Glyph = _isFullscreen ? "\uE73F" : "\uE740";
        ToolTipService.SetToolTip(FullscreenButton, _isFullscreen ? "ウィンドウ表示に戻る (Esc)" : "全画面表示 (F)");
        FullscreenButton.SetValue(Microsoft.UI.Xaml.Automation.AutomationProperties.NameProperty, _isFullscreen ? "ウィンドウ表示に戻る" : "全画面表示");
    }

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
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

        EditorPanel.Visibility = EditorPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (EditorPanel.Visibility == Visibility.Visible)
        {
            ValidateEditRange();
        }

        UpdateCropOverlay();
    }

    private void VideoRegion_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (_player is null)
        {
            return;
        }

        var menu = new MenuFlyout();
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
        if (position.HasValue)
        {
            TrimStartBox.Value = Math.Clamp(position.Value, 0, _mediaInfo?.DurationSeconds ?? position.Value);
        }
    }

    private void MarkTrimEndButton_Click(object sender, RoutedEventArgs e)
    {
        var position = _player?.GetNumber("time-pos");
        if (position.HasValue)
        {
            TrimEndBox.Value = Math.Clamp(position.Value, 0, _mediaInfo?.DurationSeconds ?? position.Value);
        }
    }

    private void ValidateEditRange()
    {
        if (_mediaInfo is null)
        {
            ExportButton.IsEnabled = false;
            EditRangeText.Text = "開始と終了を確認してください";
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
    }

    private void CropModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_mediaInfo is null)
        {
            return;
        }

        SetCropMode(!_isCropMode);
    }

    private void SetCropMode(bool enabled)
    {
        _isCropMode = enabled && _mediaInfo is not null;
        CropOverlayCanvas.Visibility = _isCropMode ? Visibility.Visible : Visibility.Collapsed;
        CropModeButton.Content = _isCropMode ? "選択を確定" : "クロップ範囲を選択";
        ToolTipService.SetToolTip(CropModeButton, _isCropMode ? "動画上をドラッグして範囲を選択。もう一度押すと確定" : "動画上をドラッグして切り抜く範囲を選択");
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
    }

    private void CropOverlayCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateCropOverlay();

    private void CropResetButton_Click(object sender, RoutedEventArgs e)
    {
        _crop = null;
        _cropDragPreview = null;
        UpdateCropInfo();
        UpdateCropOverlay();
    }

    private void CropOverlayCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_isCropMode || _mediaInfo is null)
        {
            return;
        }

        var point = e.GetCurrentPoint(CropOverlayCanvas).Position;
        var bounds = GetVideoContentBounds();
        if (point.X < bounds.Left || point.X > bounds.Right || point.Y < bounds.Top || point.Y > bounds.Bottom)
        {
            return;
        }

        CropOverlayCanvas.CapturePointer(e.Pointer);
        _cropDragStart = point;
        _cropDragPreview = null;
        UpdateCropOverlay(point);
        e.Handled = true;
    }

    private void CropOverlayCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_cropDragStart is not { } start || _mediaInfo is null)
        {
            return;
        }

        var point = e.GetCurrentPoint(CropOverlayCanvas).Position;
        UpdateCropOverlay(point);
        e.Handled = true;
    }

    private void CropOverlayCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_cropDragStart is null)
        {
            return;
        }

        var point = e.GetCurrentPoint(CropOverlayCanvas).Position;
        var selected = MapDragToCrop(_cropDragStart.Value, point);
        CropOverlayCanvas.ReleasePointerCapture(e.Pointer);
        _cropDragStart = null;
        if (selected is { } crop && crop.Width >= 2 && crop.Height >= 2)
        {
            try
            {
                MediaGeometry.ValidateCrop(crop, _mediaInfo!.DisplayWidth, _mediaInfo.DisplayHeight);
                _crop = crop;
                _cropDragPreview = null;
                UpdateCropInfo();
            }
            catch (ExportValidationException ex)
            {
                ShowError(ex.UserMessage);
            }
        }
        else
        {
            ShowError("クロップ範囲は 2 × 2 ピクセル以上にしてください。");
        }

        UpdateCropOverlay();
        e.Handled = true;
    }

    private void CropOverlayCanvas_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        _cropDragStart = null;
        _cropDragPreview = null;
        UpdateCropOverlay();
    }

    private Windows.Foundation.Rect GetVideoContentBounds()
    {
        if (_mediaInfo is null || CropOverlayCanvas.ActualWidth <= 0 || CropOverlayCanvas.ActualHeight <= 0)
        {
            return new Windows.Foundation.Rect(0, 0, CropOverlayCanvas.ActualWidth, CropOverlayCanvas.ActualHeight);
        }

        var contentAspect = (double)_mediaInfo.DisplayWidth / _mediaInfo.DisplayHeight;
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

    private CropRect? MapDragToCrop(Windows.Foundation.Point first, Windows.Foundation.Point second)
    {
        if (_mediaInfo is null)
        {
            return null;
        }

        var bounds = GetVideoContentBounds();
        var left = Math.Clamp(Math.Min(first.X, second.X), bounds.Left, bounds.Right);
        var right = Math.Clamp(Math.Max(first.X, second.X), bounds.Left, bounds.Right);
        var top = Math.Clamp(Math.Min(first.Y, second.Y), bounds.Top, bounds.Bottom);
        var bottom = Math.Clamp(Math.Max(first.Y, second.Y), bounds.Top, bounds.Bottom);
        var displayLeft = (left - bounds.Left) / bounds.Width * _mediaInfo.DisplayWidth;
        var displayRight = (right - bounds.Left) / bounds.Width * _mediaInfo.DisplayWidth;
        var displayTop = (top - bounds.Top) / bounds.Height * _mediaInfo.DisplayHeight;
        var displayBottom = (bottom - bounds.Top) / bounds.Height * _mediaInfo.DisplayHeight;
        var maxWidth = _mediaInfo.DisplayWidth & ~1;
        var maxHeight = _mediaInfo.DisplayHeight & ~1;
        if (maxWidth < 2 || maxHeight < 2)
        {
            return null;
        }

        var x = Math.Clamp(EvenFloor(displayLeft), 0, maxWidth - 2);
        var y = Math.Clamp(EvenFloor(displayTop), 0, maxHeight - 2);
        var endX = Math.Clamp(EvenCeiling(displayRight), x + 2, maxWidth);
        var endY = Math.Clamp(EvenCeiling(displayBottom), y + 2, maxHeight);
        if (endX <= x || endY <= y)
        {
            return null;
        }

        return new CropRect(x, y, endX - x, endY - y);
    }

    private static int EvenFloor(double value) => Math.Max(0, (int)Math.Floor(value / 2) * 2);

    private static int EvenCeiling(double value) => checked((int)Math.Ceiling(value / 2) * 2);

    private void UpdateCropOverlay(Windows.Foundation.Point? currentPoint = null)
    {
        if (_mediaInfo is null || CropOverlayCanvas.ActualWidth <= 0 || CropOverlayCanvas.ActualHeight <= 0)
        {
            return;
        }

        var bounds = GetVideoContentBounds();
        if (_cropDragStart is { } start && currentPoint is { } current)
        {
            _cropDragPreview = MapDragToCrop(start, current);
        }

        var activeCrop = _cropDragPreview ?? _crop;
        if (activeCrop is not { } crop)
        {
            CropSelectionRectangle.Visibility = Visibility.Collapsed;
            CropMaskLeft.Visibility = Visibility.Collapsed;
            CropMaskTop.Visibility = Visibility.Collapsed;
            CropMaskRight.Visibility = Visibility.Collapsed;
            CropMaskBottom.Visibility = Visibility.Collapsed;
            return;
        }

        var scaleX = bounds.Width / _mediaInfo.DisplayWidth;
        var scaleY = bounds.Height / _mediaInfo.DisplayHeight;
        var x = bounds.Left + crop.X * scaleX;
        var y = bounds.Top + crop.Y * scaleY;
        var width = crop.Width * scaleX;
        var height = crop.Height * scaleY;
        Canvas.SetLeft(CropSelectionRectangle, x);
        Canvas.SetTop(CropSelectionRectangle, y);
        CropSelectionRectangle.Width = width;
        CropSelectionRectangle.Height = height;
        CropSelectionRectangle.Visibility = Visibility.Visible;

        SetMask(CropMaskLeft, 0, 0, x, CropOverlayCanvas.ActualHeight);
        SetMask(CropMaskTop, x, 0, width, y);
        SetMask(CropMaskRight, x + width, 0, Math.Max(0, CropOverlayCanvas.ActualWidth - x - width), CropOverlayCanvas.ActualHeight);
        SetMask(CropMaskBottom, x, y + height, width, Math.Max(0, CropOverlayCanvas.ActualHeight - y - height));
    }

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
        }
    }

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

        var mode = ApproximateCopyCheckBox.IsChecked == true ? ExportMode.StreamCopyApproximate : ExportMode.AccurateReencode;
        if (mode == ExportMode.AccurateReencode && _mediaInfo.IsHdr)
        {
            ShowError("HDR 動画の正確な再エンコードには対応していません。高速コピーを選ぶと元の色を保てますが、切り出し位置は前後する場合があります。");
            return;
        }

        if (mode == ExportMode.StreamCopyApproximate && _crop is not null)
        {
            ShowError("クロップと高速コピーは同時に使えません。クロップを解除するか、正確な再エンコードを選んでください。");
            return;
        }

        var fileStem = OutputNameBox.Text.Trim();
        if (fileStem.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            fileStem = fileStem[..^4];
        }

        if (string.IsNullOrWhiteSpace(fileStem) || fileStem is "." or ".." || Path.GetFileName(fileStem) != fileStem || fileStem.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            ShowError("出力ファイル名には、ファイル名として使える文字を入力してください。");
            return;
        }

        var directory = _outputDirectory ?? Path.GetDirectoryName(_loadedPath)!;
        var destination = Path.Combine(directory, fileStem + ".mp4");
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
            mode);
        var exportCancellation = new CancellationTokenSource();
        _exportCancellation = exportCancellation;
        var token = exportCancellation.Token;
        _isExporting = true;
        NotificationBar.IsOpen = false;
        ExportProgressBar.Value = 0;
        ExportProgressBar.Visibility = Visibility.Visible;
        CancelExportButton.Visibility = Visibility.Visible;
        ExportButton.Content = "書き出し中…";
        ApproximateCopyCheckBox.IsEnabled = false;
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
                    ExportButton.Content = "MP4 に書き出す";
                    ApproximateCopyCheckBox.IsEnabled = true;
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
        ExportButton.Content = "MP4 に書き出す";
        ApproximateCopyCheckBox.IsEnabled = true;
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
        try
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FrameDock",
                "clipboard-frames");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"frame-{Guid.NewGuid():N}.png");
            await player.SaveScreenshotAsync(path);
            phase = "保存した画像を開く";
            var file = await StorageFile.GetFileFromPathAsync(path);
            phase = "クリップボードへ転送";
            await SetClipboardImageWithRetryAsync(file);
            ShowNotice("表示中のフレームを画像としてコピーしました。", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            var cause = ex.InnerException ?? ex;
            ShowError($"画像をコピーできませんでした（{phase}、{cause.GetType().Name} 0x{cause.HResult:X8}）: {ex.Message}");
        }
    }

    private static async Task SetClipboardImageWithRetryAsync(StorageFile file)
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
        _statusTimer?.Stop();
        _compositionResizeTimer?.Stop();
        _exportCancellation?.Cancel();
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
