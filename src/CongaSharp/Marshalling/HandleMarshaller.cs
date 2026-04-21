namespace CongaSharp.Marshalling;

using CongaSharp.Core;
using CongaSharp.Errors;

/// <summary>
/// Validates and resolves nint handles to Root instances.
/// </summary>
public static class HandleMarshaller
{
    /// <summary>
    /// Looks up a Root by handle. Returns null if handle is zero or not found.
    /// </summary>
    public static Root? Resolve(nint handle)
    {
        if (handle == nint.Zero) return null;
        return HandleTable.Lookup(handle);
    }

    /// <summary>
    /// Resolves handle and returns error code if invalid.
    /// </summary>
    public static int TryResolve(nint handle, out Root? root)
    {
        root = Resolve(handle);
        return root == null ? ErrorCodes.InvalidHandle : ErrorCodes.Success;
    }
}
