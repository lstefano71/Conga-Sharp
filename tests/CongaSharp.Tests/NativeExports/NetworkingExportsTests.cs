namespace CongaSharp.Tests.NativeExports;

using CongaSharp.Core;
using CongaSharp.Errors;
using CongaSharp.Events;

using Xunit;

[Collection("HandleTable")]
public class NetworkingExportsTests : IDisposable
{
  private readonly nint _handle;

  public unsafe NetworkingExportsTests()
  {
    nint h;
    NativeApi.Init(&h);
    _handle = h;
  }

  public void Dispose()
  {
    NativeApi.Shutdown(_handle);
  }

  // ── conga_wait timeout ─────────────────────────────────────────────

  [Fact]
  public unsafe void WaitTimeout_ReturnsTimeoutEvent()
  {
    var outObj = new char[256];
    var outEvent = new char[256];
    int eventCode;
    var outData = new byte[1024];
    int dataLen;
    var outHeaders = new byte[1024];
    int headersLen;

    fixed (char* filterPtr = ".")
    fixed (char* outObjPtr = outObj)
    fixed (char* outEventPtr = outEvent)
    fixed (byte* outDataPtr = outData)
    fixed (byte* outHeadersPtr = outHeaders) {
      int rc = NativeApi.Wait(
          _handle, filterPtr, 50,
          outObjPtr, 256,
          outEventPtr, 256,
          &eventCode,
          outDataPtr, 1024, &dataLen,
          outHeadersPtr, 1024, &headersLen);

      Assert.Equal(ErrorCodes.Success, rc);
      Assert.Equal(".", new string(outObjPtr));
      Assert.Equal("Timeout", new string(outEventPtr));
      Assert.Equal(100, eventCode);
    }
  }

  // ── Server create / start ──────────────────────────────────────────

  [Fact]
  public unsafe void CreateServer_AutoName_ReturnsSRV00000000()
  {
    var outName = new char[256];
    int outLen;

    fixed (char* namePtr = "")
    fixed (char* addrPtr = "")
    fixed (char* modePtr = "Raw")
    fixed (char* outNamePtr = outName) {
      int rc = NativeApi.SrvCreate(
          _handle, namePtr, addrPtr, 0, modePtr, 8192,
          outNamePtr, 256, &outLen);

      Assert.Equal(ErrorCodes.Success, rc);
      Assert.Equal("SRV00000000", new string(outNamePtr));
    }
  }

  [Fact]
  public unsafe void CreateServerAndStart_Success()
  {
    CreateServer("Raw");

    fixed (char* namePtr = "SRV00000000") {
      int rc = NativeApi.SrvStart(_handle, namePtr);
      Assert.Equal(ErrorCodes.Success, rc);
    }
  }

  [Fact]
  public unsafe void CreateServer_InvalidMode_ReturnsError()
  {
    var outName = new char[256];
    int outLen;

    fixed (char* namePtr = "")
    fixed (char* addrPtr = "")
    fixed (char* modePtr = "InvalidMode")
    fixed (char* outNamePtr = outName) {
      int rc = NativeApi.SrvCreate(
          _handle, namePtr, addrPtr, 0, modePtr, 8192,
          outNamePtr, 256, &outLen);

      Assert.Equal(ErrorCodes.InvalidMode, rc);
    }
  }

  [Fact]
  public unsafe void StartNonServer_ReturnsNotServer()
  {
    CreateClient("127.0.0.1", 9999, "Raw");

    fixed (char* namePtr = "CLT00000000") {
      int rc = NativeApi.SrvStart(_handle, namePtr);
      Assert.Equal(ErrorCodes.NotServer, rc);
    }
  }

  [Fact]
  public unsafe void ConnectNonClient_ReturnsNotClient()
  {
    CreateServer("Raw");

    fixed (char* namePtr = "SRV00000000") {
      int rc = NativeApi.CltConnect(_handle, namePtr, 100);
      Assert.Equal(ErrorCodes.NotClient, rc);
    }
  }

  // ── Server + Client connect ────────────────────────────────────────

  [Fact]
  public unsafe void ConnectClient_ReceivesConnectEvent()
  {
    CreateServer("Raw");
    StartServer("SRV00000000");
    int port = GetLocalPort("SRV00000000");

    CreateClient("127.0.0.1", port, "Raw");
    ConnectClient("CLT00000000", 5000);

    var (objName, eventName, eventCode, _, _, rc) = WaitForEvent("SRV00000000", 5000);
    Assert.Equal(ErrorCodes.Success, rc);
    Assert.StartsWith("SRV00000000.CON", objName);
    Assert.Equal("Connect", eventName);
    Assert.Equal((int)EventType.Connect, eventCode);
  }

  // ── Raw send / receive ─────────────────────────────────────────────

  [Fact]
  public unsafe void SendRawData_ReceiveOnServer()
  {
    SetupRawConnection(out var connName);

    var data = "Hello Server"u8.ToArray();
    fixed (char* namePtr = "CLT00000000")
    fixed (byte* dataPtr = data) {
      int rc = NativeApi.Send(
          _handle, namePtr, dataPtr, data.Length, null, 0, 0);
      Assert.Equal(ErrorCodes.Success, rc);
    }

    var (objName, eventName, _, payload, _, rc2) = WaitForEvent("SRV00000000", 5000);
    Assert.Equal(ErrorCodes.Success, rc2);
    Assert.Equal(connName, objName);
    Assert.Equal("Receive", eventName);
    Assert.Equal("Hello Server", System.Text.Encoding.UTF8.GetString(payload));
  }

  // ── Command mode: send → receive → respond → receive response ──────

  [Fact]
  public unsafe void CommandMode_SendReceiveRespond()
  {
    CreateServer("Command");
    StartServer("SRV00000000");
    int port = GetLocalPort("SRV00000000");

    CreateClient("127.0.0.1", port, "Command");
    ConnectClient("CLT00000000", 5000);

    // Consume Connect event
    var (connName, _, _, _, _, connectRc) = WaitForEvent("SRV00000000", 5000);
    Assert.Equal(ErrorCodes.Success, connectRc);
    Assert.StartsWith("SRV00000000.CON", connName);

    // Client sends command "GetInfo"
    var request = "What is your status?"u8.ToArray();
    fixed (char* namePtr = "CLT00000000.GetInfo")
    fixed (byte* dataPtr = request) {
      int rc = NativeApi.Send(
          _handle, namePtr, dataPtr, request.Length, null, 0, 0);
      Assert.Equal(ErrorCodes.Success, rc);
    }

    // Server receives command (server-side name is auto-generated from correlation GUID)
    var (cmdObjName, evtName, _, reqPayload, _, recvRc) = WaitForEvent("SRV00000000", 5000);
    Assert.Equal(ErrorCodes.Success, recvRc);
    Assert.StartsWith($"{connName}.", cmdObjName);
    Assert.Equal("Receive", evtName);
    Assert.Equal("What is your status?",
        System.Text.Encoding.UTF8.GetString(reqPayload));

    // Server responds
    var response = "All systems operational"u8.ToArray();
    fixed (char* namePtr = cmdObjName)
    fixed (byte* dataPtr = response) {
      int rc = NativeApi.Respond(
          _handle, namePtr, dataPtr, response.Length);
      Assert.Equal(ErrorCodes.Success, rc);
    }

    // Client receives response
    var (clientCmd, clientEvt, _, respPayload, _, respRc) = WaitForEvent("CLT00000000", 5000);
    Assert.Equal(ErrorCodes.Success, respRc);
    Assert.Equal("CLT00000000.GetInfo", clientCmd);
    Assert.Equal("Receive", clientEvt);
    Assert.Equal("All systems operational",
        System.Text.Encoding.UTF8.GetString(respPayload));
  }

  // ── conga_close stops server ───────────────────────────────────────

  [Fact]
  public unsafe void CloseServer_RemovesFromRegistry()
  {
    CreateServer("Raw");
    StartServer("SRV00000000");

    fixed (char* namePtr = "SRV00000000") {
      int rc = NativeApi.Close(_handle, namePtr);
      Assert.Equal(ErrorCodes.Success, rc);
    }

    Assert.Null(HandleTable.Lookup(_handle)!.Registry.Lookup("SRV00000000"));
  }

  // ── conga_send with close_flag=1 ───────────────────────────────────

  [Fact]
  public unsafe void SendWithCloseFlag_ClosesConnection()
  {
    SetupRawConnection(out _);

    var data = "Goodbye"u8.ToArray();
    fixed (char* namePtr = "CLT00000000")
    fixed (byte* dataPtr = data) {
      int rc = NativeApi.Send(
          _handle, namePtr, dataPtr, data.Length, null, 0, 1);
      Assert.Equal(ErrorCodes.Success, rc);
    }

    var root = HandleTable.Lookup(_handle)!;
    Assert.Null(root.Registry.Lookup("CLT00000000"));
  }

  // ── Buffer too small for string output in conga_wait ───────────────

  [Fact]
  public unsafe void WaitBufferTooSmall_ObjName_ReturnsError()
  {
    SetupRawConnection(out _);

    // Send data so there's an event to dequeue
    var data = "Hello"u8.ToArray();
    fixed (char* namePtr = "CLT00000000")
    fixed (byte* dataPtr = data) {
      NativeApi.Send(_handle, namePtr, dataPtr, data.Length, null, 0, 0);
    }

    // Wait with tiny outObj buffer (too small for "SRV00000000.CON00000000")
    var outObj = new char[3];
    var outEvent = new char[256];
    int eventCode;
    var outData = new byte[4096];
    int dataLen;
    var outHeaders = new byte[1024];
    int headersLen;

    fixed (char* filterPtr = "SRV00000000")
    fixed (char* outObjPtr = outObj)
    fixed (char* outEventPtr = outEvent)
    fixed (byte* outDataPtr = outData)
    fixed (byte* outHeadersPtr = outHeaders) {
      int rc = NativeApi.Wait(
          _handle, filterPtr, 5000,
          outObjPtr, 3,
          outEventPtr, 256,
          &eventCode,
          outDataPtr, 4096, &dataLen,
          outHeadersPtr, 1024, &headersLen);

      Assert.Equal(ErrorCodes.BufferTooSmall, rc);
    }
  }

  // ── Data truncation in conga_wait ──────────────────────────────────

  [Fact]
  public unsafe void WaitDataTruncation_ReportsActualLength()
  {
    SetupRawConnection(out _);

    var data = "Hello Server, this is a longer message"u8.ToArray();
    fixed (char* namePtr = "CLT00000000")
    fixed (byte* dataPtr = data) {
      NativeApi.Send(_handle, namePtr, dataPtr, data.Length, null, 0, 0);
    }

    var outObj = new char[256];
    var outEvent = new char[256];
    int eventCode;
    var outData = new byte[5]; // Only 5 bytes
    int dataLen;
    var outHeaders = new byte[1024];
    int headersLen;

    fixed (char* filterPtr = "SRV00000000")
    fixed (char* outObjPtr = outObj)
    fixed (char* outEventPtr = outEvent)
    fixed (byte* outDataPtr = outData)
    fixed (byte* outHeadersPtr = outHeaders) {
      int rc = NativeApi.Wait(
          _handle, filterPtr, 5000,
          outObjPtr, 256,
          outEventPtr, 256,
          &eventCode,
          outDataPtr, 5, &dataLen,
          outHeadersPtr, 1024, &headersLen);

      Assert.Equal(ErrorCodes.Success, rc);
      Assert.Equal(data.Length, dataLen);
      Assert.Equal(data[..5], outData[..5]);
    }
  }

  // ════════════════════════════════════════════════════════════════════
  // Send parity tests (DRC.Send A.26 semantics)
  // ════════════════════════════════════════════════════════════════════

  // ── Command mode: auto-name generation ────────────────────────────

  [Fact]
  public unsafe void CommandSend_BaseClientName_ReturnsAutoHandle()
  {
    SetupCommandConnection(out _);

    var data = "request"u8.ToArray();
    var (rc, handle) = SendWithHandle("CLT00000000", data);

    Assert.Equal(ErrorCodes.Success, rc);
    Assert.Equal("CLT00000000.Auto00000000", handle);
  }

  [Fact]
  public unsafe void CommandSend_BaseClientName_IncrementsAutoHandle()
  {
    SetupCommandConnection(out var connName);

    var data = "req"u8.ToArray();

    var (rc1, handle1) = SendWithHandle("CLT00000000", data);
    Assert.Equal(ErrorCodes.Success, rc1);
    Assert.Equal("CLT00000000.Auto00000000", handle1);

    // Consume server event and respond so the first command is complete
    var (srvObj1, _, _, _, _, srvRc1) = WaitForEvent("SRV00000000", 5000);
    Assert.Equal(ErrorCodes.Success, srvRc1);

    var resp = "ok"u8.ToArray();
    fixed (char* namePtr = srvObj1)
    fixed (byte* dataPtr = resp) {
      NativeApi.Respond(_handle, namePtr, dataPtr, resp.Length);
    }

    // Consume client response
    WaitForEvent("CLT00000000", 5000);

    var (rc2, handle2) = SendWithHandle("CLT00000000", data);
    Assert.Equal(ErrorCodes.Success, rc2);
    Assert.Equal("CLT00000000.Auto00000001", handle2);
  }

  // ── Command mode: explicit dotted name returned unaltered ─────────

  [Fact]
  public unsafe void CommandSend_ExplicitName_ReturnedUnaltered()
  {
    SetupCommandConnection(out _);

    var data = "request"u8.ToArray();
    var (rc, handle) = SendWithHandle("CLT00000000.MyCmd", data);

    Assert.Equal(ErrorCodes.Success, rc);
    Assert.Equal("CLT00000000.MyCmd", handle);
  }

  // ── Command mode: wait by handle isolates one command ─────────────

  [Fact]
  public unsafe void CommandSend_WaitByHandle_IsolatesCommand()
  {
    SetupCommandConnection(out var connName);

    var data = "req"u8.ToArray();

    // Send two parallel commands
    var (rc1, handle1) = SendWithHandle("CLT00000000", data);
    var (rc2, handle2) = SendWithHandle("CLT00000000", data);
    Assert.Equal(ErrorCodes.Success, rc1);
    Assert.Equal(ErrorCodes.Success, rc2);
    Assert.NotEqual(handle1, handle2);

    // Server receives both — respond to them
    var (srvObj1, _, _, _, _, sRc1) = WaitForEvent("SRV00000000", 5000);
    var (srvObj2, _, _, _, _, sRc2) = WaitForEvent("SRV00000000", 5000);
    Assert.Equal(ErrorCodes.Success, sRc1);
    Assert.Equal(ErrorCodes.Success, sRc2);

    var resp1 = "response-1"u8.ToArray();
    var resp2 = "response-2"u8.ToArray();

    // Respond to second command first (out of order)
    fixed (char* n2 = srvObj2)
    fixed (byte* d2 = resp2) {
      NativeApi.Respond(_handle, n2, d2, resp2.Length);
    }

    fixed (char* n1 = srvObj1)
    fixed (byte* d1 = resp1) {
      NativeApi.Respond(_handle, n1, d1, resp1.Length);
    }

    // Wait by specific handle — should get the correct response
    var (obj2, _, _, payload2, _, wRc2) = WaitForEvent(handle2, 5000);
    Assert.Equal(ErrorCodes.Success, wRc2);
    Assert.Equal(handle2, obj2);
    Assert.Equal("response-2", System.Text.Encoding.UTF8.GetString(payload2));

    var (obj1, _, _, payload1, _, wRc1) = WaitForEvent(handle1, 5000);
    Assert.Equal(ErrorCodes.Success, wRc1);
    Assert.Equal(handle1, obj1);
    Assert.Equal("response-1", System.Text.Encoding.UTF8.GetString(payload1));
  }

  // ── Command mode: wait by client name gets all command events ──────

  [Fact]
  public unsafe void CommandSend_WaitByClient_ReceivesAllCommands()
  {
    SetupCommandConnection(out var connName);

    var data = "req"u8.ToArray();
    var (rc1, _) = SendWithHandle("CLT00000000", data);
    Assert.Equal(ErrorCodes.Success, rc1);

    // Server receives and responds
    var (srvObj, _, _, _, _, sRc) = WaitForEvent("SRV00000000", 5000);
    Assert.Equal(ErrorCodes.Success, sRc);
    var resp = "done"u8.ToArray();
    fixed (char* n = srvObj)
    fixed (byte* d = resp) {
      NativeApi.Respond(_handle, n, d, resp.Length);
    }

    // Client waits on "CLT00000000" — should receive the Auto command response
    var (obj, evt, _, payload, _, wRc) = WaitForEvent("CLT00000000", 5000);
    Assert.Equal(ErrorCodes.Success, wRc);
    Assert.StartsWith("CLT00000000.Auto", obj);
    Assert.Equal("Receive", evt);
    Assert.Equal("done", System.Text.Encoding.UTF8.GetString(payload));
  }

  // ── Command mode: server-side Send rejected ───────────────────────

  [Fact]
  public unsafe void CommandSend_ServerSide_ReturnsInvalidMode()
  {
    SetupCommandConnection(out var connName);

    // Server-side connection trying to Send in Command mode
    var data = "illegal"u8.ToArray();
    var (rc, _) = SendWithHandle(connName, data);

    Assert.Equal(ErrorCodes.InvalidMode, rc);
  }

  // ── Raw mode: auto-name generation ────────────────────────────────

  [Fact]
  public unsafe void RawSend_BaseClientName_ReturnsAutoHandle()
  {
    SetupRawConnection(out _);

    var data = "hello"u8.ToArray();
    var (rc, handle) = SendWithHandle("CLT00000000", data);

    Assert.Equal(ErrorCodes.Success, rc);
    Assert.Equal("CLT00000000.Auto00000000", handle);
  }

  [Fact]
  public unsafe void RawSend_ExplicitName_ReturnedUnaltered()
  {
    SetupRawConnection(out _);

    var data = "hello"u8.ToArray();
    var (rc, handle) = SendWithHandle("CLT00000000.MyMsg", data);

    Assert.Equal(ErrorCodes.Success, rc);
    Assert.Equal("CLT00000000.MyMsg", handle);
  }

  // ── Send outName buffer too small ─────────────────────────────────

  [Fact]
  public unsafe void Send_OutNameBufferTooSmall_ReturnsError()
  {
    SetupRawConnection(out _);

    var data = "hello"u8.ToArray();
    var outName = new char[3]; // too small for "CLT00000000.Auto00000000"
    int outNameLen;

    fixed (char* namePtr = "CLT00000000")
    fixed (byte* dataPtr = data)
    fixed (char* outNamePtr = outName) {
      int rc = NativeApi.Send(
          _handle, namePtr, dataPtr, data.Length,
          null, 0, 0, 0, 0, 0, outNamePtr, 3, &outNameLen);

      Assert.Equal(ErrorCodes.BufferTooSmall, rc);
      Assert.Equal("CLT00000000.Auto00000000".Length + 1, outNameLen);
    }
  }

  // ════════════════════════════════════════════════════════════════════
  // Helpers
  // ════════════════════════════════════════════════════════════════════

  private unsafe void CreateServer(string mode)
  {
    var outName = new char[256];
    int outLen;

    fixed (char* namePtr = "")
    fixed (char* addrPtr = "")
    fixed (char* modePtr = mode)
    fixed (char* outNamePtr = outName) {
      int rc = NativeApi.SrvCreate(
          _handle, namePtr, addrPtr, 0, modePtr,
          mode == "Command" ? 16384 : 8192,
          outNamePtr, 256, &outLen);
      Assert.Equal(ErrorCodes.Success, rc);
    }
  }

  private unsafe void StartServer(string name)
  {
    fixed (char* namePtr = name) {
      int rc = NativeApi.SrvStart(_handle, namePtr);
      Assert.Equal(ErrorCodes.Success, rc);
    }
  }

  private unsafe void CreateClient(string addr, int port, string mode)
  {
    var outName = new char[256];
    int outLen;

    fixed (char* namePtr = "")
    fixed (char* addrPtr = addr)
    fixed (char* modePtr = mode)
    fixed (char* outNamePtr = outName) {
      int rc = NativeApi.CltCreate(
          _handle, namePtr, addrPtr, port, modePtr,
          mode == "Command" ? 16384 : 8192,
          outNamePtr, 256, &outLen);
      Assert.Equal(ErrorCodes.Success, rc);
    }
  }

  private unsafe void ConnectClient(string name, int timeoutMs)
  {
    fixed (char* namePtr = name) {
      int rc = NativeApi.CltConnect(_handle, namePtr, timeoutMs);
      Assert.Equal(ErrorCodes.Success, rc);
    }
  }

  private unsafe int GetLocalPort(string serverName)
  {
    var outBuf = new char[64];
    int outLen;

    fixed (char* objPtr = serverName)
    fixed (char* propPtr = "LocalPort")
    fixed (char* outBufPtr = outBuf) {
      int rc = NativeApi.GetProp(
          _handle, objPtr, propPtr, outBufPtr, 64, &outLen);
      Assert.Equal(ErrorCodes.Success, rc);
      return int.Parse(new string(outBufPtr));
    }
  }

  private unsafe (string objName, string eventName, int eventCode,
      byte[] payload, byte[] headers, int rc) WaitForEvent(string filter, int timeoutMs)
  {
    var outObj = new char[256];
    var outEvent = new char[256];
    int eventCode;
    var outData = new byte[65536];
    int dataLen;
    var outHeaders = new byte[4096];
    int headersLen;

    fixed (char* filterPtr = filter)
    fixed (char* outObjPtr = outObj)
    fixed (char* outEventPtr = outEvent)
    fixed (byte* outDataPtr = outData)
    fixed (byte* outHeadersPtr = outHeaders) {
      int rc = NativeApi.Wait(
          _handle, filterPtr, timeoutMs,
          outObjPtr, 256,
          outEventPtr, 256,
          &eventCode,
          outDataPtr, 65536, &dataLen,
          outHeadersPtr, 4096, &headersLen);

      string objName = rc == ErrorCodes.Success ? new string(outObjPtr) : "";
      string eventName = rc == ErrorCodes.Success ? new string(outEventPtr) : "";

      byte[] payload = rc == ErrorCodes.Success && dataLen > 0
          ? outData[..dataLen] : Array.Empty<byte>();

      byte[] headers = rc == ErrorCodes.Success && headersLen > 0
          ? outHeaders[..headersLen] : Array.Empty<byte>();

      return (objName, eventName, eventCode, payload, headers, rc);
    }
  }

  /// <summary>
  /// Creates a Raw server on an ephemeral port, connects a client,
  /// and consumes the Connect event. Returns the server-side connection name.
  /// </summary>
  private unsafe void SetupRawConnection(out string connName)
  {
    CreateServer("Raw");
    StartServer("SRV00000000");
    int port = GetLocalPort("SRV00000000");

    CreateClient("127.0.0.1", port, "Raw");
    ConnectClient("CLT00000000", 5000);

    var (obj, _, _, _, _, rc) = WaitForEvent("SRV00000000", 5000);
    Assert.Equal(ErrorCodes.Success, rc);
    connName = obj;
  }

  /// <summary>
  /// Creates a Command server on an ephemeral port, connects a client,
  /// and consumes the Connect event. Returns the server-side connection name.
  /// </summary>
  private unsafe void SetupCommandConnection(out string connName)
  {
    CreateServer("Command");
    StartServer("SRV00000000");
    int port = GetLocalPort("SRV00000000");

    CreateClient("127.0.0.1", port, "Command");
    ConnectClient("CLT00000000", 5000);

    var (obj, _, _, _, _, rc) = WaitForEvent("SRV00000000", 5000);
    Assert.Equal(ErrorCodes.Success, rc);
    connName = obj;
  }

  /// <summary>
  /// Sends data and returns the resolved handle name written to outName buffer.
  /// </summary>
  private unsafe (int rc, string resolvedHandle) SendWithHandle(
      string objectName, byte[] data,
      byte[]? headers = null, int closeFlag = 0)
  {
    var outName = new char[256];
    int outNameLen;

    fixed (char* namePtr = objectName)
    fixed (byte* dataPtr = data)
    fixed (byte* hdrPtr = headers)
    fixed (char* outNamePtr = outName) {
      int rc = NativeApi.Send(
          _handle, namePtr, dataPtr, data.Length,
          hdrPtr, headers?.Length ?? 0, closeFlag,
          0, 0, 0, outNamePtr, 256, &outNameLen);

      string handle = rc == ErrorCodes.Success
          ? new string(outNamePtr)
          : "";
      return (rc, handle);
    }
  }
}
