using System.Runtime.InteropServices;

namespace CongaSharp.ExternalBenchmark;

/// <summary>
/// Raw P/Invoke declarations mirroring the C ABI of congasharp.dll.
/// Uses NativeLibrary.SetDllImportResolver so the DLL path can be
/// specified at runtime (not baked at compile time).
/// </summary>
internal static unsafe partial class CongaNative
{
    private const string Lib = "congasharp";

    /// <summary>
    /// Call once at startup to register the custom DLL resolver so that
    /// [DllImport("congasharp")] finds the user-supplied path.
    /// </summary>
    public static void RegisterResolver(string dllPath)
    {
        var fullPath = Path.GetFullPath(dllPath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"DLL not found: {fullPath}");

        NativeLibrary.SetDllImportResolver(
            typeof(CongaNative).Assembly,
            (name, assembly, searchPath) =>
            {
                if (name == Lib)
                    return NativeLibrary.Load(fullPath);
                return IntPtr.Zero;
            });
    }

    // ─── Lifecycle ────────────────────────────────────────────────

    [DllImport(Lib, EntryPoint = "conga_version", CharSet = CharSet.Unicode)]
    public static extern int Version(char* outVersion, int versionCap);

    [DllImport(Lib, EntryPoint = "conga_init")]
    public static extern int Init(nint* outHandle);

    [DllImport(Lib, EntryPoint = "conga_shutdown")]
    public static extern int Shutdown(nint handle);

    [DllImport(Lib, EntryPoint = "conga_close", CharSet = CharSet.Unicode)]
    public static extern int Close(nint handle, char* name);

    // ─── Server ───────────────────────────────────────────────────

    [DllImport(Lib, EntryPoint = "conga_srv_create", CharSet = CharSet.Unicode)]
    public static extern int SrvCreate(
        nint handle,
        char* name,
        char* addr,
        int port,
        char* mode,
        int bufferSize,
        char* outName,
        int outNameCap,
        int* outNameLen);

    [DllImport(Lib, EntryPoint = "conga_srv_start", CharSet = CharSet.Unicode)]
    public static extern int SrvStart(nint handle, char* name);

    // ─── Client ───────────────────────────────────────────────────

    [DllImport(Lib, EntryPoint = "conga_clt_create", CharSet = CharSet.Unicode)]
    public static extern int CltCreate(
        nint handle,
        char* name,
        char* addr,
        int port,
        char* mode,
        int bufferSize,
        char* outName,
        int outNameCap,
        int* outNameLen);

    [DllImport(Lib, EntryPoint = "conga_clt_connect", CharSet = CharSet.Unicode)]
    public static extern int CltConnect(nint handle, char* name, int timeoutMs);

    // ─── Wait ─────────────────────────────────────────────────────

    [DllImport(Lib, EntryPoint = "conga_wait", CharSet = CharSet.Unicode)]
    public static extern int Wait(
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
        int* outHeadersLen);

    // ─── Send / Respond / Progress ────────────────────────────────

    [DllImport(Lib, EntryPoint = "conga_send", CharSet = CharSet.Unicode)]
    public static extern int Send(
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
        int* outNameLen);

    [DllImport(Lib, EntryPoint = "conga_respond", CharSet = CharSet.Unicode)]
    public static extern int Respond(
        nint handle,
        char* name,
        byte* data,
        int dataLen,
        int compression,
        int compressionLevel);

    [DllImport(Lib, EntryPoint = "conga_progress", CharSet = CharSet.Unicode)]
    public static extern int Progress(
        nint handle,
        char* name,
        byte* data,
        int dataLen,
        int compression,
        int compressionLevel);

    // ─── Properties ───────────────────────────────────────────────

    [DllImport(Lib, EntryPoint = "conga_setprop", CharSet = CharSet.Unicode)]
    public static extern int SetProp(nint handle, char* obj, char* prop, char* jsonValue);

    [DllImport(Lib, EntryPoint = "conga_getprop", CharSet = CharSet.Unicode)]
    public static extern int GetProp(
        nint handle,
        char* obj,
        char* prop,
        char* outJson,
        int outJsonCap,
        int* outJsonLen);
}
