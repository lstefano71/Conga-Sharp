namespace CongaSharp.Modes;

using CongaSharp.Events;

/// <summary>
/// Raw mode: unframed, sends/receives raw TCP bytes.
/// Each TCP read produces a Receive event.
/// </summary>
public sealed class RawMode : IConnectionMode
{
    public bool UsesFraming => false;

    public IReadOnlyList<CongaEvent> OnBytesReceived(string connectionName, ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return Array.Empty<CongaEvent>();
        return [new CongaEvent
        {
            ObjectName = connectionName,
            Type = EventType.Receive,
            Payload = data.ToArray()
        }];
    }

    public IReadOnlyList<CongaEvent> OnFrameReceived(string connectionName, FrameData frame)
        => throw new InvalidOperationException("Raw mode does not use framing");

    public OutboundMessage PrepareOutbound(string connectionName, ReadOnlyMemory<byte> payload, byte[]? userHeaders, PostSendAction closeFlag, string? cmdName)
    {
        // Raw mode ignores headers and command names
        if (closeFlag == PostSendAction.CloseCommand || closeFlag == PostSendAction.EmitSentEvent)
            return new OutboundMessage { Payload = payload, ErrorCode = Errors.ErrorCodes.InvalidMode };

        return new OutboundMessage
        {
            Payload = payload,
            PostAction = closeFlag
        };
    }

    public IReadOnlyList<CongaEvent> OnDisconnected(string connectionName) =>
        [new CongaEvent { ObjectName = connectionName, Type = EventType.Closed }];

    public void ResetState(string connectionName) { }
}
