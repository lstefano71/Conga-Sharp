namespace CongaSharp.Core;

using CongaSharp.Diagnostics;
using CongaSharp.Properties;

/// <summary>
/// Top-level container for a Conga-Sharp instance.
/// Owns the object registry, event queue, and configuration.
/// Created by conga_init, destroyed by conga_shutdown.
/// </summary>
public sealed class Root : IDisposable
{
    private readonly CancellationTokenSource _shutdownCts = new();
    private int _disposed;

    public nint Handle { get; internal set; }
    public bool IsShuttingDown => _shutdownCts.IsCancellationRequested;
    public CancellationToken ShutdownToken => _shutdownCts.Token;
    public ObjectRegistry Registry { get; } = new();
    public PropertyStore Properties { get; } = new(ObjectType.Root);
    public TraceLogger Trace { get; } = new();

    /// <summary>
    /// Initiates shutdown: signals all pending operations to cancel.
    /// </summary>
    public void Shutdown()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) == 0)
        {
            _shutdownCts.Cancel();
            // Future: close all connections, drain event queue
        }
    }

    public void Dispose()
    {
        Shutdown();
        Trace.Dispose();
        _shutdownCts.Dispose();
    }
}
