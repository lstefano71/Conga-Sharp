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
    /// True if this event terminates a command lifecycle (e.g., Respond frame).
    /// Used by EventQueue to mark the command's mailbox as complete.
    /// </summary>
    public bool IsTerminal { get; init; }

    private static readonly string[] EventTypeNames = Enum.GetValues<EventType>()
        .OrderBy(e => (int)e)
        .Select(e => e.ToString())
        .ToArray();

    private static readonly int MinEventValue = (int)Enum.GetValues<EventType>().Min();

    /// <summary>
    /// Human-readable event name for the C API output. Cached to avoid per-call allocation.
    /// </summary>
    public string EventName
    {
        get
        {
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
}
