namespace SheetMusicViewer;

/// <summary>
/// Serializes expensive rendering work. A newer request cancels queued work and
/// waits for a non-cancellable platform render to leave the gate before starting.
/// </summary>
public sealed class RenderController : IDisposable
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(150);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<Task> _tasks = [];
    private readonly object _tasksLock = new();
    private CancellationTokenSource? _currentCancellation;
    private bool _disposed;

    public Task QueueAsync(Func<CancellationToken, Task> render, bool debounce)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _currentCancellation?.Cancel();
        var cancellation = _currentCancellation = new CancellationTokenSource();
        var task = RunAsync(render, debounce, cancellation);
        lock (_tasksLock) _tasks.Add(task);
        _ = task.ContinueWith(_ => { lock (_tasksLock) _tasks.Remove(task); }, TaskScheduler.Default);
        return task;
    }

    public async Task CancelAndWaitAsync()
    {
        _currentCancellation?.Cancel();
        Task[] tasks;
        lock (_tasksLock) tasks = _tasks.ToArray();
        if (tasks.Length > 0) await Task.WhenAll(tasks);
    }

    private async Task RunAsync(Func<CancellationToken, Task> render, bool debounce, CancellationTokenSource cancellation)
    {
        var acquired = false;
        try
        {
            if (debounce) await Task.Delay(DebounceDelay, cancellation.Token);
            await _gate.WaitAsync(cancellation.Token);
            acquired = true;
            cancellation.Token.ThrowIfCancellationRequested();
            await render(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            if (acquired) _gate.Release();
            Interlocked.CompareExchange(ref _currentCancellation, null, cancellation);
            cancellation.Dispose();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _currentCancellation?.Cancel();
        _currentCancellation?.Dispose();
        _gate.Dispose();
    }
}
