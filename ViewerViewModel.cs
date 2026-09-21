using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace SheetMusicViewer;

/// <summary>Bind-only state for the viewer; it deliberately has no WPF controls or rendering code.</summary>
public sealed class ViewerViewModel : INotifyPropertyChanged
{
    private readonly SettingsService _settings;
    private string _statusText = "PDF öffnen, dann mit → / ← blättern";
    private string _missingRecentFileNotice = string.Empty;
    private string _pdfOpenErrorNotice = string.Empty;
    private int _leftPageIndex;
    private int _pageCount;
    private double _zoom;
    private bool _isFullScreen;
    private Visibility _zoomIndicatorVisibility = Visibility.Collapsed;

    public ViewerViewModel(SettingsService settings)
    {
        _settings = settings;
        _zoom = settings.Settings.Zoom;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }
    public string MissingRecentFileNotice { get => _missingRecentFileNotice; set { if (Set(ref _missingRecentFileNotice, value)) Notify(nameof(MissingRecentFileNoticeVisibility)); } }
    public string PdfOpenErrorNotice { get => _pdfOpenErrorNotice; set { if (Set(ref _pdfOpenErrorNotice, value)) Notify(nameof(PdfOpenErrorNoticeVisibility)); } }
    public int LeftPageIndex => _leftPageIndex;
    public int PageCount => _pageCount;
    public double Zoom => _zoom;
    public bool IsDocumentOpen => _pageCount > 0;
    public bool IsFullScreen { get => _isFullScreen; set { if (Set(ref _isFullScreen, value)) NotifyFullScreen(); } }
    public string PreviousToolTip => $"Vorherige Seite ({KeyText(_settings.PreviousKey)})";
    public string NextToolTip => $"Nächste Seite ({KeyText(_settings.NextKey)})";
    public string ClosePdfToolTip => IsFullScreen ? "Vollbild beenden (ESC)" : "PDF schließen";
    public string FullScreenToolTip => IsFullScreen ? "Vollbild beenden (ESC)" : "Vollbild aktivieren (F11)";
    public Visibility FullScreenButtonVisibility => !IsFullScreen && IsDocumentOpen ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PdfContentVisibility => IsDocumentOpen ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EmptyContentVisibility => IsDocumentOpen ? Visibility.Collapsed : Visibility.Visible;
    public IReadOnlyList<string> RecentFiles => _settings.RecentFiles;
    public bool HasRecentFiles => RecentFiles.Count > 0;
    public Visibility RecentFilesVisibility => HasRecentFiles ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoRecentFilesVisibility => HasRecentFiles ? Visibility.Collapsed : Visibility.Visible;
    public Visibility MissingRecentFileNoticeVisibility => string.IsNullOrEmpty(MissingRecentFileNotice) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility PdfOpenErrorNoticeVisibility => string.IsNullOrEmpty(PdfOpenErrorNotice) ? Visibility.Collapsed : Visibility.Visible;
    public bool CanMovePrevious => IsDocumentOpen && _leftPageIndex > 0;
    public bool CanMoveNext => IsDocumentOpen && _leftPageIndex < _pageCount - 2;
    public string LeftPageNumberText => IsDocumentOpen ? $"{_leftPageIndex + 1} / {_pageCount}" : string.Empty;
    public string RightPageNumberText => IsDocumentOpen && _leftPageIndex + 1 < _pageCount ? $"{_leftPageIndex + 2} / {_pageCount}" : string.Empty;
    public string ZoomIndicatorText => $"{Math.Round(_zoom * 100):0} %";
    public Visibility ZoomIndicatorVisibility { get => _zoomIndicatorVisibility; set => Set(ref _zoomIndicatorVisibility, value); }

    public void SetDocument(int pageCount) => SynchronizeDocument(pageCount, 0);
    public void SynchronizeDocument(int pageCount, int leftPageIndex)
    {
        _pageCount = pageCount;
        _leftPageIndex = Math.Clamp(leftPageIndex, 0, Math.Max(0, pageCount - 1));
        RefreshDocumentState();
    }
    public void CloseDocument() => SetDocument(0);
    public bool Move(int delta)
    {
        if (!IsDocumentOpen) return false;
        var target = Math.Clamp(_leftPageIndex + delta, 0, Math.Max(0, _pageCount - 2));
        if (target == _leftPageIndex) return false;
        _leftPageIndex = target;
        NotifyPageState();
        return true;
    }
    public void ChangeZoom(double amount)
    {
        _zoom = Math.Clamp(_zoom + amount, 0.5, 3.0);
        _settings.SetZoom(_zoom);
        Notify(nameof(Zoom), nameof(ZoomIndicatorText));
    }
    public void RefreshRecentFiles() => Notify(nameof(RecentFiles), nameof(HasRecentFiles), nameof(RecentFilesVisibility), nameof(NoRecentFilesVisibility));

    private void RefreshDocumentState() => Notify(nameof(IsDocumentOpen), nameof(PdfContentVisibility), nameof(EmptyContentVisibility), nameof(FullScreenButtonVisibility), nameof(LeftPageNumberText), nameof(RightPageNumberText), nameof(CanMovePrevious), nameof(CanMoveNext));
    private void NotifyPageState() => Notify(nameof(LeftPageIndex), nameof(LeftPageNumberText), nameof(RightPageNumberText), nameof(CanMovePrevious), nameof(CanMoveNext));
    private void NotifyFullScreen() => Notify(nameof(ClosePdfToolTip), nameof(FullScreenToolTip), nameof(FullScreenButtonVisibility));
    private static string KeyText(System.Windows.Input.Key key) => key switch { System.Windows.Input.Key.Left => "←", System.Windows.Input.Key.Right => "→", _ => key.ToString() };
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null) { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; Notify(name!); return true; }
    private void Notify(params string[] names) { foreach (var name in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)); }
}
