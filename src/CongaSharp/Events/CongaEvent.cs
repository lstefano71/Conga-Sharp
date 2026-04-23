namespace CongaSharp.Events;

/// <summary>
/// Represents a single event to be delivered via conga_wait.
/// Immutable once created. Implements <see cref="IDisposable"/> to return
/// pooled buffers (Payload, UserHeaders) when the event is consumed.
/// </summary>
public sealed class CongaEvent : IDisposable
{
  public required string ObjectName { get; init; }
  public required EventType Type { get; init; }
  public ReadOnlyMemory<byte> Payload { get; init; }
  public ReadOnlyMemory<byte> UserHeaders { get; init; }
  public DateTime Timestamp { get; init; } = DateTime.UtcNow;

  /// <summary>
  /// True if this event terminates a command lifecycle (e.g., Respond frame).
  /// Used by EventQueue to mark the command's mailbox as complete.
  /// </summary>
  public bool IsTerminal { get; init; }

  /// <summary>
  /// Optional owner for the Payload buffer. Disposed when the event is consumed.
  /// Use <see cref="TakePayloadOwner"/> for move-only transfer.
  /// </summary>
  internal IDisposable? PayloadOwner
  {
    get => _payloadOwner;
    init => _payloadOwner = value;
  }
  private IDisposable? _payloadOwner;

  /// <summary>
  /// Optional owner for the UserHeaders buffer. Disposed when the event is consumed.
  /// Use <see cref="TakeUserHeadersOwner"/> for move-only transfer.
  /// </summary>
  internal IDisposable? UserHeadersOwner
  {
    get => _userHeadersOwner;
    init => _userHeadersOwner = value;
  }
  private IDisposable? _userHeadersOwner;

  /// <summary>
  /// Atomically takes ownership of the Payload buffer, clearing it from this event.
  /// Returns null if already taken or no owner was set.
  /// </summary>
  internal IDisposable? TakePayloadOwner()
      => Interlocked.Exchange(ref _payloadOwner, null);

  /// <summary>
  /// Atomically takes ownership of the UserHeaders buffer, clearing it from this event.
  /// Returns null if already taken or no owner was set.
  /// </summary>
  internal IDisposable? TakeUserHeadersOwner()
      => Interlocked.Exchange(ref _userHeadersOwner, null);

  private static readonly string[] EventTypeNames = Enum.GetValues<EventType>()
      .OrderBy(e => (int)e)
      .Select(e => e.ToString())
      .ToArray();

  private static readonly int MinEventValue = (int)Enum.GetValues<EventType>().Min();

  /// <summary>
  /// Human-readable event name for the C API output. Cached to avoid per-call allocation.
  /// </summary>
  public string EventName {
    get {
      int index = (int)Type - MinEventValue;
      return (index >= 0 && index < EventTypeNames.Length)
          ? EventTypeNames[index]
          : Type.ToString();
    }
  }

  /// <summary>
  /// Numeric event code for the C API output.
  /// </summary>
  public int EventCode => (int)Type;

  /// <summary>
  /// Returns pooled buffers. Safe to call multiple times.
  /// </summary>
  public void Dispose()
  {
    Interlocked.Exchange(ref _payloadOwner, null)?.Dispose();
    Interlocked.Exchange(ref _userHeadersOwner, null)?.Dispose();
  }
}
