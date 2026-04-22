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
    public unsafe void WaitTimeout_ReturnsOne()
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
        fixed (byte* outHeadersPtr = outHeaders)
        {
            int rc = NativeApi.Wait(
                _handle, filterPtr, 50,
                outObjPtr, 256,
                outEventPtr, 256,
                &eventCode,
                outDataPtr, 1024, &dataLen,
                outHeadersPtr, 1024, &headersLen);

            Assert.Equal(ErrorCodes.WaitTimeout, rc);
        }
    }

    // ── Server create / start ──────────────────────────────────────────

    [Fact]
    public unsafe void CreateServer_AutoName_ReturnsS1()
    {
        var outName = new char[256];
        int outLen;

        fixed (char* namePtr = "")
        fixed (char* addrPtr = "")
        fixed (char* modePtr = "Raw")
        fixed (char* outNamePtr = outName)
        {
            int rc = NativeApi.SrvCreate(
                _handle, namePtr, addrPtr, 0, modePtr, 8192,
                outNamePtr, 256, &outLen);

            Assert.Equal(ErrorCodes.Success, rc);
            Assert.Equal("S1", new string(outNamePtr));
        }
    }

    [Fact]
    public unsafe void CreateServerAndStart_Success()
    {
        CreateServer("Raw");

        fixed (char* namePtr = "S1")
        {
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
        fixed (char* outNamePtr = outName)
        {
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

        fixed (char* namePtr = "C1")
        {
            int rc = NativeApi.SrvStart(_handle, namePtr);
            Assert.Equal(ErrorCodes.NotServer, rc);
        }
    }

    [Fact]
    public unsafe void ConnectNonClient_ReturnsNotClient()
    {
        CreateServer("Raw");

        fixed (char* namePtr = "S1")
        {
            int rc = NativeApi.CltConnect(_handle, namePtr, 100);
            Assert.Equal(ErrorCodes.NotClient, rc);
        }
    }

    // ── Server + Client connect ────────────────────────────────────────

    [Fact]
    public unsafe void ConnectClient_ReceivesConnectEvent()
    {
        CreateServer("Raw");
        StartServer("S1");
        int port = GetLocalPort("S1");

        CreateClient("127.0.0.1", port, "Raw");
        ConnectClient("C1", 5000);

        var (objName, eventName, eventCode, _, _, rc) = WaitForEvent("S1", 5000);
        Assert.Equal(ErrorCodes.Success, rc);
        Assert.StartsWith("S1.CON", objName);
        Assert.Equal("Connect", eventName);
        Assert.Equal((int)EventType.Connect, eventCode);
    }

    // ── Raw send / receive ─────────────────────────────────────────────

    [Fact]
    public unsafe void SendRawData_ReceiveOnServer()
    {
        SetupRawConnection(out var connName);

        var data = "Hello Server"u8.ToArray();
        fixed (char* namePtr = "C1")
        fixed (byte* dataPtr = data)
        {
            int rc = NativeApi.Send(
                _handle, namePtr, dataPtr, data.Length, null, 0, 0);
            Assert.Equal(ErrorCodes.Success, rc);
        }

        var (objName, eventName, _, payload, _, rc2) = WaitForEvent("S1", 5000);
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
        StartServer("S1");
        int port = GetLocalPort("S1");

        CreateClient("127.0.0.1", port, "Command");
        ConnectClient("C1", 5000);

        // Consume Connect event
        var (connName, _, _, _, _, connectRc) = WaitForEvent("S1", 5000);
        Assert.Equal(ErrorCodes.Success, connectRc);
        Assert.StartsWith("S1.CON", connName);

        // Client sends command "GetInfo"
        var request = "What is your status?"u8.ToArray();
        fixed (char* namePtr = "C1.GetInfo")
        fixed (byte* dataPtr = request)
        {
            int rc = NativeApi.Send(
                _handle, namePtr, dataPtr, request.Length, null, 0, 0);
            Assert.Equal(ErrorCodes.Success, rc);
        }

        // Server receives command
        var (cmdObjName, evtName, _, reqPayload, _, recvRc) = WaitForEvent("S1", 5000);
        Assert.Equal(ErrorCodes.Success, recvRc);
        Assert.Equal($"{connName}.GetInfo", cmdObjName);
        Assert.Equal("Receive", evtName);
        Assert.Equal("What is your status?",
            System.Text.Encoding.UTF8.GetString(reqPayload));

        // Server responds
        var response = "All systems operational"u8.ToArray();
        fixed (char* namePtr = cmdObjName)
        fixed (byte* dataPtr = response)
        {
            int rc = NativeApi.Respond(
                _handle, namePtr, dataPtr, response.Length);
            Assert.Equal(ErrorCodes.Success, rc);
        }

        // Client receives response
        var (clientCmd, clientEvt, _, respPayload, _, respRc) = WaitForEvent("C1", 5000);
        Assert.Equal(ErrorCodes.Success, respRc);
        Assert.Equal("C1.GetInfo", clientCmd);
        Assert.Equal("Receive", clientEvt);
        Assert.Equal("All systems operational",
            System.Text.Encoding.UTF8.GetString(respPayload));
    }

    // ── conga_close stops server ───────────────────────────────────────

    [Fact]
    public unsafe void CloseServer_RemovesFromRegistry()
    {
        CreateServer("Raw");
        StartServer("S1");

        fixed (char* namePtr = "S1")
        {
            int rc = NativeApi.Close(_handle, namePtr);
            Assert.Equal(ErrorCodes.Success, rc);
        }

        Assert.Null(HandleTable.Lookup(_handle)!.Registry.Lookup("S1"));
    }

    // ── conga_send with close_flag=1 ───────────────────────────────────

    [Fact]
    public unsafe void SendWithCloseFlag_ClosesConnection()
    {
        SetupRawConnection(out _);

        var data = "Goodbye"u8.ToArray();
        fixed (char* namePtr = "C1")
        fixed (byte* dataPtr = data)
        {
            int rc = NativeApi.Send(
                _handle, namePtr, dataPtr, data.Length, null, 0, 1);
            Assert.Equal(ErrorCodes.Success, rc);
        }

        var root = HandleTable.Lookup(_handle)!;
        Assert.Null(root.Registry.Lookup("C1"));
    }

    // ── Buffer too small for string output in conga_wait ───────────────

    [Fact]
    public unsafe void WaitBufferTooSmall_ObjName_ReturnsError()
    {
        SetupRawConnection(out _);

        // Send data so there's an event to dequeue
        var data = "Hello"u8.ToArray();
        fixed (char* namePtr = "C1")
        fixed (byte* dataPtr = data)
        {
            NativeApi.Send(_handle, namePtr, dataPtr, data.Length, null, 0, 0);
        }

        // Wait with tiny outObj buffer (too small for "S1.CON0001")
        var outObj = new char[3];
        var outEvent = new char[256];
        int eventCode;
        var outData = new byte[4096];
        int dataLen;
        var outHeaders = new byte[1024];
        int headersLen;

        fixed (char* filterPtr = "S1")
        fixed (char* outObjPtr = outObj)
        fixed (char* outEventPtr = outEvent)
        fixed (byte* outDataPtr = outData)
        fixed (byte* outHeadersPtr = outHeaders)
        {
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
        fixed (char* namePtr = "C1")
        fixed (byte* dataPtr = data)
        {
            NativeApi.Send(_handle, namePtr, dataPtr, data.Length, null, 0, 0);
        }

        var outObj = new char[256];
        var outEvent = new char[256];
        int eventCode;
        var outData = new byte[5]; // Only 5 bytes
        int dataLen;
        var outHeaders = new byte[1024];
        int headersLen;

        fixed (char* filterPtr = "S1")
        fixed (char* outObjPtr = outObj)
        fixed (char* outEventPtr = outEvent)
        fixed (byte* outDataPtr = outData)
        fixed (byte* outHeadersPtr = outHeaders)
        {
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
    // Helpers
    // ════════════════════════════════════════════════════════════════════

    private unsafe void CreateServer(string mode)
    {
        var outName = new char[256];
        int outLen;

        fixed (char* namePtr = "")
        fixed (char* addrPtr = "")
        fixed (char* modePtr = mode)
        fixed (char* outNamePtr = outName)
        {
            int rc = NativeApi.SrvCreate(
                _handle, namePtr, addrPtr, 0, modePtr,
                mode == "Command" ? 16384 : 8192,
                outNamePtr, 256, &outLen);
            Assert.Equal(ErrorCodes.Success, rc);
        }
    }

    private unsafe void StartServer(string name)
    {
        fixed (char* namePtr = name)
        {
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
        fixed (char* outNamePtr = outName)
        {
            int rc = NativeApi.CltCreate(
                _handle, namePtr, addrPtr, port, modePtr,
                mode == "Command" ? 16384 : 8192,
                outNamePtr, 256, &outLen);
            Assert.Equal(ErrorCodes.Success, rc);
        }
    }

    private unsafe void ConnectClient(string name, int timeoutMs)
    {
        fixed (char* namePtr = name)
        {
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
        fixed (char* outBufPtr = outBuf)
        {
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
        fixed (byte* outHeadersPtr = outHeaders)
        {
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
        StartServer("S1");
        int port = GetLocalPort("S1");

        CreateClient("127.0.0.1", port, "Raw");
        ConnectClient("C1", 5000);

        var (obj, _, _, _, _, rc) = WaitForEvent("S1", 5000);
        Assert.Equal(ErrorCodes.Success, rc);
        connName = obj;
    }
}
