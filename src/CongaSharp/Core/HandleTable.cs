using System.Collections.Concurrent;

namespace CongaSharp.Core;

/// <summary>
/// Maps opaque nint handles to Root instances. Thread-safe.
/// Handles are exposed to the C API as uintptr_t (P type in ⎕NA).
/// </summary>
public static class HandleTable
{
  private static readonly ConcurrentDictionary<nint, Root> _handles = new();
  private static long _nextHandle = 1; // 0 reserved for "invalid"

  public static nint Allocate(Root root)
  {
    var handle = (nint)Interlocked.Increment(ref _nextHandle);
    if (!_handles.TryAdd(handle, root))
      throw new InvalidOperationException("Handle collision");
    return handle;
  }

  public static Root? Lookup(nint handle)
  {
    _handles.TryGetValue(handle, out var root);
    return root;
  }

  public static bool Free(nint handle)
  {
    return _handles.TryRemove(handle, out _);
  }

  // For testing only — clears entries but keeps counter monotonic to avoid
  // collisions when multiple test classes run in parallel.
  internal static void Reset()
  {
    _handles.Clear();
  }
}
