using CongaSharp.Core;
using CongaSharp.Errors;
using CongaSharp.Events;
using CongaSharp.Modes;
using CongaSharp.Networking;
using CongaSharp.Properties;

using Dyalog.DWA;
using Dyalog.DWA.Native;

using System.Buffers;

namespace CongaSharp;

/// <summary>
/// DWA Kit exports for Conga-Sharp.
/// These use [DwaExport] so the source generator creates native entry points
/// that Dyalog APL can call via ⎕NA with PP (LOCALP) parameters.
/// The class must be partial for the source generator.
/// </summary>
public static partial class DwaExports
{
  private const string Version = "0.1.0";

  // ════════════════════════════════════════════════════════════════════
  // Lifecycle
  // ════════════════════════════════════════════════════════════════════

  /// <summary>
  /// Initialise a new Conga-Sharp root. Returns an opaque handle (nint).
  /// ⎕NA 'P dll|conga_pp_init'
  /// </summary>
  [DwaExport("conga_pp_init")]
  public static nint CongaInit()
  {
    var root = new Root();
    var handle = HandleTable.Allocate(root);
    root.Handle = handle;
    return handle;
  }

  /// <summary>
  /// Shut down a Conga-Sharp root and release all resources.
  /// ⎕NA 'I4 dll|conga_pp_shutdown P'
  /// </summary>
  [DwaExport("conga_pp_shutdown")]
  public static int CongaShutdown(nint handle)
  {
    var root = HandleTable.Lookup(handle);
    if (root == null) return ErrorCodes.InvalidHandle;
    root.Shutdown();
    HandleTable.Free(handle);
    root.Dispose();
    return ErrorCodes.Success;
  }

  /// <summary>
  /// Get version string.
  /// ⎕NA 'dll|conga_pp_version >PP'
  /// </summary>
  [DwaExport("conga_pp_version")]
  public static void CongaVersion(Localp rslt)
  {
    rslt.SetString(Version);
  }

  // ════════════════════════════════════════════════════════════════════
  // Server lifecycle: create → setprop → start
  // ════════════════════════════════════════════════════════════════════

  /// <summary>
  /// Create a server object. Returns (rc name) as a 2-element nested vector.
  /// ⎕NA 'dll|conga_pp_srv_create P <PP <PP I4 <PP I4 >PP'
  /// </summary>
  [DwaExport("conga_pp_srv_create")]
  public static void CongaSrvCreate(
      nint handle, Localp nameArg, Localp addrArg,
      int port, Localp modeArg, int bufferSize,
      Localp rslt)
  {
    var root = HandleTable.Lookup(handle);
    int rc;
    string resultName = "";

    if (root == null) { rc = ErrorCodes.InvalidHandle; }
    else if (root.IsShuttingDown) { rc = ErrorCodes.ShuttingDown; }
    else {
      var modeStr = ReadLocalpString(modeArg);
      if (ModeKindExtensions.TryParse(modeStr) == null) {
        rc = ErrorCodes.InvalidMode;
      } else {
        var nameStr = ReadLocalpString(nameArg);
        var addrStr = ReadLocalpString(addrArg) ?? "";

        if (string.IsNullOrEmpty(nameStr)) {
          nameStr = root.Registry.GenerateServerName();
        } else if (root.Registry.Lookup(nameStr) != null) {
          rc = ErrorCodes.NameInUse;
          WriteRcName(rslt, rc, resultName);
          return;
        }

        var server = new ServerObject(nameStr, addrStr, port, modeStr!, bufferSize);
        if (!root.Registry.TryAdd(server)) {
          rc = ErrorCodes.NameInUse;
        } else {
          rc = ErrorCodes.Success;
          resultName = nameStr;
        }
      }
    }

    WriteRcName(rslt, rc, resultName);
  }

  /// <summary>
  /// Start a server (begin accepting connections).
  /// ⎕NA 'I4 dll|conga_pp_srv_start P <PP'
  /// </summary>
  [DwaExport("conga_pp_srv_start")]
  public static int CongaSrvStart(nint handle, Localp nameArg)
  {
    var root = HandleTable.Lookup(handle);
    if (root == null) return ErrorCodes.InvalidHandle;
    if (root.IsShuttingDown) return ErrorCodes.ShuttingDown;

    var nameStr = ReadLocalpString(nameArg);
    if (string.IsNullOrEmpty(nameStr)) return ErrorCodes.InvalidName;

    var obj = root.Registry.Lookup(nameStr);
    if (obj == null) return ErrorCodes.InvalidName;
    if (obj is not ServerObject server) return ErrorCodes.NotServer;

    return server.Start(root);
  }

  // ════════════════════════════════════════════════════════════════════
  // Client lifecycle: create → setprop → connect
  // ════════════════════════════════════════════════════════════════════

  /// <summary>
  /// Create a client object. Returns (rc name) as a 2-element nested vector.
  /// ⎕NA 'dll|conga_pp_clt_create P <PP <PP I4 <PP I4 >PP'
  /// </summary>
  [DwaExport("conga_pp_clt_create")]
  public static void CongaCltCreate(
      nint handle, Localp nameArg, Localp addrArg,
      int port, Localp modeArg, int bufferSize,
      Localp rslt)
  {
    var root = HandleTable.Lookup(handle);
    int rc;
    string resultName = "";

    if (root == null) { rc = ErrorCodes.InvalidHandle; }
    else if (root.IsShuttingDown) { rc = ErrorCodes.ShuttingDown; }
    else {
      var modeStr = ReadLocalpString(modeArg);
      if (ModeKindExtensions.TryParse(modeStr) == null) {
        rc = ErrorCodes.InvalidMode;
      } else {
        var nameStr = ReadLocalpString(nameArg);
        var addrStr = ReadLocalpString(addrArg) ?? "";

        if (string.IsNullOrEmpty(nameStr)) {
          nameStr = root.Registry.GenerateClientName();
        } else if (root.Registry.Lookup(nameStr) != null) {
          rc = ErrorCodes.NameInUse;
          WriteRcName(rslt, rc, resultName);
          return;
        }

        var client = new ClientObject(nameStr, addrStr, port, modeStr!, bufferSize);
        if (!root.Registry.TryAdd(client)) {
          rc = ErrorCodes.NameInUse;
        } else {
          rc = ErrorCodes.Success;
          resultName = nameStr;
        }
      }
    }

    WriteRcName(rslt, rc, resultName);
  }

  /// <summary>
  /// Connect a client to its server.
  /// ⎕NA 'I4 dll|conga_pp_clt_connect P <PP I4'
  /// </summary>
  [DwaExport("conga_pp_clt_connect")]
  public static int CongaCltConnect(nint handle, Localp nameArg, int timeoutMs)
  {
    var root = HandleTable.Lookup(handle);
    if (root == null) return ErrorCodes.InvalidHandle;
    if (root.IsShuttingDown) return ErrorCodes.ShuttingDown;

    var nameStr = ReadLocalpString(nameArg);
    if (string.IsNullOrEmpty(nameStr)) return ErrorCodes.InvalidName;

    var obj = root.Registry.Lookup(nameStr);
    if (obj == null) return ErrorCodes.InvalidName;
    if (obj is not ClientObject client) return ErrorCodes.NotClient;

    return client.ConnectAsync(root, timeoutMs).GetAwaiter().GetResult();
  }

  // ════════════════════════════════════════════════════════════════════
  // Wait — returns 4-element nested vector (rc objName eventType data)
  // ════════════════════════════════════════════════════════════════════

  /// <summary>
  /// Wait for the next event. Returns (rc objName eventType data) as a
  /// 4-element nested APL vector directly into the workspace.
  /// ⎕NA 'dll|conga_pp_wait P <PP I4 >PP'
  /// </summary>
  [DwaExport("conga_pp_wait")]
  public static void CongaWait(nint handle, Localp filterArg, int timeoutMs, Localp rslt)
  {
    var root = HandleTable.Lookup(handle);
    if (root == null) {
      WriteEventTuple(rslt, ErrorCodes.InvalidHandle, "", "Error", ReadOnlyMemory<byte>.Empty);
      return;
    }

    var filter = ReadLocalpString(filterArg);
    CongaEvent? evt = null;
    try {
      evt = root.Events.Wait(filter, timeoutMs, root.ShutdownToken);

      // Always rc=0 (EventMode 1). The event type and ReasonCode carry semantics.
      WriteEventTuple(rslt, ErrorCodes.Success,
          evt.ObjectName, evt.EventName,
          evt.Payload,
          evt.EventCode);
    } catch {
      WriteEventTuple(rslt, -1, "", "Error", ReadOnlyMemory<byte>.Empty);
    } finally {
      evt?.Dispose();
    }
  }

  // ════════════════════════════════════════════════════════════════════
  // Send / Respond / Progress
  // ════════════════════════════════════════════════════════════════════

  /// <summary>
  /// Send data on a connection. Returns (rc resolvedHandle) as 2-element nested vector.
  /// Data is a type-83 byte vector (from 220⌶).
  /// ⎕NA 'dll|conga_pp_send P <PP <PP I4 I4 I4 >PP'
  /// </summary>
  [DwaExport("conga_pp_send")]
  public static void CongaSend(
      nint handle, Localp nameArg, Localp dataArg,
      int closeFlag, int compression, int track,
      Localp rslt)
  {
    var root = HandleTable.Lookup(handle);
    if (root == null) { WriteRcName(rslt, ErrorCodes.InvalidHandle, ""); return; }
    if (root.IsShuttingDown) { WriteRcName(rslt, ErrorCodes.ShuttingDown, ""); return; }

    var nameStr = ReadLocalpString(nameArg);
    if (string.IsNullOrEmpty(nameStr)) { WriteRcName(rslt, ErrorCodes.InvalidName, ""); return; }

    // Read data bytes from the Localp (type 83 byte vector)
    ReadOnlyMemory<byte> payload;
    byte[]? payloadArray = null;
    int dataLen = dataArg.Bound();
    if (dataLen > 0) {
      payloadArray = dataArg.ReadSpan<byte>().ToArray();
      payload = payloadArray;
    } else {
      payload = ReadOnlyMemory<byte>.Empty;
    }

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
      var lastDot = nameStr.LastIndexOf('.');
      if (lastDot <= 0) { WriteRcName(rslt, ErrorCodes.InvalidName, ""); return; }

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
        WriteRcName(rslt, ErrorCodes.InvalidName, "");
        return;
      }
    }

    if (pipeline == null) { WriteRcName(rslt, ErrorCodes.ObjectNotReady, ""); return; }

    bool isCommandMode = pipeline.Mode is CommandMode;
    if (isCommandMode && isServerSide) { WriteRcName(rslt, ErrorCodes.InvalidMode, ""); return; }

    // Determine resolved handle name
    string resolvedHandle;
    if (cmdName == null) {
      resolvedHandle = root.Registry.GenerateAutoName(connName);
      if (isCommandMode)
        cmdName = resolvedHandle[(connName.Length + 1)..];
    } else {
      resolvedHandle = nameStr;
    }

    // Guard: reject duplicate pending command names (D4)
    if (isCommandMode && cmdName != null) {
      var existingCmd = root.Registry.Lookup(resolvedHandle);
      if (existingCmd != null || root.Events.GetMailbox(resolvedHandle) != null) {
        WriteRcName(rslt, ErrorCodes.CommandNameInUse, "");
        return;
      }
    }

    // Pre-register mailbox for tracked Command mode sends
    bool tracked = track != 0 && isCommandMode;
    if (tracked)
      root.Events.RegisterMailbox(resolvedHandle);

    try {
      var postAction = (PostSendAction)closeFlag;
      var msg = pipeline.Mode.PrepareOutbound(connName, payload, null, postAction, cmdName);
      if (msg.ErrorCode != 0) {
        if (tracked) root.Events.UnregisterMailbox(resolvedHandle);
        WriteRcName(rslt, msg.ErrorCode, "");
        return;
      }

      msg.Compression = (Protocol.CompressionAlgorithm)compression;
      pipeline.SendSync(msg);

      NativeExports.HandlePostAction(root, resolvedHandle, connName, msg.PostAction);
    } catch {
      if (tracked) root.Events.UnregisterMailbox(resolvedHandle);
      WriteRcName(rslt, -1, "");
      return;
    }

    WriteRcName(rslt, ErrorCodes.Success, resolvedHandle);
  }

  /// <summary>
  /// Respond to a command (server-side).
  /// ⎕NA 'I4 dll|conga_pp_respond P <PP <PP I4'
  /// </summary>
  [DwaExport("conga_pp_respond")]
  public static int CongaRespond(nint handle, Localp nameArg, Localp dataArg, int compression)
  {
    var root = HandleTable.Lookup(handle);
    if (root == null) return ErrorCodes.InvalidHandle;
    if (root.IsShuttingDown) return ErrorCodes.ShuttingDown;

    var nameStr = ReadLocalpString(nameArg);
    if (string.IsNullOrEmpty(nameStr)) return ErrorCodes.InvalidName;

    var (pipeline, connName, cmdName, resolveError) = NativeExports.ResolvePipelineForCommand(root, nameStr);
    if (resolveError != ErrorCodes.Success) return resolveError;
    if (pipeline == null) return ErrorCodes.ObjectNotReady;
    if (cmdName == null) return ErrorCodes.InvalidName;
    if (pipeline.Mode is not CommandMode commandMode) return ErrorCodes.InvalidMode;

    int dataLen = dataArg.Bound();
    ReadOnlyMemory<byte> payload = dataLen > 0
        ? new ReadOnlyMemory<byte>(dataArg.ReadSpan<byte>().ToArray())
        : ReadOnlyMemory<byte>.Empty;

    var msg = commandMode.TryPrepareRespond(connName, payload, cmdName);
    if (msg == null) return ErrorCodes.InvalidName;
    msg.Compression = (Protocol.CompressionAlgorithm)compression;
    pipeline.SendSync(msg);

    root.Registry.TryRemove(nameStr, out _);
    return ErrorCodes.Success;
  }

  /// <summary>
  /// Send a progress event for a command (server-side).
  /// ⎕NA 'I4 dll|conga_pp_progress P <PP <PP I4'
  /// </summary>
  [DwaExport("conga_pp_progress")]
  public static int CongaProgress(nint handle, Localp nameArg, Localp dataArg, int compression)
  {
    var root = HandleTable.Lookup(handle);
    if (root == null) return ErrorCodes.InvalidHandle;
    if (root.IsShuttingDown) return ErrorCodes.ShuttingDown;

    var nameStr = ReadLocalpString(nameArg);
    if (string.IsNullOrEmpty(nameStr)) return ErrorCodes.InvalidName;

    var (pipeline, connName, cmdName, resolveError) = NativeExports.ResolvePipelineForCommand(root, nameStr);
    if (resolveError != ErrorCodes.Success) return resolveError;
    if (pipeline == null) return ErrorCodes.ObjectNotReady;
    if (cmdName == null) return ErrorCodes.InvalidName;
    if (pipeline.Mode is not CommandMode commandMode) return ErrorCodes.InvalidMode;

    int dataLen = dataArg.Bound();
    ReadOnlyMemory<byte> payload = dataLen > 0
        ? new ReadOnlyMemory<byte>(dataArg.ReadSpan<byte>().ToArray())
        : ReadOnlyMemory<byte>.Empty;

    var msg = commandMode.TryPrepareProgress(connName, payload, cmdName);
    if (msg == null) return ErrorCodes.InvalidName;
    msg.Compression = (Protocol.CompressionAlgorithm)compression;
    pipeline.SendSync(msg);

    return ErrorCodes.Success;
  }

  // ════════════════════════════════════════════════════════════════════
  // Close / Names / Exists / Properties
  // ════════════════════════════════════════════════════════════════════

  /// <summary>
  /// Close a named object and all its children.
  /// ⎕NA 'I4 dll|conga_pp_close P <PP'
  /// </summary>
  [DwaExport("conga_pp_close")]
  public static int CongaClose(nint handle, Localp nameArg)
  {
    var root = HandleTable.Lookup(handle);
    if (root == null) return ErrorCodes.InvalidHandle;

    var objName = ReadLocalpString(nameArg);
    if (string.IsNullOrEmpty(objName)) return ErrorCodes.InvalidName;

    var removed = root.Registry.RemoveTree(objName);
    if (removed.Count == 0) return ErrorCodes.InvalidName;

    root.Events.UnregisterMailboxesByPrefix(objName);

    foreach (var obj in removed) {
      try {
        if (obj is ServerObject server)
          server.DisposeAsync().GetAwaiter().GetResult();
        else if (obj is ClientObject client)
          client.DisposeAsync().GetAwaiter().GetResult();
        else if (obj is ConnectionObject conn && conn.Pipeline != null)
          conn.Pipeline.DisposeAsync().GetAwaiter().GetResult();
      } catch { }
      obj.Parent?.TryRemoveChild(obj.Name);
    }

    return ErrorCodes.Success;
  }

  /// <summary>
  /// Check if a name exists in the registry. Returns 0 if found, 1002 if not.
  /// ⎕NA 'I4 dll|conga_pp_exists P <PP'
  /// </summary>
  [DwaExport("conga_pp_exists")]
  public static int CongaExists(nint handle, Localp nameArg)
  {
    var root = HandleTable.Lookup(handle);
    if (root == null) return ErrorCodes.InvalidHandle;

    var objName = ReadLocalpString(nameArg);
    if (string.IsNullOrEmpty(objName)) return ErrorCodes.InvalidName;

    return root.Registry.Lookup(objName) != null
        ? ErrorCodes.Success
        : ErrorCodes.InvalidName;
  }

  /// <summary>
  /// Get child names as a nested string vector.
  /// ⎕NA 'dll|conga_pp_names P <PP >PP'
  /// </summary>
  [DwaExport("conga_pp_names")]
  public static void CongaNames(nint handle, Localp objArg, Localp rslt)
  {
    var root = HandleTable.Lookup(handle);
    if (root == null) { rslt.SetString(""); return; }

    var objName = ReadLocalpString(objArg) ?? ".";
    var names = root.Registry.GetChildNames(objName);

    rslt.AllocNested(names.Count);
    for (int i = 0; i < names.Count; i++)
      rslt.SetString(names[i], i);
  }

  /// <summary>
  /// Set a property on a named object.
  /// ⎕NA 'I4 dll|conga_pp_setprop P <PP <PP <PP'
  /// </summary>
  [DwaExport("conga_pp_setprop")]
  public static int CongaSetProp(nint handle, Localp objArg, Localp propArg, Localp valueArg)
  {
    var root = HandleTable.Lookup(handle);
    if (root == null) return ErrorCodes.InvalidHandle;

    var objName = ReadLocalpString(objArg) ?? ".";
    var propName = ReadLocalpString(propArg);
    var value = ReadLocalpString(valueArg);
    if (string.IsNullOrEmpty(propName) || value == null)
      return ErrorCodes.InvalidProperty;

    PropertyStore store;
    if (objName == ".") {
      store = root.Properties;
    } else {
      var congaObj = root.Registry.Lookup(objName);
      if (congaObj == null) return ErrorCodes.InvalidName;
      store = congaObj.Properties;
    }

    var rc = store.Set(propName, value);

    if (rc == ErrorCodes.Success && objName == ".") {
      if (propName.Equals("Trace", StringComparison.OrdinalIgnoreCase)) {
        if (int.TryParse(value, out var level) && level >= 0 && level <= 4)
          root.Trace.Level = (Diagnostics.TraceLevel)level;
      } else if (propName.Equals("TraceFile", StringComparison.OrdinalIgnoreCase)) {
        var path = value.Trim('"');
        root.Trace.FilePath = string.IsNullOrEmpty(path) ? null : path;
      }
    }

    return rc;
  }

  /// <summary>
  /// Get a property value. Returns the value as a string in the result pocket.
  /// ⎕NA 'I4 dll|conga_pp_getprop P <PP <PP >PP'
  /// </summary>
  [DwaExport("conga_pp_getprop")]
  public static int CongaGetProp(nint handle, Localp objArg, Localp propArg, Localp rslt)
  {
    var root = HandleTable.Lookup(handle);
    if (root == null) return ErrorCodes.InvalidHandle;

    var objName = ReadLocalpString(objArg) ?? ".";
    var propName = ReadLocalpString(propArg);
    if (string.IsNullOrEmpty(propName)) return ErrorCodes.InvalidProperty;

    PropertyStore store;
    if (objName == ".") {
      store = root.Properties;
    } else {
      var congaObj = root.Registry.Lookup(objName);
      if (congaObj == null) return ErrorCodes.InvalidName;
      store = congaObj.Properties;
    }

    var rc = store.Get(propName, out var jsonResult);
    if (rc != ErrorCodes.Success) return rc;

    rslt.SetString(jsonResult);
    return ErrorCodes.Success;
  }

  // ════════════════════════════════════════════════════════════════════
  // Helpers
  // ════════════════════════════════════════════════════════════════════

  /// <summary>
  /// Read a string from a Localp. Returns null for empty/null pockets.
  /// </summary>
  private static string? ReadLocalpString(Localp lp)
  {
    if (lp.Bound() == 0) return null;
    return lp.ReadString();
  }

  /// <summary>
  /// Write a 2-element nested result: (rc name).
  /// </summary>
  private static void WriteRcName(Localp rslt, int rc, string name)
  {
    rslt.AllocNested(2);
    rslt.SetInt32(rc, 0);
    rslt.SetString(name, 1);
  }

  /// <summary>
  /// Write a 4-element event tuple: (rc objName eventType data).
  /// The 4th element is the event code (int) for Timeout/Closed, or byte data for Receive.
  /// </summary>
  private static void WriteEventTuple(Localp rslt, int rc, string objName, string eventName,
      ReadOnlyMemory<byte> data, int eventCode = 0)
  {
    rslt.AllocNested(4);
    rslt.SetInt32(rc, 0);
    rslt.SetString(objName, 1);
    rslt.SetString(eventName, 2);

    // Slot 3: data depends on event type
    if (data.Length > 0) {
      // Receive event — return payload as byte vector (type 83)
      var span = rslt.AllocVector<byte>(ELTYPES.APLSINT, data.Length, 3);
      data.Span.CopyTo(span);
    } else if (eventCode != 0) {
      // Timeout, Closed, etc — return the numeric code
      rslt.SetInt32(eventCode, 3);
    } else {
      // Empty data (e.g. Connect event)
      rslt.SetString("", 3);
    }
  }
}
