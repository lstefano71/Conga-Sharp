namespace CongaSharp.Modes;

using System.Collections.Concurrent;
using CongaSharp.Events;
using CongaSharp.Protocol;

/// <summary>
/// Command mode: framed, supports named commands with Progress and Respond.
/// Each connection can have multiple parallel commands.
/// </summary>
public sealed class CommandMode : IConnectionMode
{
    // Tracks active command names per connection
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, bool>> _activeCommands = new();

    public bool UsesFraming => true;

    public IReadOnlyList<CongaEvent> OnBytesReceived(string connectionName, ReadOnlySpan<byte> data)
        => throw new InvalidOperationException("Command mode uses framing, not raw bytes");

    public IReadOnlyList<CongaEvent> OnFrameReceived(string connectionName, FrameData frame)
    {
        var events = new List<CongaEvent>();
        var cmdName = frame.CmdName;
        var cmdObjectName = string.IsNullOrEmpty(cmdName) ? connectionName : $"{connectionName}.{cmdName}";

        var userHeadersBytes = frame.UserHeaders.Count > 0
            ? UserHeaders.Encode(frame.UserHeaders)
            : Array.Empty<byte>();

        switch (frame.MsgType)
        {
            case MsgType.Data:
                // New or existing command — enqueue Receive event
                TrackCommand(connectionName, cmdName);
                events.Add(new CongaEvent
                {
                    ObjectName = cmdObjectName,
                    Type = EventType.Receive,
                    Payload = frame.Payload,
                    UserHeaders = userHeadersBytes
                });
                break;

            case MsgType.Progress:
                events.Add(new CongaEvent
                {
                    ObjectName = cmdObjectName,
                    Type = EventType.Progress,
                    Payload = frame.Payload,
                    UserHeaders = userHeadersBytes
                });
                break;

            case MsgType.Respond:
                events.Add(new CongaEvent
                {
                    ObjectName = cmdObjectName,
                    Type = EventType.Receive,
                    Payload = frame.Payload,
                    UserHeaders = userHeadersBytes
                });
                UntrackCommand(connectionName, cmdName);
                break;
        }

        return events;
    }

    public OutboundMessage PrepareOutbound(string connectionName, ReadOnlyMemory<byte> payload, byte[]? userHeaders, PostSendAction closeFlag, string? cmdName)
    {
        return new OutboundMessage
        {
            Payload = payload,
            UserHeaders = userHeaders,
            MsgType = MsgType.Data,
            CmdName = cmdName,
            PostAction = closeFlag
        };
    }

    /// <summary>
    /// Prepares a Respond message (final response, closes the command).
    /// </summary>
    public OutboundMessage PrepareRespond(string connectionName, ReadOnlyMemory<byte> payload, string cmdName)
    {
        return new OutboundMessage
        {
            Payload = payload,
            MsgType = MsgType.Respond,
            CmdName = cmdName,
            PostAction = PostSendAction.CloseCommand
        };
    }

    /// <summary>
    /// Prepares a Progress message (interim update).
    /// </summary>
    public OutboundMessage PrepareProgress(string connectionName, ReadOnlyMemory<byte> payload, string cmdName)
    {
        return new OutboundMessage
        {
            Payload = payload,
            MsgType = MsgType.Progress,
            CmdName = cmdName,
            PostAction = PostSendAction.None
        };
    }

    public IReadOnlyList<CongaEvent> OnDisconnected(string connectionName)
    {
        var events = new List<CongaEvent>();

        // Close all active commands for this connection
        if (_activeCommands.TryRemove(connectionName, out var commands))
        {
            foreach (var cmdName in commands.Keys)
            {
                events.Add(new CongaEvent
                {
                    ObjectName = $"{connectionName}.{cmdName}",
                    Type = EventType.Closed
                });
            }
        }

        events.Add(new CongaEvent { ObjectName = connectionName, Type = EventType.Closed });
        return events;
    }

    public void ResetState(string connectionName)
    {
        _activeCommands.TryRemove(connectionName, out _);
    }

    public bool IsCommandActive(string connectionName, string cmdName)
    {
        if (_activeCommands.TryGetValue(connectionName, out var commands))
            return commands.ContainsKey(cmdName);
        return false;
    }

    private void TrackCommand(string connectionName, string cmdName)
    {
        if (string.IsNullOrEmpty(cmdName)) return;
        var commands = _activeCommands.GetOrAdd(connectionName, _ => new ConcurrentDictionary<string, bool>());
        commands.TryAdd(cmdName, true);
    }

    private void UntrackCommand(string connectionName, string cmdName)
    {
        if (string.IsNullOrEmpty(cmdName)) return;
        if (_activeCommands.TryGetValue(connectionName, out var commands))
            commands.TryRemove(cmdName, out _);
    }
}
