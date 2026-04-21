namespace CongaSharp.Properties;

using System.Collections.Concurrent;
using CongaSharp.Core;
using CongaSharp.Errors;

/// <summary>
/// Per-object property storage. Stores JSON string values.
/// Falls back to PropertyDefinitions for defaults.
/// </summary>
public sealed class PropertyStore
{
    private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObjectType _objectType;

    public PropertyStore(ObjectType objectType)
    {
        _objectType = objectType;
    }

    /// <summary>
    /// Sets a property value. Returns an error code if the property is unknown or read-only.
    /// </summary>
    public int Set(string name, string jsonValue)
    {
        var def = PropertyDefinitions.Get(name);
        if (def == null)
            return ErrorCodes.InvalidProperty;
        if (def.ReadOnly)
            return ErrorCodes.PropertyReadOnly;
        if (!def.ApplicableTo.Contains(_objectType))
            return ErrorCodes.InvalidProperty;

        if (string.IsNullOrWhiteSpace(jsonValue))
            return ErrorCodes.InvalidJson;

        _values[name] = jsonValue;
        return ErrorCodes.Success;
    }

    /// <summary>
    /// Gets a property value. Returns the overridden value, or the default.
    /// For PropList, returns the list of applicable property names.
    /// </summary>
    public int Get(string name, out string jsonValue)
    {
        // Special case: PropList returns list of applicable property names
        if (name.Equals("PropList", StringComparison.OrdinalIgnoreCase))
        {
            var names = PropertyDefinitions.GetApplicableNames(_objectType);
            jsonValue = "[" + string.Join(",", names.Select(n => $"\"{n}\"")) + "]";
            return ErrorCodes.Success;
        }

        var def = PropertyDefinitions.Get(name);
        if (def == null)
        {
            jsonValue = "";
            return ErrorCodes.InvalidProperty;
        }

        if (!def.ApplicableTo.Contains(_objectType))
        {
            jsonValue = "";
            return ErrorCodes.InvalidProperty;
        }

        jsonValue = _values.TryGetValue(name, out var val) ? val : def.DefaultJson;
        return ErrorCodes.Success;
    }

    /// <summary>
    /// Sets a read-only property value (for internal use, e.g. LocalAddr after bind).
    /// </summary>
    public void SetInternal(string name, string jsonValue)
    {
        _values[name] = jsonValue;
    }

    /// <summary>
    /// Returns all properties as a JSON object string.
    /// </summary>
    public string ToJson()
    {
        var applicableNames = PropertyDefinitions.GetApplicableNames(_objectType).ToList();
        var pairs = new List<string>();
        foreach (var name in applicableNames)
        {
            Get(name, out var val);
            pairs.Add($"\"{name}\":{val}");
        }
        return "{" + string.Join(",", pairs) + "}";
    }
}
