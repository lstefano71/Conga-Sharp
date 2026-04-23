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
  /// </summary>
  public CongaEvent? TryReceive(int timeoutMs, CancellationToken cancellationToken = default)
  {
    // Re-enqueued events have priority: caller already consumed them once.
    if (_requeued.TryDequeue(out var requeued))
      return requeued;

    using var timeoutCts = new CancellationTokenSource(timeoutMs);
    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
        timeoutCts.Token, cancellationToken);

    try {
      // Synchronously block on the async read — matches the sync C API contract
      var task = _channel.Reader.ReadAsync(linkedCts.Token).AsTask();
      return task.GetAwaiter().GetResult();
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
  }
}
