namespace CongaSharp.Core;

using CongaSharp.Errors;
using CongaSharp.Events;
using CongaSharp.Modes;
using CongaSharp.Networking;

using System.Net;
using System.Net.Sockets;

public sealed class ServerObject : CongaObject, IAsyncDisposable
{
  public string Address { get; }
  public int Port { get; }
  public string Mode { get; }
  public int BufferSize { get; }
  public ModeKind ModeKind { get; }

  /// <summary>
  /// Actual port after bind (may differ from Port when ephemeral port 0 is used).
  /// </summary>
  public int LocalPort { get; private set; }

  private Socket? _listener;
  private Task? _acceptLoopTask;
  private Root? _root;
  private int _disposed;

  public ServerObject(string name, string address, int port, string mode, int bufferSize, CongaObject? parent = null)
      : base(name, ObjectType.Server, parent)
  {
    Address = address;
    Port = port;
    Mode = mode;
    BufferSize = bufferSize;
    ModeKind = ModeKindExtensions.TryParse(mode) ?? ModeKind.Raw;
  }

  /// <summary>
  /// Binds the listener socket, starts listening, and begins the accept loop.
  /// Three-phase lifecycle: Created → Start() → Started.
  /// </summary>
  public int Start(Root root)
  {
    if (!TryTransition(ObjectState.Created, ObjectState.Started))
      return ErrorCodes.ObjectAlreadyStarted;

    _root = root;

    try {
      var protocol = "IPv4";
      Properties.Get("Protocol", out var protocolJson);
      if (protocolJson != null)
        protocol = protocolJson.Trim('"');

      var addressFamily = protocol.Equals("IPv6", StringComparison.OrdinalIgnoreCase)
          ? AddressFamily.InterNetworkV6
          : AddressFamily.InterNetwork;

      var bindAddress = string.IsNullOrEmpty(Address) || Address == "0.0.0.0"
          ? (addressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any)
          : IPAddress.Parse(Address);

      _listener = new Socket(addressFamily, SocketType.Stream, ProtocolType.Tcp);
      _listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
      _listener.Bind(new IPEndPoint(bindAddress, Port));
      _listener.Listen(128);

      var localEp = (IPEndPoint)_listener.LocalEndPoint!;
      LocalPort = localEp.Port;

      // Set read-only properties
      Properties.SetInternal("LocalAddr", $"[\"{localEp.Address}\",{localEp.Port}]");
      Properties.SetInternal("LocalPort", localEp.Port.ToString());

      root.Trace.LogConnection($"Server {Name} listening on {localEp}");

      // Start accept loop
      _acceptLoopTask = Task.Run(() => AcceptLoopAsync(), CancellationToken.None);

      return ErrorCodes.Success;
    } catch (SocketException ex) {
      _listener?.Dispose();
      _listener = null;
      State = ObjectState.Created;
      root.Trace.LogException(ex);
      return ErrorCodes.BindFailed;
    } catch (Exception ex) {
      _listener?.Dispose();
      _listener = null;
      State = ObjectState.Created;
      root.Trace.LogException(ex);
      return ErrorCodes.BindFailed;
    }
  }

  public async ValueTask DisposeAsync()
  {
    if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
      return;

    State = ObjectState.Closed;

    // Close the listener to unblock AcceptAsync
    try { _listener?.Close(); } catch { }

    if (_acceptLoopTask != null) {
      try { await _acceptLoopTask.ConfigureAwait(false); } catch { }
    }

    _listener?.Dispose();
  }

  private async Task AcceptLoopAsync()
  {
    var root = _root!;
    try {
      while (!root.ShutdownToken.IsCancellationRequested && State == ObjectState.Started) {
        Socket clientSocket;
        try {
          clientSocket = await _listener!.AcceptAsync(root.ShutdownToken).ConfigureAwait(false);
        } catch (OperationCanceledException) {
          break;
        } catch (ObjectDisposedException) {
          break;
        } catch (SocketException) {
          break;
        }

        try {
          // Apply TCPNoDelay (Nagle) setting from server properties
          bool noDelay = true;
          if (Properties.Get("TCPNoDelay", out var noDelayJson) == ErrorCodes.Success)
            noDelay = noDelayJson != "0";
          clientSocket.NoDelay = noDelay;

          // Create connection object
          var connName = root.Registry.GenerateConnectionName(Name);
          var conn = new ConnectionObject(connName, this);
          root.Registry.TryAdd(conn);
          TryAddChild(conn);

          // Set connection properties from peer info
          var remoteEp = clientSocket.RemoteEndPoint as IPEndPoint;
          var localEp = clientSocket.LocalEndPoint as IPEndPoint;
          if (remoteEp != null)
            conn.Properties.SetInternal("PeerAddr", $"[\"{remoteEp.Address}\",{remoteEp.Port}]");
          if (localEp != null)
            conn.Properties.SetInternal("LocalAddr", $"[\"{localEp.Address}\",{localEp.Port}]");

          // Create mode instance for this connection
          var mode = ModeFactory.Create(ModeKind);
          ConfigureMode(mode);

          // Create and start pipeline
          var pipeline = new SocketPipeline(
              clientSocket, mode, root.Events, connName,
              BufferSize, root.ShutdownToken, root.Trace);
          conn.Pipeline = pipeline;
          conn.State = ObjectState.Started;
          pipeline.StartReadLoop();

          // Enqueue Connect event
          root.Events.Enqueue(new CongaEvent {
            ObjectName = connName,
            Type = EventType.Connect
          });

          root.Trace.LogConnection($"Accepted connection {connName} from {remoteEp}");
        } catch (Exception ex) {
          // Dispose the accepted socket if anything fails during setup
          try { clientSocket.Dispose(); } catch { }
          root.Trace.LogException(ex);
          continue;
        }
      }
    } catch (OperationCanceledException) {
      // Normal shutdown
    } catch (Exception ex) {
      root.Trace.LogException(ex);
    }
  }

  /// <summary>
  /// Configures a mode instance with properties from this server (e.g., EOM for TextMode).
  /// </summary>
  private void ConfigureMode(IConnectionMode mode)
  {
    if (mode is TextMode textMode) {
      if (Properties.Get("EOM", out var eomJson) == ErrorCodes.Success && eomJson != "[]")
        textMode.ConfigureEomFromJson(eomJson);
    }
  }
}
