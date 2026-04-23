namespace CongaSharp.Core;

using CongaSharp.Errors;
using CongaSharp.Modes;
using CongaSharp.Networking;

using System.Net;
using System.Net.Sockets;

public sealed class ClientObject : CongaObject, IAsyncDisposable
{
  public string Address { get; }
  public int Port { get; }
  public string Mode { get; }
  public int BufferSize { get; }
  public ModeKind ModeKind { get; }

  private SocketPipeline? _pipeline;
  private Root? _root;
  private int _disposed;

  public SocketPipeline? Pipeline => _pipeline;

  public ClientObject(string name, string address, int port, string mode, int bufferSize, CongaObject? parent = null)
      : base(name, ObjectType.Client, parent)
  {
    Address = address;
    Port = port;
    Mode = mode;
    BufferSize = bufferSize;
    ModeKind = ModeKindExtensions.TryParse(mode) ?? ModeKind.Raw;
  }

  /// <summary>
  /// Resolves DNS, connects with timeout, creates pipeline, transitions to Started.
  /// Returns 0 on success, error code on failure.
  /// </summary>
  public async Task<int> ConnectAsync(Root root, int timeoutMs)
  {
    if (!TryTransition(ObjectState.Created, ObjectState.Started))
      return ErrorCodes.ObjectAlreadyStarted;

    _root = root;

    Socket? socket = null;
    try {
      var protocol = "IPv4";
      Properties.Get("Protocol", out var protocolJson);
      if (protocolJson != null)
        protocol = protocolJson.Trim('"');

      bool preferIPv6 = protocol.Equals("IPv6", StringComparison.OrdinalIgnoreCase);
      var ipAddress = await DnsResolver.ResolveAsync(Address, preferIPv6).ConfigureAwait(false);

      socket = new Socket(ipAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

      // Apply TCPNoDelay (Nagle) setting — default is enabled for low latency
      bool noDelay = true;
      if (Properties.Get("TCPNoDelay", out var noDelayJson) == ErrorCodes.Success)
        noDelay = noDelayJson != "0";
      socket.NoDelay = noDelay;

      using var timeoutCts = new CancellationTokenSource(timeoutMs);
      using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
          timeoutCts.Token, root.ShutdownToken);

      try {
        await socket.ConnectAsync(new IPEndPoint(ipAddress, Port), linkedCts.Token).ConfigureAwait(false);
      } catch (OperationCanceledException) {
        if (root.ShutdownToken.IsCancellationRequested)
          return ErrorCodes.ShuttingDown;
        return ErrorCodes.Timeout;
      } catch (SocketException) {
        return ErrorCodes.ConnectFailed;
      }

      // Set read-only properties
      var localEp = socket.LocalEndPoint as IPEndPoint;
      var remoteEp = socket.RemoteEndPoint as IPEndPoint;
      if (localEp != null)
        Properties.SetInternal("LocalAddr", $"[\"{localEp.Address}\",{localEp.Port}]");
      if (remoteEp != null)
        Properties.SetInternal("PeerAddr", $"[\"{remoteEp.Address}\",{remoteEp.Port}]");

      // Create mode instance
      var mode = ModeFactory.Create(ModeKind);
      ConfigureMode(mode);

      // Create and start pipeline
      _pipeline = new SocketPipeline(
          socket, mode, root.Events, Name,
          BufferSize, root.ShutdownToken, root.Trace);
      socket = null; // Ownership transferred to pipeline

      _pipeline.StartReadLoop();

      root.Trace.LogConnection($"Client {Name} connected to {remoteEp}");

      return ErrorCodes.Success;
    } catch (SocketException ex) {
      State = ObjectState.Created;
      root.Trace.LogException(ex);
      return ErrorCodes.ConnectFailed;
    } catch (Exception ex) {
      State = ObjectState.Created;
      root.Trace.LogException(ex);
      return ErrorCodes.ConnectFailed;
    } finally {
      socket?.Dispose();
    }
  }

  public async ValueTask DisposeAsync()
  {
    if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
      return;

    State = ObjectState.Closed;

    if (_pipeline != null) {
      await _pipeline.DisposeAsync().ConfigureAwait(false);
      _pipeline = null;
    }
  }

  /// <summary>
  /// Configures a mode instance with properties from this client (e.g., EOM for TextMode).
  /// </summary>
  private void ConfigureMode(IConnectionMode mode)
  {
    if (mode is TextMode textMode) {
      if (Properties.Get("EOM", out var eomJson) == ErrorCodes.Success && eomJson != "[]")
        textMode.ConfigureEomFromJson(eomJson);
    }
  }
}
