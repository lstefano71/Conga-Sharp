namespace CongaSharp.Core;

using System.Collections.Concurrent;
using CongaSharp.Properties;

/// <summary>
/// Base class for all Conga-Sharp objects (Server, Client, Connection, Command).
/// </summary>
public abstract class CongaObject
{
    public string Name { get; }
    public ObjectType Type { get; }
    public CongaObject? Parent { get; }
    private int _state = (int)ObjectState.Created;

    public ObjectState State
    {
        get => (ObjectState)Volatile.Read(ref _state);
        protected internal set => Volatile.Write(ref _state, (int)value);
    }

    /// <summary>
    /// Atomically transitions from one state to another. Returns true if successful.
    /// </summary>
    protected bool TryTransition(ObjectState from, ObjectState to)
        => Interlocked.CompareExchange(ref _state, (int)to, (int)from) == (int)from;
    public PropertyStore Properties { get; }

    private readonly ConcurrentDictionary<string, CongaObject> _children = new(StringComparer.OrdinalIgnoreCase);

    protected CongaObject(string name, ObjectType type, CongaObject? parent)
    {
        Name = name;
        Type = type;
        Parent = parent;
        Properties = new PropertyStore(type);
    }

    public IReadOnlyCollection<CongaObject> Children => _children.Values.ToList().AsReadOnly();

    public bool TryAddChild(CongaObject child) => _children.TryAdd(child.Name, child);

    public bool TryRemoveChild(string name) => _children.TryRemove(name, out _);

    public CongaObject? FindChild(string name)
    {
        _children.TryGetValue(name, out var child);
        return child;
    }

    public IReadOnlyList<string> ChildNames => _children.Keys.ToList().AsReadOnly();
}
