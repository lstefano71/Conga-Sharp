namespace CongaSharp.Core;

using System.Collections.Concurrent;

/// <summary>
/// Thread-safe registry mapping object names to CongaObject instances.
/// Handles auto-name generation (SRV00000000, CLT00000000, SRV00000000.CON00000000, etc.)
/// </summary>
public sealed class ObjectRegistry
{
  private readonly ConcurrentDictionary<string, CongaObject> _objects = new(StringComparer.OrdinalIgnoreCase);
  private int _nextServer = -1;
  private int _nextClient = -1;
  private readonly ConcurrentDictionary<string, int> _nextConnection = new(StringComparer.OrdinalIgnoreCase);
  private readonly ConcurrentDictionary<string, int> _nextAuto = new(StringComparer.OrdinalIgnoreCase);

  // Preallocated static delegate to avoid per-call closure allocation in AddOrUpdate
  private static readonly Func<string, int, int> IncrementValue = static (_, old) => old + 1;

  private static int GetPaddedDecimalWidth(int value, int minimumDigits)
  {
    if (value < 0)
      throw new ArgumentOutOfRangeException(nameof(value));

    var digits =
        value >= 1_000_000_000 ? 10 :
        value >= 100_000_000 ? 9 :
        value >= 10_000_000 ? 8 :
        value >= 1_000_000 ? 7 :
        value >= 100_000 ? 6 :
        value >= 10_000 ? 5 :
        value >= 1_000 ? 4 :
        value >= 100 ? 3 :
        value >= 10 ? 2 : 1;

    return Math.Max(digits, minimumDigits);
  }

  public string GenerateServerName()
  {
    var n = Interlocked.Increment(ref _nextServer);
    return $"SRV{n:D8}";
  }

  public string GenerateClientName()
  {
    var n = Interlocked.Increment(ref _nextClient);
    return $"CLT{n:D8}";
  }

  public string GenerateConnectionName(string parentName)
  {
    var n = _nextConnection.AddOrUpdate(parentName, 0, IncrementValue);
    return $"{parentName}.CON{n:D8}";
  }

  /// <summary>
  /// Generates a unique auto name for Send operations.
  /// Format: parentName.Auto00000000, parentName.Auto00000001, etc.
  /// Counter is per-parent and thread-safe.
  /// </summary>
  public string GenerateAutoName(string parentName)
  {
    var n = _nextAuto.AddOrUpdate(parentName, 0, IncrementValue);
    var width = GetPaddedDecimalWidth(n, 8);
    return string.Create(parentName.Length + 5 + width, (parentName, n), static (span, state) => {
      state.parentName.AsSpan().CopyTo(span);
      span[state.parentName.Length] = '.';
      "Auto".AsSpan().CopyTo(span[(state.parentName.Length + 1)..]);
      if (!state.n.TryFormat(span[(state.parentName.Length + 5)..], out _, "D8"))
        throw new InvalidOperationException("Failed to format auto-generated name.");
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
