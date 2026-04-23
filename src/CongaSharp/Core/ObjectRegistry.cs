namespace CongaSharp.Core;

using System.Collections.Concurrent;

/// <summary>
/// Thread-safe registry mapping object names to CongaObject instances.
/// Handles auto-name generation (S1, S2, C1, C2, S1.CON0001, etc.)
/// </summary>
public sealed class ObjectRegistry
{
  private readonly ConcurrentDictionary<string, CongaObject> _objects = new(StringComparer.OrdinalIgnoreCase);
  private int _nextServer;
  private int _nextClient;
  private readonly ConcurrentDictionary<string, int> _nextConnection = new(StringComparer.OrdinalIgnoreCase);
  private readonly ConcurrentDictionary<string, int> _nextAuto = new(StringComparer.OrdinalIgnoreCase);

  // Preallocated static delegate to avoid per-call closure allocation in AddOrUpdate
  private static readonly Func<string, int, int> IncrementValue = static (_, old) => old + 1;

  public string GenerateServerName()
  {
    var n = Interlocked.Increment(ref _nextServer);
    return $"S{n}";
  }

  public string GenerateClientName()
  {
    var n = Interlocked.Increment(ref _nextClient);
    return $"C{n}";
  }

  public string GenerateConnectionName(string parentName)
  {
    var n = _nextConnection.AddOrUpdate(parentName, 1, IncrementValue);
    return string.Create(parentName.Length + 8, (parentName, n), static (span, state) => {
      state.parentName.AsSpan().CopyTo(span);
      span[state.parentName.Length] = '.';
      "CON".AsSpan().CopyTo(span[(state.parentName.Length + 1)..]);
      state.n.TryFormat(span[(state.parentName.Length + 4)..], out _, "D4");
    });
  }

  /// <summary>
  /// Generates a unique auto name for Send operations.
  /// Format: parentName.Auto00000000, parentName.Auto00000001, etc.
  /// Counter is per-parent and thread-safe.
  /// </summary>
  public string GenerateAutoName(string parentName)
  {
    var n = _nextAuto.AddOrUpdate(parentName, 0, IncrementValue);
    return string.Create(parentName.Length + 13, (parentName, n), static (span, state) => {
      state.parentName.AsSpan().CopyTo(span);
      span[state.parentName.Length] = '.';
      "Auto".AsSpan().CopyTo(span[(state.parentName.Length + 1)..]);
      state.n.TryFormat(span[(state.parentName.Length + 5)..], out _, "D8");
    });
  }

  public bool TryAdd(CongaObject obj)
  {
    return _objects.TryAdd(obj.Name, obj);
  }

  public bool TryRemove(string name, out CongaObject? obj)
  {
    return _objects.TryRemove(name, out obj);
  }

  public CongaObject? Lookup(string name)
  {
    _objects.TryGetValue(name, out var obj);
    return obj;
  }

  public IReadOnlyList<string> GetChildNames(string parentName)
  {
    if (parentName == "." || string.IsNullOrEmpty(parentName)) {
      return _objects.Keys.Where(k => !k.Contains('.')).ToList().AsReadOnly();
    }

    var prefix = parentName + ".";
    return _objects.Keys
        .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && !k[prefix.Length..].Contains('.'))
        .ToList().AsReadOnly();
  }

  public IReadOnlyList<CongaObject> GetAllObjects() => _objects.Values.ToList().AsReadOnly();

  /// <summary>
  /// Removes an object and all its descendants.
  /// </summary>
  public List<CongaObject> RemoveTree(string name)
  {
    var removed = new List<CongaObject>();
    var prefix = name + ".";

    // Loop to catch items added between snapshot and removal
    bool foundAny;
    do {
      foundAny = false;
      foreach (var key in _objects.Keys.ToList()) {
        if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) {
          if (_objects.TryRemove(key, out var child)) {
            removed.Add(child);
            foundAny = true;
          }
        }
      }
    } while (foundAny);

    if (_objects.TryRemove(name, out var obj))
      removed.Add(obj);

    return removed;
  }
}
