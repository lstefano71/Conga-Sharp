using System.Runtime.InteropServices;
using CongaSharp.Core;
using CongaSharp.Errors;
using CongaSharp.Marshalling;
using CongaSharp.Properties;

namespace CongaSharp;

/// <summary>
/// C-callable exports for the Conga-Sharp library.
/// All functions use [UnmanagedCallersOnly] for NativeAOT export.
/// </summary>
public static partial class NativeExports
{
    private const string Version = "0.1.0";

    [UnmanagedCallersOnly(EntryPoint = "conga_version")]
    public static unsafe int CongaVersion(char* outVersion, int versionCap)
    {
        try
        {
            // +1 for null terminator
            int requiredLen = Version.Length + 1;
            if (versionCap < requiredLen)
            {
                return ErrorCodes.BufferTooSmall;
            }

            for (int i = 0; i < Version.Length; i++)
            {
                outVersion[i] = Version[i];
            }
            outVersion[Version.Length] = '\0';

            return ErrorCodes.Success;
        }
        catch
        {
            return -1;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "conga_init")]
    public static unsafe int CongaInit(nint* outHandle)
    {
        try
        {
            var root = new Root();
            var handle = HandleTable.Allocate(root);
            root.Handle = handle;
            *outHandle = handle;
            return ErrorCodes.Success;
        }
        catch
        {
            return -1;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "conga_shutdown")]
    public static int CongaShutdown(nint handle)
    {
        try
        {
            var root = HandleTable.Lookup(handle);
            if (root == null)
                return ErrorCodes.InvalidHandle;

            root.Shutdown();
            HandleTable.Free(handle);
            root.Dispose();
            return ErrorCodes.Success;
        }
        catch
        {
            return -1;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "conga_close")]
    public static unsafe int CongaClose(nint handle, char* name)
    {
        try
        {
            var root = HandleTable.Lookup(handle);
            if (root == null) return ErrorCodes.InvalidHandle;

            var objName = StringMarshaller.ReadFromPointer(name);
            if (string.IsNullOrEmpty(objName)) return ErrorCodes.InvalidName;

            var removed = root.Registry.RemoveTree(objName);
            if (removed.Count == 0) return ErrorCodes.InvalidName;

            return ErrorCodes.Success;
        }
        catch { return -1; }
    }

    [UnmanagedCallersOnly(EntryPoint = "conga_names")]
    public static unsafe int CongaNames(nint handle, char* obj, char* outJson, int outJsonCap, int* outJsonLen)
    {
        try
        {
            var root = HandleTable.Lookup(handle);
            if (root == null) return ErrorCodes.InvalidHandle;

            var objName = StringMarshaller.ReadFromPointer(obj) ?? ".";
            var names = root.Registry.GetChildNames(objName);

            var json = "[" + string.Join(",", names.Select(n => $"\"{EscapeJson(n)}\"")) + "]";
            return StringMarshaller.WriteToBuffer(json, outJson, outJsonCap, outJsonLen);
        }
        catch { return -1; }
    }

    [UnmanagedCallersOnly(EntryPoint = "conga_tree")]
    public static unsafe int CongaTree(nint handle, char* obj, char* outJson, int outJsonCap, int* outJsonLen)
    {
        try
        {
            var root = HandleTable.Lookup(handle);
            if (root == null) return ErrorCodes.InvalidHandle;

            var objName = StringMarshaller.ReadFromPointer(obj) ?? ".";
            var json = BuildTreeJson(root.Registry, objName);
            return StringMarshaller.WriteToBuffer(json, outJson, outJsonCap, outJsonLen);
        }
        catch { return -1; }
    }

    [UnmanagedCallersOnly(EntryPoint = "conga_describe")]
    public static unsafe int CongaDescribe(nint handle, char* obj, char* outJson, int outJsonCap, int* outJsonLen)
    {
        try
        {
            var root = HandleTable.Lookup(handle);
            if (root == null) return ErrorCodes.InvalidHandle;

            var objName = StringMarshaller.ReadFromPointer(obj) ?? ".";

            string json;
            if (objName == ".")
            {
                json = root.Properties.ToJson();
            }
            else
            {
                var congaObj = root.Registry.Lookup(objName);
                if (congaObj == null) return ErrorCodes.InvalidName;
                json = congaObj.Properties.ToJson();
            }

            return StringMarshaller.WriteToBuffer(json, outJson, outJsonCap, outJsonLen);
        }
        catch { return -1; }
    }

    [UnmanagedCallersOnly(EntryPoint = "conga_setprop")]
    public static unsafe int CongaSetProp(nint handle, char* obj, char* prop, char* jsonValue)
    {
        try
        {
            var root = HandleTable.Lookup(handle);
            if (root == null) return ErrorCodes.InvalidHandle;

            var objName = StringMarshaller.ReadFromPointer(obj) ?? ".";
            var propName = StringMarshaller.ReadFromPointer(prop);
            var value = StringMarshaller.ReadFromPointer(jsonValue);
            if (string.IsNullOrEmpty(propName) || value == null)
                return ErrorCodes.InvalidProperty;

            PropertyStore store;
            if (objName == ".")
            {
                store = root.Properties;
            }
            else
            {
                var congaObj = root.Registry.Lookup(objName);
                if (congaObj == null) return ErrorCodes.InvalidName;
                store = congaObj.Properties;
            }

            var rc = store.Set(propName, value);

            // Special handling for Trace/TraceFile on root
            if (rc == ErrorCodes.Success && objName == ".")
            {
                if (propName.Equals("Trace", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(value, out var level) && level >= 0 && level <= 4)
                        root.Trace.Level = (Diagnostics.TraceLevel)level;
                }
                else if (propName.Equals("TraceFile", StringComparison.OrdinalIgnoreCase))
                {
                    var path = value.Trim('"');
                    root.Trace.FilePath = string.IsNullOrEmpty(path) ? null : path;
                }
            }

            return rc;
        }
        catch { return -1; }
    }

    [UnmanagedCallersOnly(EntryPoint = "conga_getprop")]
    public static unsafe int CongaGetProp(nint handle, char* obj, char* prop, char* outJson, int outJsonCap, int* outJsonLen)
    {
        try
        {
            var root = HandleTable.Lookup(handle);
            if (root == null) return ErrorCodes.InvalidHandle;

            var objName = StringMarshaller.ReadFromPointer(obj) ?? ".";
            var propName = StringMarshaller.ReadFromPointer(prop);
            if (string.IsNullOrEmpty(propName)) return ErrorCodes.InvalidProperty;

            PropertyStore store;
            if (objName == ".")
            {
                store = root.Properties;
            }
            else
            {
                var congaObj = root.Registry.Lookup(objName);
                if (congaObj == null) return ErrorCodes.InvalidName;
                store = congaObj.Properties;
            }

            var rc = store.Get(propName, out var jsonResult);
            if (rc != ErrorCodes.Success) return rc;

            return StringMarshaller.WriteToBuffer(jsonResult, outJson, outJsonCap, outJsonLen);
        }
        catch { return -1; }
    }

    private static string BuildTreeJson(ObjectRegistry registry, string name)
    {
        var type = "Root";
        var obj = registry.Lookup(name);
        if (obj != null) type = obj.Type.ToString();

        var children = registry.GetChildNames(name);
        var childrenJson = string.Join(",", children.Select(c => BuildTreeJson(registry, c)));
        return $"{{\"name\":\"{EscapeJson(name)}\",\"type\":\"{type}\",\"children\":[{childrenJson}]}}";
    }

    private static string EscapeJson(string s)
    {
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
