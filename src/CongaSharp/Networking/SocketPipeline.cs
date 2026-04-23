namespace CongaSharp.Networking;

using CongaSharp.Diagnostics;
using CongaSharp.Events;
using CongaSharp.Modes;
using CongaSharp.Protocol;

using System.Net.Sockets;

/// <summary>
/// Owns the async read/write loops for a single TCP connection.
/// The mode interprets bytes/frames; the pipeline does I/O.
/// </summary>
public sealed class SocketPipeline : IAsyncDisposable
{
  private readonly Socket _socket;
  private readonly NetworkStream _stream;
  private readonly IConnectionMode _mode;
  private readonly EventQueue _events;

  internal IConnectionMode Mode => _mode;
  private readonly string _connectionName;
  private readonly CancellationTokenSource _cts;
  private readonly int _bufferSize;
  private readonly TraceLogger _trace;
  private readonly SemaphoreSlim _writeLock = new(1, 1);
  private Task? _readLoopTask;
  private int _disposed;

  public SocketPipeline(
      Socket socket,
      IConnectionMode mode,
      EventQueue events,
      string connectionName,
      int bufferSize,
      CancellationToken shutdownToken,
      TraceLogger trace)
  {
    _socket = socket;
    _stream = new NetworkStream(socket, ownsSocket: false);
    _mode = mode;
    _events = events;
    _connectionName = connectionName;
    _bufferSize = bufferSize;
    _trace = trace;
    _cts = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
  }

  /// <summary>
  /// Starts the async read loop as a background task.
  /// </summary>
  public void StartReadLoop()
  {
    _readLoopTask = _mode.UsesFraming
        ? Task.Run(() => FramedReadLoopAsync(), CancellationToken.None)
        : Task.Run(() => UnframedReadLoopAsync(), CancellationToken.None);
  }

  /// <summary>
  /// Sends data on this connection. Thread-safe via write lock.
  /// For unframed modes, writes raw bytes. For framed modes, writes a wire frame.
  /// </summary>
  public async Task SendAsync(OutboundMessage msg)
  {
    await _writeLock.WaitAsync(_cts.Token).ConfigureAwait(false);
    try {
      if (_mode.UsesFraming) {
        var userHeaders = msg.UserHeaders != null && msg.UserHeaders.Length > 0
            ? UserHeaders.Decode(msg.UserHeaders)
            : null;

        await AsyncFrameIO.WriteFrameAsync(
            _stream,
            msg.MsgType,
            msg.CorrelationId,
            msg.Payload,
            userHeaders,
            msg.Compression,
            msg.CompressionLevel,
            0,
            _cts.Token).ConfigureAwait(false);
      } else {
        await _stream.WriteAsync(msg.Payload, _cts.Token).ConfigureAwait(false);
        await _stream.FlushAsync(_cts.Token).ConfigureAwait(false);
      }
    } finally {
      _writeLock.Release();
    }
  }

  public async ValueTask DisposeAsync()
  {
    if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
      return;

    await _cts.CancelAsync().ConfigureAwait(false);

    if (_readLoopTask != null) {
      try { await _readLoopTask.ConfigureAwait(false); } catch (OperationCanceledException) { } catch (Exception) { }
    }

    await _stream.DisposeAsync().ConfigureAwait(false);

    try { _socket.Shutdown(SocketShutdown.Both); } catch { }
    _socket.Dispose();

    _writeLock.Dispose();
    _cts.Dispose();
  }

  private async Task UnframedReadLoopAsync()
  {
    var buffer = new byte[_bufferSize];
    try {
      while (!_cts.IsCancellationRequested) {
        int read = await _stream.ReadAsync(buffer, _cts.Token).ConfigureAwait(false);
        if (read == 0) {
          EmitDisconnectEvents();
          break;
        }

        var events = _mode.OnBytesReceived(_connectionName, buffer.AsSpan(0, read));
        foreach (var evt in events)
          _events.Enqueue(evt);
      }
    } catch (OperationCanceledException) {
      // Graceful shutdown
    } catch (IOException) {
      EmitDisconnectEvents();
    } catch (SocketException) {
      EmitDisconnectEvents();
    } catch (Exception ex) {
      _trace.LogException(ex);
      EmitDisconnectEvents();
    }
  }

  private async Task FramedReadLoopAsync()
  {
    try {
      while (!_cts.IsCancellationRequested) {
        var result = await AsyncFrameIO.ReadFrameAsync(_stream, _cts.Token).ConfigureAwait(false);
        if (!result.Success) {
          if (result.ErrorCode == Errors.ErrorCodes.SocketClosed) {
            EmitDisconnectEvents();
          } else {
            _trace.LogError($"Frame read error on {_connectionName}: {result.ErrorCode}");
            _events.Enqueue(new CongaEvent {
              ObjectName = _connectionName,
              Type = EventType.Error,
              Payload = System.Text.Encoding.UTF8.GetBytes($"{{\"error\":{result.ErrorCode}}}")
            });
          }
          break;
        }

        var frameData = new FrameData {
          MsgType = result.Header.MsgType,
          CmdName = "", // resolved by the mode from its correlation map
          CorrelationId = result.Header.CorrelationId,
          Payload = result.Payload,
          UserHeaders = result.UserHeaders ?? new Dictionary<string, byte[]>()
        };

        var events = _mode.OnFrameReceived(_connectionName, frameData);
        foreach (var evt in events)
          _events.Enqueue(evt);
      }
    } catch (OperationCanceledException) {
      // Graceful shutdown
    } catch (IOException) {
      EmitDisconnectEvents();
    } catch (SocketException) {
      EmitDisconnectEvents();
    } catch (Exception ex) {
      _trace.LogException(ex);
      EmitDisconnectEvents();
    }
  }

  private void EmitDisconnectEvents()
  {
    var events = _mode.OnDisconnected(_connectionName);
    foreach (var evt in events)
      _events.Enqueue(evt);
  }
}
