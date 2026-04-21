namespace CongaSharp.Events;

/// <summary>
/// Represents a single event to be delivered via conga_wait.
/// Immutable once created.
/// </summary>
public sealed class CongaEvent
{
    public required string ObjectName { get; init; }
    public required EventType Type { get; init; }
    public byte[] Payload { get; init; } = Array.Empty<byte>();
    public byte[] UserHeaders { get; init; } = Array.Empty<byte>();
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Human-readable event name for the C API output.
    /// </summary>
    public string EventName => Type.ToString();

    /// <summary>
    /// Numeric event code for the C API output.
    /// </summary>
    public int EventCode => (int)Type;
}
