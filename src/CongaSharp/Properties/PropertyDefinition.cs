namespace CongaSharp.Properties;

using CongaSharp.Core;

/// <summary>
/// Defines a known property: name, JSON value type, default, read-only flag, applicable object types.
/// </summary>
public sealed class PropertyDefinition
{
    public string Name { get; }
    public string DefaultJson { get; }
    public bool ReadOnly { get; }
    public ObjectType[] ApplicableTo { get; }

    public PropertyDefinition(string name, string defaultJson, bool readOnly, params ObjectType[] applicableTo)
    {
        Name = name;
        DefaultJson = defaultJson;
        ReadOnly = readOnly;
        ApplicableTo = applicableTo;
    }
}
