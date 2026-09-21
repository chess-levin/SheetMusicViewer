using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Threading;

namespace SheetMusicViewer;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly PdfRenderer _renderer = new();
    private readonly SettingsService _settingsService = new();
    private readonly ViewerViewModel _viewModel;
    private ViewerSettings _settings => _settingsService.Settings;
    private int _leftPageIndex;
    private double _zoom;
    private string _statusText = "PDF öffnen, dann mit → / ← blättern";
    private string _missingRecentFileNotice = string.Empty;
    private string _pdfOpenErrorNotice = string.Empty;
    private readonly DispatcherTimer _missingRecentFileNoticeTimer;
    private readonly DispatcherTimer _pdfOpenErrorNoticeTimer;
    private readonly RenderController _renderController = new();
    private static readonly TimeSpan ZoomIndicatorDisplayDelay = TimeSpan.FromMilliseconds(500);
    private CancellationTokenSource? _zoomIndicatorCancellation;
    private int _zoomIndicatorGeneration;
    private Visibility _zoomIndicatorVisibility = Visibility.Collapsed;
    private volatile bool _isClosed;
    // A document transition must not admit resize-driven rendering after the
    // current render task snapshot has been awaited.
    private int _isDocumentTransitioning;
    private int _documentGeneration;
    private bool _isFullScreen;
    private WindowStyle _savedWindowStyle;
    private ResizeMode _savedResizeMode;
    private WindowState _savedWindowState;
    private bool _savedTopmost;
    private double _savedLeft;
    private double _savedTop;
    private double _savedWidth;
    private double _savedHeight;

    private bool IsDocumentTransitioning => Volatile.Read(ref _isDocumentTransitioning) != 0;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new ViewerViewModel(_settingsService);
        DataContext = _viewModel;
        _zoom = _settings.Zoom;
        _missingRecentFileNoticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _missingRecentFileNoticeTimer.Tick += MissingRecentFileNoticeTimer_Tick;
        _pdfOpenErrorNoticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _pdfOpenErrorNoticeTimer.Tick += PdfOpenErrorNoticeTimer_Tick;
        Closed += Window_Closed;
    }

    public string StatusText
    {
        get => _statusText;
        private set { _statusText = value; _viewModel.StatusText = value; OnPropertyChanged(); }
    }

    public string PreviousToolTip => $"Vorherige Seite ({KeyText(_settings.PreviousKey)})";
    public string NextToolTip => $"Nächste Seite ({KeyText(_settings.NextKey)})";
    public string ClosePdfToolTip => _isFullScreen ? "Vollbild beenden (ESC)" : "PDF schließen";
    public string FullScreenToolTip => _isFullScreen ? "Vollbild beenden (ESC)" : "Vollbild aktivieren (F11)";
    public Visibility FullScreenButtonVisibility => !_isFullScreen && _renderer.IsOpen ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PdfContentVisibility => _renderer.IsOpen ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EmptyContentVisibility => _renderer.IsOpen ? Visibility.Collapsed : Visibility.Visible;
    public IReadOnlyList<string> RecentFiles => _settings.AvailableRecentFiles();
    public bool HasRecentFiles => RecentFiles.Count > 0;
    public Visibility RecentFilesVisibility => HasRecentFiles ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoRecentFilesVisibility => HasRecentFiles ? Visibility.Collapsed : Visibility.Visible;
    public string MissingRecentFileNotice => _missingRecentFileNotice;
    public Visibility MissingRecentFileNoticeVisibility => string.IsNullOrEmpty(MissingRecentFileNotice)
        ? Visibility.Collapsed
        : Visibility.Visible;
    public string PdfOpenErrorNotice => _pdfOpenErrorNotice;
    public Visibility PdfOpenErrorNoticeVisibility => string.IsNullOrEmpty(PdfOpenErrorNotice)
        ? Visibility.Collapsed
        : Visibility.Visible;
    public bool CanMovePrevious => _renderer.IsOpen && _leftPageIndex > 0;
    public bool CanMoveNext => _renderer.IsOpen && _leftPageIndex < _renderer.PageCount - 2;
    public string LeftPageNumberText => _renderer.IsOpen ? $"{_leftPageIndex + 1} / {_renderer.PageCount}" : string.Empty;
    public string RightPageNumberText => _renderer.IsOpen && _leftPageIndex + 1 < _renderer.PageCount
        ? $"{_leftPageIndex + 2} / {_renderer.PageCount}"
        : string.Empty;
    public string ZoomIndicatorText => $"{Math.Round(_zoom * 100):0} %";
    public Visibility ZoomIndicatorVisibility
    {
        get => _zoomIndicatorVisibility;
        private set { _zoomIndicatorVisibility = value; OnPropertyChanged(); }
    }

    private static string KeyText(Key key) => key switch
    {
        Key.Left => "←",
        Key.Right => "→",
        _ => key.ToString()
    };

    private async void OpenPdf_Click(object sender, RoutedEventArgs e)
    {
        var recentFiles = _settings.AvailableRecentFiles();
        if (recentFiles.Count == 0)
        {
            var dialog = new OpenFileDialog { Filter = "PDF-Dateien (*.pdf)|*.pdf", Multiselect = false };
            if (dialog.ShowDialog(this) != true) return;
            await OpenPdfAsync(dialog.FileName);
            return;
        }

        var chooser = new RecentPdfsWindow(recentFiles, RemoveMissingRecentFile) { Owner = this };
        if (chooser.ShowDialog() != true || chooser.SelectedPath is null) return;
        await OpenPdfAsync(chooser.SelectedPath);
    }

    private async Task OpenPdfAsync(string path)
    {
        if (!System.IO.File.Exists(path))
        {
            RemoveMissingRecentFile(path);
            return;
        }

        int? zoomIndicatorGeneration = null;
        var opened = false;
        Volatile.Write(ref _isDocumentTransitioning, 1);
        Interlocked.Increment(ref _documentGeneration);
        try
        {
            await StopRenderingAsync();
            await _renderer.OpenAsync(path);
            _leftPageIndex = 0;
            _settingsService.RememberFile(path);
            _pdfOpenErrorNotice = string.Empty;
            Title = $"Sheet Music PDF Viewer – {System.IO.Path.GetFileName(path)}";
            RefreshDocumentState();
            zoomIndicatorGeneration = ShowZoomIndicator();
            opened = true;
        }
        catch (PdfResourceLimitException ex)
        {
            ResetClosedDocumentState();
            ShowPdfOpenError(ex.Message);
        }
        catch (Exception ex)
        {
            ResetClosedDocumentState();
            ShowPdfOpenError($"Die PDF konnte nicht geöffnet werden.\n{ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _isDocumentTransitioning, 0);
        }

        if (opened) await RenderViewportAsync(zoomIndicatorGeneration: zoomIndicatorGeneration);
    }

    private async void Next_Click(object sender, RoutedEventArgs e) => await MoveAsync(1);
    private async void Previous_Click(object sender, RoutedEventArgs e) => await MoveAsync(-1);

    private async void BrowsePdf_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "PDF-Dateien (*.pdf)|*.pdf", Multiselect = false };
        if (dialog.ShowDialog(this) == true) await OpenPdfAsync(dialog.FileName);
    }

    private async void OpenSelectedRecent_Click(object sender, RoutedEventArgs e)
    {
        if (EmptyRecentFilesList.SelectedItem is string path) await OpenPdfAsync(path);
    }

    private async void EmptyRecentFilesList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (EmptyRecentFilesList.SelectedItem is string path) await OpenPdfAsync(path);
    }

    private async void EmptyRecentFilesList_KeyDown(object sender, KeyEventArgs e)
    {
        if (EmptyRecentFilesList.SelectedItem is not string path) return;

        if (e.Key == Key.Return)
        {
            e.Handled = true;
            await OpenPdfAsync(path);
            return;
        }

        if (e.Key == Key.Delete)
        {
            e.Handled = true;
            var previousIndex = EmptyRecentFilesList.SelectedIndex;
            _settingsService.ForgetFile(path);
            RefreshDocumentState();
            EmptyRecentFilesList.SelectedIndex = Math.Min(previousIndex, EmptyRecentFilesList.Items.Count - 1);
        }
    }

    private void EmptyRecentFilesList_Loaded(object sender, RoutedEventArgs e)
    {
        FocusFirstRecentFile();
    }

    private void RemoveMissingRecentFile(string path)
    {
        _settingsService.ForgetFile(path);
        _missingRecentFileNotice = $"Die Datei „{System.IO.Path.GetFileName(path)}“ wurde nicht gefunden und aus der Liste entfernt.";
        _missingRecentFileNoticeTimer.Stop();
        _missingRecentFileNoticeTimer.Start();
        RefreshDocumentState();
        FocusFirstRecentFile();
    }

    private void ShowPdfOpenError(string message)
    {
        _pdfOpenErrorNotice = message;
        _viewModel.PdfOpenErrorNotice = message;
        _pdfOpenErrorNoticeTimer.Stop();
        _pdfOpenErrorNoticeTimer.Start();
        OnPropertyChanged(nameof(PdfOpenErrorNotice));
        OnPropertyChanged(nameof(PdfOpenErrorNoticeVisibility));
    }

    private void PdfOpenErrorNoticeTimer_Tick(object? sender, EventArgs e)
    {
        _pdfOpenErrorNoticeTimer.Stop();
        _pdfOpenErrorNotice = string.Empty;
        _viewModel.PdfOpenErrorNotice = string.Empty;
        OnPropertyChanged(nameof(PdfOpenErrorNotice));
        OnPropertyChanged(nameof(PdfOpenErrorNoticeVisibility));
    }

    private void ResetClosedDocumentState()
    {
        _renderer.Close();
        _leftPageIndex = 0;
        LeftPage.Source = null;
        RightPage.Source = null;
        HideZoomIndicator();
        Title = "Sheet Music PDF Viewer";
        RefreshDocumentState();
        FocusFirstRecentFile();
    }

    private void MissingRecentFileNoticeTimer_Tick(object? sender, EventArgs e)
    {
        _missingRecentFileNoticeTimer.Stop();
        _missingRecentFileNotice = string.Empty;
        _viewModel.MissingRecentFileNotice = string.Empty;
        OnPropertyChanged(nameof(MissingRecentFileNotice));
        OnPropertyChanged(nameof(MissingRecentFileNoticeVisibility));
        OnPropertyChanged(nameof(PdfOpenErrorNotice));
        OnPropertyChanged(nameof(PdfOpenErrorNoticeVisibility));
    }

    private void FullScreenButton_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();

    private void LicenseLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void FocusFirstRecentFile()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (EmptyRecentFilesList.Items.Count == 0) return;
            EmptyRecentFilesList.SelectedIndex = 0;
            EmptyRecentFilesList.Focus();
        }, DispatcherPriority.Input);
    }

    private async void ClosePdf_Click(object sender, RoutedEventArgs e)
    {
        if (_isFullScreen)
        {
            ToggleFullScreen();
            return;
        }

        Volatile.Write(ref _isDocumentTransitioning, 1);
        Interlocked.Increment(ref _documentGeneration);
        try
        {
            await StopRenderingAsync();
            _renderer.Close();
            _leftPageIndex = 0;
            LeftPage.Source = null;
            RightPage.Source = null;
            HideZoomIndicator();
            Title = "Sheet Music PDF Viewer";
            RefreshDocumentState();
            FocusFirstRecentFile();
        }
        finally
        {
            Volatile.Write(ref _isDocumentTransitioning, 0);
        }
    }

    private async Task MoveAsync(int delta)
    {
        if (!_renderer.IsOpen) return;
        // Keep a two-page viewport whenever the document contains at least two pages.
        var target = Math.Clamp(_leftPageIndex + delta, 0, Math.Max(0, _renderer.PageCount - 2));
        if (target == _leftPageIndex) return;
        _leftPageIndex = target;
        OnPropertyChanged(nameof(LeftPageNumberText));
        OnPropertyChanged(nameof(RightPageNumberText));
        OnPropertyChanged(nameof(CanMovePrevious));
        OnPropertyChanged(nameof(CanMoveNext));
        _viewModel.SynchronizeDocument(_renderer.PageCount, _leftPageIndex);
        await RenderViewportAsync();
    }

    private async void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11 && _renderer.IsOpen) { ToggleFullScreen(); e.Handled = true; }
        else if (e.Key == Key.Escape && _isFullScreen) { ToggleFullScreen(); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.None && e.Key == _settings.NextKey) { e.Handled = true; await MoveAsync(1); }
        else if (Keyboard.Modifiers == ModifierKeys.None && e.Key == _settings.PreviousKey) { e.Handled = true; await MoveAsync(-1); }
        else if (HasOnlyZoomModifiers() && IsZoomInKey(e.Key)) { e.Handled = true; ChangeZoom(0.1); }
        else if (HasOnlyZoomModifiers() && IsZoomOutKey(e.Key)) { e.Handled = true; ChangeZoom(-0.1); }
    }

    private bool IsZoomInKey(Key key) => key == _settings.ZoomInKey || key == Key.OemPlus;
    private bool IsZoomOutKey(Key key) => key == _settings.ZoomOutKey || key == Key.OemMinus;
    private static bool HasOnlyZoomModifiers() => Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift;

    private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_renderer.IsOpen) return;
        ChangeZoom(e.Delta > 0 ? 0.1 : -0.1);
        e.Handled = true;
    }

    private void ToggleFullScreen()
    {
        if (!_isFullScreen)
        {
            _savedWindowStyle = WindowStyle;
            _savedResizeMode = ResizeMode;
            _savedWindowState = WindowState;
            _savedTopmost = Topmost;
            _savedLeft = Left;
            _savedTop = Top;
            _savedWidth = Width;
            _savedHeight = Height;
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            _isFullScreen = true;
            _viewModel.IsFullScreen = true;
            OnPropertyChanged(nameof(ClosePdfToolTip));
            OnPropertyChanged(nameof(FullScreenToolTip));
            OnPropertyChanged(nameof(FullScreenButtonVisibility));
            SetFullScreenMonitorBounds();
        }
        else
        {
            WindowState = WindowState.Normal;
            WindowStyle = _savedWindowStyle;
            ResizeMode = _savedResizeMode;
            Topmost = _savedTopmost;
            Left = _savedLeft;
            Top = _savedTop;
            Width = _savedWidth;
            Height = _savedHeight;
            WindowState = _savedWindowState;
            _isFullScreen = false;
            _viewModel.IsFullScreen = false;
            OnPropertyChanged(nameof(ClosePdfToolTip));
            OnPropertyChanged(nameof(FullScreenToolTip));
            OnPropertyChanged(nameof(FullScreenButtonVisibility));
        }
    }

    private void SetFullScreenMonitorBounds()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref monitorInfo)) return;

        var bounds = monitorInfo.Monitor;
        SetWindowPos(handle, IntPtr.Zero, bounds.Left, bounds.Top,
            bounds.Right - bounds.Left, bounds.Bottom - bounds.Top,
            SetWindowPosNoZOrder | SetWindowPosFrameChanged | SetWindowPosShowWindow);
    }

    private const uint MonitorDefaultToNearest = 2;
    private const uint SetWindowPosNoZOrder = 0x0004;
    private const uint SetWindowPosFrameChanged = 0x0020;
    private const uint SetWindowPosShowWindow = 0x0040;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr windowHandle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr windowHandle, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect WorkArea;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => ChangeZoom(0.1);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ChangeZoom(-0.1);

    private void ChangeZoom(double amount)
    {
        _zoom = Math.Clamp(_zoom + amount, 0.5, 3.0);
        _viewModel.ChangeZoom(amount);
        var zoomIndicatorGeneration = ShowZoomIndicator();
        ScheduleSettingsSave();
        _ = RenderViewportAsync(debounce: true, zoomIndicatorGeneration: zoomIndicatorGeneration);
    }

    private int ShowZoomIndicator()
    {
        _zoomIndicatorCancellation?.Cancel();
        _zoomIndicatorCancellation?.Dispose();
        _zoomIndicatorCancellation = null;
        ZoomIndicatorVisibility = Visibility.Visible;
        _viewModel.ZoomIndicatorVisibility = Visibility.Visible;
        OnPropertyChanged(nameof(ZoomIndicatorText));
        return ++_zoomIndicatorGeneration;
    }

    private void HideZoomIndicator()
    {
        _zoomIndicatorCancellation?.Cancel();
        _zoomIndicatorCancellation?.Dispose();
        _zoomIndicatorCancellation = null;
        ZoomIndicatorVisibility = Visibility.Collapsed;
        _viewModel.ZoomIndicatorVisibility = Visibility.Collapsed;
    }

    private void HideZoomIndicatorAfterRender(int generation)
    {
        if (generation != _zoomIndicatorGeneration) return;
        _zoomIndicatorCancellation?.Cancel();
        _zoomIndicatorCancellation?.Dispose();
        var cancellation = _zoomIndicatorCancellation = new CancellationTokenSource();
        _ = HideZoomIndicatorAfterDelayAsync(generation, cancellation.Token);
    }

    private async Task HideZoomIndicatorAfterDelayAsync(int generation, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(ZoomIndicatorDisplayDelay, cancellationToken);
            if (!cancellationToken.IsCancellationRequested && generation == _zoomIndicatorGeneration)
            {
                ZoomIndicatorVisibility = Visibility.Collapsed;
                _viewModel.ZoomIndicatorVisibility = Visibility.Collapsed;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A newer zoom operation keeps the indicator visible.
        }
    }

    private void ScheduleSettingsSave()
    {
        _settingsService.ScheduleSave();
    }

    private async void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_renderer.IsOpen) await RenderViewportAsync(debounce: true);
    }

    private Task RenderViewportAsync(bool debounce = false, int? zoomIndicatorGeneration = null)
    {
        if (_isClosed || IsDocumentTransitioning || !_renderer.IsOpen || ActualWidth < 100 || ActualHeight < 100) return Task.CompletedTask;
        var documentGeneration = Volatile.Read(ref _documentGeneration);
        return _renderController.QueueAsync(
            cancellation => RenderViewportCoreAsync(cancellation, documentGeneration, zoomIndicatorGeneration), debounce);
    }

    private async Task RenderViewportCoreAsync(CancellationToken cancellation, int documentGeneration, int? zoomIndicatorGeneration)
    {
        try
        {
            cancellation.ThrowIfCancellationRequested();
            if (IsDocumentTransitioning || documentGeneration != Volatile.Read(ref _documentGeneration) || !_renderer.IsOpen) return;

            StatusText = $"Seiten {_leftPageIndex + 1}–{Math.Min(_leftPageIndex + 2, _renderer.PageCount)} von {_renderer.PageCount}";
            var (availableWidth, availableHeight) = GetSafeRenderBounds();
            var left = _renderer.RenderPageAsync(_leftPageIndex, availableWidth, availableHeight, cancellation);
            Task<BitmapSource?> right = _leftPageIndex + 1 < _renderer.PageCount
                ? _renderer.RenderPageAsync(_leftPageIndex + 1, availableWidth, availableHeight, cancellation)
                : Task.FromResult<BitmapSource?>(null);
            await Task.WhenAll(left, right).ConfigureAwait(false);
            if (!cancellation.IsCancellationRequested && !_isClosed && documentGeneration == Volatile.Read(ref _documentGeneration) && !Dispatcher.HasShutdownStarted)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (!cancellation.IsCancellationRequested && !_isClosed && documentGeneration == Volatile.Read(ref _documentGeneration))
                    {
                        LeftPage.Source = left.Result;
                        RightPage.Source = right.Result;
                        if (zoomIndicatorGeneration.HasValue)
                        {
                            HideZoomIndicatorAfterRender(zoomIndicatorGeneration.Value);
                        }
                    }
                }).Task.ConfigureAwait(false);
            }
        }
        catch (PdfResourceLimitException ex)
        {
            if (!_isClosed && documentGeneration == Volatile.Read(ref _documentGeneration) && !Dispatcher.HasShutdownStarted)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (!_isClosed && documentGeneration == Volatile.Read(ref _documentGeneration))
                    {
                        StatusText = $"Darstellung abgebrochen: {ex.Message}";
                    }
                }).Task.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_isClosed && documentGeneration == Volatile.Read(ref _documentGeneration) && !Dispatcher.HasShutdownStarted)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (!_isClosed && documentGeneration == Volatile.Read(ref _documentGeneration))
                    {
                        StatusText = $"Darstellungsfehler: {ex.Message}";
                    }
                }).Task.ConfigureAwait(false);
            }
        }
        finally
        {
        }
    }

    private (int Width, int Height) GetSafeRenderBounds()
    {
        var width = Math.Clamp(Math.Max(400, (int)((ActualWidth - 60) / 2 * _zoom)), 1, PdfRenderer.MaximumRenderDimension);
        var height = Math.Clamp(Math.Max(400, (int)((ActualHeight - 100) * _zoom)), 1, PdfRenderer.MaximumRenderDimension);
        var pixels = (long)width * height;
        if (pixels <= PdfRenderer.MaximumRenderPixels) return (width, height);

        var scale = Math.Sqrt(PdfRenderer.MaximumRenderPixels / (double)pixels);
        return (Math.Max(1, (int)Math.Floor(width * scale)), Math.Max(1, (int)Math.Floor(height * scale)));
    }

    private async Task StopRenderingAsync()
    {
        await _renderController.CancelAndWaitAsync();
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _isClosed = true;
        _missingRecentFileNoticeTimer.Stop();
        _pdfOpenErrorNoticeTimer.Stop();
        _settingsService.Dispose();
        _zoomIndicatorCancellation?.Cancel();
        _zoomIndicatorCancellation?.Dispose();
        _zoomIndicatorCancellation = null;
        _ = _settingsService.SaveAsync();
        _ = StopRenderingAndDisposeAsync();
    }

    private async Task StopRenderingAndDisposeAsync()
    {
        await _renderController.CancelAndWaitAsync();
        _renderController.Dispose();
        _renderer.Dispose();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void RefreshDocumentState()
    {
        _viewModel.SynchronizeDocument(_renderer.PageCount, _leftPageIndex);
        _viewModel.RefreshRecentFiles();
        _viewModel.MissingRecentFileNotice = _missingRecentFileNotice;
        _viewModel.PdfOpenErrorNotice = _pdfOpenErrorNotice;
        OnPropertyChanged(nameof(PdfContentVisibility));
        OnPropertyChanged(nameof(EmptyContentVisibility));
        OnPropertyChanged(nameof(FullScreenButtonVisibility));
        OnPropertyChanged(nameof(RecentFiles));
        OnPropertyChanged(nameof(HasRecentFiles));
        OnPropertyChanged(nameof(RecentFilesVisibility));
        OnPropertyChanged(nameof(NoRecentFilesVisibility));
        OnPropertyChanged(nameof(MissingRecentFileNotice));
        OnPropertyChanged(nameof(MissingRecentFileNoticeVisibility));
        OnPropertyChanged(nameof(LeftPageNumberText));
        OnPropertyChanged(nameof(RightPageNumberText));
        OnPropertyChanged(nameof(CanMovePrevious));
        OnPropertyChanged(nameof(CanMoveNext));
    }
}
