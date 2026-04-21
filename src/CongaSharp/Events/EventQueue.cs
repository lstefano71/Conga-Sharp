namespace CongaSharp.Events;

using CongaSharp.Errors;

/// <summary>
/// Thread-safe event queue with filtered blocking Wait.
/// Uses a lock-protected list + SemaphoreSlim for signaling.
/// Supports multiple concurrent waiters filtering on different objects.
/// </summary>
public sealed class EventQueue : IDisposable
{
    private readonly List<CongaEvent> _events = new();
    private readonly object _lock = new();
    private readonly SemaphoreSlim _signal = new(0);
    private volatile bool _shutdown;

    /// <summary>
    /// Number of events currently in the queue.
    /// </summary>
    public int Count
    {
        get { lock (_lock) return _events.Count; }
    }

    /// <summary>
    /// Enqueues an event and signals all waiters.
    /// </summary>
    public void Enqueue(CongaEvent evt)
    {
        if (_shutdown) return;

        lock (_lock)
        {
            _events.Add(evt);
        }

        // Release enough permits for all potential waiters to re-scan.
        // Over-releasing is harmless — waiters that find nothing just re-wait.
        try { _signal.Release(); } catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Blocks until a matching event is found or timeout expires.
    /// If objectFilter is null/empty/".", matches all events.
    /// Returns a Timeout event if no match found within timeoutMs.
    /// On shutdown, returns error code 2002.
    /// </summary>
    public CongaEvent Wait(string? objectFilter, int timeoutMs, CancellationToken shutdownToken = default)
    {
        if (_shutdown)
            return MakeShutdownEvent(objectFilter);

        var deadline = Environment.TickCount64 + timeoutMs;
        bool matchAll = string.IsNullOrEmpty(objectFilter) || objectFilter == ".";

        while (true)
        {
            // Scan for a matching event under the lock
            lock (_lock)
            {
                for (int i = 0; i < _events.Count; i++)
                {
                    var evt = _events[i];
                    if (matchAll || IsMatch(evt.ObjectName, objectFilter!))
                    {
                        _events.RemoveAt(i);
                        return evt;
                    }
                }
            }

            if (_shutdown)
                return MakeShutdownEvent(objectFilter);

            // Calculate remaining timeout
            var remaining = (int)(deadline - Environment.TickCount64);
            if (remaining <= 0)
            {
                return new CongaEvent
                {
                    ObjectName = objectFilter ?? ".",
                    Type = EventType.Timeout
                };
            }

            // Wait for a signal (new event enqueued or shutdown)
            try
            {
                _signal.Wait(remaining, shutdownToken);
            }
            catch (OperationCanceledException)
            {
                return MakeShutdownEvent(objectFilter);
            }
        }
    }

    /// <summary>
    /// Signals shutdown: all current and future Wait calls return error 2002.
    /// </summary>
    public void SignalShutdown()
    {
        _shutdown = true;
        // Wake all waiters by releasing a generous number of permits
        try { _signal.Release(100); } catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        SignalShutdown();
        _signal.Dispose();
    }

    /// <summary>
    /// Checks if an event's object name matches the filter.
    /// The filter matches the object itself or any descendant (prefix match on dot boundary).
    /// </summary>
    private static bool IsMatch(string eventObjectName, string filter)
    {
        // Exact match
        if (eventObjectName.Equals(filter, StringComparison.OrdinalIgnoreCase))
            return true;

        // Prefix match: filter "S1" matches "S1.CON0001", "S1.CON0001.Cmd1"
        if (eventObjectName.StartsWith(filter, StringComparison.OrdinalIgnoreCase)
            && eventObjectName.Length > filter.Length
            && eventObjectName[filter.Length] == '.')
            return true;

        return false;
    }

    private static CongaEvent MakeShutdownEvent(string? objectFilter) => new()
    {
        ObjectName = objectFilter ?? ".",
        Type = EventType.Error,
        Payload = System.Text.Encoding.UTF8.GetBytes($"{{\"error\":{ErrorCodes.ShuttingDown}}}")
    };
}
