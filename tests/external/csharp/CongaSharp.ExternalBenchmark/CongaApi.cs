namespace CongaSharp.ExternalBenchmark;

/// <summary>
/// High-level managed wrapper around the raw CongaNative P/Invoke calls.
/// Mirrors the Python conga_api.CongaApi class.
/// Each instance has its own conga_init handle (Root).
/// </summary>
internal sealed unsafe class CongaApi : IDisposable
{
    private const int Success = 0;
    private const int WaitTimeout = 1;
    private const int BufferTooSmall = 2001;

    private nint _handle;

    public nint Handle => _handle;

    public void Init()
    {
        nint h;
        Check("conga_init", CongaNative.Init(&h));
        _handle = h;
    }

    public void Shutdown()
    {
        if (_handle == 0) return;
        Check("conga_shutdown", CongaNative.Shutdown(_handle));
        _handle = 0;
    }

    public string Version()
    {
        Span<char> buf = stackalloc char[256];
        fixed (char* p = buf)
        {
            Check("conga_version", CongaNative.Version(p, 256));
            return new string(p);
        }
    }

    public string CreateServer(string name, string addr, int port, string mode, int bufferSize)
    {
        Span<char> outName = stackalloc char[512];
        int outLen;
        fixed (char* pName = name)
        fixed (char* pAddr = addr)
        fixed (char* pMode = mode)
        fixed (char* pOut = outName)
        {
            Check("conga_srv_create", CongaNative.SrvCreate(
                _handle, pName, pAddr, port, pMode, bufferSize,
                pOut, 512, &outLen));
            return new string(pOut);
        }
    }

    public void StartServer(string name)
    {
        fixed (char* pName = name)
            Check("conga_srv_start", CongaNative.SrvStart(_handle, pName));
    }

    public string CreateClient(string name, string addr, int port, string mode, int bufferSize)
    {
        Span<char> outName = stackalloc char[512];
        int outLen;
        fixed (char* pName = name)
        fixed (char* pAddr = addr)
        fixed (char* pMode = mode)
        fixed (char* pOut = outName)
        {
            Check("conga_clt_create", CongaNative.CltCreate(
                _handle, pName, pAddr, port, pMode, bufferSize,
                pOut, 512, &outLen));
            return new string(pOut);
        }
    }

    public void ConnectClient(string name, int timeoutMs)
    {
        fixed (char* pName = name)
            Check("conga_clt_connect", CongaNative.CltConnect(_handle, pName, timeoutMs));
    }

    public void SetProp(string obj, string prop, string jsonValue)
    {
        fixed (char* pObj = obj)
        fixed (char* pProp = prop)
        fixed (char* pVal = jsonValue)
            Check("conga_setprop", CongaNative.SetProp(_handle, pObj, pProp, pVal));
    }

    public string GetProp(string obj, string prop, int initialCap = 256)
    {
        int cap = Math.Max(64, initialCap);
        while (true)
        {
            var buf = new char[cap];
            int outLen;
            fixed (char* pObj = obj)
            fixed (char* pProp = prop)
            fixed (char* pBuf = buf)
            {
                int rc = CongaNative.GetProp(_handle, pObj, pProp, pBuf, cap, &outLen);
                if (rc == BufferTooSmall && outLen > cap)
                {
                    cap = outLen;
                    continue;
                }
                Check("conga_getprop", rc);
                return new string(pBuf);
            }
        }
    }

    public WaitResult? Wait(string nameFilter, int timeoutMs,
        int outObjCap = 1024, int outEventCap = 1024,
        int outDataCap = 2 * 1024 * 1024, int outHeadersCap = 64 * 1024)
    {
        var outObj = new char[outObjCap];
        var outEvent = new char[outEventCap];
        int outEventCode;
        var outData = new byte[outDataCap];
        int outDataLen;
        var outHeaders = new byte[outHeadersCap];
        int outHeadersLen;

        fixed (char* pFilter = nameFilter)
        fixed (char* pObj = outObj)
        fixed (char* pEvent = outEvent)
        fixed (byte* pData = outData)
        fixed (byte* pHeaders = outHeaders)
        {
            int rc = CongaNative.Wait(
                _handle, pFilter, timeoutMs,
                pObj, outObjCap,
                pEvent, outEventCap,
                &outEventCode,
                pData, outDataCap, &outDataLen,
                pHeaders, outHeadersCap, &outHeadersLen);

            if (rc == WaitTimeout)
                return null;
            Check("conga_wait", rc);

            int dataCount = Math.Min(outDataCap, outDataLen);
            int headersCount = Math.Min(outHeadersCap, outHeadersLen);

            return new WaitResult(
                new string(pObj),
                new string(pEvent),
                outEventCode,
                dataCount > 0 ? outData[..dataCount] : [],
                headersCount > 0 ? outHeaders[..headersCount] : []);
        }
    }

    public string Send(string name, byte[] data,
        byte[]? headers = null,
        int closeFlag = 0, int compression = 0, int compressionLevel = 0,
        int track = 0)
    {
        Span<char> outName = stackalloc char[256];
        int outNameLen;

        fixed (char* pName = name)
        fixed (byte* pData = data)
        fixed (byte* pHeaders = headers)
        fixed (char* pOut = outName)
        {
            Check("conga_send", CongaNative.Send(
                _handle, pName,
                pData, data.Length,
                pHeaders, headers?.Length ?? 0,
                closeFlag, compression, compressionLevel, track,
                pOut, 256, &outNameLen));
            return new string(pOut);
        }
    }

    public void Respond(string name, byte[] data, int compression = 0, int compressionLevel = 0)
    {
        fixed (char* pName = name)
        fixed (byte* pData = data)
            Check("conga_respond", CongaNative.Respond(
                _handle, pName, pData, data.Length, compression, compressionLevel));
    }

    public void Progress(string name, byte[] data, int compression = 0, int compressionLevel = 0)
    {
        fixed (char* pName = name)
        fixed (byte* pData = data)
            Check("conga_progress", CongaNative.Progress(
                _handle, pName, pData, data.Length, compression, compressionLevel));
    }

    public void Close(string name)
    {
        fixed (char* pName = name)
            Check("conga_close", CongaNative.Close(_handle, pName));
    }

    public void Dispose() => Shutdown();

    private static void Check(string operation, int rc)
    {
        if (rc != Success)
            throw new CongaException(operation, rc);
    }
}

/// <summary>
/// Result from conga_wait.
/// </summary>
internal sealed record WaitResult(
    string ObjectName,
    string EventName,
    int EventCode,
    byte[] Payload,
    byte[] Headers);

/// <summary>
/// Exception wrapping a non-zero error code from the C ABI.
/// </summary>
internal sealed class CongaException(string operation, int code)
    : Exception($"{operation} failed with rc={code}")
{
    public string Operation { get; } = operation;
    public int Code { get; } = code;
}
