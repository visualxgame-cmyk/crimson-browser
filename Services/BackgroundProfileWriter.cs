namespace CrimsonBrowser.Services;

/// <summary>Serializes immutable snapshots off the UI thread, keeping only the newest pending snapshot.</summary>
internal sealed class BackgroundProfileWriter<T>(string path, Action<string, T>? write = null)
{
    private readonly object _gate = new();
    private Task _worker = Task.CompletedTask;
    private T? _pending;
    private bool _hasPending;
    private Exception? _lastError;

    public void Schedule(T snapshot)
    {
        lock (_gate)
        {
            _pending = snapshot;
            _hasPending = true;
            if (_worker.IsCompleted) _worker = Task.Run(WritePending);
        }
    }

    private void WritePending()
    {
        while (true)
        {
            T snapshot;
            lock (_gate)
            {
                if (!_hasPending)
                {
                    // Mark idle while holding the same lock used by Schedule.
                    _worker = Task.CompletedTask;
                    return;
                }
                snapshot = _pending!;
                _pending = default;
                _hasPending = false;
            }
            try
            {
                if (write != null) write(path, snapshot);
                else AtomicFileStore.Write(path, snapshot);
                lock (_gate) _lastError = null;
            }
            catch (Exception error)
            {
                lock (_gate) _lastError = error;
                ProfileStorageDiagnostics.Report(path, error);
            }
        }
    }

    // Shutdown waits for disk I/O only. The worker never waits on the UI dispatcher.
    public bool Flush()
    {
        Task worker;
        lock (_gate) worker = _worker;
        worker.GetAwaiter().GetResult();
        lock (_gate) return _lastError == null;
    }
}
