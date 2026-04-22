namespace CongaSharp.Tests.NativeExports;

/// <summary>
/// Wraps [UnmanagedCallersOnly] exports via function pointers so tests can call them.
/// </summary>
internal static unsafe class NativeApi
{
    public static int Init(nint* outHandle)
    {
        delegate* unmanaged<nint*, int> fn = &CongaSharp.NativeExports.CongaInit;
        return fn(outHandle);
    }

    public static int Shutdown(nint handle)
    {
        delegate* unmanaged<nint, int> fn = &CongaSharp.NativeExports.CongaShutdown;
        return fn(handle);
    }

    public static int Close(nint handle, char* name)
    {
        delegate* unmanaged<nint, char*, int> fn = &CongaSharp.NativeExports.CongaClose;
        return fn(handle, name);
    }

    public static int GetProp(nint handle, char* obj, char* prop,
        char* outJson, int outJsonCap, int* outJsonLen)
    {
        delegate* unmanaged<nint, char*, char*, char*, int, int*, int> fn =
            &CongaSharp.NativeExports.CongaGetProp;
        return fn(handle, obj, prop, outJson, outJsonCap, outJsonLen);
    }

    public static int SrvCreate(nint handle, char* name, char* addr, int port,
        char* mode, int bufferSize, char* outName, int outNameCap, int* outNameLen)
    {
        delegate* unmanaged<nint, char*, char*, int, char*, int, char*, int, int*, int> fn =
            &CongaSharp.NativeExports.CongaSrvCreate;
        return fn(handle, name, addr, port, mode, bufferSize, outName, outNameCap, outNameLen);
    }

    public static int SrvStart(nint handle, char* name)
    {
        delegate* unmanaged<nint, char*, int> fn = &CongaSharp.NativeExports.CongaSrvStart;
        return fn(handle, name);
    }

    public static int CltCreate(nint handle, char* name, char* addr, int port,
        char* mode, int bufferSize, char* outName, int outNameCap, int* outNameLen)
    {
        delegate* unmanaged<nint, char*, char*, int, char*, int, char*, int, int*, int> fn =
            &CongaSharp.NativeExports.CongaCltCreate;
        return fn(handle, name, addr, port, mode, bufferSize, outName, outNameCap, outNameLen);
    }

    public static int CltConnect(nint handle, char* name, int timeoutMs)
    {
        delegate* unmanaged<nint, char*, int, int> fn =
            &CongaSharp.NativeExports.CongaCltConnect;
        return fn(handle, name, timeoutMs);
    }

    public static int Wait(nint handle, char* name, int timeoutMs,
        char* outObj, int outObjCap,
        char* outEvent, int outEventCap,
        int* outEventCode,
        byte* outData, int outDataCap, int* outDataLen,
        byte* outHeaders, int outHeadersCap, int* outHeadersLen)
    {
        delegate* unmanaged<nint, char*, int, char*, int, char*, int, int*,
            byte*, int, int*, byte*, int, int*, int> fn =
            &CongaSharp.NativeExports.CongaWait;
        return fn(handle, name, timeoutMs,
            outObj, outObjCap, outEvent, outEventCap, outEventCode,
            outData, outDataCap, outDataLen,
            outHeaders, outHeadersCap, outHeadersLen);
    }

    public static int Send(nint handle, char* name,
        byte* data, int dataLen,
        byte* headers, int headersLen,
        int closeFlag,
        int compression = 0,
        int compressionLevel = 0)
    {
        delegate* unmanaged<nint, char*, byte*, int, byte*, int, int, int, int, int> fn =
            &CongaSharp.NativeExports.CongaSend;
        return fn(handle, name, data, dataLen, headers, headersLen, closeFlag, compression, compressionLevel);
    }

    public static int Respond(nint handle, char* name, byte* data, int dataLen,
        int compression = 0, int compressionLevel = 0)
    {
        delegate* unmanaged<nint, char*, byte*, int, int, int, int> fn =
            &CongaSharp.NativeExports.CongaRespond;
        return fn(handle, name, data, dataLen, compression, compressionLevel);
    }

    public static int Progress(nint handle, char* name, byte* data, int dataLen,
        int compression = 0, int compressionLevel = 0)
    {
        delegate* unmanaged<nint, char*, byte*, int, int, int, int> fn =
            &CongaSharp.NativeExports.CongaProgress;
        return fn(handle, name, data, dataLen, compression, compressionLevel);
    }
}
