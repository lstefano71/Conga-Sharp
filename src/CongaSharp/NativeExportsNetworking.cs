using CongaSharp.Core;
using CongaSharp.Errors;
using CongaSharp.Events;
using CongaSharp.Marshalling;
using CongaSharp.Modes;
using CongaSharp.Networking;

using System.Buffers;
using System.Runtime.InteropServices;

namespace CongaSharp;

public static partial class NativeExports
{
  [UnmanagedCallersOnly(EntryPoint = "conga_srv_create")]
  public static unsafe int CongaSrvCreate(
      nint handle,
      char* name,
      char* addr,
      int port,
      char* mode,
      int bufferSize,
      char* outName,
      int outNameCap,
      int* outNameLen)
  {
    try {
      var root = HandleTable.Lookup(handle);
      if (root == null) return ErrorCodes.InvalidHandle;
      if (root.IsShuttingDown) return ErrorCodes.ShuttingDown;

      var modeStr = StringMarshaller.ReadFromPointer(mode);
      if (ModeKindExtensions.TryParse(modeStr) == null)
        return ErrorCodes.InvalidMode;

      var nameStr = StringMarshaller.ReadFromPointer(name);
      var addrStr = StringMarshaller.ReadFromPointer(addr) ?? "";

      if (string.IsNullOrEmpty(nameStr)) {
        nameStr = root.Registry.GenerateServerName();
      } else {
        if (root.Registry.Lookup(nameStr) != null)
          return ErrorCodes.NameInUse;
      }

      var server = new ServerObject(nameStr, addrStr, port, modeStr!, bufferSize);
      if (!root.Registry.TryAdd(server))
        return ErrorCodes.NameInUse;

      return StringMarshaller.WriteToBuffer(nameStr, outName, outNameCap, outNameLen);
    } catch { return -1; }
  }

  [UnmanagedCallersOnly(EntryPoint = "conga_srv_start")]
  public static unsafe int CongaSrvStart(nint handle, char* name)
  {
    try {
      var root = HandleTable.Lookup(handle);
      if (root == null) return ErrorCodes.InvalidHandle;
      if (root.IsShuttingDown) return ErrorCodes.ShuttingDown;

      var nameStr = StringMarshaller.ReadFromPointer(name);
      if (string.IsNullOrEmpty(nameStr)) return ErrorCodes.InvalidName;

      var obj = root.Registry.Lookup(nameStr);
      if (obj == null) return ErrorCodes.InvalidName;
      if (obj is not ServerObject server) return ErrorCodes.NotServer;

      return server.Start(root);
    } catch { return -1; }
  }

  [UnmanagedCallersOnly(EntryPoint = "conga_clt_create")]
  public static unsafe int CongaCltCreate(
      nint handle,
      char* name,
      char* addr,
      int port,
      char* mode,
      int bufferSize,
      char* outName,
      int outNameCap,
      int* outNameLen)
  {
    try {
      var root = HandleTable.Lookup(handle);
      if (root == null) return ErrorCodes.InvalidHandle;
      if (root.IsShuttingDown) return ErrorCodes.ShuttingDown;

      var modeStr = StringMarshaller.ReadFromPointer(mode);
      if (ModeKindExtensions.TryParse(modeStr) == null)
        return ErrorCodes.InvalidMode;

      var nameStr = StringMarshaller.ReadFromPointer(name);
      var addrStr = StringMarshaller.ReadFromPointer(addr) ?? "";

      if (string.IsNullOrEmpty(nameStr)) {
        nameStr = root.Registry.GenerateClientName();
      } else {
        if (root.Registry.Lookup(nameStr) != null)
          return ErrorCodes.NameInUse;
      }

      var client = new ClientObject(nameStr, addrStr, port, modeStr!, bufferSize);
      if (!root.Registry.TryAdd(client))
        return ErrorCodes.NameInUse;

      return StringMarshaller.WriteToBuffer(nameStr, outName, outNameCap, outNameLen);
    } catch { return -1; }
  }

  [UnmanagedCallersOnly(EntryPoint = "conga_clt_connect")]
  public static unsafe int CongaCltConnect(nint handle, char* name, int timeoutMs)
  {
    try {
      var root = HandleTable.Lookup(handle);
      if (root == null) return ErrorCodes.InvalidHandle;
      if (root.IsShuttingDown) return ErrorCodes.ShuttingDown;

      var nameStr = StringMarshaller.ReadFromPointer(name);
      if (string.IsNullOrEmpty(nameStr)) return ErrorCodes.InvalidName;

      var obj = root.Registry.Lookup(nameStr);
      if (obj == null) return ErrorCodes.InvalidName;
      if (obj is not ClientObject client) return ErrorCodes.NotClient;

      return client.ConnectAsync(root, timeoutMs).GetAwaiter().GetResult();
    } catch { return -1; }
  }

  [UnmanagedCallersOnly(EntryPoint = "conga_wait")]
  public static unsafe int CongaWait(
      nint handle,
      char* name,
      int timeoutMs,
      char* outObj,
      int outObjCap,
      char* outEvent,
      int outEventCap,
      int* outEventCode,
      byte* outData,
      int outDataCap,
      int* outDataLen,
      byte* outHeaders,
      int outHeadersCap,
      int* outHeadersLen)
  {
    CongaEvent? evt = null;
    try {
      var root = HandleTable.Lookup(handle);
      if (root == null) return ErrorCodes.InvalidHandle;

      var filter = StringMarshaller.ReadFromPointer(name);

      evt = root.Events.Wait(filter, timeoutMs, root.ShutdownToken);

      // Always return rc=0 (EventMode 1 — everything is an event).
      // The event type and ReasonCode carry the semantic information.

      // Write object name (string buffer — re-enqueue if too small)
      var objRc = StringMarshaller.WriteToBuffer(evt.ObjectName, outObj, outObjCap);
      if (objRc != ErrorCodes.Success) {
        root.Events.ReEnqueue(evt);
        return objRc;
      }

      // Write event name (string buffer — re-enqueue if too small)
      var evtRc = StringMarshaller.WriteToBuffer(evt.EventName, outEvent, outEventCap);
      if (evtRc != ErrorCodes.Success) {
        root.Events.ReEnqueue(evt);
        return evtRc;
      }

      // Write event code
      if (outEventCode != null)
        *outEventCode = evt.EventCode;

      // Write payload (truncate if buffer too small, report actual length)
      WriteBytesToBuffer(evt.Payload, outData, outDataCap, outDataLen);

      // Write user headers (truncate if buffer too small, report actual length)
      WriteBytesToBuffer(evt.UserHeaders, outHeaders, outHeadersCap, outHeadersLen);

      return ErrorCodes.Success;
    } catch { return -1; }
    finally {       // Ensure event is returned to pool on any exception
     // Event fully consumed — return pooled buffers
     evt?.Dispose();
    }
  }

  [UnmanagedCallersOnly(EntryPoint = "conga_send")]
  public static unsafe int CongaSend(
      nint handle,
      char* name,
      byte* data,
      int dataLen,
      byte* headers,
      int headersLen,
      int closeFlag,
      int compression,
      int compressionLevel,
      int track,
      char* outName,
      int outNameCap,
      int* outNameLen)
  {
    try {
      var root = HandleTable.Lookup(handle);
      if (root == null) return ErrorCodes.InvalidHandle;
      if (root.IsShuttingDown) return ErrorCodes.ShuttingDown;

      var nameStr = StringMarshaller.ReadFromPointer(name);
      if (string.IsNullOrEmpty(nameStr)) return ErrorCodes.InvalidName;

      // Resolve pipeline, connection name, optional command name, and role
      var obj = root.Registry.Lookup(nameStr);
      SocketPipeline? pipeline;
      string connName;
      string? cmdName;
      bool isServerSide;

      if (obj is ClientObject client) {
        pipeline = client.Pipeline;
        connName = nameStr;
        cmdName = null;
        isServerSide = false;
      } else if (obj is ConnectionObject conn) {
        pipeline = conn.Pipeline;
        connName = nameStr;
        cmdName = null;
        isServerSide = true;
      } else {
        // Dotted name (e.g. "C1.GetInfo") or unknown — split and resolve parent
        var lastDot = nameStr.LastIndexOf('.');
        if (lastDot <= 0) return ErrorCodes.InvalidName;

        connName = nameStr[..lastDot];
        cmdName = nameStr[(lastDot + 1)..];

        var parentObj = root.Registry.Lookup(connName);
        if (parentObj is ConnectionObject parentConn) {
          pipeline = parentConn.Pipeline;
          isServerSide = true;
        } else if (parentObj is ClientObject parentClient) {
          pipeline = parentClient.Pipeline;
          isServerSide = false;
        } else {
          return ErrorCodes.InvalidName;
        }
      }

      if (pipeline == null) return ErrorCodes.ObjectNotReady;

      // Command-mode server cannot Send (must use Respond/Progress)
      bool isCommandMode = pipeline.Mode is CommandMode;
      if (isCommandMode && isServerSide)
        return ErrorCodes.InvalidMode;

      // Determine resolved handle name per DRC.Send semantics (A.26):
      // - base object name → auto-generate suffix
      // - explicit dotted name → return as-is
      string resolvedHandle;
      if (cmdName == null) {
        resolvedHandle = root.Registry.GenerateAutoName(connName);
        // For Command mode, extract suffix as the CmdName for the wire frame
        if (isCommandMode)
          cmdName = resolvedHandle[(connName.Length + 1)..];
      } else {
        resolvedHandle = nameStr;
      }

      // Pre-check output buffer capacity before sending (send is not undoable)
      if (outName != null && outNameCap < resolvedHandle.Length + 1) {
        if (outNameLen != null)
          *outNameLen = resolvedHandle.Length + 1;
        return ErrorCodes.BufferTooSmall;
      }

      // Guard: reject duplicate pending command names (D4).
      // If an explicit command name was provided and it's already registered
      // (as a CommandObject or as an active mailbox), return 1008.
      if (isCommandMode && cmdName != null) {
        var existingCmd = root.Registry.Lookup(resolvedHandle);
        if (existingCmd != null || root.Events.GetMailbox(resolvedHandle) != null)
          return ErrorCodes.CommandNameInUse;
      }

      // Pre-register mailbox for tracked Command mode sends.
      // This MUST happen before bytes hit the wire to prevent the
      // "fast server / slow client" race condition.
      bool tracked = track != 0 && isCommandMode;
      if (tracked)
        root.Events.RegisterMailbox(resolvedHandle);

      byte[]? rentedPayload = null;
      try {
        ReadOnlyMemory<byte> payload;
        if (dataLen > 0 && data != null) {
          rentedPayload = ArrayPool<byte>.Shared.Rent(dataLen);
          new ReadOnlySpan<byte>(data, dataLen).CopyTo(rentedPayload);
          payload = new ReadOnlyMemory<byte>(rentedPayload, 0, dataLen);
        } else {
          payload = ReadOnlyMemory<byte>.Empty;
        }

        byte[]? userHeaders = headersLen > 0 && headers != null
            ? new ReadOnlySpan<byte>(headers, headersLen).ToArray()
            : null;

        var postAction = (PostSendAction)closeFlag;
        var msg = pipeline.Mode.PrepareOutbound(connName, payload, userHeaders, postAction, cmdName);
        if (msg.ErrorCode != 0) {
          if (tracked) root.Events.UnregisterMailbox(resolvedHandle);
          return msg.ErrorCode;
        }

        msg.Compression = (Protocol.CompressionAlgorithm)compression;
        msg.CompressionLevel = compressionLevel;

        pipeline.SendSync(msg);

        HandlePostAction(root, resolvedHandle, connName, msg.PostAction);
      } catch {
        // Rollback mailbox on send failure
        if (tracked) root.Events.UnregisterMailbox(resolvedHandle);
        throw;
      } finally {
        if (rentedPayload != null)
          ArrayPool<byte>.Shared.Return(rentedPayload);
      }

      // Write resolved handle to output buffer after successful send
      if (outNameLen != null)
        *outNameLen = resolvedHandle.Length + 1;

      if (outName != null)
        StringMarshaller.WriteToBuffer(resolvedHandle, outName, outNameCap);

      return ErrorCodes.Success;
    } catch { return -1; }
  }

  [UnmanagedCallersOnly(EntryPoint = "conga_respond")]
  public static unsafe int CongaRespond(
      nint handle,
      char* name,
      byte* data,
      int dataLen,
      int compression,
      int compressionLevel)
  {
    try {
      var root = HandleTable.Lookup(handle);
      if (root == null) return ErrorCodes.InvalidHandle;
      if (root.IsShuttingDown) return ErrorCodes.ShuttingDown;

      var nameStr = StringMarshaller.ReadFromPointer(name);
      if (string.IsNullOrEmpty(nameStr)) return ErrorCodes.InvalidName;

      var (pipeline, connName, cmdName, resolveError) = ResolvePipelineForCommand(root, nameStr);
      if (resolveError != ErrorCodes.Success) return resolveError;
      if (pipeline == null) return ErrorCodes.ObjectNotReady;
      if (cmdName == null) return ErrorCodes.InvalidName;

      if (pipeline.Mode is not CommandMode commandMode)
        return ErrorCodes.InvalidMode;

      byte[]? rentedPayload = null;
      try {
        ReadOnlyMemory<byte> payload;
        if (dataLen > 0 && data != null) {
          rentedPayload = ArrayPool<byte>.Shared.Rent(dataLen);
          new ReadOnlySpan<byte>(data, dataLen).CopyTo(rentedPayload);
          payload = new ReadOnlyMemory<byte>(rentedPayload, 0, dataLen);
        } else {
          payload = ReadOnlyMemory<byte>.Empty;
        }

        var msg = commandMode.TryPrepareRespond(connName, payload, cmdName);
        if (msg == null) return ErrorCodes.InvalidName;
        msg.Compression = (Protocol.CompressionAlgorithm)compression;
        msg.CompressionLevel = compressionLevel;
        pipeline.SendSync(msg);
      } finally {
        if (rentedPayload != null)
          ArrayPool<byte>.Shared.Return(rentedPayload);
      }

      // Close command object if registered
      root.Registry.TryRemove(nameStr, out _);

      return ErrorCodes.Success;
    } catch { return -1; }
  }

  [UnmanagedCallersOnly(EntryPoint = "conga_progress")]
  public static unsafe int CongaProgress(
      nint handle,
      char* name,
      byte* data,
      int dataLen,
      int compression,
      int compressionLevel)
  {
    try {
      var root = HandleTable.Lookup(handle);
      if (root == null) return ErrorCodes.InvalidHandle;
      if (root.IsShuttingDown) return ErrorCodes.ShuttingDown;

      var nameStr = StringMarshaller.ReadFromPointer(name);
      if (string.IsNullOrEmpty(nameStr)) return ErrorCodes.InvalidName;

      var (pipeline, connName, cmdName, resolveError) = ResolvePipelineForCommand(root, nameStr);
      if (resolveError != ErrorCodes.Success) return resolveError;
      if (pipeline == null) return ErrorCodes.ObjectNotReady;
      if (cmdName == null) return ErrorCodes.InvalidName;

      if (pipeline.Mode is not CommandMode commandMode)
        return ErrorCodes.InvalidMode;

      byte[]? rentedPayload = null;
      try {
        ReadOnlyMemory<byte> payload;
        if (dataLen > 0 && data != null) {
          rentedPayload = ArrayPool<byte>.Shared.Rent(dataLen);
          new ReadOnlySpan<byte>(data, dataLen).CopyTo(rentedPayload);
          payload = new ReadOnlyMemory<byte>(rentedPayload, 0, dataLen);
        } else {
          payload = ReadOnlyMemory<byte>.Empty;
        }

        var msg = commandMode.TryPrepareProgress(connName, payload, cmdName);
        if (msg == null) return ErrorCodes.InvalidName;
        msg.Compression = (Protocol.CompressionAlgorithm)compression;
        msg.CompressionLevel = compressionLevel;
        pipeline.SendSync(msg);
      } finally {
        if (rentedPayload != null)
          ArrayPool<byte>.Shared.Return(rentedPayload);
      }

      return ErrorCodes.Success;
    } catch { return -1; }
  }

  /// <summary>
  /// Writes byte data to an output buffer, truncating if capacity is insufficient.
  /// Always reports the actual data length via outLen.
  /// </summary>
  private static unsafe void WriteBytesToBuffer(ReadOnlyMemory<byte> data, byte* buffer, int bufferCap, int* outLen)
  {
    if (outLen != null)
      *outLen = data.Length;

    if (buffer != null && bufferCap > 0 && data.Length > 0) {
      var copyLen = Math.Min(data.Length, bufferCap);
      data.Span[..copyLen].CopyTo(new Span<byte>(buffer, copyLen));
    }
  }

  /// <summary>
  /// Resolves a name to its pipeline, connection name, and optional command name.
  /// Works for ConnectionObject, ClientObject, and command names.
  /// </summary>
  private static (SocketPipeline? pipeline, string connName, string? cmdName, int errorCode)
      ResolvePipeline(Root root, string name)
  {
    var obj = root.Registry.Lookup(name);

    if (obj is ConnectionObject conn)
      return (conn.Pipeline, name, null,
          conn.Pipeline != null ? ErrorCodes.Success : ErrorCodes.ObjectNotReady);

    if (obj is ClientObject client)
      return (client.Pipeline, name, null,
          client.Pipeline != null ? ErrorCodes.Success : ErrorCodes.ObjectNotReady);

    // CommandObject or unregistered command name — try command resolution
    if (obj is CommandObject || obj == null)
      return ResolvePipelineForCommand(root, name);

    // ServerObject or other unsupported type
    return (null, name, null, ErrorCodes.InvalidName);
  }

  /// <summary>
  /// Parses a dotted command name ("CONN.CmdName") and resolves to the parent
  /// connection's pipeline.
  /// </summary>
  internal static (SocketPipeline? pipeline, string connName, string? cmdName, int errorCode)
      ResolvePipelineForCommand(Root root, string name)
  {
    var lastDot = name.LastIndexOf('.');
    if (lastDot <= 0)
      return (null, name, null, ErrorCodes.InvalidName);

    var connName = name[..lastDot];
    var cmdName = name[(lastDot + 1)..];

    var connObj = root.Registry.Lookup(connName);
    if (connObj is ConnectionObject parentConn)
      return (parentConn.Pipeline, connName, cmdName,
          parentConn.Pipeline != null ? ErrorCodes.Success : ErrorCodes.ObjectNotReady);
    if (connObj is ClientObject parentClient)
      return (parentClient.Pipeline, connName, cmdName,
          parentClient.Pipeline != null ? ErrorCodes.Success : ErrorCodes.ObjectNotReady);

    return (null, connName, cmdName, ErrorCodes.InvalidName);
  }

  /// <summary>
  /// Executes post-send actions: close connection, close command, or emit Sent event.
  /// </summary>
  internal static void HandlePostAction(Root root, string objectName, string connName, PostSendAction action)
  {
    switch (action) {
      case PostSendAction.CloseConnection:
        DisposeAndRemoveConnection(root, connName);
        break;
      case PostSendAction.CloseCommand:
        root.Registry.TryRemove(objectName, out _);
        break;
      case PostSendAction.EmitSentEvent:
        root.Events.Enqueue(new CongaEvent {
          ObjectName = objectName,
          Type = EventType.Sent
        });
        break;
    }
  }

  /// <summary>
  /// Disposes a connection's pipeline and removes it (and children) from the registry.
  /// </summary>
  internal static void DisposeAndRemoveConnection(Root root, string connName)
  {
    var obj = root.Registry.Lookup(connName);
    if (obj is ConnectionObject conn && conn.Pipeline != null) {
      try { conn.Pipeline.DisposeAsync().GetAwaiter().GetResult(); } catch { }
      conn.State = ObjectState.Closed;
    } else if (obj is ClientObject client) {
      try { client.DisposeAsync().GetAwaiter().GetResult(); } catch { }
    }

    var removed = root.Registry.RemoveTree(connName);
    foreach (var r in removed)
      r.Parent?.TryRemoveChild(r.Name);
  }
}
