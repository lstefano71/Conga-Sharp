namespace CongaSharp.Modes;

using CongaSharp.Events;
using CongaSharp.Protocol;

/// <summary>
/// BlkText mode: framed, same as BlkRaw but payload treated as text.
/// Uses the full Conga-Sharp wire protocol.
/// </summary>
public sealed class BlkTextMode : IConnectionMode
{
    public bool UsesFraming => true;

    public IReadOnlyList<CongaEvent> OnBytesReceived(string connectionName, ReadOnlySpan<byte> data)
        => throw new InvalidOperationException("BlkText mode uses framing, not raw bytes");

    public IReadOnlyList<CongaEvent> OnFrameReceived(string connectionName, FrameData frame)
    {
        var eventType = frame.MsgType == MsgType.Data ? EventType.Block : EventType.BlockLast;

        var userHeadersBytes = frame.UserHeaders.Count > 0
            ? UserHeaders.Encode(frame.UserHeaders)
            : Array.Empty<byte>();

        return [new CongaEvent
        {
            ObjectName = connectionName,
            Type = eventType,
            Payload = frame.Payload,
            UserHeaders = userHeadersBytes
        }];
    }

    public OutboundMessage PrepareOutbound(string connectionName, byte[] payload, byte[]? userHeaders, PostSendAction closeFlag, string? cmdName)
    {
        if (closeFlag == PostSendAction.CloseCommand)
            return new OutboundMessage { Payload = payload, ErrorCode = Errors.ErrorCodes.InvalidMode };

        return new OutboundMessage
        {
            Payload = payload,
            UserHeaders = userHeaders,
            MsgType = MsgType.Data,
            PostAction = closeFlag
        };
    }

    public IReadOnlyList<CongaEvent> OnDisconnected(string connectionName) =>
        [new CongaEvent { ObjectName = connectionName, Type = EventType.Closed }];

    public void ResetState(string connectionName) { }
}
