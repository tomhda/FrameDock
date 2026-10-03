using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using FrameDock.Core;
using FrameDock.Localization;
using FrameDock.Player;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
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
    private string? _thumbnailFailedSourcePath;
    private int _thumbnailFailedRotation;
    private bool _hasThumbnailFailure;
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
    private double _editorZoomFactor = 1;
    private double _editorZoomFocusX = 0.5;
    private double _editorZoomFocusY = 0.5;
    private bool _isUpdatingZoomControls;
    private bool _isZoomPanning;
    private Windows.Foundation.Point? _zoomPanPointerOrigin;
    private Pointer? _zoomPanPointer;
    private double _zoomPanFocusXOrigin;
    private double _zoomPanFocusYOrigin;
    // Viewing speed lives in _settings.Speed; the editor previews and exports
    // at its own speed so a habitual 1.5x viewing speed never leaks into output.
    private double _editSpeed = 1;
    private const int MaxSegmentCount = 20;
    private const double SegmentSplitEdgeTolerance = 0.1;
    private const double SegmentMinLength = 0.05;
    private readonly List<EditSegment> _segments = new();
    private int _currentSegmentIndex = -1;
    private bool _isSyncingSegments;
    private readonly List<Microsoft.UI.Xaml.Shapes.Rectangle> _segmentSplitLines = new();
    private readonly List<Border> _segmentDeletedOverlays = new();
    private Border? _segmentSelectedBar;
    private bool _isRestoringSettings;
    private bool _isClosing;
    private bool _isExporting;
    private Task? _exportTask;
    private bool _allowClose;
    private bool _confirmingClose;
    private double _lastRasterizationScale;
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
            App.WriteErrorLog(ex);
            throw;
        }
        TrimStartBox.NumberFormatter = new TrimTimeFormatter();
        TrimEndBox.NumberFormatter = new TrimTimeFormatter();
        TrimStartBox.ValueChanged += TrimBox_ValueChanged;
        TrimEndBox.ValueChanged += TrimBox_ValueChanged;
        ZoomSlider.ValueChanged += ZoomSlider_ValueChanged;
        _initialPath = string.IsNullOrWhiteSpace(initialPath) ? null : initialPath;
        _settings = PlayerSettings.Load();
        _exportService = new MediaExportService(new ExportOptions(
            Path.Combine(AppContext.BaseDirectory, "Media", "ffmpeg.exe"),
            Path.Combine(AppContext.BaseDirectory, "Media", "ffprobe.exe")));
        _windowHandle = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_windowHandle));
        _appWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "FrameDock.ico"));
        _appWindow.Changed += AppWindow_Changed;
        _appWindow.Closing += AppWindow_Closing;
        CleanupStaleClipboardFrames();
        _isMuted = _settings.Muted;
        _displayMode = _settings.DisplayMode;
        _modeBeforeFullscreen = _displayMode == PlayerDisplayMode.Fullscreen
            ? PlayerDisplayMode.MaximizedOverlay
            : _displayMode;

        _isRestoringSettings = true;
        TimelineSlider.ValueChanged += TimelineSlider_ValueChanged;
        // Slider marks its own pointer events handled, so the XAML attributes never fire.
        // handledEventsToo keeps the drag flag accurate for status polling and seeking.
        TimelineSlider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(TimelineSlider_PointerPressed), true);
        TimelineSlider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(TimelineSlider_PointerReleased), true);
        TimelineSlider.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(TimelineSlider_PointerCanceled), true);
        TimelineSlider.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(TimelineSlider_PointerCaptureLost), true);
        TimelineSlider.ThumbToolTipValueConverter = new TimelineThumbToolTipConverter();
        VolumeSlider.ValueChanged += VolumeSlider_ValueChanged;
        VolumeSlider.Value = _settings.Volume;
        UpdateMuteButton();
        UpdateSkipLabels();
        UpdateSpeedMenu(_settings.Speed);
        UpdateEditSpeedControl(_editSpeed);
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
            if (NotificationBar.Severity is InfoBarSeverity.Success or InfoBarSeverity.Informational || NotificationBar.ActionButton is not null)
            {
                // Errors stay until dismissed; routine notices clear themselves.
                NotificationBar.IsOpen = false;
            }

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
            NotificationBar.Visibility = NotificationBar.IsOpen ? Visibility.Visible : Visibility.Collapsed;
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
        if (RootGrid.XamlRoot is { } xamlRoot)
        {
            _lastRasterizationScale = xamlRoot.RasterizationScale;
            xamlRoot.Changed += XamlRoot_Changed;
        }

        ApplyDisplayMode(_settings.DisplayMode, persist: false, revealControls: false);
        if (_initialPath is not null)
        {
            try
            {
                await OpenFileAsync(_initialPath);
            }
            catch (Exception ex)
            {
                App.WriteErrorLog(ex);
                ShowError(Strings.Get("Error_OpenFileFailed"));
            }
        }
    }

    private void XamlRoot_Changed(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (_isClosing)
        {
            return;
        }

        if (Math.Abs(sender.RasterizationScale - _lastRasterizationScale) > 0.001)
        {
            _lastRasterizationScale = sender.RasterizationScale;
            ScheduleCompositionResize(force: true);
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
            App.WriteErrorLog(ex);
            ShowError(Strings.Get("Error_RenderSizeFailed"));
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

        // AppWindowTitleBar colors alone leave a one-pixel strip of the default
        // (light) frame above and below the caption at fractional scales such
        // as 125%. Theme the frame itself so nothing light shows through.
        var useDarkFrame = dark ? 1 : 0;
        var captionColor = background.R | (background.G << 8) | (background.B << 16);
        _ = DwmSetWindowAttribute(_windowHandle, DwmUseImmersiveDarkMode, ref useDarkFrame, sizeof(int));
        _ = DwmSetWindowAttribute(_windowHandle, DwmCaptionColor, ref captionColor, sizeof(int));
    }

    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmCaptionColor = 35;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

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
        // async void: anything escaping here would terminate the app, e.g. the
        // file picker throws when FrameDock runs elevated.
        try
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
        catch (Exception ex)
        {
            App.WriteErrorLog(ex);
            ShowError(Strings.Get("Error_FilePickerFailed"));
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

        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var file = items.OfType<StorageFile>().FirstOrDefault();
            if (file is not null)
            {
                await OpenFileAsync(file.Path);
            }
        }
        catch (Exception ex)
        {
            App.WriteErrorLog(ex);
            ShowError(Strings.Get("Error_DropOpenFailed"));
        }
    }

    private async Task OpenFileAsync(string path)
    {
        if (_isExporting)
        {
            var confirmOpen = new ContentDialog
            {
                Title = Strings.Get("Dialog_AbortExportTitle"),
                Content = Strings.Get("Dialog_OpenDuringExportContent"),
                PrimaryButtonText = Strings.Get("Dialog_AbortAndOpen"),
                CloseButtonText = Strings.Get("Dialog_Cancel"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = RootGrid.XamlRoot
            };
            ContentDialogResult openChoice;
            try
            {
                openChoice = await confirmOpen.ShowAsync();
            }
            catch (Exception ex)
            {
                App.WriteErrorLog(ex);
                return;
            }

            if (openChoice != ContentDialogResult.Primary)
            {
                return;
            }

            CancelExportForNewFile();
            await WaitForExportCompletionAsync();
        }
        else
        {
            CancelExportForNewFile();
        }

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
                ShowError(Strings.Format("Error_FileNotFound", path));
                return;
            }
        }
        catch (Exception ex)
        {
            App.WriteErrorLog(ex);
            ShowError(Strings.Get("Error_OpenFileAccess"));
            return;
        }

        try
        {
            NotificationBar.IsOpen = false;
            InlineNoticeText.Visibility = Visibility.Collapsed;
            InlineNoticeText.Text = string.Empty;
            _noticeTimer?.Stop();
            _statusTimer?.Stop();
            var previousPlayer = _player;
            _player = null;
            SwapChainPanelInterop.Attach(MpvSwapChainPanel, IntPtr.Zero);
            MpvSwapChainPanel.Visibility = Visibility.Collapsed;
            if (previousPlayer is not null)
            {
                _ = Task.Run(() => previousPlayer.Dispose());
            }

            _loadedPath = fullPath;
            OutputContainerComboBox.SelectedIndex = 0;
            CancelThumbnailGeneration();
            TrimThumbnailStrip.Children.Clear();
            _thumbnailSourcePath = null;
            _thumbnailRotation = -1;
            _thumbnailFailedSourcePath = null;
            _hasThumbnailFailure = false;
            _thumbnailBitmaps = Array.Empty<BitmapImage>();
            _thumbnailFrameCount = 0;
            _mediaInfo = null;
            _segments.Clear();
            _currentSegmentIndex = -1;
            _crop = null;
            _additionalRotationDegreesClockwise = 0;
            _editorZoomFactor = 1;
            _editorZoomFocusX = 0.5;
            _editorZoomFocusY = 0.5;
            ApproximateCopyCheckBox.IsChecked = false;
            _editSpeed = 1;
            UpdateEditSpeedControl(_editSpeed);
            UpdateZoomControls();
            _outputDirectory = Path.GetDirectoryName(fullPath);
            SetCropMode(false);
            EditorPanel.Visibility = Visibility.Collapsed;
            SetEditorLayout(false);
            ScheduleControlsHide();
            CropOverlayCanvas.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = Visibility.Collapsed;
            TimelineSlider.IsEnabled = false;
            OutputFolderText.Text = Strings.Get("OutputFolder_Default");
            ToolTipService.SetToolTip(OutputFolderText, Strings.Get("OutputFolder_Default"));
            OutputNameBox.Text = $"{Path.GetFileNameWithoutExtension(fullPath)}-clip";
            CropInfoText.Text = string.Empty;
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
                    App.WriteErrorLog(ex);
                    ShowError(Strings.Get("Error_PrepareRenderFailed"));
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
                using var inspectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                MediaInfo inspected;
                try
                {
                    inspected = await _exportService.InspectAsync(fullPath, inspectTimeout.Token);
                }
                catch (OperationCanceledException timeout) when (inspectTimeout.IsCancellationRequested)
                {
                    App.WriteErrorLog(timeout);
                    ShowError(Strings.Get("Error_InspectTimeout"));
                    return;
                }
                if (_isClosing || !ReferenceEquals(_player, player))
                {
                    return;
                }

                _mediaInfo = inspected;
                TrimStartBox.Maximum = inspected.DurationSeconds;
                TrimEndBox.Maximum = inspected.DurationSeconds;
                TrimStartBox.Value = 0;
                TrimEndBox.Value = inspected.DurationSeconds;
                ResetSegmentsToFullRange();
                HdrNoteText.Visibility = inspected.IsHdr ? Visibility.Visible : Visibility.Collapsed;
                UpdateZoomControls();
                UpdateRotationControl();
                UpdateCropInfo();
                ValidateEditRange();
                UpdateApproximateCopyAvailability();
                if (Environment.GetEnvironmentVariable("FRAMEDOCK_OPEN_EDITOR") == "1" && EditorPanel.Visibility != Visibility.Visible)
                {
                    // UI automation hook: open the editor without pointer input.
                    EditButton_Click(this, new RoutedEventArgs());
                }
            }
            catch (ExportException ex)
            {
                ShowError(ex.UserMessage);
            }
            catch (Exception ex)
            {
                App.WriteErrorLog(ex);
                ShowError(Strings.Get("Error_InspectFallback"));
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
            App.WriteErrorLog(ex);
            ShowError(Strings.Get("Error_PlaybackFailed"));
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
            if (EditorPanel.Visibility == Visibility.Visible && _mediaInfo is not null && _segments.Count > 0 && !_isExporting)
            {
                var selectedSegment = GetSelectedSegmentIndex(position.Value);
                if (selectedSegment >= 0 && selectedSegment != _currentSegmentIndex)
                {
                    SwitchEditorSegment(selectedSegment);
                }

                if (_currentSegmentIndex >= 0 && _currentSegmentIndex < _segments.Count && _segments[_currentSegmentIndex].IsDeleted)
                {
                    var segmentIsPaused = _player.GetFlag("pause");
                    if (!segmentIsPaused && !_isTrimTimelineDragging && !_isTimelineDragging)
                    {
                        var nextSegment = -1;
                        for (var segmentIndex = _currentSegmentIndex + 1; segmentIndex < _segments.Count; segmentIndex++)
                        {
                            if (!_segments[segmentIndex].IsDeleted)
                            {
                                nextSegment = segmentIndex;
                                break;
                            }
                        }

                        try
                        {
                            if (nextSegment >= 0)
                            {
                                _player.SeekAbsolute(_segments[nextSegment].StartSeconds);
                            }
                            else
                            {
                                _player.SeekAbsolute(GetEditorRangeEnd());
                                _player.SetPaused(true);
                            }
                        }
                        catch (Exception seekException)
                        {
                            App.WriteErrorLog(seekException);
                        }
                    }
                }
            }
        }

        var isPaused = _player.GetFlag("pause");
        PlayIcon.Glyph = isPaused ? "\uE768" : "\uE769";
        EditPlayIcon.Glyph = isPaused ? "\uE768" : "\uE769";
        PlayButton.SetValue(Microsoft.UI.Xaml.Automation.AutomationProperties.NameProperty, isPaused ? Strings.Get("Accessibility_Play") : Strings.Get("Accessibility_Pause"));
        EditPlayButton.SetValue(Microsoft.UI.Xaml.Automation.AutomationProperties.NameProperty, isPaused ? Strings.Get("Accessibility_Play") : Strings.Get("Accessibility_Pause"));
    }

    internal static string FormatTime(double seconds)
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
            App.WriteErrorLog(ex);
            ShowError(Strings.Get("Error_TogglePlayFailed"));
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
            App.WriteErrorLog(ex);
            ShowError(Strings.Get("Error_StepFrameFailed"));
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
            App.WriteErrorLog(ex);
            ShowError(Strings.Get("Error_SeekFailed"));
        }
    }

    private void TimelineSlider_PointerPressed(object sender, PointerRoutedEventArgs e) => _isTimelineDragging = true;

    private void TimelineSlider_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _isTimelineDragging = false;
        SeekToTimelineValue();
        RefreshStatus();
    }

    private void TimelineSlider_PointerCanceled(object sender, PointerRoutedEventArgs e) => _isTimelineDragging = false;

    private void TimelineSlider_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => _isTimelineDragging = false;

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
            App.WriteErrorLog(ex);
            ShowError(Strings.Get("Error_SeekFailed"));
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

        if (!e.GetCurrentPoint(TrimTimelineCanvas).Properties.IsLeftButtonPressed)
        {
            FinishTrimDrag(e);
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
        FinishTrimDrag(e);
    }

    private void TrimTimelineCanvas_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        // ReleasePointerCapture also raises this after PointerReleased; that path is a no-op.
        _isTrimTimelineDragging = false;
        _trimDragPointerOffset = 0;
        UpdateTrimTimeline();
    }

    private void FinishTrimDrag(PointerRoutedEventArgs e)
    {
        _isTrimTimelineDragging = false;
        _trimDragPointerOffset = 0;
        TrimTimelineCanvas.ReleasePointerCapture(e.Pointer);
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

        TrimStartReadout.Text = Strings.Format("TrimReadout_Start", FormatTime(start));
        TrimPlayheadReadout.Text = Strings.Format("TrimReadout_Position", FormatTime(position));
        TrimEndReadout.Text = Strings.Format("TrimReadout_End", FormatTime(end));
        UpdateSegmentTimelineVisuals(trackWidth, duration, pad, trackY, trackHeight, position);
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

        var rotation = 0;
        if (!force && string.Equals(_thumbnailSourcePath, sourcePath, StringComparison.OrdinalIgnoreCase) &&
            _thumbnailRotation == rotation && TrimThumbnailStrip.Children.Count > 0)
        {
            return;
        }

        if (!force && _hasThumbnailFailure &&
            string.Equals(_thumbnailFailedSourcePath, sourcePath, StringComparison.OrdinalIgnoreCase) &&
            _thumbnailFailedRotation == rotation)
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
            if (thumbnailPaths is null)
            {
                _thumbnailFailedSourcePath = sourcePath;
                _thumbnailFailedRotation = rotation;
                _hasThumbnailFailure = true;
                return;
            }

            if (cancellation.IsCancellationRequested || _isClosing ||
                EditorPanel.Visibility != Visibility.Visible ||
                !string.Equals(_loadedPath, sourcePath, StringComparison.OrdinalIgnoreCase))
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
                !string.Equals(_loadedPath, sourcePath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _thumbnailBitmaps = bitmaps;
            _thumbnailFrameCount = bitmaps.Count;
            _thumbnailSourcePath = sourcePath;
            _thumbnailRotation = rotation;
            _thumbnailFailedSourcePath = null;
            _hasThumbnailFailure = false;
            PositionThumbnailStrip(Math.Max(0, TrimTimelineCanvas.ActualWidth - 16));
        }
        catch (OperationCanceledException)
        {
            // Loading a new video, rotation, or editor state supersedes this strip.
        }
        catch
        {
            // Keep the real timeline rail available if ffmpeg cannot decode a still frame.
            _thumbnailFailedSourcePath = sourcePath;
            _thumbnailFailedRotation = rotation;
            _hasThumbnailFailure = true;
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
            App.WriteErrorLog(ex);
            ShowError(Strings.Get("Error_VolumeFailed"));
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
            App.WriteErrorLog(ex);
            ShowError(Strings.Get("Error_MuteFailed"));
        }

        ScheduleSettingsSave();
    }

    private void UpdateMuteButton()
    {
        MuteIcon.Glyph = _isMuted ? "\uE74F" : "\uE767";
        MuteButton.SetValue(Microsoft.UI.Xaml.Automation.AutomationProperties.NameProperty, _isMuted ? Strings.Get("Accessibility_Unmute") : Strings.Get("Accessibility_Mute"));
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
        if (EditorPanel.Visibility != Visibility.Visible)
        {
            ApplyPlayerSpeed(speed);
        }

        ScheduleSettingsSave();
    }

    private void EditSpeedMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleMenuFlyoutItem item ||
            !double.TryParse(item.Tag?.ToString(), CultureInfo.InvariantCulture, out var speed))
        {
            return;
        }

        _editSpeed = speed;
        SaveEditorFieldsToCurrentSegment();
        UpdateEditSpeedControl(speed);
        if (EditorPanel.Visibility == Visibility.Visible)
        {
            ApplyEditorPreview();
        }

        UpdateApproximateCopyAvailability();
        ValidateEditRange();
    }

    private void ApplyPlayerSpeed(double speed)
    {
        try
        {
            _player?.SetSpeed(speed);
        }
        catch (Exception ex)
        {
            App.WriteErrorLog(ex);
            ShowError(Strings.Get("Error_SpeedFailed"));
        }
    }

    private void UpdateSpeedMenu(double speed)
    {
        foreach (var item in SpeedMenuItem.Items.OfType<ToggleMenuFlyoutItem>())
        {
            item.IsChecked = double.TryParse(item.Tag?.ToString(), CultureInfo.InvariantCulture, out var itemSpeed) &&
                Math.Abs(itemSpeed - speed) < 0.001;
        }

        SpeedMenuItem.Text = Strings.Format("PlaybackSpeed_Menu", speed.ToString("0.##", CultureInfo.InvariantCulture));
    }

    private void UpdateEditSpeedControl(double speed)
    {
        var label = $"{speed.ToString("0.##", CultureInfo.InvariantCulture)}×";
        EditSpeedText.Text = label;
        ToolTipService.SetToolTip(EditSpeedButton, Strings.Format("EditSpeed_Tooltip", label));
        EditSpeedButton.SetValue(Microsoft.UI.Xaml.Automation.AutomationProperties.NameProperty, Strings.Format("EditSpeed_Name", label));
        if (EditSpeedButton.Flyout is MenuFlyout flyout)
        {
            foreach (var item in flyout.Items.OfType<ToggleMenuFlyoutItem>())
            {
                item.IsChecked = double.TryParse(item.Tag?.ToString(), CultureInfo.InvariantCulture, out var itemSpeed) &&
                    Math.Abs(itemSpeed - speed) < 0.001;
            }
        }
    }

    private CropRect GetEditorZoomViewport() => _mediaInfo is null
        ? new CropRect(0, 0, 2, 2)
        : _crop ?? new CropRect(0, 0, _mediaInfo.DisplayWidth, _mediaInfo.DisplayHeight);

    private double GetMaximumEditorZoom()
    {
        var viewport = GetEditorZoomViewport();
        return Math.Clamp(Math.Min(4d, Math.Min(viewport.Width, viewport.Height) / 2d), 1d, 4d);
    }

    private void UpdateZoomControls()
    {
        if (ZoomSlider is null)
        {
            return;
        }

        var maximum = GetMaximumEditorZoom();
        if (_cropDragOrigin is null)
        {
            // A crop drag can pass through a tiny rectangle; clamp only once it settles.
            _editorZoomFactor = Math.Clamp(_editorZoomFactor, 1, maximum);
        }
        _isUpdatingZoomControls = true;
        try
        {
            ZoomSlider.Maximum = Math.Max(maximum, _editorZoomFactor);
            ZoomSlider.Value = _editorZoomFactor;
            var label = $"{_editorZoomFactor.ToString("0.#", CultureInfo.InvariantCulture)}×";
            ZoomFactorText.Text = label;
            ZoomFlyoutReadout.Text = label;
            var canZoom = _mediaInfo is not null && EditorPanel.Visibility == Visibility.Visible && !_isCropMode && !_isExporting;
            ZoomSlider.IsEnabled = canZoom && maximum > 1;
            ZoomResetButton.IsEnabled = canZoom && (_editorZoomFactor > 1.001 || Math.Abs(_editorZoomFocusX - 0.5) > 0.001 || Math.Abs(_editorZoomFocusY - 0.5) > 0.001);
            ZoomButton.IsEnabled = canZoom && maximum > 1;
        }
        finally
        {
            _isUpdatingZoomControls = false;
        }

        UpdateZoomPanOverlay();
    }

    private void ZoomSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_isUpdatingZoomControls || _mediaInfo is null || _isCropMode || _isExporting || EditorPanel.Visibility != Visibility.Visible)
        {
            return;
        }

        var previousZoom = _editorZoomFactor;
        var previousFocusX = _editorZoomFocusX;
        var previousFocusY = _editorZoomFocusY;
        _editorZoomFactor = Math.Clamp(Math.Round(e.NewValue, 1, MidpointRounding.AwayFromZero), 1, GetMaximumEditorZoom());
        ClampZoomFocusToSample();
        if (!ApplyEditorPreview())
        {
            _editorZoomFactor = previousZoom;
            _editorZoomFocusX = previousFocusX;
            _editorZoomFocusY = previousFocusY;
            UpdateZoomControls();
            ApplyEditorPreview(showError: false);
            return;
        }

        SaveEditorFieldsToCurrentSegment();
        UpdateSegmentControls();
        UpdateZoomControls();
        UpdateApproximateCopyAvailability();
    }

    private void ZoomResetButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isExporting || _isCropMode)
        {
            return;
        }

        var previousZoom = _editorZoomFactor;
        var previousFocusX = _editorZoomFocusX;
        var previousFocusY = _editorZoomFocusY;
        _editorZoomFactor = 1;
        _editorZoomFocusX = 0.5;
        _editorZoomFocusY = 0.5;
        if (!ApplyEditorPreview())
        {
            _editorZoomFactor = previousZoom;
            _editorZoomFocusX = previousFocusX;
            _editorZoomFocusY = previousFocusY;
            ApplyEditorPreview(showError: false);
        }

        SaveEditorFieldsToCurrentSegment();
        UpdateSegmentControls();
        UpdateZoomControls();
        UpdateApproximateCopyAvailability();
    }

    private void ClampZoomFocusToSample()
    {
        if (_mediaInfo is null || _editorZoomFactor <= 1)
        {
            _editorZoomFocusX = 0.5;
            _editorZoomFocusY = 0.5;
            return;
        }

        var viewport = GetEditorZoomViewport();
        var sample = MediaGeometry.ComputeZoomSampleRect(viewport, _editorZoomFactor, 0.5, 0.5);
        var halfWidth = sample.Width / (2d * viewport.Width);
        var halfHeight = sample.Height / (2d * viewport.Height);
        _editorZoomFocusX = Math.Clamp(_editorZoomFocusX, halfWidth, 1 - halfWidth);
        _editorZoomFocusY = Math.Clamp(_editorZoomFocusY, halfHeight, 1 - halfHeight);
    }

    private bool ApplyEditorPreview(bool showError = true)
    {
        if (_player is null)
        {
            UpdateZoomPanOverlay();
            return true;
        }

        try
        {
            if (_mediaInfo is null || EditorPanel.Visibility != Visibility.Visible)
            {
                _player.SetVideoCrop(null);
                _player.SetVideoAspectOverride(null);
                _player.SetVideoRotation(0);
                _player.SetSpeed(_settings.Speed);
                UpdateZoomPanOverlay();
                return true;
            }

            _player.SetSpeed(_editSpeed);
            _player.SetVideoRotation(_isCropMode ? 0 : _additionalRotationDegreesClockwise);
            if (_isCropMode)
            {
                _player.SetVideoCrop(null);
                _player.SetVideoAspectOverride(null);
            }
            else if (_crop.HasValue || _editorZoomFactor > 1.001)
            {
                _editorZoomFactor = Math.Clamp(_editorZoomFactor, 1, GetMaximumEditorZoom());
                ClampZoomFocusToSample();
                var viewport = GetEditorZoomViewport();
                var sample = MediaGeometry.ComputeZoomSampleRect(viewport, _editorZoomFactor, _editorZoomFocusX, _editorZoomFocusY);
                var codedCrop = MediaGeometry.MapDisplayRectToCodedRect(_mediaInfo, sample);
                var (aspectWidth, aspectHeight) = MediaGeometry.ComputeBaseViewportDimensions(_mediaInfo, _crop);
                if (_mediaInfo.RotationDegreesClockwise is 90 or 270)
                {
                    (aspectWidth, aspectHeight) = (aspectHeight, aspectWidth);
                }

                var desiredPreMetadataAspect = aspectWidth / (double)aspectHeight;
                var codedCropAspect = codedCrop.Width / (double)codedCrop.Height;
                var codedSourceAspect = _mediaInfo.CodedWidth / (double)_mediaInfo.CodedHeight;
                var sourceAspectOverride = desiredPreMetadataAspect / codedCropAspect * codedSourceAspect;
                _player.SetVideoCrop(codedCrop);
                _player.SetVideoAspectOverride(sourceAspectOverride);
            }
            else
            {
                _player.SetVideoCrop(null);
                _player.SetVideoAspectOverride(null);
            }

            UpdateZoomPanOverlay();
            return true;
        }
        catch (Exception ex)
        {
            App.WriteErrorLog(ex);
            if (showError)
            {
                ShowError(Strings.Get("Error_PreviewFailed"));
            }

            return false;
        }
    }

    private void UpdateZoomPanOverlay()
    {
        if (ZoomPanCanvas is null)
        {
            return;
        }

        var enabled = _mediaInfo is not null && EditorPanel.Visibility == Visibility.Visible && !_isCropMode && !_isExporting && _editorZoomFactor > 1.001;
        ZoomPanCanvas.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        ZoomPanCanvas.IsHitTestVisible = enabled;
        if (!enabled && _isZoomPanning)
        {
            EndZoomPan(_zoomPanPointer);
        }
    }

    private Windows.Foundation.Rect GetZoomPreviewContentBounds()
    {
        return GetVideoContentBounds(ZoomPanCanvas.ActualWidth, ZoomPanCanvas.ActualHeight);
    }

    private void ZoomPanCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_mediaInfo is null || _isCropMode || _isExporting || _editorZoomFactor <= 1.001 || !e.GetCurrentPoint(ZoomPanCanvas).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var point = e.GetCurrentPoint(ZoomPanCanvas).Position;
        if (!GetZoomPreviewContentBounds().Contains(point))
        {
            return;
        }

        _isZoomPanning = true;
        _zoomPanPointerOrigin = point;
        _zoomPanPointer = e.Pointer;
        _zoomPanFocusXOrigin = _editorZoomFocusX;
        _zoomPanFocusYOrigin = _editorZoomFocusY;
        ZoomPanCanvas.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void ZoomPanCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isZoomPanning || _zoomPanPointerOrigin is not { } origin || _mediaInfo is null)
        {
            return;
        }

        if (!e.GetCurrentPoint(ZoomPanCanvas).Properties.IsLeftButtonPressed)
        {
            EndZoomPan(e.Pointer);
            return;
        }

        var bounds = GetZoomPreviewContentBounds();
        var (baseWidth, baseHeight) = MediaGeometry.ComputeBaseViewportDimensions(_mediaInfo, _crop);
        var rotatedWidth = _additionalRotationDegreesClockwise is 90 or 270 ? baseHeight : baseWidth;
        if (bounds.Width <= 0 || bounds.Height <= 0 || rotatedWidth <= 0)
        {
            return;
        }

        var scale = bounds.Width / rotatedWidth;
        var point = e.GetCurrentPoint(ZoomPanCanvas).Position;
        var deltaX = point.X - origin.X;
        var deltaY = point.Y - origin.Y;
        var zoomScale = scale * _editorZoomFactor;
        var (focusDeltaX, focusDeltaY) = _additionalRotationDegreesClockwise switch
        {
            90 => (-deltaY / (zoomScale * baseWidth), deltaX / (zoomScale * baseHeight)),
            180 => (deltaX / (zoomScale * baseWidth), deltaY / (zoomScale * baseHeight)),
            270 => (deltaY / (zoomScale * baseWidth), -deltaX / (zoomScale * baseHeight)),
            _ => (-deltaX / (zoomScale * baseWidth), -deltaY / (zoomScale * baseHeight))
        };

        var previousX = _editorZoomFocusX;
        var previousY = _editorZoomFocusY;
        _editorZoomFocusX = Math.Clamp(_zoomPanFocusXOrigin + focusDeltaX, 0, 1);
        _editorZoomFocusY = Math.Clamp(_zoomPanFocusYOrigin + focusDeltaY, 0, 1);
        ClampZoomFocusToSample();
        if (!ApplyEditorPreview())
        {
            _editorZoomFocusX = previousX;
            _editorZoomFocusY = previousY;
            ApplyEditorPreview(showError: false);
        }
        else
        {
            SaveEditorFieldsToCurrentSegment();
            UpdateSegmentControls();
            UpdateZoomControls();
        }

        e.Handled = true;
    }

    private void ZoomPanCanvas_PointerReleased(object sender, PointerRoutedEventArgs e) => EndZoomPan(e.Pointer);
    private void ZoomPanCanvas_PointerCanceled(object sender, PointerRoutedEventArgs e) => EndZoomPan(e.Pointer);
    private void ZoomPanCanvas_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => EndZoomPan(null);

    private void EndZoomPan(Pointer? pointer)
    {
        var capturedPointer = pointer ?? _zoomPanPointer;
        _isZoomPanning = false;
        _zoomPanPointerOrigin = null;
        _zoomPanPointer = null;
        if (capturedPointer is not null)
        {
            ZoomPanCanvas.ReleasePointerCapture(capturedPointer);
        }
    }

    private void UpdateSkipLabels()
    {
        var seconds = _settings.SkipSeconds.ToString("0.##", CultureInfo.InvariantCulture);
        BackSkipText.Text = seconds;
        ForwardSkipText.Text = seconds;
        var backLabel = Strings.Format("Accessibility_SkipBack", seconds);
        var forwardLabel = Strings.Format("Accessibility_SkipForward", seconds);
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
                App.WriteErrorLog(ex);
                ShowError(Strings.Get("Error_SettingsSaveFailed"));
            }
        }
    }

    private async void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var skipBox = new NumberBox
        {
            Header = Strings.Get("Settings_SkipHeader"),
            Minimum = 1,
            Maximum = 600,
            Value = _settings.SkipSeconds,
            SmallChange = 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Width = 160,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        var languageLabel = new TextBlock
        {
            Text = Strings.Get("Settings_LanguageHeader")
        };
        var languageBox = new ComboBox { MinWidth = 280 };
        languageBox.Items.Add(new ComboBoxItem { Content = Strings.Get("Settings_LanguageSystem"), Tag = "" });
        languageBox.Items.Add(new ComboBoxItem { Content = Strings.Get("Settings_LanguageJapanese"), Tag = "ja-JP" });
        languageBox.Items.Add(new ComboBoxItem { Content = Strings.Get("Settings_LanguageEnglish"), Tag = "en-US" });
        languageBox.SelectedIndex = _settings.Language switch
        {
            "ja-JP" => 1,
            "en-US" => 2,
            _ => 0
        };
        var frameSaveLabel = new TextBlock
        {
            Text = Strings.Get("Settings_FrameSaveHeader")
        };
        var frameSaveBox = new ComboBox { MinWidth = 280 };
        frameSaveBox.Items.Add(new ComboBoxItem { Content = Strings.Get("Settings_FrameSavePictures"), Tag = FrameSaveLocation.Pictures });
        frameSaveBox.Items.Add(new ComboBoxItem { Content = Strings.Get("Settings_FrameSaveCustom"), Tag = FrameSaveLocation.CustomFolder });
        frameSaveBox.Items.Add(new ComboBoxItem { Content = Strings.Get("Settings_FrameSaveVideoFolder"), Tag = FrameSaveLocation.VideoFolder });
        frameSaveBox.Items.Add(new ComboBoxItem { Content = Strings.Get("Settings_FrameSaveVideoSubfolder"), Tag = FrameSaveLocation.VideoSubfolder });
        frameSaveBox.SelectedIndex = (int)_settings.FrameSaveLocation;
        var frameSaveFolder = _settings.FrameSaveFolder;
        var frameSaveFolderText = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(frameSaveFolder) ? Strings.Get("Settings_FrameSaveFolderNone") : frameSaveFolder,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 300
        };
        ToolTipService.SetToolTip(frameSaveFolderText, frameSaveFolderText.Text);
        var frameSaveFolderButton = new Button { Content = Strings.Get("Settings_FrameSaveChoose") };
        frameSaveFolderButton.Click += async (_, _) =>
        {
            try
            {
                var picker = new FolderPicker();
                picker.FileTypeFilter.Add("*");
                InitializeWithWindow.Initialize(picker, _windowHandle);
                var folder = await picker.PickSingleFolderAsync();
                if (folder is not null)
                {
                    frameSaveFolder = folder.Path;
                    frameSaveFolderText.Text = folder.Path;
                    ToolTipService.SetToolTip(frameSaveFolderText, folder.Path);
                }
            }
            catch (Exception pickerFailure)
            {
                App.WriteErrorLog(pickerFailure);
                ShowError(Strings.Get("Error_FolderPickerFailed"));
            }
        };
        var frameSaveFolderRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        frameSaveFolderRow.Children.Add(frameSaveFolderButton);
        frameSaveFolderRow.Children.Add(frameSaveFolderText);
        var frameSaveSubfolderBox = new TextBox
        {
            Header = Strings.Get("Settings_FrameSaveSubfolderHeader"),
            Text = _settings.FrameSaveSubfolder,
            MaxLength = 80,
            Width = 280,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 4, 0, 0)
        };
        void UpdateFrameSaveFields()
        {
            var location = (frameSaveBox.SelectedItem as ComboBoxItem)?.Tag as FrameSaveLocation? ?? FrameSaveLocation.Pictures;
            frameSaveFolderRow.Visibility = location == FrameSaveLocation.CustomFolder ? Visibility.Visible : Visibility.Collapsed;
            frameSaveSubfolderBox.Visibility = location == FrameSaveLocation.VideoSubfolder ? Visibility.Visible : Visibility.Collapsed;
        }

        frameSaveBox.SelectionChanged += (_, _) => UpdateFrameSaveFields();
        UpdateFrameSaveFields();
        // Each setting keeps its label close; settings are spaced apart from each other.
        var frameSaveGroup = new StackPanel { Spacing = 8 };
        frameSaveGroup.Children.Add(frameSaveLabel);
        frameSaveGroup.Children.Add(frameSaveBox);
        frameSaveGroup.Children.Add(frameSaveFolderRow);
        frameSaveGroup.Children.Add(frameSaveSubfolderBox);
        var languageGroup = new StackPanel { Spacing = 8 };
        languageGroup.Children.Add(languageLabel);
        languageGroup.Children.Add(languageBox);
        var settingsPanel = new StackPanel { Spacing = 24, MinWidth = 360, Margin = new Thickness(0, 8, 0, 4) };
        settingsPanel.Children.Add(skipBox);
        settingsPanel.Children.Add(frameSaveGroup);
        settingsPanel.Children.Add(languageGroup);
        var dialog = new ContentDialog
        {
            Title = Strings.Get("Settings_Title"),
            Content = settingsPanel,
            PrimaryButtonText = Strings.Get("Settings_Save"),
            CloseButtonText = Strings.Get("Dialog_Cancel"),
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
                    ShowError(Strings.Get("Error_SkipRange"));
                    return;
                }

                var frameSaveLocation = (frameSaveBox.SelectedItem as ComboBoxItem)?.Tag as FrameSaveLocation? ?? FrameSaveLocation.Pictures;
                if (frameSaveLocation == FrameSaveLocation.CustomFolder && (string.IsNullOrWhiteSpace(frameSaveFolder) || !Directory.Exists(frameSaveFolder)))
                {
                    ShowError(Strings.Get("Error_FrameSaveFolderRequired"));
                    return;
                }

                if (frameSaveLocation == FrameSaveLocation.VideoSubfolder && !PlayerSettings.IsValidFolderName(frameSaveSubfolderBox.Text))
                {
                    ShowError(Strings.Get("Error_FrameSaveSubfolderInvalid"));
                    return;
                }

                _settings.FrameSaveLocation = frameSaveLocation;
                _settings.FrameSaveFolder = frameSaveFolder ?? "";
                if (PlayerSettings.IsValidFolderName(frameSaveSubfolderBox.Text))
                {
                    _settings.FrameSaveSubfolder = frameSaveSubfolderBox.Text.Trim();
                }

                _settings.SkipSeconds = skipBox.Value;
                UpdateSkipLabels();
                var selectedLanguage = (languageBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
                var languageChanged = !string.Equals(_settings.Language, selectedLanguage, StringComparison.Ordinal);
                _settings.Language = selectedLanguage;
                await SaveSettingsAsync();
                if (languageChanged)
                {
                    ShowNotice(Strings.Get("Settings_LanguageRestartNote"), InfoBarSeverity.Informational);
                }
            }
        }
        catch (Exception ex)
        {
            // ShowAsync throws while another dialog is open.
            App.WriteErrorLog(ex);
            ShowError(Strings.Get("Error_SettingsOpenFailed"));
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
        var fullscreenLabel = _isFullscreen ? Strings.Get("Fullscreen_Exit") : Strings.Get("Fullscreen_Enter");
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
            PlayerDisplayMode.AlwaysVisible => Strings.Get("DisplayMode_Always"),
            PlayerDisplayMode.MaximizedOverlay => Strings.Get("DisplayMode_Maximized"),
            PlayerDisplayMode.Fullscreen => Strings.Get("DisplayMode_Fullscreen"),
            _ => Strings.Get("DisplayMode_Plain")
        };
    }

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!e.Handled)
        {
            ShowControls();
            ScheduleControlsHide();
        }

        // This runs as PreviewKeyDown so a focused Button cannot swallow Space
        // (or replay its own click) before the player shortcuts see it.
        var focusedInput = GetFocusedInputKind();
        if (e.Handled || focusedInput == FocusedInputKind.TextEntry)
        {
            return;
        }

        // A focused Slider keeps its own arrow/Home/End stepping; every other
        // shortcut still works after clicking the timeline or volume bar.
        if (focusedInput == FocusedInputKind.Slider && e.Key is
            Windows.System.VirtualKey.Left or Windows.System.VirtualKey.Right or
            Windows.System.VirtualKey.Up or Windows.System.VirtualKey.Down or
            Windows.System.VirtualKey.Home or Windows.System.VirtualKey.End or
            Windows.System.VirtualKey.PageUp or Windows.System.VirtualKey.PageDown)
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
            case Windows.System.VirtualKey.I when EditorPanel.Visibility == Visibility.Visible && !_isExporting:
                MarkTrimStartButton_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.O when EditorPanel.Visibility == Visibility.Visible && !_isExporting:
                MarkTrimEndButton_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.S when EditorPanel.Visibility == Visibility.Visible && !_isExporting:
                SplitButton_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Delete when EditorPanel.Visibility == Visibility.Visible && !_isExporting:
                DeleteSegmentButton_Click(this, new RoutedEventArgs());
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

    private enum FocusedInputKind
    {
        None,
        TextEntry,
        Slider
    }

    private FocusedInputKind GetFocusedInputKind()
    {
        var focused = FocusManager.GetFocusedElement(RootGrid.XamlRoot) as DependencyObject;
        while (focused is not null)
        {
            if (focused is TextBox or PasswordBox or ComboBox or NumberBox)
            {
                return FocusedInputKind.TextEntry;
            }

            if (focused is Slider)
            {
                return FocusedInputKind.Slider;
            }

            focused = VisualTreeHelper.GetParent(focused);
        }

        return FocusedInputKind.None;
    }

    private void EditButton_Click(object sender, RoutedEventArgs e)
    {
        if (_mediaInfo is null)
        {
            ShowError(Strings.Get("Error_NoEditableVideo"));
            return;
        }

        var showEditor = EditorPanel.Visibility != Visibility.Visible;
        EditorPanel.Visibility = showEditor ? Visibility.Visible : Visibility.Collapsed;
        SetEditorLayout(showEditor);
        ApplyPlayerSpeed(showEditor ? _editSpeed : _settings.Speed);
        if (showEditor)
        {
            ShowControls();
            if (_segments.Count == 0)
            {
                ResetSegmentsToFullRange();
            }

            ValidateEditRange();
            var openPosition = _player?.GetNumber("time-pos") ?? GetEditorRangeStart();
            var openSelected = GetSelectedSegmentIndex(openPosition);
            if (openSelected >= 0 && openSelected != _currentSegmentIndex)
            {
                SwitchEditorSegment(openSelected);
            }

            UpdateTrimTimeline();
            UpdateApproximateCopyAvailability();
            UpdateSegmentControls();
            // Recompute the overlay visibility so a kept crop range shows its mask again.
            SetCropMode(false);
            _ = LoadTrimThumbnailsAsync();
        }
        else
        {
            SaveEditorFieldsToCurrentSegment();
            SetCropMode(false);
            CancelThumbnailGeneration();
            UpdateSegmentControls();
            ShowControls();
            ScheduleControlsHide();
        }

        ApplyEditorPreview();
        UpdateZoomControls();
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

    private void ResetEditButton_Click(object sender, RoutedEventArgs e)
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
        _editorZoomFactor = 1;
        _editorZoomFocusX = 0.5;
        _editorZoomFocusY = 0.5;
        _isTrimTimelineDragging = false;
        TrimStartBox.Value = 0;
        TrimEndBox.Value = _mediaInfo?.DurationSeconds ?? 0;
        ApproximateCopyCheckBox.IsChecked = false;
        OutputContainerComboBox.SelectedIndex = 0;
        _outputDirectory = _loadedPath is null ? null : Path.GetDirectoryName(_loadedPath);
        OutputFolderText.Text = Strings.Get("OutputFolder_Default");
        ToolTipService.SetToolTip(OutputFolderText, Strings.Get("OutputFolder_Default"));
        OutputNameBox.Text = _loadedPath is null ? string.Empty : $"{Path.GetFileNameWithoutExtension(_loadedPath)}-clip";

        _editSpeed = 1;
        UpdateEditSpeedControl(_editSpeed);
        ResetSegmentsToFullRange();
        UpdateCropInfo();
        UpdateRotationControl();
        UpdateApproximateCopyAvailability();
        ValidateEditRange();
        UpdateCropOverlay();
        ApplyEditorPreview();
        UpdateZoomControls();
        _ = LoadTrimThumbnailsAsync();
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
        var copyItem = new MenuFlyoutItem { Text = Strings.Get("ContextMenu_CopyFrame") };
        copyItem.Icon = new FontIcon { Glyph = "\uE8C8" };
        copyItem.Click += CopyFrameButton_Click;
        menu.Items.Add(copyItem);
        menu.ShowAt(VideoRegion, e.GetPosition(VideoRegion));
        e.Handled = true;
    }

    private void TrimBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        SyncSegmentsToRange();
        ValidateEditRange();
    }

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

    private double GetEditorRangeStart()
    {
        var duration = _mediaInfo?.DurationSeconds ?? 0;
        var start = TrimStartBox.Value;
        if (!double.IsFinite(start))
        {
            start = 0;
        }

        return Math.Clamp(start, 0, duration);
    }

    private double GetEditorRangeEnd()
    {
        var duration = _mediaInfo?.DurationSeconds ?? 0;
        var end = TrimEndBox.Value;
        if (!double.IsFinite(end))
        {
            end = duration;
        }

        return Math.Clamp(end, 0, duration);
    }

    private void ResetSegmentsToFullRange()
    {
        _segments.Clear();
        var start = GetEditorRangeStart();
        var end = GetEditorRangeEnd();
        if (!(end > start))
        {
            _currentSegmentIndex = -1;
            UpdateSegmentControls();
            return;
        }

        _segments.Add(new EditSegment
        {
            StartSeconds = start,
            EndSeconds = end,
            Crop = _crop,
            AdditionalRotationDegreesClockwise = _additionalRotationDegreesClockwise,
            ZoomFactor = _editorZoomFactor,
            ZoomFocusX = _editorZoomFocusX,
            ZoomFocusY = _editorZoomFocusY,
            Speed = _editSpeed,
        });
        _currentSegmentIndex = 0;
        UpdateSegmentControls();
    }

    private void SaveEditorFieldsToCurrentSegment()
    {
        if (_currentSegmentIndex < 0 || _currentSegmentIndex >= _segments.Count)
        {
            return;
        }

        var segment = _segments[_currentSegmentIndex];
        segment.Crop = _crop;
        segment.AdditionalRotationDegreesClockwise = _additionalRotationDegreesClockwise;
        segment.ZoomFactor = _editorZoomFactor;
        segment.ZoomFocusX = _editorZoomFocusX;
        segment.ZoomFocusY = _editorZoomFocusY;
        segment.Speed = _editSpeed;
    }

    private int GetSelectedSegmentIndex(double position)
    {
        if (_segments.Count == 0)
        {
            return -1;
        }

        if (!double.IsFinite(position))
        {
            position = 0;
        }

        for (var i = 0; i < _segments.Count; i++)
        {
            if (position < _segments[i].EndSeconds)
            {
                return i;
            }
        }

        return _segments.Count - 1;
    }

    private void SwitchEditorSegment(int newIndex)
    {
        if (newIndex < 0 || newIndex >= _segments.Count || newIndex == _currentSegmentIndex)
        {
            return;
        }

        EndZoomPan(null);
        if (_isCropMode)
        {
            SetCropMode(false);
            if (_crop is { } finished && _mediaInfo is { } finishedMedia)
            {
                var fullWidth = finishedMedia.DisplayWidth & ~1;
                var fullHeight = finishedMedia.DisplayHeight & ~1;
                if (finished.X == 0 && finished.Y == 0 && finished.Width == fullWidth && finished.Height == fullHeight)
                {
                    _crop = null;
                }
            }
        }

        SaveEditorFieldsToCurrentSegment();
        _currentSegmentIndex = newIndex;
        var segment = _segments[newIndex];
        _crop = segment.Crop;
        _additionalRotationDegreesClockwise = segment.AdditionalRotationDegreesClockwise;
        _editorZoomFactor = segment.ZoomFactor;
        _editorZoomFocusX = segment.ZoomFocusX;
        _editorZoomFocusY = segment.ZoomFocusY;
        _editSpeed = segment.Speed;
        UpdateRotationControl();
        UpdateZoomControls();
        UpdateEditSpeedControl(_editSpeed);
        UpdateCropInfo();
        UpdateApproximateCopyAvailability();
        UpdateCropOverlay();
        ApplyEditorPreview();
        UpdateSegmentControls();
        ValidateEditRange();
    }

    private void SyncSegmentsToRange()
    {
        if (_isSyncingSegments || _isExporting)
        {
            return;
        }

        if (_mediaInfo is null || EditorPanel.Visibility != Visibility.Visible)
        {
            return;
        }

        var duration = _mediaInfo.DurationSeconds;
        if (!double.IsFinite(duration) || duration <= 0)
        {
            return;
        }

        var start = GetEditorRangeStart();
        var end = GetEditorRangeEnd();
        if (!(end > start))
        {
            return;
        }

        _isSyncingSegments = true;
        try
        {
            SaveEditorFieldsToCurrentSegment();
            if (_segments.Count == 0)
            {
                _segments.Add(new EditSegment
                {
                    StartSeconds = start,
                    EndSeconds = end,
                    Crop = _crop,
                    AdditionalRotationDegreesClockwise = _additionalRotationDegreesClockwise,
                    ZoomFactor = _editorZoomFactor,
                    ZoomFocusX = _editorZoomFocusX,
                    ZoomFocusY = _editorZoomFocusY,
                    Speed = _editSpeed,
                });
                _currentSegmentIndex = 0;
                UpdateSegmentControls();
                return;
            }

            var current = _currentSegmentIndex >= 0 && _currentSegmentIndex < _segments.Count
                ? _segments[_currentSegmentIndex]
                : null;
            _segments.RemoveAll(segment => segment.EndSeconds <= start || segment.StartSeconds >= end);
            if (_segments.Count == 0)
            {
                _segments.Add(new EditSegment
                {
                    StartSeconds = start,
                    EndSeconds = end,
                    Crop = _crop,
                    AdditionalRotationDegreesClockwise = _additionalRotationDegreesClockwise,
                    ZoomFactor = _editorZoomFactor,
                    ZoomFocusX = _editorZoomFocusX,
                    ZoomFocusY = _editorZoomFocusY,
                    Speed = _editSpeed,
                });
                _currentSegmentIndex = 0;
                UpdateSegmentControls();
                return;
            }

            _segments.Sort((a, b) => a.StartSeconds.CompareTo(b.StartSeconds));
            _segments[0].StartSeconds = start;
            _segments[^1].EndSeconds = end;
            for (var i = 0; i < _segments.Count;)
            {
                var length = _segments[i].EndSeconds - _segments[i].StartSeconds;
                if (length < SegmentMinLength && _segments.Count > 1)
                {
                    var removed = _segments[i];
                    _segments.RemoveAt(i);
                    if (i < _segments.Count)
                    {
                        _segments[i].StartSeconds = removed.StartSeconds;
                    }
                    else
                    {
                        _segments[^1].EndSeconds = removed.EndSeconds;
                    }

                    continue;
                }

                i++;
            }

            if (_segments.TrueForAll(segment => segment.IsDeleted))
            {
                // The range moved off every kept segment; bring one back so there is something to export.
                _segments[0].IsDeleted = false;
            }

            if (current is not null && _segments.Contains(current))
            {
                _currentSegmentIndex = _segments.IndexOf(current);
            }
            else
            {
                if (_isCropMode)
                {
                    SetCropMode(false);
                }

                var position = _player?.GetNumber("time-pos") ?? start;
                if (!double.IsFinite(position))
                {
                    position = start;
                }

                _currentSegmentIndex = GetSelectedSegmentIndex(Math.Clamp(position, start, end));
                var segment = _segments[_currentSegmentIndex];
                _crop = segment.Crop;
                _additionalRotationDegreesClockwise = segment.AdditionalRotationDegreesClockwise;
                _editorZoomFactor = segment.ZoomFactor;
                _editorZoomFocusX = segment.ZoomFocusX;
                _editorZoomFocusY = segment.ZoomFocusY;
                _editSpeed = segment.Speed;
                UpdateRotationControl();
                UpdateZoomControls();
                UpdateEditSpeedControl(_editSpeed);
                UpdateCropInfo();
                UpdateApproximateCopyAvailability();
                UpdateCropOverlay();
                ApplyEditorPreview();
            }

            UpdateSegmentControls();
        }
        finally
        {
            _isSyncingSegments = false;
        }
    }

    private void UpdateSegmentControls()
    {
        var showGroup = EditorPanel.Visibility == Visibility.Visible && _segments.Count >= 2;
        SegmentGroup.Visibility = showGroup ? Visibility.Visible : Visibility.Collapsed;
        SegmentSeparator.Visibility = showGroup ? Visibility.Visible : Visibility.Collapsed;
        if (showGroup && _currentSegmentIndex >= 0 && _currentSegmentIndex < _segments.Count)
        {
            var readout = $"{_currentSegmentIndex + 1} / {_segments.Count}";
            if (_segments[_currentSegmentIndex].IsDeleted)
            {
                readout += Strings.Get("Segment_DeletedSuffix");
            }

            SegmentReadoutText.Text = readout;
        }

        if (EditorPanel.Visibility != Visibility.Visible || _mediaInfo is null || _isExporting ||
            _currentSegmentIndex < 0 || _currentSegmentIndex >= _segments.Count)
        {
            SplitButton.IsEnabled = false;
            DeleteSegmentButton.IsEnabled = false;
            MergeSegmentButton.IsEnabled = false;
            return;
        }

        var current = _segments[_currentSegmentIndex];
        var remainingCount = 0;
        foreach (var segment in _segments)
        {
            if (!segment.IsDeleted)
            {
                remainingCount++;
            }
        }

        MergeSegmentButton.IsEnabled = _segments.Count > 1 && _currentSegmentIndex > 0;
        if (_segments.Count <= 1)
        {
            DeleteSegmentButton.IsEnabled = false;
        }
        else
        {
            DeleteSegmentButton.IsEnabled = current.IsDeleted || remainingCount > 1;
        }

        if (current.IsDeleted)
        {
            DeleteSegmentIcon.Glyph = "\uE7A7";
            ToolTipService.SetToolTip(DeleteSegmentButton, Strings.Get("DeleteSegment_TooltipRestore"));
            DeleteSegmentButton.SetValue(Microsoft.UI.Xaml.Automation.AutomationProperties.NameProperty, Strings.Get("DeleteSegment_NameRestore"));
        }
        else
        {
            DeleteSegmentIcon.Glyph = "\uE74D";
            ToolTipService.SetToolTip(DeleteSegmentButton, Strings.Get("DeleteSegment_TooltipDelete"));
            DeleteSegmentButton.SetValue(Microsoft.UI.Xaml.Automation.AutomationProperties.NameProperty, Strings.Get("DeleteSegment_NameDelete"));
        }

        var canSplit = _segments.Count < MaxSegmentCount && !current.IsDeleted;
        if (canSplit)
        {
            var splitPosition = _player?.GetNumber("time-pos");
            if (!splitPosition.HasValue || !double.IsFinite(splitPosition.Value))
            {
                canSplit = false;
            }
            else
            {
                var distanceFromStart = splitPosition.Value - current.StartSeconds;
                var distanceFromEnd = current.EndSeconds - splitPosition.Value;
                canSplit = distanceFromStart >= SegmentSplitEdgeTolerance && distanceFromEnd >= SegmentSplitEdgeTolerance;
            }
        }

        SplitButton.IsEnabled = canSplit;
    }

    private void EnsureSegmentTimelineElements()
    {
        if (_segmentSelectedBar is null)
        {
            // A white outline reads against both the thumbnails and the accent-colored range frame.
            _segmentSelectedBar = new Border
            {
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(3),
                CornerRadius = new CornerRadius(3),
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
            };
            TrimTimelineCanvas.Children.Add(_segmentSelectedBar);
        }

        while (_segmentSplitLines.Count < Math.Max(0, _segments.Count - 1))
        {
            var line = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = 2,
                Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)),
                Opacity = 0.9,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
            };
            _segmentSplitLines.Add(line);
            TrimTimelineCanvas.Children.Add(line);
        }

        while (_segmentDeletedOverlays.Count < _segments.Count)
        {
            var overlay = new Border
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xAA, 0x00, 0x00, 0x00)),
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
            };
            _segmentDeletedOverlays.Add(overlay);
            TrimTimelineCanvas.Children.Add(overlay);
        }

        Canvas.SetZIndex(TrimStartHandle, 10);
        Canvas.SetZIndex(TrimEndHandle, 10);
        Canvas.SetZIndex(TrimPlayheadLine, 11);
        Canvas.SetZIndex(TrimPlayheadKnob, 11);
        if (_segmentSelectedBar is not null)
        {
            Canvas.SetZIndex(_segmentSelectedBar, 4);
        }

        foreach (var line in _segmentSplitLines)
        {
            Canvas.SetZIndex(line, 3);
        }

        foreach (var overlay in _segmentDeletedOverlays)
        {
            Canvas.SetZIndex(overlay, 2);
        }
    }

    private void HideSegmentTimelineElements()
    {
        if (_segmentSelectedBar is not null)
        {
            _segmentSelectedBar.Visibility = Visibility.Collapsed;
        }

        foreach (var line in _segmentSplitLines)
        {
            line.Visibility = Visibility.Collapsed;
        }

        foreach (var overlay in _segmentDeletedOverlays)
        {
            overlay.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateSegmentTimelineVisuals(double trackWidth, double duration, double pad, double trackY, double trackHeight, double position)
    {
        if (EditorPanel.Visibility != Visibility.Visible || _mediaInfo is null || _segments.Count == 0)
        {
            HideSegmentTimelineElements();
            return;
        }

        EnsureSegmentTimelineElements();
        var show = EditorPanel.Visibility == Visibility.Visible && _mediaInfo is not null && _segments.Count > 0;
        for (var i = 0; i < _segmentSplitLines.Count; i++)
        {
            var line = _segmentSplitLines[i];
            if (show && _segments.Count >= 2 && i < _segments.Count - 1)
            {
                var boundary = _segments[i].EndSeconds;
                var x = pad + boundary / duration * trackWidth;
                SetTimelineElement(line, x - 1, trackY, 2, trackHeight);
                line.Visibility = Visibility.Visible;
            }
            else
            {
                line.Visibility = Visibility.Collapsed;
            }
        }

        if (_segmentSelectedBar is not null)
        {
            if (show && _segments.Count >= 2 && _currentSegmentIndex >= 0 && _currentSegmentIndex < _segments.Count)
            {
                var selected = _segments[_currentSegmentIndex];
                var x = pad + selected.StartSeconds / duration * trackWidth;
                var width = Math.Max(0, (selected.EndSeconds - selected.StartSeconds) / duration * trackWidth);
                SetTimelineElement(_segmentSelectedBar, x, trackY, width, trackHeight);
                _segmentSelectedBar.Visibility = Visibility.Visible;
            }
            else
            {
                _segmentSelectedBar.Visibility = Visibility.Collapsed;
            }
        }

        for (var i = 0; i < _segmentDeletedOverlays.Count; i++)
        {
            var overlay = _segmentDeletedOverlays[i];
            if (show && i < _segments.Count && _segments[i].IsDeleted)
            {
                var segment = _segments[i];
                var x = pad + segment.StartSeconds / duration * trackWidth;
                var width = Math.Max(0, (segment.EndSeconds - segment.StartSeconds) / duration * trackWidth);
                SetTimelineElement(overlay, x, trackY, width, trackHeight);
                overlay.Visibility = Visibility.Visible;
            }
            else
            {
                overlay.Visibility = Visibility.Collapsed;
            }
        }

        UpdateSegmentControls();
    }

    private void SplitButton_Click(object sender, RoutedEventArgs e)
    {
        if (EditorPanel.Visibility != Visibility.Visible || _mediaInfo is null || _isExporting)
        {
            return;
        }

        if (_currentSegmentIndex < 0 || _currentSegmentIndex >= _segments.Count || _segments.Count >= MaxSegmentCount)
        {
            return;
        }

        if (_isCropMode)
        {
            CropModeButton_Click(this, new RoutedEventArgs());
        }

        var current = _segments[_currentSegmentIndex];
        if (current.IsDeleted)
        {
            return;
        }

        var position = _player?.GetNumber("time-pos");
        if (!position.HasValue || !double.IsFinite(position.Value))
        {
            return;
        }

        var rounded = Math.Round(Math.Clamp(position.Value, 0, _mediaInfo.DurationSeconds), 2, MidpointRounding.AwayFromZero);
        if (!(rounded > current.StartSeconds && rounded < current.EndSeconds))
        {
            return;
        }

        if (!(rounded - current.StartSeconds >= SegmentSplitEdgeTolerance && current.EndSeconds - rounded >= SegmentSplitEdgeTolerance))
        {
            return;
        }

        SaveEditorFieldsToCurrentSegment();
        var right = new EditSegment
        {
            StartSeconds = rounded,
            EndSeconds = current.EndSeconds,
            Crop = current.Crop,
            AdditionalRotationDegreesClockwise = current.AdditionalRotationDegreesClockwise,
            ZoomFactor = current.ZoomFactor,
            ZoomFocusX = current.ZoomFocusX,
            ZoomFocusY = current.ZoomFocusY,
            Speed = current.Speed,
            IsDeleted = false,
        };
        current.EndSeconds = rounded;
        _segments.Insert(_currentSegmentIndex + 1, right);
        _currentSegmentIndex++;
        UpdateRotationControl();
        UpdateZoomControls();
        UpdateEditSpeedControl(_editSpeed);
        UpdateCropInfo();
        UpdateApproximateCopyAvailability();
        UpdateCropOverlay();
        ApplyEditorPreview();
        UpdateSegmentControls();
        ValidateEditRange();
    }

    private void DeleteSegmentButton_Click(object sender, RoutedEventArgs e)
    {
        if (EditorPanel.Visibility != Visibility.Visible || _mediaInfo is null || _isExporting)
        {
            return;
        }

        if (_currentSegmentIndex < 0 || _currentSegmentIndex >= _segments.Count || _segments.Count <= 1)
        {
            return;
        }

        var current = _segments[_currentSegmentIndex];
        if (!current.IsDeleted)
        {
            var remainingCount = 0;
            foreach (var segment in _segments)
            {
                if (!segment.IsDeleted)
                {
                    remainingCount++;
                }
            }

            if (remainingCount <= 1)
            {
                return;
            }

            SaveEditorFieldsToCurrentSegment();
            current.IsDeleted = true;
        }
        else
        {
            current.IsDeleted = false;
        }

        UpdateCropInfo();
        UpdateApproximateCopyAvailability();
        UpdateSegmentControls();
        ValidateEditRange();
    }

    private void MergeSegmentButton_Click(object sender, RoutedEventArgs e)
    {
        if (EditorPanel.Visibility != Visibility.Visible || _mediaInfo is null || _isExporting)
        {
            return;
        }

        if (_currentSegmentIndex <= 0 || _currentSegmentIndex >= _segments.Count || _segments.Count <= 1)
        {
            return;
        }

        if (_isCropMode)
        {
            SetCropMode(false);
        }

        var removed = _segments[_currentSegmentIndex];
        var previous = _segments[_currentSegmentIndex - 1];
        previous.EndSeconds = removed.EndSeconds;
        // Merging a kept segment into a deleted one must not delete the footage it carried.
        previous.IsDeleted = previous.IsDeleted && removed.IsDeleted;
        _segments.RemoveAt(_currentSegmentIndex);
        _currentSegmentIndex--;
        _crop = previous.Crop;
        _additionalRotationDegreesClockwise = previous.AdditionalRotationDegreesClockwise;
        _editorZoomFactor = previous.ZoomFactor;
        _editorZoomFocusX = previous.ZoomFocusX;
        _editorZoomFocusY = previous.ZoomFocusY;
        _editSpeed = previous.Speed;
        UpdateRotationControl();
        UpdateZoomControls();
        UpdateEditSpeedControl(_editSpeed);
        UpdateCropInfo();
        UpdateApproximateCopyAvailability();
        UpdateCropOverlay();
        ApplyEditorPreview();
        UpdateSegmentControls();
        ValidateEditRange();
    }

    private void ValidateEditRange()
    {
        SyncSegmentsToRange();
        if (_mediaInfo is null)
        {
            ExportButton.IsEnabled = false;
            EditRangeText.Text = Strings.Get("EditRange_Confirm");
            UpdateTrimTimeline();
            return;
        }

        var start = TrimStartBox.Value;
        var end = TrimEndBox.Value;
        var valid = double.IsFinite(start) && double.IsFinite(end) && start >= 0 && end > start && end <= _mediaInfo.DurationSeconds;
        ExportButton.IsEnabled = valid && !_isExporting;
        if (valid)
        {
            if (EditorPanel.Visibility == Visibility.Visible && _segments.Count >= 2)
            {
                var remainingCount = 0;
                var totalOutputLength = 0d;
                foreach (var segment in _segments)
                {
                    if (!segment.IsDeleted)
                    {
                        remainingCount++;
                        totalOutputLength += (segment.EndSeconds - segment.StartSeconds) / segment.Speed;
                    }
                }

                EditRangeText.Text = Strings.Format("EditRange_Multi", remainingCount, FormatTime(totalOutputLength));
            }
            else
            {
                EditRangeText.Text = Strings.Format("EditRange_Single", FormatTime(start), FormatTime(end), FormatTime(end - start));
            }
        }
        else
        {
            EditRangeText.Text = Strings.Get("EditRange_Invalid");
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
            ShowNotice(Strings.Get("Notice_CropNeedsZeroRotation"), InfoBarSeverity.Informational);
            return;
        }

        if (!_isCropMode && _crop is null)
        {
            var evenWidth = _mediaInfo.DisplayWidth & ~1;
            var evenHeight = _mediaInfo.DisplayHeight & ~1;
            if (evenWidth < 2 || evenHeight < 2)
            {
                ShowError(Strings.Get("Error_CropImpossible"));
                return;
            }

            _crop = new CropRect(0, 0, evenWidth, evenHeight);
            UpdateCropInfo();
            UpdateApproximateCopyAvailability();
        }

        var completingCrop = _isCropMode;
        SetCropMode(!_isCropMode);
        if (completingCrop && _crop is { } finished && _mediaInfo is { } finishedMedia)
        {
            // Accepting the untouched full-frame range is the same as no crop.
            var fullWidth = finishedMedia.DisplayWidth & ~1;
            var fullHeight = finishedMedia.DisplayHeight & ~1;
            if (finished.X == 0 && finished.Y == 0 && finished.Width == fullWidth && finished.Height == fullHeight)
            {
                _crop = null;
                UpdateCropInfo();
                UpdateApproximateCopyAvailability();
                UpdateZoomControls();
                ApplyEditorPreview();
                UpdateCropOverlay();
            }
        }
        SaveEditorFieldsToCurrentSegment();
        UpdateSegmentControls();
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
        CropModeButtonText.Text = _isCropMode ? Strings.Get("CropButton_Done") : Strings.Get("CropButton_Crop");
        CropModeButton.SetValue(Microsoft.UI.Xaml.Automation.AutomationProperties.NameProperty, _isCropMode ? Strings.Get("CropMode_NameActive") : Strings.Get("CropMode_NameInactive"));
        ToolTipService.SetToolTip(CropModeButton, _isCropMode
            ? Strings.Get("CropMode_TooltipActive")
            : Strings.Get("CropMode_TooltipInactive"));
        CropOverlayCanvas.IsHitTestVisible = _isCropMode;
        CropOverlayCanvas.Visibility = _isCropMode ? Visibility.Visible : Visibility.Collapsed;
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
        ApplyEditorPreview();
        UpdateZoomControls();
    }

    private void CropOverlayCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateCropOverlay();

    private void CropResetButton_Click(object sender, RoutedEventArgs e)
    {
        SetCropMode(false);
        _crop = null;
        SaveEditorFieldsToCurrentSegment();
        UpdateCropInfo();
        UpdateApproximateCopyAvailability();
        UpdateZoomControls();
        UpdateCropOverlay();
        ApplyEditorPreview();
        UpdateSegmentControls();
    }

    private void RotateButton_Click(object sender, RoutedEventArgs e)
    {
        var previous = _additionalRotationDegreesClockwise;
        _additionalRotationDegreesClockwise = (_additionalRotationDegreesClockwise + 90) % 360;
        SetCropMode(false);
        if (!ApplyEditorPreview())
        {
            _additionalRotationDegreesClockwise = previous;
            ApplyEditorPreview(showError: false);
        }

        UpdateRotationControl();
        SaveEditorFieldsToCurrentSegment();
        UpdateCropInfo();
        UpdateCropOverlay();
        UpdateApproximateCopyAvailability();
        UpdateSegmentControls();
        ValidateEditRange();
    }

    private void UpdateRotationControl()
    {
        RotateButtonText.Text = $"{_additionalRotationDegreesClockwise}°";
        RotateButton.SetValue(Microsoft.UI.Xaml.Automation.AutomationProperties.NameProperty, Strings.Format("Rotate_Name", _additionalRotationDegreesClockwise));
        ToolTipService.SetToolTip(RotateButton, Strings.Format("Rotate_Tooltip", _additionalRotationDegreesClockwise));
        CropModeButton.IsEnabled = _mediaInfo is not null && _additionalRotationDegreesClockwise == 0;
        ToolTipService.SetToolTip(CropModeButton, _additionalRotationDegreesClockwise == 0
            ? Strings.Get("CropMode_TooltipInactive")
            : Strings.Get("CropDisabledByRotation"));
    }

    private void UpdateApproximateCopyAvailability()
    {
        if (EditorPanel.Visibility == Visibility.Visible && _segments.Count > 0)
        {
            SaveEditorFieldsToCurrentSegment();
            var remainingSegments = new List<EditSegment>();
            foreach (var segment in _segments)
            {
                if (!segment.IsDeleted)
                {
                    remainingSegments.Add(segment);
                }
            }

            var hasSegmentTransforms = remainingSegments.Count != 1;
            if (!hasSegmentTransforms)
            {
                var only = remainingSegments[0];
                hasSegmentTransforms = only.Crop.HasValue || only.AdditionalRotationDegreesClockwise != 0 ||
                    Math.Abs(only.Speed - 1) > 0.001 || only.ZoomFactor > 1.001;
            }

            if (hasSegmentTransforms && ApproximateCopyCheckBox.IsChecked == true)
            {
                ApproximateCopyCheckBox.IsChecked = false;
            }

            ApproximateCopyCheckBox.IsEnabled = _mediaInfo is not null && !hasSegmentTransforms && !_isExporting;
            CancelEditButton.IsEnabled = !_isExporting;
            UpdateZoomControls();
            return;
        }

        var hasTransforms = _crop.HasValue || _additionalRotationDegreesClockwise != 0 ||
            Math.Abs(_editSpeed - 1) > 0.001 || _editorZoomFactor > 1.001;
        if (hasTransforms && ApproximateCopyCheckBox.IsChecked == true)
        {
            ApproximateCopyCheckBox.IsChecked = false;
        }

        ApproximateCopyCheckBox.IsEnabled = _mediaInfo is not null && !hasTransforms && !_isExporting;
        CancelEditButton.IsEnabled = !_isExporting;
        UpdateZoomControls();
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

        if (!e.GetCurrentPoint(CropOverlayCanvas).Properties.IsLeftButtonPressed)
        {
            FinishCropDrag(e);
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
        FinishCropDrag(e);
    }

    private void CropOverlayCanvas_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        // FinishCropDrag clears the drag before releasing capture, so only a
        // capture lost mid-drag (Alt+Tab, another window) reverts the rectangle.
        if (_cropDragOrigin is { } originalCrop)
        {
            _crop = originalCrop;
        }
        SaveEditorFieldsToCurrentSegment();

        _activeCropHandle = null;
        _cropDragOrigin = null;
        _cropDragPointerOrigin = null;
        _cropDragHandleOrigin = null;
        UpdateCropInfo();
        UpdateApproximateCopyAvailability();
        UpdateCropOverlay();
    }

    private void FinishCropDrag(PointerRoutedEventArgs e)
    {
        // Clear the drag first: releasing capture raises PointerCaptureLost,
        // which would otherwise restore the pre-drag rectangle.
        _activeCropHandle = null;
        _cropDragOrigin = null;
        _cropDragPointerOrigin = null;
        _cropDragHandleOrigin = null;
        CropOverlayCanvas.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void CropOverlayCanvas_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (_cropDragOrigin is { } originalCrop)
        {
            _crop = originalCrop;
        }
        SaveEditorFieldsToCurrentSegment();

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
        return GetVideoContentBounds(CropOverlayCanvas.ActualWidth, CropOverlayCanvas.ActualHeight);
    }

    private Windows.Foundation.Rect GetVideoContentBounds(double panelWidth, double panelHeight)
    {
        if (_mediaInfo is null || panelWidth <= 0 || panelHeight <= 0)
        {
            return new Windows.Foundation.Rect(0, 0, panelWidth, panelHeight);
        }

        var (previewWidth, previewHeight) = EditorPanel.Visibility == Visibility.Visible && !_isCropMode
            ? MediaGeometry.ComputeBaseViewportDimensions(_mediaInfo, _crop)
            : GetPreviewDimensions();
        if (EditorPanel.Visibility == Visibility.Visible && !_isCropMode && _additionalRotationDegreesClockwise is 90 or 270)
        {
            (previewWidth, previewHeight) = (previewHeight, previewWidth);
        }

        var contentAspect = (double)previewWidth / previewHeight;
        var panelAspect = panelWidth / panelHeight;
        if (panelAspect > contentAspect)
        {
            var width = panelHeight * contentAspect;
            var left = (panelWidth - width) / 2;
            return new Windows.Foundation.Rect(left, 0, width, panelHeight);
        }

        var height = panelWidth / contentAspect;
        var top = (panelHeight - height) / 2;
        return new Windows.Foundation.Rect(0, top, panelWidth, height);
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
            SaveEditorFieldsToCurrentSegment();
            UpdateCropInfo();
            UpdateApproximateCopyAvailability();
            UpdateCropOverlay();
            UpdateSegmentControls();
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
        if (_mediaInfo is not { } media)
        {
            CropInfoText.Text = string.Empty;
            return;
        }

        // Mirrors the export size: crop, then the extra rotation, rounded up to even pixels.
        // With segments, the first remaining segment defines the canvas, matching Core.
        var effectiveCrop = _crop;
        var effectiveRotation = _additionalRotationDegreesClockwise;
        if (EditorPanel.Visibility == Visibility.Visible && _segments.Count > 0)
        {
            foreach (var segment in _segments)
            {
                if (!segment.IsDeleted)
                {
                    effectiveCrop = segment.Crop;
                    effectiveRotation = segment.AdditionalRotationDegreesClockwise;
                    break;
                }
            }
        }

        var width = effectiveCrop?.Width ?? media.DisplayWidth;
        var height = effectiveCrop?.Height ?? media.DisplayHeight;
        if (effectiveRotation is 90 or 270)
        {
            (width, height) = (height, width);
        }

        width += width & 1;
        height += height & 1;
        CropInfoText.Text = Strings.Format("Crop_OutputSize", width, height);
    }

    private async void OutputFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
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
        catch (Exception ex)
        {
            App.WriteErrorLog(ex);
            ShowError(Strings.Get("Error_FolderPickerFailed"));
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
            ShowError(Strings.Get("Error_NoEditableVideo"));
            return;
        }

        var start = TrimStartBox.Value;
        var end = TrimEndBox.Value;
        if (!double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end <= start || end > _mediaInfo.DurationSeconds)
        {
            ShowError(Strings.Get("Error_TimeRangeInvalid"));
            return;
        }

        SaveEditorFieldsToCurrentSegment();
        var remainingExportSegments = new List<EditSegment>();
        var hasDeletedExportSegments = false;
        foreach (var segment in _segments)
        {
            if (segment.IsDeleted)
            {
                hasDeletedExportSegments = true;
            }
            else
            {
                remainingExportSegments.Add(segment);
            }
        }

        if (EditorPanel.Visibility == Visibility.Visible && _segments.Count > 0 && remainingExportSegments.Count == 0)
        {
            ShowError(Strings.Get("Error_TimeRangeInvalid"));
            return;
        }

        var mode = ApproximateCopyCheckBox.IsChecked == true ? ExportMode.StreamCopyApproximate : ExportMode.AccurateReencode;
        if (mode == ExportMode.AccurateReencode && _mediaInfo.IsHdr)
        {
            ShowError(Strings.Get("Error_HdrExportNotSupported"));
            return;
        }

        var copySegment = remainingExportSegments.Count > 0 ? remainingExportSegments[0] : null;
        var copyCrop = copySegment is null ? _crop : copySegment.Crop;
        var copyRotation = copySegment?.AdditionalRotationDegreesClockwise ?? _additionalRotationDegreesClockwise;
        var copySpeed = copySegment?.Speed ?? _editSpeed;
        var copyZoom = copySegment?.ZoomFactor ?? _editorZoomFactor;
        if (mode == ExportMode.StreamCopyApproximate && copyCrop is not null)
        {
            ShowError(Strings.Get("Error_StreamCopyCropConflict"));
            return;
        }

        if (mode == ExportMode.StreamCopyApproximate &&
            (copyRotation != 0 || Math.Abs(copySpeed - 1) > 0.001 || copyZoom > 1.001))
        {
            ShowError(Strings.Get("Error_StreamCopyTransformConflict"));
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
            ShowError(Strings.Get("Error_FileNameInvalid"));
            return;
        }

        var directory = _outputDirectory ?? Path.GetDirectoryName(_loadedPath)!;
        var outputContainer = GetSelectedExportContainer();
        var destination = Path.Combine(directory, fileStem + GetExportExtension(outputContainer));
        if (File.Exists(destination))
        {
            ShowError(Strings.Get("Error_FileExists"));
            return;
        }

        var useMultiSegmentExport = EditorPanel.Visibility == Visibility.Visible && (remainingExportSegments.Count != 1 || hasDeletedExportSegments);
        ExportRequest request;
        if (!useMultiSegmentExport)
        {
            request = new ExportRequest(
                _loadedPath,
                destination,
                start,
                end,
                _crop,
                mode,
                AdditionalRotationDegreesClockwise: _additionalRotationDegreesClockwise,
                PlaybackSpeed: _editSpeed,
                OutputContainer: outputContainer,
                ZoomFactor: _editorZoomFactor,
                ZoomFocusX: _editorZoomFocusX,
                ZoomFocusY: _editorZoomFocusY);
        }
        else
        {
            var exportSegments = new List<ExportSegment>();
            foreach (var segment in remainingExportSegments)
            {
                exportSegments.Add(new ExportSegment(
                    segment.StartSeconds,
                    segment.EndSeconds,
                    segment.Crop,
                    segment.AdditionalRotationDegreesClockwise,
                    segment.Speed,
                    segment.ZoomFactor,
                    segment.ZoomFocusX,
                    segment.ZoomFocusY));
            }

            var firstRemaining = remainingExportSegments[0];
            request = new ExportRequest(
                _loadedPath,
                destination,
                start,
                end,
                firstRemaining.Crop,
                mode,
                AdditionalRotationDegreesClockwise: firstRemaining.AdditionalRotationDegreesClockwise,
                PlaybackSpeed: firstRemaining.Speed,
                OutputContainer: outputContainer,
                ZoomFactor: firstRemaining.ZoomFactor,
                ZoomFocusX: firstRemaining.ZoomFocusX,
                ZoomFocusY: firstRemaining.ZoomFocusY,
                Segments: exportSegments);
        }
        var exportCancellation = new CancellationTokenSource();
        _exportCancellation = exportCancellation;
        var token = exportCancellation.Token;
        _isExporting = true;
        UpdateApproximateCopyAvailability();
        NotificationBar.IsOpen = false;
        ExportProgressBar.Value = 0;
        ExportProgressBar.Visibility = Visibility.Visible;
        CancelExportButton.Visibility = Visibility.Visible;
        ExportButton.Content = Strings.Get("ExportButton_Exporting");
        OutputContainerComboBox.IsEnabled = false;
        ApproximateCopyCheckBox.IsEnabled = false;
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

        var exportTask = _exportService.ExportAsync(request, progress, token);
        _exportTask = exportTask;
        try
        {
            var result = await exportTask;
            if (!_isClosing && ReferenceEquals(_exportCancellation, exportCancellation))
            {
                var successMessage = Strings.Format("Notice_ExportDone", result.DestinationPath);
                if (result.BoundariesAreApproximate)
                {
                    successMessage += Strings.Get("Notice_ExportDoneApproxSuffix");
                }

                ShowNotice(successMessage, InfoBarSeverity.Success, revealPath: result.DestinationPath);
            }
        }
        catch (OperationCanceledException)
        {
            if (!_isClosing && ReferenceEquals(_exportCancellation, exportCancellation))
            {
                ShowNotice(Strings.Get("Notice_ExportCancelled"), InfoBarSeverity.Informational);
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
                App.WriteErrorLog(ex);
                ShowError(Strings.Get("Error_ExportFailedGeneric"));
            }
        }
        finally
        {
            exportCancellation.Dispose();
            if (ReferenceEquals(_exportTask, exportTask))
            {
                _exportTask = null;
            }

            if (ReferenceEquals(_exportCancellation, exportCancellation))
            {
                _exportCancellation = null;
                _isExporting = false;
                if (!_isClosing)
                {
                    ExportProgressBar.IsIndeterminate = false;
                    ExportProgressBar.Visibility = Visibility.Collapsed;
                    CancelExportButton.Visibility = Visibility.Collapsed;
                    ExportButton.Content = Strings.Get("ExportButton_Export");
                    CancelEditButton.IsEnabled = true;
                    UpdateApproximateCopyAvailability();
                    OutputContainerComboBox.IsEnabled = true;
                    OutputFolderButton.IsEnabled = true;
                    OutputNameBox.IsEnabled = true;
                    ValidateEditRange();
                }
            }
        }
    }

    private void CancelExportButton_Click(object sender, RoutedEventArgs e) => _exportCancellation?.Cancel();

    private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose)
        {
            return;
        }

        // async void: closing continues on Close() after the export and settings are settled.
        args.Cancel = true;
        if (_confirmingClose)
        {
            return;
        }

        _confirmingClose = true;
        try
        {
            if (_isExporting)
            {
                var confirmClose = new ContentDialog
                {
                    Title = Strings.Get("Dialog_AbortExportTitle"),
                    Content = Strings.Get("Dialog_CloseDuringExportContent"),
                    PrimaryButtonText = Strings.Get("Dialog_AbortAndClose"),
                    CloseButtonText = Strings.Get("Dialog_Cancel"),
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = RootGrid.XamlRoot
                };
                ContentDialogResult closeChoice;
                try
                {
                    closeChoice = await confirmClose.ShowAsync();
                }
                catch (Exception ex)
                {
                    App.WriteErrorLog(ex);
                    return;
                }

                if (closeChoice != ContentDialogResult.Primary)
                {
                    return;
                }

                CancelExportForNewFile();
                await WaitForExportCompletionAsync();
            }

            try
            {
                await _settings.SaveAsync();
            }
            catch (Exception ex)
            {
                App.WriteErrorLog(ex);
            }

            _allowClose = true;
            Close();
        }
        finally
        {
            _confirmingClose = false;
        }
    }

    private async Task WaitForExportCompletionAsync()
    {
        var pending = _exportTask;
        if (pending is null)
        {
            return;
        }

        try
        {
            await pending;
        }
        catch
        {
            // Cancellation is the expected outcome here; anything else is already reported.
        }
    }

    private void CancelExportForNewFile()
    {
        var cancellation = _exportCancellation;
        _exportCancellation = null;
        cancellation?.Cancel();
        _isExporting = false;
        ExportProgressBar.IsIndeterminate = false;
        ExportProgressBar.Visibility = Visibility.Collapsed;
        CancelExportButton.Visibility = Visibility.Collapsed;
        ExportButton.Content = Strings.Get("ExportButton_Export");
        OutputContainerComboBox.IsEnabled = true;
        OutputFolderButton.IsEnabled = true;
        OutputNameBox.IsEnabled = true;
    }

    private async void SaveFrameButton_Click(object sender, RoutedEventArgs e)
    {
        var player = _player;
        if (player is null)
        {
            ShowError(Strings.Get("Error_OpenVideoFirst"));
            return;
        }

        // Capture the name inputs before the first await; playback continues underneath.
        var screenshotBaseName = BuildScreenshotName();
        var picturesDirectory = GetPicturesFrameDirectory();
        var preferredDirectory = GetPreferredFrameDirectory(picturesDirectory);
        string? temporary = null;
        try
        {
            string directory;
            try
            {
                directory = preferredDirectory;
                Directory.CreateDirectory(directory);
                temporary = Path.Combine(directory, $".FrameDock-{Guid.NewGuid():N}.png");
                await player.SaveScreenshotAsync(temporary);
            }
            catch (Exception preferredFailure) when (!string.Equals(preferredDirectory, picturesDirectory, StringComparison.OrdinalIgnoreCase))
            {
                // A read-only disc or a removed drive must not lose the frame.
                App.WriteErrorLog(preferredFailure);
                directory = picturesDirectory;
                Directory.CreateDirectory(directory);
                temporary = Path.Combine(directory, $".FrameDock-{Guid.NewGuid():N}.png");
                await player.SaveScreenshotAsync(temporary);
            }

            var destination = MoveScreenshotToAvailablePath(temporary, directory, screenshotBaseName);
            temporary = null;
            var usedFallback = !string.Equals(directory, preferredDirectory, StringComparison.OrdinalIgnoreCase);
            ShowNotice(
                Strings.Format(usedFallback ? "Notice_FrameSavedFallback" : "Notice_FrameSaved", destination),
                usedFallback ? InfoBarSeverity.Warning : InfoBarSeverity.Success,
                revealPath: destination);
        }
        catch (Exception ex)
        {
            App.WriteErrorLog(ex);
            ShowError(Strings.Get("Error_FrameSaveFailed"));
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

    private static string GetPicturesFrameDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), PlayerSettings.DefaultFrameSaveSubfolder);

    private string GetPreferredFrameDirectory(string picturesDirectory)
    {
        var videoFolder = _loadedPath is null ? null : Path.GetDirectoryName(_loadedPath);
        return _settings.FrameSaveLocation switch
        {
            FrameSaveLocation.CustomFolder when !string.IsNullOrWhiteSpace(_settings.FrameSaveFolder) => _settings.FrameSaveFolder,
            FrameSaveLocation.VideoFolder when !string.IsNullOrEmpty(videoFolder) => videoFolder,
            FrameSaveLocation.VideoSubfolder when !string.IsNullOrEmpty(videoFolder) => Path.Combine(videoFolder, _settings.FrameSaveSubfolder),
            _ => picturesDirectory
        };
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
            ShowError(Strings.Get("Error_OpenVideoFirst"));
            return;
        }

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
            var file = await StorageFile.GetFileFromPathAsync(path);
            await SetClipboardImageWithRetryAsync(file, () => clipboardDataSet = true);
            clipboardFlushed = true;
            ShowNotice(Strings.Get("Notice_FrameCopied"), InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            App.WriteErrorLog(ex);
            var cause = ex.InnerException ?? ex;
            var busy = cause is COMException com && com.HResult == ClipboardCannotOpenHResult;
            if (cause is IOException { InnerException: COMException inner } && inner.HResult == ClipboardCannotOpenHResult)
            {
                busy = true;
            }
            ShowError(busy
                ? Strings.Get("Error_CopyFrameBusy")
                : Strings.Get("Error_CopyFrameFailed"));
        }
        finally
        {
            // A successful Flush makes the clipboard independent from this source file.
            // If the clipboard accepted data but Flush failed, keep the file for deferred
            // rendering; CleanupStaleClipboardFrames removes it on the next launch.
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

                lastBusyError = new COMException(Strings.Get("Log_ClipboardBusy"), ClipboardCannotOpenHResult);
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

        throw new IOException(Strings.Get("Log_ClipboardBusyRetry"), lastBusyError);
    }

    private static void CleanupStaleClipboardFrames()
    {
        try
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FrameDock",
                "clipboard-frames");
            if (!Directory.Exists(folder))
            {
                return;
            }

            foreach (var stale in Directory.EnumerateFiles(folder, "frame-*.png"))
            {
                TryDeleteClipboardFrame(stale);
            }
        }
        catch
        {
            // Startup cleanup must never block opening the window.
        }
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

    private sealed class EditSegment
    {
        public double StartSeconds;
        public double EndSeconds;
        public CropRect? Crop;
        public int AdditionalRotationDegreesClockwise;
        public double ZoomFactor = 1;
        public double ZoomFocusX = 0.5;
        public double ZoomFocusY = 0.5;
        public double Speed = 1;
        public bool IsDeleted;
    }

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int key);

    private void ShowError(string message) => ShowNotice(message, InfoBarSeverity.Error);

    private void ShowNotice(string message, InfoBarSeverity severity, string? revealPath = null)
    {
        if (_isClosing)
        {
            return;
        }

        _noticeTimer?.Stop();
        ShowControls();
        if ((severity is InfoBarSeverity.Success or InfoBarSeverity.Informational) &&
            EditorPanel.Visibility != Visibility.Visible && revealPath is null)
        {
            NotificationBar.IsOpen = false;
            InlineNoticeText.Text = message;
            ToolTipService.SetToolTip(InlineNoticeText, message);
            InlineNoticeText.SetValue(Microsoft.UI.Xaml.Automation.AutomationProperties.NameProperty, message);
            InlineNoticeText.Visibility = Visibility.Visible;
            if (_noticeTimer is not null)
            {
                _noticeTimer.Interval = TimeSpan.FromSeconds(6);
                _noticeTimer.Start();
            }
            return;
        }

        InlineNoticeText.Visibility = Visibility.Collapsed;
        InlineNoticeText.Text = string.Empty;
        NotificationBar.Severity = severity;
        NotificationBar.Title = severity switch
        {
            InfoBarSeverity.Error => Strings.Get("NotifyTitle_Error"),
            InfoBarSeverity.Warning => Strings.Get("NotifyTitle_Warning"),
            InfoBarSeverity.Success => Strings.Get("NotifyTitle_Success"),
            _ => "FrameDock"
        };
        NotificationBar.Message = message;
        NotificationBar.ActionButton = revealPath is null ? null : CreateRevealButton(revealPath);
        NotificationBar.IsOpen = true;
        if ((severity is InfoBarSeverity.Success or InfoBarSeverity.Informational || revealPath is not null) && _noticeTimer is not null)
        {
            // Leave time to reach the folder button on an export notice.
            _noticeTimer.Interval = TimeSpan.FromSeconds(revealPath is null ? 6 : 12);
            _noticeTimer.Start();
        }
    }

    private Button CreateRevealButton(string path)
    {
        var button = new Button { Content = Strings.Get("RevealButton_OpenFolder") };
        button.Click += (_, _) =>
        {
            try
            {
                // /select opens the folder with the exported file highlighted.
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
            }
            catch (Exception ex)
            {
                App.WriteErrorLog(ex);
                ShowError(Strings.Get("Error_OpenFolderFailed"));
            }
        };
        return button;
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

/// <summary>
/// Shows the timeline thumb tooltip as a playback time instead of raw seconds.
/// </summary>
public sealed class TimelineThumbToolTipConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        MainWindow.FormatTime(value is double seconds ? seconds : 0);

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>
/// Shows trim times as m:ss.ss (h:mm:ss.ss from one hour) and accepts either
/// that form or plain seconds when typed.
/// </summary>
public sealed class TrimTimeFormatter : Windows.Globalization.NumberFormatting.INumberFormatter2, Windows.Globalization.NumberFormatting.INumberParser
{
    public string FormatDouble(double value)
    {
        if (!double.IsFinite(value) || value < 0)
        {
            value = 0;
        }

        var hundredths = (long)Math.Round(value * 100, MidpointRounding.AwayFromZero);
        var hours = hundredths / 360000;
        var minutes = hundredths / 6000 % 60;
        var seconds = hundredths % 6000 / 100d;
        var secondsText = seconds.ToString("00.00", CultureInfo.InvariantCulture);
        return hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours}:{minutes:00}:{secondsText}")
            : string.Create(CultureInfo.InvariantCulture, $"{minutes}:{secondsText}");
    }

    public string FormatInt(long value) => FormatDouble(value);

    public string FormatUInt(ulong value) => FormatDouble(value);

    public double? ParseDouble(string text)
    {
        var parts = (text ?? string.Empty).Trim().Replace('：', ':').Split(':');
        if (parts.Length is < 1 or > 3)
        {
            return null;
        }

        double total = 0;
        for (var i = 0; i < parts.Length; i++)
        {
            var isLast = i == parts.Length - 1;
            if (!double.TryParse(parts[i], isLast ? NumberStyles.AllowDecimalPoint : NumberStyles.None, CultureInfo.InvariantCulture, out var part) ||
                (parts.Length > 1 && i > 0 && part >= 60))
            {
                return null;
            }

            total = total * 60 + part;
        }

        return double.IsFinite(total) ? total : null;
    }

    public long? ParseInt(string text) => ParseDouble(text) is { } value ? (long)value : null;

    public ulong? ParseUInt(string text) => ParseDouble(text) is { } value ? (ulong)value : null;
}
