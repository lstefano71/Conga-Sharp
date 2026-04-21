namespace CongaSharp.Properties;

using CongaSharp.Core;

/// <summary>
/// Registry of all known Conga-Sharp properties with their metadata.
/// </summary>
public static class PropertyDefinitions
{
    private static readonly Dictionary<string, PropertyDefinition> _definitions = new(StringComparer.OrdinalIgnoreCase);

    static PropertyDefinitions()
    {
        // Configurable properties
        Register("EventMode", "0", false, ObjectType.Root, ObjectType.Server, ObjectType.Client);
        Register("Protocol", "\"IPv4\"", false, ObjectType.Server, ObjectType.Client);
        Register("KeepAlive", "[0,0]", false, ObjectType.Server, ObjectType.Client, ObjectType.Connection);
        Register("EOM", "[]", false, ObjectType.Server, ObjectType.Client, ObjectType.Connection);
        Register("Magic", "0", false, ObjectType.Server, ObjectType.Client);
        Register("Pause", "0", false, ObjectType.Root, ObjectType.Server, ObjectType.Client, ObjectType.Connection);
        Register("BufferSize", "16384", false, ObjectType.Server, ObjectType.Client, ObjectType.Connection);
        Register("ConnectionOnly", "0", false, ObjectType.Server);
        Register("ReadyStrategy", "\"auto\"", false, ObjectType.Server, ObjectType.Client);
        Register("Trace", "0", false, ObjectType.Root);
        Register("TraceFile", "\"\"", false, ObjectType.Root);
        Register("TCPLookup", "\"auto\"", false, ObjectType.Server, ObjectType.Client);

        // Read-only properties
        Register("LocalAddr", "null", true, ObjectType.Server, ObjectType.Client, ObjectType.Connection);
        Register("PeerAddr", "null", true, ObjectType.Connection);
        Register("LocalPort", "0", true, ObjectType.Server);
        Register("PropList", "null", true, ObjectType.Root, ObjectType.Server, ObjectType.Client, ObjectType.Connection, ObjectType.Command);
    }

    private static void Register(string name, string defaultJson, bool readOnly, params ObjectType[] applicableTo)
    {
        _definitions[name] = new PropertyDefinition(name, defaultJson, readOnly, applicableTo);
    }

    public static PropertyDefinition? Get(string name)
    {
        _definitions.TryGetValue(name, out var def);
        return def;
    }

    public static IReadOnlyCollection<string> AllNames => _definitions.Keys;

    public static IEnumerable<string> GetApplicableNames(ObjectType type)
    {
        return _definitions.Values
            .Where(d => d.ApplicableTo.Contains(type))
            .Select(d => d.Name);
    }
}
