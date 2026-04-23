namespace CongaSharp.Events;

using System.Collections.Concurrent;
using CongaSharp.Errors;

/// <summary>
/// Thread-safe event queue with filtered blocking Wait.
/// Uses a lock-protected list + SemaphoreSlim for signaling.
/// Supports multiple concurrent waiters filtering on different objects.
///
/// For Command mode with tracked sends (track=1), per-command mailboxes
/// buffer events between Send and Wait, preventing the "fast server / slow client"
/// race condition where a broad catch-all waiter could steal a specific command's response.
/// </summary>
public sealed class EventQueue : IDisposable
{
    private static readonly byte[] ShutdownPayload =
        System.Text.Encoding.UTF8.GetBytes($"{{\"error\":{ErrorCodes.ShuttingDown}}}");

    private readonly LinkedList<CongaEvent> _events = new();
    private readonly object _lock = new();
    private readonly SemaphoreSlim _signal = new(0);
    private volatile bool _shutdown;

    // Per-command mailboxes for tracked sends (Command mode)
    private readonly ConcurrentDictionary<string, CommandMailbox> _mailboxes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Number of events currently in the global queue (excludes mailboxed events).
    /// </summary>
    public int Count
    {
        get { lock (_lock) return _events.Count; }
    }

    /// <summary>
    /// Number of active mailboxes.
    /// </summary>
    public int MailboxCount => _mailboxes.Count;

    /// <summary>
    /// Registers a per-command mailbox. Events with this exact ObjectName
    /// will be routed to the mailbox instead of the global queue.
    /// Must be called BEFORE sending bytes on the wire.
    /// </summary>
    public CommandMailbox RegisterMailbox(string objectName)
    {
        return _mailboxes.GetOrAdd(objectName, _ => new CommandMailbox());
    }

    /// <summary>
    /// Removes and disposes a mailbox. Returns true if one was found.
    /// </summary>
    public bool UnregisterMailbox(string objectName)
    {
        if (_mailboxes.TryRemove(objectName, out var mailbox))
        {
            mailbox.Dispose();
            return true;
        }
        return false;
    }

    /// <summary>
    /// Completes all mailboxes whose keys start with the given prefix (dot-boundary match).
    /// Posts a Closed event to each mailbox and marks it complete, but leaves it in the
    /// registry for pending waiters to consume. Lazy cleanup in Wait removes it after
    /// the last event is read.
    /// Used on connection close/disconnect to clean up pending command mailboxes.
    /// </summary>
    public void UnregisterMailboxesByPrefix(string prefix)
    {
        foreach (var kvp in _mailboxes)
        {
            bool match = kvp.Key.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                || (kvp.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && kvp.Key.Length > prefix.Length
                    && kvp.Key[prefix.Length] == '.');

            if (match && !kvp.Value.IsMarkedComplete)
            {
                kvp.Value.Post(new CongaEvent
                {
                    ObjectName = kvp.Key,
                    Type = EventType.Closed,
                    IsTerminal = true
                });
                kvp.Value.Complete();
            }
        }
    }

    /// <summary>
    /// Looks up a mailbox by exact name. Used for re-enqueue routing.
    /// </summary>
    public CommandMailbox? GetMailbox(string objectName)
    {
        _mailboxes.TryGetValue(objectName, out var mailbox);
        return mailbox;
    }

    /// <summary>
    /// Enqueues an event. If a mailbox exists for the event's ObjectName,
    /// routes to the mailbox; otherwise adds to the global queue.
    /// </summary>
    public void Enqueue(CongaEvent evt)
    {
        if (_shutdown) return;

        // Route to mailbox if one exists for this exact ObjectName
        if (_mailboxes.TryGetValue(evt.ObjectName, out var mailbox))
        {
            mailbox.Post(evt);
            if (evt.IsTerminal)
                mailbox.Complete();
            return;
        }

        lock (_lock)
        {
            _events.AddLast(evt);
        }

        // Release enough permits for all potential waiters to re-scan.
        // Over-releasing is harmless — waiters that find nothing just re-wait.
        try { _signal.Release(); } catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Blocks until a matching event is found or timeout expires.
    /// If objectFilter is null/empty/".", matches all events in the global queue.
    /// For exact-match filters with a registered mailbox, reads from the mailbox instead.
    /// Returns a Timeout event if no match found within timeoutMs.
    /// On shutdown, returns error code 2002.
    /// </summary>
    public CongaEvent Wait(string? objectFilter, int timeoutMs, CancellationToken shutdownToken = default)
    {
        if (_shutdown)
            return MakeShutdownEvent(objectFilter);

        bool matchAll = string.IsNullOrEmpty(objectFilter) || objectFilter == ".";

        // Fast path: exact-match filter with a registered mailbox
        if (!matchAll && _mailboxes.TryGetValue(objectFilter!, out var mailbox))
        {
            var evt = mailbox.TryReceive(timeoutMs, shutdownToken);
            if (evt != null)
            {
                // Lazy cleanup: if mailbox is complete and empty, unregister it
                if (mailbox.IsCompleted)
                    _mailboxes.TryRemove(objectFilter!, out _);
                return evt;
            }

            if (_shutdown)
                return MakeShutdownEvent(objectFilter);

            return new CongaEvent
            {
                ObjectName = objectFilter!,
                Type = EventType.Timeout
            };
        }

        // Standard path: scan the global queue
        var deadline = Environment.TickCount64 + timeoutMs;

        while (true)
        {
            // Scan for a matching event under the lock
            lock (_lock)
            {
                var node = _events.First;
                while (node != null)
                {
                    if (matchAll || IsMatch(node.Value.ObjectName, objectFilter!))
                    {
                        _events.Remove(node);
                        return node.Value;
                    }
                    node = node.Next;
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
    /// Also delivers shutdown events to all active mailboxes.
    /// </summary>
    public void SignalShutdown()
    {
        _shutdown = true;

        // Deliver shutdown to all mailboxes
        foreach (var kvp in _mailboxes)
        {
            kvp.Value.Post(new CongaEvent
            {
                ObjectName = kvp.Key,
                Type = EventType.Error,
                Payload = ShutdownPayload,
                IsTerminal = true
            });
            kvp.Value.Complete();
        }

        // Wake all global queue waiters by releasing a generous number of permits
        try { _signal.Release(100); } catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Re-enqueues an event that could not be delivered (e.g., buffer too small).
    /// Mailbox-aware: if a mailbox exists for the event, re-posts there instead of the global queue.
    /// </summary>
    public void ReEnqueue(CongaEvent evt)
    {
        if (_shutdown) return;

        // Route back to mailbox if one exists
        if (_mailboxes.TryGetValue(evt.ObjectName, out var mailbox))
        {
            mailbox.Post(evt);
            return;
        }

        lock (_lock)
        {
            _events.AddFirst(evt);
        }
        try { _signal.Release(); } catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        SignalShutdown();

        // Dispose all remaining mailboxes
        foreach (var kvp in _mailboxes)
        {
            if (_mailboxes.TryRemove(kvp.Key, out var mailbox))
                mailbox.Dispose();
        }

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
        Payload = ShutdownPayload
    };
}
