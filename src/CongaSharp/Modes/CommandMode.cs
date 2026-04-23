namespace CongaSharp.Modes;

using CongaSharp.Events;
using CongaSharp.Protocol;

using System.Collections.Concurrent;

/// <summary>
/// Command mode: framed, supports named commands with Progress and Respond.
/// Each connection can have multiple parallel commands.
/// Uses a bidirectional Guid↔CmdName map for wire correlation.
/// </summary>
public sealed class CommandMode : IConnectionMode
{
  // Tracks active command names per connection
  private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, bool>> _activeCommands = new();

  // Bidirectional correlation maps per connection: wire Guid ↔ local CmdName
  private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, string>> _guidToCmd = new();
  private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, Guid>> _cmdToGuid = new();

  // Per-connection counter for receiver-side auto-generated command names (Cmd00000000, Cmd00000001, ...)
  // Uses "Cmd" prefix to avoid collisions with sender-side "Auto" names from ObjectRegistry.
  private readonly ConcurrentDictionary<string, int> _nextRecvCmd = new();

  public bool UsesFraming => true;

  public IReadOnlyList<CongaEvent> OnBytesReceived(string connectionName, ReadOnlySpan<byte> data)
      => throw new InvalidOperationException("Command mode uses framing, not raw bytes");

  public IReadOnlyList<CongaEvent> OnFrameReceived(string connectionName, FrameData frame)
  {
    var events = new List<CongaEvent>();
    var correlationId = frame.CorrelationId;

    switch (frame.MsgType) {
      case MsgType.Data: {
          // Look up or create a local cmdName for this correlation ID
          var cmdName = ResolveOrCreateCmdName(connectionName, correlationId);
          var cmdObjectName = string.IsNullOrEmpty(cmdName) ? connectionName : $"{connectionName}.{cmdName}";

          TrackCommand(connectionName, cmdName);
          events.Add(new CongaEvent {
            ObjectName = cmdObjectName,
            Type = EventType.Receive,
            Payload = frame.Payload,
            PayloadOwner = frame.TakePayloadOwner(),
            UserHeaders = frame.RawUserHeaders,
            UserHeadersOwner = frame.TakeRawUserHeadersOwner()
          });
          break;
        }

      case MsgType.Progress: {
          var cmdName = LookupCmdName(connectionName, correlationId) ?? "";
          var cmdObjectName = string.IsNullOrEmpty(cmdName) ? connectionName : $"{connectionName}.{cmdName}";

          events.Add(new CongaEvent {
            ObjectName = cmdObjectName,
            Type = EventType.Progress,
            Payload = frame.Payload,
            PayloadOwner = frame.TakePayloadOwner(),
            UserHeaders = frame.RawUserHeaders,
            UserHeadersOwner = frame.TakeRawUserHeadersOwner()
          });
          break;
        }

      case MsgType.Respond: {
          var cmdName = LookupCmdName(connectionName, correlationId) ?? "";
          var cmdObjectName = string.IsNullOrEmpty(cmdName) ? connectionName : $"{connectionName}.{cmdName}";

          events.Add(new CongaEvent {
            ObjectName = cmdObjectName,
            Type = EventType.Receive,
            Payload = frame.Payload,
            PayloadOwner = frame.TakePayloadOwner(),
            UserHeaders = frame.RawUserHeaders,
            UserHeadersOwner = frame.TakeRawUserHeadersOwner(),
            IsTerminal = true
          });
          UntrackCommand(connectionName, cmdName);
          RemoveCorrelation(connectionName, correlationId, cmdName);
          break;
        }
    }

    return events;
  }

  public OutboundMessage PrepareOutbound(string connectionName, ReadOnlyMemory<byte> payload, byte[]? userHeaders, PostSendAction closeFlag, string? cmdName)
  {
    var correlationId = AllocateCorrelation(connectionName, cmdName ?? "");
    return new OutboundMessage {
      Payload = payload,
      UserHeaders = userHeaders,
      MsgType = MsgType.Data,
      CmdName = cmdName,
      CorrelationId = correlationId,
      PostAction = closeFlag
    };
  }

  /// <summary>
  /// Prepares a Respond message (final response, closes the command).
  /// Returns null if the command is not active (already responded or never existed).
  /// </summary>
  public OutboundMessage? TryPrepareRespond(string connectionName, ReadOnlyMemory<byte> payload, string cmdName)
  {
    // Atomic claim: TryRemove from _cmdToGuid so only one caller succeeds
    if (!_cmdToGuid.TryGetValue(connectionName, out var cmdMap) ||
        !cmdMap.TryRemove(cmdName, out var correlationId))
      return null;

    // Clean reverse map
    if (_guidToCmd.TryGetValue(connectionName, out var guidMap))
      guidMap.TryRemove(correlationId, out _);

    UntrackCommand(connectionName, cmdName);

    return new OutboundMessage {
      Payload = payload,
      MsgType = MsgType.Respond,
      CmdName = cmdName,
      CorrelationId = correlationId,
      PostAction = PostSendAction.CloseCommand
    };
  }

  /// <summary>
  /// Prepares a Progress message (interim update).
  /// Returns null if the command is not active.
  /// </summary>
  public OutboundMessage? TryPrepareProgress(string connectionName, ReadOnlyMemory<byte> payload, string cmdName)
  {
    var correlationId = LookupGuid(connectionName, cmdName);
    if (correlationId == Guid.Empty)
      return null;

    return new OutboundMessage {
      Payload = payload,
      MsgType = MsgType.Progress,
      CmdName = cmdName,
      CorrelationId = correlationId,
      PostAction = PostSendAction.None
    };
  }

  public IReadOnlyList<CongaEvent> OnDisconnected(string connectionName)
  {
    var events = new List<CongaEvent>();

    // Close all active commands for this connection
    if (_activeCommands.TryRemove(connectionName, out var commands)) {
      foreach (var cmdName in commands.Keys) {
        events.Add(new CongaEvent {
          ObjectName = $"{connectionName}.{cmdName}",
          Type = EventType.Closed
        });
      }
    }

    // Clear correlation maps and receiver counter
    _guidToCmd.TryRemove(connectionName, out _);
    _cmdToGuid.TryRemove(connectionName, out _);
    _nextRecvCmd.TryRemove(connectionName, out _);

    events.Add(new CongaEvent { ObjectName = connectionName, Type = EventType.Closed });
    return events;
  }

  public void ResetState(string connectionName)
  {
    _activeCommands.TryRemove(connectionName, out _);
    _guidToCmd.TryRemove(connectionName, out _);
    _cmdToGuid.TryRemove(connectionName, out _);
    _nextRecvCmd.TryRemove(connectionName, out _);
  }

  public bool IsCommandActive(string connectionName, string cmdName)
  {
    if (_activeCommands.TryGetValue(connectionName, out var commands))
      return commands.ContainsKey(cmdName);
    return false;
  }

  // --- Correlation helpers ---

  /// <summary>
  /// Allocates a new Guid for an outbound command (client Send or server first contact).
  /// If the cmdName already has a Guid (e.g. repeated Data), returns the existing one.
  /// </summary>
  private Guid AllocateCorrelation(string connectionName, string cmdName)
  {
    if (string.IsNullOrEmpty(cmdName))
      return Guid.NewGuid();

    var cmdMap = _cmdToGuid.GetOrAdd(connectionName, _ => new ConcurrentDictionary<string, Guid>());
    var guidMap = _guidToCmd.GetOrAdd(connectionName, _ => new ConcurrentDictionary<Guid, string>());

    // If already mapped (repeated send for same command), reuse
    if (cmdMap.TryGetValue(cmdName, out var existing))
      return existing;

    var newGuid = Guid.NewGuid();
    cmdMap[cmdName] = newGuid;
    guidMap[newGuid] = cmdName;
    return newGuid;
  }

  /// <summary>
  /// Resolves an incoming CorrelationId to its local cmdName, or creates a new entry
  /// using a "Cmd########" auto-generated name (valid APL variable name — starts with letter).
  /// </summary>
  private string ResolveOrCreateCmdName(string connectionName, Guid correlationId)
  {
    var guidMap = _guidToCmd.GetOrAdd(connectionName, _ => new ConcurrentDictionary<Guid, string>());

    if (guidMap.TryGetValue(correlationId, out var existing))
      return existing;

    // Generate a receiver-side name. Uses "Cmd" prefix (not "Auto") to avoid
    // collisions with sender-side auto-names from ObjectRegistry.GenerateAutoName.
    var n = _nextRecvCmd.AddOrUpdate(connectionName, 0, (_, old) => old + 1);
    var localName = $"Cmd{n:D8}";

    var cmdMap = _cmdToGuid.GetOrAdd(connectionName, _ => new ConcurrentDictionary<string, Guid>());
    guidMap[correlationId] = localName;
    cmdMap[localName] = correlationId;
    return localName;
  }

  private string? LookupCmdName(string connectionName, Guid correlationId)
  {
    if (_guidToCmd.TryGetValue(connectionName, out var guidMap) &&
        guidMap.TryGetValue(correlationId, out var cmdName))
      return cmdName;
    return null;
  }

  private Guid LookupGuid(string connectionName, string cmdName)
  {
    if (_cmdToGuid.TryGetValue(connectionName, out var cmdMap) &&
        cmdMap.TryGetValue(cmdName, out var guid))
      return guid;
    return Guid.Empty;
  }

  private void RemoveCorrelation(string connectionName, Guid correlationId, string cmdName)
  {
    if (_guidToCmd.TryGetValue(connectionName, out var guidMap))
      guidMap.TryRemove(correlationId, out _);
    if (_cmdToGuid.TryGetValue(connectionName, out var cmdMap))
      cmdMap.TryRemove(cmdName, out _);
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
