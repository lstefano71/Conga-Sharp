using System.Buffers;
using System.Runtime.InteropServices;
using CongaSharp.Core;
using CongaSharp.Errors;
using CongaSharp.Events;
using CongaSharp.Marshalling;
using CongaSharp.Modes;
using CongaSharp.Networking;

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
        try
        {
            var root = HandleTable.Lookup(handle);
            if (root == null) return ErrorCodes.InvalidHandle;
            if (root.IsShuttingDown) return ErrorCodes.ShuttingDown;

            var modeStr = StringMarshaller.ReadFromPointer(mode);
            if (ModeKindExtensions.TryParse(modeStr) == null)
                return ErrorCodes.InvalidMode;

            var nameStr = StringMarshaller.ReadFromPointer(name);
            var addrStr = StringMarshaller.ReadFromPointer(addr) ?? "";

            if (string.IsNullOrEmpty(nameStr))
            {
                nameStr = root.Registry.GenerateServerName();
            }
            else
            {
                if (root.Registry.Lookup(nameStr) != null)
                    return ErrorCodes.NameInUse;
            }

            var server = new ServerObject(nameStr, addrStr, port, modeStr!, bufferSize);
            if (!root.Registry.TryAdd(server))
                return ErrorCodes.NameInUse;

            return StringMarshaller.WriteToBuffer(nameStr, outName, outNameCap, outNameLen);
        }
        catch { return -1; }
    }

    [UnmanagedCallersOnly(EntryPoint = "conga_srv_start")]
    public static unsafe int CongaSrvStart(nint handle, char* name)
    {
        try
        {
            var root = HandleTable.Lookup(handle);
            if (root == null) return ErrorCodes.InvalidHandle;
            if (root.IsShuttingDown) return ErrorCodes.ShuttingDown;

            var nameStr = StringMarshaller.ReadFromPointer(name);
            if (string.IsNullOrEmpty(nameStr)) return ErrorCodes.InvalidName;

            var obj = root.Registry.Lookup(nameStr);
            if (obj == null) return ErrorCodes.InvalidName;
            if (obj is not ServerObject server) return ErrorCodes.NotServer;

            return server.Start(root);
        }
        catch { return -1; }
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
        try
        {
            var root = HandleTable.Lookup(handle);
            if (root == null) return ErrorCodes.InvalidHandle;
            if (root.IsShuttingDown) return ErrorCodes.ShuttingDown;

            var modeStr = StringMarshaller.ReadFromPointer(mode);
            if (ModeKindExtensions.TryParse(modeStr) == null)
                return ErrorCodes.InvalidMode;

            var nameStr = StringMarshaller.ReadFromPointer(name);
            var addrStr = StringMarshaller.ReadFromPointer(addr) ?? "";

            if (string.IsNullOrEmpty(nameStr))
            {
                nameStr = root.Registry.GenerateClientName();
            }
            else
            {
                if (root.Registry.Lookup(nameStr) != null)
                    return ErrorCodes.NameInUse;
            }

            var client = new ClientObject(nameStr, addrStr, port, modeStr!, bufferSize);
            if (!root.Registry.TryAdd(client))
                return ErrorCodes.NameInUse;

            return StringMarshaller.WriteToBuffer(nameStr, outName, outNameCap, outNameLen);
        }
        catch { return -1; }
    }

    [UnmanagedCallersOnly(EntryPoint = "conga_clt_connect")]
    public static unsafe int CongaCltConnect(nint handle, char* name, int timeoutMs)
    {
        try
        {
            var root = HandleTable.Lookup(handle);
            if (root == null) return ErrorCodes.InvalidHandle;
            if (root.IsShuttingDown) return ErrorCodes.ShuttingDown;

            var nameStr = StringMarshaller.ReadFromPointer(name);
            if (string.IsNullOrEmpty(nameStr)) return ErrorCodes.InvalidName;

            var obj = root.Registry.Lookup(nameStr);
            if (obj == null) return ErrorCodes.InvalidName;
            if (obj is not ClientObject client) return ErrorCodes.NotClient;

            return client.ConnectAsync(root, timeoutMs).GetAwaiter().GetResult();
        }
        catch { return -1; }
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
        try
        {
            var root = HandleTable.Lookup(handle);
            if (root == null) return ErrorCodes.InvalidHandle;

            var filter = StringMarshaller.ReadFromPointer(name);

            var evt = root.Events.Wait(filter, timeoutMs, root.ShutdownToken);

            if (evt.Type == EventType.Timeout)
                return ErrorCodes.WaitTimeout;

            if (evt.Type == EventType.Error)
                return ErrorCodes.ShuttingDown;

            // Write object name (string buffer — re-enqueue if too small)
            var objRc = StringMarshaller.WriteToBuffer(evt.ObjectName, outObj, outObjCap);
            if (objRc != ErrorCodes.Success)
            {
                root.Events.ReEnqueue(evt);
                return objRc;
            }

            // Write event name (string buffer — re-enqueue if too small)
            var evtRc = StringMarshaller.WriteToBuffer(evt.EventName, outEvent, outEventCap);
            if (evtRc != ErrorCodes.Success)
            {
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
        }
        catch { return -1; }
    }

    [UnmanagedCallersOnly(EntryPoint = "conga_send")]
    public static unsafe int CongaSend(
        nint handle,
        char* name,
        byte* data,
        int dataLen,
        byte* headers,
        int headersLen,
        int closeFlag)
    {
        try
        {
            var root = HandleTable.Lookup(handle);
            if (root == null) return ErrorCodes.InvalidHandle;
            if (root.IsShuttingDown) return ErrorCodes.ShuttingDown;

            var nameStr = StringMarshaller.ReadFromPointer(name);
            if (string.IsNullOrEmpty(nameStr)) return ErrorCodes.InvalidName;

            var (pipeline, connName, cmdName, resolveError) = ResolvePipeline(root, nameStr);
            if (resolveError != ErrorCodes.Success) return resolveError;
            if (pipeline == null) return ErrorCodes.ObjectNotReady;

            byte[]? rentedPayload = null;
            try
            {
                ReadOnlyMemory<byte> payload;
                if (dataLen > 0 && data != null)
                {
                    rentedPayload = ArrayPool<byte>.Shared.Rent(dataLen);
                    new ReadOnlySpan<byte>(data, dataLen).CopyTo(rentedPayload);
                    payload = new ReadOnlyMemory<byte>(rentedPayload, 0, dataLen);
                }
                else
                {
                    payload = ReadOnlyMemory<byte>.Empty;
                }

                byte[]? userHeaders = headersLen > 0 && headers != null
                    ? new ReadOnlySpan<byte>(headers, headersLen).ToArray()
                    : null;

                var postAction = (PostSendAction)closeFlag;
                var msg = pipeline.Mode.PrepareOutbound(connName, payload, userHeaders, postAction, cmdName);
                if (msg.ErrorCode != 0) return msg.ErrorCode;

                pipeline.SendAsync(msg).GetAwaiter().GetResult();

                HandlePostAction(root, nameStr, connName, msg.PostAction);
            }
            finally
            {
                if (rentedPayload != null)
                    ArrayPool<byte>.Shared.Return(rentedPayload);
            }

            return ErrorCodes.Success;
        }
        catch { return -1; }
    }

    [UnmanagedCallersOnly(EntryPoint = "conga_respond")]
    public static unsafe int CongaRespond(
        nint handle,
        char* name,
        byte* data,
        int dataLen)
    {
        try
        {
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
            try
            {
                ReadOnlyMemory<byte> payload;
                if (dataLen > 0 && data != null)
                {
                    rentedPayload = ArrayPool<byte>.Shared.Rent(dataLen);
                    new ReadOnlySpan<byte>(data, dataLen).CopyTo(rentedPayload);
                    payload = new ReadOnlyMemory<byte>(rentedPayload, 0, dataLen);
                }
                else
                {
                    payload = ReadOnlyMemory<byte>.Empty;
                }

                var msg = commandMode.PrepareRespond(connName, payload, cmdName);
                pipeline.SendAsync(msg).GetAwaiter().GetResult();
            }
            finally
            {
                if (rentedPayload != null)
                    ArrayPool<byte>.Shared.Return(rentedPayload);
            }

            // Close command object if registered
            root.Registry.TryRemove(nameStr, out _);

            return ErrorCodes.Success;
        }
        catch { return -1; }
    }

    [UnmanagedCallersOnly(EntryPoint = "conga_progress")]
    public static unsafe int CongaProgress(
        nint handle,
        char* name,
        byte* data,
        int dataLen)
    {
        try
        {
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
            try
            {
                ReadOnlyMemory<byte> payload;
                if (dataLen > 0 && data != null)
                {
                    rentedPayload = ArrayPool<byte>.Shared.Rent(dataLen);
                    new ReadOnlySpan<byte>(data, dataLen).CopyTo(rentedPayload);
                    payload = new ReadOnlyMemory<byte>(rentedPayload, 0, dataLen);
                }
                else
                {
                    payload = ReadOnlyMemory<byte>.Empty;
                }

                var msg = commandMode.PrepareProgress(connName, payload, cmdName);
                pipeline.SendAsync(msg).GetAwaiter().GetResult();
            }
            finally
            {
                if (rentedPayload != null)
                    ArrayPool<byte>.Shared.Return(rentedPayload);
            }

            return ErrorCodes.Success;
        }
        catch { return -1; }
    }

    /// <summary>
    /// Writes byte data to an output buffer, truncating if capacity is insufficient.
    /// Always reports the actual data length via outLen.
    /// </summary>
    private static unsafe void WriteBytesToBuffer(byte[] data, byte* buffer, int bufferCap, int* outLen)
    {
        if (outLen != null)
            *outLen = data.Length;

        if (buffer != null && bufferCap > 0 && data.Length > 0)
        {
            var copyLen = Math.Min(data.Length, bufferCap);
            data.AsSpan(0, copyLen).CopyTo(new Span<byte>(buffer, copyLen));
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
    private static (SocketPipeline? pipeline, string connName, string? cmdName, int errorCode)
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
    private static void HandlePostAction(Root root, string objectName, string connName, PostSendAction action)
    {
        switch (action)
        {
            case PostSendAction.CloseConnection:
                DisposeAndRemoveConnection(root, connName);
                break;
            case PostSendAction.CloseCommand:
                root.Registry.TryRemove(objectName, out _);
                break;
            case PostSendAction.EmitSentEvent:
                root.Events.Enqueue(new CongaEvent
                {
                    ObjectName = objectName,
                    Type = EventType.Sent
                });
                break;
        }
    }

    /// <summary>
    /// Disposes a connection's pipeline and removes it (and children) from the registry.
    /// </summary>
    private static void DisposeAndRemoveConnection(Root root, string connName)
    {
        var obj = root.Registry.Lookup(connName);
        if (obj is ConnectionObject conn && conn.Pipeline != null)
        {
            try { conn.Pipeline.DisposeAsync().GetAwaiter().GetResult(); }
            catch { }
            conn.State = ObjectState.Closed;
        }
        else if (obj is ClientObject client)
        {
            try { client.DisposeAsync().GetAwaiter().GetResult(); }
            catch { }
        }

        var removed = root.Registry.RemoveTree(connName);
        foreach (var r in removed)
            r.Parent?.TryRemoveChild(r.Name);
    }
}
