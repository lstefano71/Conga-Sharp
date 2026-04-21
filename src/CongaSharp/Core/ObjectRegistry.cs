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
        var n = _nextConnection.AddOrUpdate(parentName, 1, (_, old) => old + 1);
        return $"{parentName}.CON{n:D4}";
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
        if (parentName == "." || string.IsNullOrEmpty(parentName))
        {
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

        foreach (var key in _objects.Keys.ToList())
        {
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                if (_objects.TryRemove(key, out var child))
                    removed.Add(child);
            }
        }

        if (_objects.TryRemove(name, out var obj))
            removed.Add(obj);

        return removed;
    }
}
