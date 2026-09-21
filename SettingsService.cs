using System.Windows.Input;

namespace SheetMusicViewer;

/// <summary>Application settings with one debounced persistence boundary.</summary>
public sealed class SettingsService : IDisposable
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(500);
    private CancellationTokenSource? _saveCancellation;

    public SettingsService(ViewerSettings? settings = null) => Settings = settings ?? ViewerSettings.Load();

    public ViewerSettings Settings { get; }
    public Key NextKey => Settings.NextKey;
    public Key PreviousKey => Settings.PreviousKey;
    public Key ZoomInKey => Settings.ZoomInKey;
    public Key ZoomOutKey => Settings.ZoomOutKey;
    public IReadOnlyList<string> RecentFiles => Settings.AvailableRecentFiles();

    public void SetZoom(double zoom)
    {
        Settings.Zoom = zoom;
        ScheduleSave();
    }

    public void RememberFile(string path)
    {
        Settings.RememberFile(path);
        ScheduleSave();
    }

    public void ForgetFile(string path)
    {
        Settings.ForgetFile(path);
        ScheduleSave();
    }

    public void ScheduleSave()
    {
        _saveCancellation?.Cancel();
        _saveCancellation?.Dispose();
        var cancellation = _saveCancellation = new CancellationTokenSource();
        _ = SaveAfterDelayAsync(cancellation.Token);
    }

    public Task SaveAsync() => Settings.SaveAsync();

    private async Task SaveAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(SaveDelay, cancellationToken);
            await Settings.SaveAsync();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    public void Dispose()
    {
        _saveCancellation?.Cancel();
        _saveCancellation?.Dispose();
        _saveCancellation = null;
    }
}
