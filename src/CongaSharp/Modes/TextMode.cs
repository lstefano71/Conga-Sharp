namespace CongaSharp.Modes;

using CongaSharp.Events;

using System.Collections.Concurrent;
using System.Text.Json;

/// <summary>
/// Text mode: unframed, accumulates bytes until EOM pattern found.
/// EOM patterns are configured via the EOM property as an array of byte arrays.
/// </summary>
public sealed class TextMode : IConnectionMode
{
  private readonly ConcurrentDictionary<string, ByteBuffer> _buffers = new();

  private volatile byte[][] _eomPatterns = [];

  /// <summary>
  /// EOM byte sequences (read-only snapshot). If empty, behaves like Raw mode.
  /// Use <see cref="ConfigureEomFromJson"/> to set patterns thread-safely.
  /// </summary>
  public byte[][] EomPatterns => _eomPatterns;

  public bool UsesFraming => false;

  public IReadOnlyList<CongaEvent> OnBytesReceived(string connectionName, ReadOnlySpan<byte> data)
  {
    if (data.IsEmpty) return Array.Empty<CongaEvent>();

    var buffer = _buffers.GetOrAdd(connectionName, _ => new ByteBuffer());
    var events = new List<CongaEvent>();

    lock (buffer) {
      buffer.Append(data);

      var patterns = _eomPatterns; // snapshot for thread safety

      // No EOM configured — deliver everything immediately
      if (patterns.Length == 0) {
        events.Add(new CongaEvent {
          ObjectName = connectionName,
          Type = EventType.Receive,
          Payload = buffer.ToArray()
        });
        buffer.Clear();
        return events;
      }

      // Scan for EOM patterns using Span
      while (TryFindEom(buffer, patterns, out int endIndex)) {
        var message = buffer.CopyRange(0, endIndex);
        buffer.ConsumeFromFront(endIndex);

        events.Add(new CongaEvent {
          ObjectName = connectionName,
          Type = EventType.Receive,
          Payload = message
        });
      }
    }

    return events;
  }

  public IReadOnlyList<CongaEvent> OnFrameReceived(string connectionName, FrameData frame)
      => throw new InvalidOperationException("Text mode does not use framing");

  public OutboundMessage PrepareOutbound(string connectionName, ReadOnlyMemory<byte> payload, byte[]? userHeaders, PostSendAction closeFlag, string? cmdName)
  {
    if (closeFlag == PostSendAction.CloseCommand || closeFlag == PostSendAction.EmitSentEvent)
      return new OutboundMessage { Payload = payload, ErrorCode = Errors.ErrorCodes.InvalidMode };

    return new OutboundMessage {
      Payload = payload,
      PostAction = closeFlag
    };
  }

  public IReadOnlyList<CongaEvent> OnDisconnected(string connectionName)
  {
    var events = new List<CongaEvent>();

    // Deliver any remaining buffered data
    if (_buffers.TryRemove(connectionName, out var buffer)) {
      lock (buffer) {
        if (buffer.Length > 0) {
          events.Add(new CongaEvent {
            ObjectName = connectionName,
            Type = EventType.Receive,
            Payload = buffer.ToArray()
          });
        }
      }
    }

    events.Add(new CongaEvent { ObjectName = connectionName, Type = EventType.Closed, ReasonCode = Errors.ErrorCodes.SocketClosed });
    return events;
  }

  public void ResetState(string connectionName)
  {
    _buffers.TryRemove(connectionName, out _);
  }

  /// <summary>
  /// Configures EOM from a JSON property value like "[[13,10]]" or "[[13,10],[10]]".
  /// Uses System.Text.Json DOM to avoid AOT trimming issues.
  /// Builds a new array and swaps atomically for thread safety.
  /// </summary>
  public void ConfigureEomFromJson(string eomJson)
  {
    if (string.IsNullOrWhiteSpace(eomJson) || eomJson == "[]") {
      _eomPatterns = [];
      return;
    }

    using var doc = JsonDocument.Parse(eomJson);
    if (doc.RootElement.ValueKind != JsonValueKind.Array) {
      _eomPatterns = [];
      return;
    }

    var patterns = new List<byte[]>();
    foreach (var inner in doc.RootElement.EnumerateArray()) {
      if (inner.ValueKind != JsonValueKind.Array) continue;
      var pattern = new List<byte>();
      foreach (var b in inner.EnumerateArray()) {
        if (b.TryGetInt32(out var val))
          pattern.Add((byte)val);
      }
      if (pattern.Count > 0)
        patterns.Add(pattern.ToArray());
    }
    _eomPatterns = patterns.ToArray();
  }

  /// <summary>
  /// SIMD-accelerated EOM pattern search using ReadOnlySpan.IndexOf.
  /// </summary>
  private static bool TryFindEom(ByteBuffer buffer, byte[][] patterns, out int endIndex)
  {
    endIndex = 0;
    var span = buffer.Span;
    foreach (var pattern in patterns) {
      int idx = span.IndexOf(pattern.AsSpan());
      if (idx >= 0) {
        endIndex = idx + pattern.Length;
        return true;
      }
    }
    return false;
  }

  /// <summary>
  /// Span-backed byte buffer with O(1) append and efficient consume-from-front.
  /// </summary>
  private sealed class ByteBuffer
  {
    private byte[] _data;
    private int _length;

    public ByteBuffer(int initialCapacity = 4096)
    {
      _data = new byte[initialCapacity];
    }

    public int Length => _length;

    public ReadOnlySpan<byte> Span => _data.AsSpan(0, _length);

    public void Append(ReadOnlySpan<byte> data)
    {
      EnsureCapacity(_length + data.Length);
      data.CopyTo(_data.AsSpan(_length));
      _length += data.Length;
    }

    public byte[] CopyRange(int start, int count)
    {
      var result = new byte[count];
      _data.AsSpan(start, count).CopyTo(result);
      return result;
    }

    public void ConsumeFromFront(int count)
    {
      if (count >= _length) {
        _length = 0;
        return;
      }
      _data.AsSpan(count, _length - count).CopyTo(_data.AsSpan());
      _length -= count;
    }

    public byte[] ToArray()
    {
      return _data.AsSpan(0, _length).ToArray();
    }

    public void Clear()
    {
      _length = 0;
    }

    private void EnsureCapacity(int required)
    {
      if (required <= _data.Length) return;
      var newCapacity = Math.Max(_data.Length * 2, required);
      var newData = new byte[newCapacity];
      _data.AsSpan(0, _length).CopyTo(newData);
      _data = newData;
    }
  }
}
