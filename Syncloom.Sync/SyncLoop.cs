namespace Syncloom.Sync;

/// <summary>
/// Runs the engine in the background: a pull when the server's change counter moved (checked every few seconds, and at
/// least every five minutes), a push when the sync root changed locally (<see cref="Poke"/>) or after a pull.
/// </summary>
public sealed class SyncLoop(SyncEngine engine, IRemote remote, Action<string> status) : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private volatile bool _dirty = true;
    private Task? _task;

    public TimeSpan Poll { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan Debounce { get; init; } = TimeSpan.FromMilliseconds(1500);
    public TimeSpan FullPull { get; init; } = TimeSpan.FromMinutes(5);

    public void Start() => _task ??= Task.Run(RunAsync);

    /// <summary>Something changed in the sync root.</summary>
    public void Poke()
    {
        _dirty = true;
        try { _wake.Release(); } catch (SemaphoreFullException) { }
    }

    private async Task RunAsync()
    {
        long lastHead = -1;
        var lastPull = DateTime.MinValue;
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var head = remote.ChangesHead();
                var pull = head != lastHead || DateTime.UtcNow - lastPull > FullPull;
                var push = _dirty || pull;
                var parts = new List<string>();
                var errors = 0;
                if (pull)
                {
                    var r = engine.Pull();
                    lastPull = DateTime.UtcNow;
                    lastHead = r.Errors.Count == 0 ? head : -1; // retry soon after errors
                    errors += r.Errors.Count;
                    if (r.Changed) parts.Add($"{r.Created} new, {r.Updated} updated, {r.DeletedLocal} removed");
                }
                if (push)
                {
                    _dirty = false;
                    var r = engine.Push();
                    errors += r.Errors.Count;
                    if (r.Changed) parts.Add($"{r.Uploaded + r.Versioned} uploaded, {r.Conflicts} conflict copies");
                }
                status(errors > 0 ? $"{errors} items could not be synced" : parts.Count > 0 ? string.Join(" · ", parts) : "Up to date");
            }
            catch (Exception e) { status(e.Message); }

            try
            {
                if (await _wake.WaitAsync(Poll, ct)) await Task.Delay(Debounce, ct); // let a burst of file events settle
            }
            catch (OperationCanceledException) { }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _task?.Wait(TimeSpan.FromSeconds(5)); } catch { }
        _cts.Dispose();
    }
}
