namespace CongaSharp.Events;

using System.Collections.Concurrent;
using System.Threading.Channels;

/// <summary>
/// Per-command event buffer that prevents race conditions between Send and Wait.
/// Created before bytes hit the wire, so responses arriving before Wait are safely buffered.
/// </summary>
public sealed class CommandMailbox : IDisposable
{
  private readonly Channel<CongaEvent> _channel = Channel.CreateUnbounded<CongaEvent>(
      new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });
  private readonly ConcurrentQueue<CongaEvent> _requeued = new();
  private volatile bool _completed;
  private int _disposed;

  /// <summary>
  /// Posts an event to the mailbox. Non-blocking.
  /// If the mailbox is completed or disposed, the event is silently dropped.
  /// </summary>
  public bool Post(CongaEvent evt)
  {
    return _channel.Writer.TryWrite(evt);
  }

  /// <summary>
  /// Re-enqueues an event to the front of mailbox delivery order.
  /// This is used when marshaling fails (e.g., buffer too small) after an event
  /// has already been dequeued by Wait. Works even if the channel writer is complete.
  /// </summary>
  public void Requeue(CongaEvent evt)
  {
    _requeued.Enqueue(evt);
  }

  /// <summary>
  /// Blocks until an event is available or timeout expires.
  /// Returns null on timeout or cancellation.
  /// Optimised to avoid CancellationTokenSource and Task allocations on the fast path.
  /// </summary>
  public CongaEvent? TryReceive(int timeoutMs, CancellationToken cancellationToken = default)
  {
    // Re-enqueued events have priority: caller already consumed them once.
    if (_requeued.TryDequeue(out var requeued))
      return requeued;

    // Fast path: event already in the channel — zero allocations
    if (_channel.Reader.TryRead(out var immediate))
      return immediate;

    // Slow path: must wait for an event to arrive
    try {
      using var timeoutCts = new CancellationTokenSource(timeoutMs);
      using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
          timeoutCts.Token, cancellationToken);

      // Spin briefly on TryRead before falling back to async wait
      var token = linkedCts.Token;
      var sw = System.Diagnostics.Stopwatch.StartNew();
      while (sw.ElapsedMilliseconds < timeoutMs) {
        if (_channel.Reader.TryRead(out var evt))
          return evt;
        if (token.IsCancellationRequested)
          return null;

        // Wait for data using WaitToReadAsync — avoids .AsTask() on the common path
        var waitTask = _channel.Reader.WaitToReadAsync(token);
        if (waitTask.IsCompletedSuccessfully) {
          if (waitTask.Result && _channel.Reader.TryRead(out evt))
            return evt;
          return null; // channel completed with no data
        }

        // Must block — convert to Task only here (rare: event hasn't arrived yet)
        try {
          if (!waitTask.AsTask().GetAwaiter().GetResult())
            return null; // channel completed
          if (_channel.Reader.TryRead(out evt))
            return evt;
        } catch (OperationCanceledException) {
          return null;
        }
      }
      return null;
    } catch (OperationCanceledException) {
      return null;
    } catch (ChannelClosedException) {
      return null;
    }
  }

  /// <summary>
  /// Marks the mailbox as complete — no more events will be posted.
  /// Existing events can still be read.
  /// </summary>
  public void Complete()
  {
    _completed = true;
    _channel.Writer.TryComplete();
  }

  /// <summary>
  /// True after Complete() has been called AND the channel has no remaining events.
  /// </summary>
  public bool IsCompleted => _completed && _requeued.IsEmpty && _channel.Reader.Count == 0;

  /// <summary>
  /// True after Complete() has been called (regardless of remaining events).
  /// </summary>
  public bool IsMarkedComplete => _completed;

  /// <summary>
  /// Number of events currently buffered.
  /// </summary>
  public int Count => _requeued.Count + _channel.Reader.Count;

  public void Dispose()
  {
    if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
      return;
    Complete();

    // Drain and dispose any remaining events
    while (_requeued.TryDequeue(out var evt))
      evt.Dispose();
    while (_channel.Reader.TryRead(out var channelEvt))
      channelEvt.Dispose();
  }
}
