namespace CongaSharp.Modes;

using CongaSharp.Events;
using CongaSharp.Protocol;

/// <summary>
/// BlkRaw mode: framed, each wire frame produces a Block/BlockLast event.
/// Uses the full Conga-Sharp wire protocol.
/// </summary>
public sealed class BlkRawMode : IConnectionMode
{
  public bool UsesFraming => true;

  public IReadOnlyList<CongaEvent> OnBytesReceived(string connectionName, ReadOnlySpan<byte> data)
      => throw new InvalidOperationException("BlkRaw mode uses framing, not raw bytes");

  public IReadOnlyList<CongaEvent> OnFrameReceived(string connectionName, FrameData frame)
  {
    // BlkRaw: Data frames produce Block or BlockLast events
    var eventType = frame.MsgType == MsgType.Data ? EventType.Block : EventType.BlockLast;

    return [new CongaEvent
        {
            ObjectName = connectionName,
            Type = eventType,
            Payload = frame.Payload,
            PayloadOwner = frame.TakePayloadOwner(),
            UserHeaders = frame.RawUserHeaders,
            UserHeadersOwner = frame.TakeRawUserHeadersOwner()
        }];
  }

  public OutboundMessage PrepareOutbound(string connectionName, ReadOnlyMemory<byte> payload, byte[]? userHeaders, PostSendAction closeFlag, string? cmdName)
  {
    if (closeFlag == PostSendAction.CloseCommand)
      return new OutboundMessage { Payload = payload, ErrorCode = Errors.ErrorCodes.InvalidMode };

    return new OutboundMessage {
      Payload = payload,
      UserHeaders = userHeaders,
      MsgType = MsgType.Data,
      PostAction = closeFlag
    };
  }

  public IReadOnlyList<CongaEvent> OnDisconnected(string connectionName) =>
      [new CongaEvent { ObjectName = connectionName, Type = EventType.Closed, ReasonCode = Errors.ErrorCodes.SocketClosed }];

  public void ResetState(string connectionName) { }
}
