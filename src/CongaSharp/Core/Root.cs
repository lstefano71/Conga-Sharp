namespace CongaSharp.Core;

using CongaSharp.Diagnostics;
using CongaSharp.Events;
using CongaSharp.Networking;
using CongaSharp.Properties;

/// <summary>
/// Top-level container for a Conga-Sharp instance.
/// Owns the object registry, event queue, and configuration.
/// Created by conga_init, destroyed by conga_shutdown.
/// </summary>
public sealed class Root : IDisposable
{
    private readonly CancellationTokenSource _shutdownCts = new();
    private int _shuttingDown;
    private int _resourcesDisposed;

    public nint Handle { get; internal set; }
    public bool IsShuttingDown => _shutdownCts.IsCancellationRequested;
    public CancellationToken ShutdownToken => _shutdownCts.Token;
    public ObjectRegistry Registry { get; } = new();
    public PropertyStore Properties { get; } = new(ObjectType.Root);
    public TraceLogger Trace { get; } = new();
    public EventQueue Events { get; } = new();

    /// <summary>
    /// Initiates shutdown: signals all pending operations to cancel,
    /// unblocks all waiters, closes all connections.
    /// </summary>
    public void Shutdown()
    {
        if (Interlocked.CompareExchange(ref _shuttingDown, 1, 0) == 0)
        {
            _shutdownCts.Cancel();
            Events.SignalShutdown();
        }
    }

    public void Dispose()
    {
        Shutdown();

        if (Interlocked.CompareExchange(ref _resourcesDisposed, 1, 0) != 0)
            return;

        foreach (var obj in Registry.GetAllObjects())
        {
            try
            {
                if (obj is IAsyncDisposable ad)
                    ad.DisposeAsync().GetAwaiter().GetResult();
                else if (obj is ConnectionObject conn && conn.Pipeline != null)
                    conn.Pipeline.DisposeAsync().GetAwaiter().GetResult();
            }
            catch { }
        }

        Events.Dispose();
        Trace.Dispose();
        _shutdownCts.Dispose();
    }
}
