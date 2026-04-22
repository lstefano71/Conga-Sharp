namespace CongaSharp.Modes;

using CongaSharp.Events;

/// <summary>
/// Abstracts the interpretation layer between raw socket I/O and application events.
/// The SocketPipeline owns the socket; the mode owns the meaning.
/// </summary>
public interface IConnectionMode
{
    /// <summary>
    /// Whether this mode uses the Conga-Sharp wire protocol framing.
    /// If true, the pipeline reads/writes FrameHeader-based frames.
    /// If false, the pipeline reads/writes raw TCP bytes.
    /// </summary>
    bool UsesFraming { get; }

    /// <summary>
    /// Processes raw bytes received from the socket (unframed modes only).
    /// Returns zero or more events to enqueue.
    /// </summary>
    IReadOnlyList<CongaEvent> OnBytesReceived(string connectionName, ReadOnlySpan<byte> data);

    /// <summary>
    /// Processes a decoded wire protocol frame (framed modes only).
    /// Returns zero or more events to enqueue.
    /// </summary>
    IReadOnlyList<CongaEvent> OnFrameReceived(string connectionName, FrameData frame);

    /// <summary>
    /// Prepares outbound data for sending.
    /// For unframed modes: returns raw bytes.
    /// For framed modes: returns frame metadata (payload, headers, msgType, cmdName).
    /// </summary>
    OutboundMessage PrepareOutbound(string connectionName, ReadOnlyMemory<byte> payload, byte[]? userHeaders, PostSendAction closeFlag, string? cmdName);

    /// <summary>
    /// Called when the remote side disconnects.
    /// Returns events to enqueue (typically a Closed event).
    /// </summary>
    IReadOnlyList<CongaEvent> OnDisconnected(string connectionName);

    /// <summary>
    /// Resets any accumulated state (e.g., partial EOM buffer) for a connection.
    /// Called when a connection is closed.
    /// </summary>
    void ResetState(string connectionName);
}

/// <summary>
/// Decoded frame data passed to the mode processor.
/// </summary>
public sealed class FrameData
{
    public required Protocol.MsgType MsgType { get; init; }
    public required string CmdName { get; init; }
    public required byte[] Payload { get; init; }
    public required Dictionary<string, byte[]> UserHeaders { get; init; }
}

/// <summary>
/// Outbound message prepared by the mode for the pipeline to send.
/// </summary>
public sealed class OutboundMessage
{
    /// <summary>
    /// For unframed modes: the raw bytes to write to socket.
    /// For framed modes: the payload bytes to include in the wire frame.
    /// May reference a pooled buffer — only the slice defined by this Memory is valid.
    /// </summary>
    public required ReadOnlyMemory<byte> Payload { get; init; }

    /// <summary>
    /// For framed modes: user headers to include. Null for unframed.
    /// </summary>
    public byte[]? UserHeaders { get; init; }

    /// <summary>
    /// For framed modes: the message type.
    /// </summary>
    public Protocol.MsgType MsgType { get; init; } = Protocol.MsgType.Data;

    /// <summary>
    /// For framed modes: the command name (Command mode only).
    /// </summary>
    public string? CmdName { get; init; }

    /// <summary>
    /// Action to take after successful send.
    /// </summary>
    public PostSendAction PostAction { get; init; } = PostSendAction.None;

    /// <summary>
    /// Error code if the outbound preparation failed (e.g., invalid close_flag for this mode).
    /// 0 = success.
    /// </summary>
    public int ErrorCode { get; init; }

    /// <summary>
    /// For framed modes: the compression algorithm to use for this message.
    /// Mutable so callers can set per-message after PrepareOutbound creates the message.
    /// </summary>
    public Protocol.CompressionAlgorithm Compression { get; set; } = Protocol.CompressionAlgorithm.None;

    /// <summary>
    /// For framed modes: the compression level (0 = algorithm's default).
    /// Deflate: 1-3; LZ4: 0-12; Zstd: 1-22. Mutable per-message.
    /// </summary>
    public int CompressionLevel { get; set; }
}
