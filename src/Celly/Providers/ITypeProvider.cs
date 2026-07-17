using Celly.Types;
using Celly.Values;

namespace Celly.Providers;

/// <summary>
/// Resolves struct (message) types, their fields, and provider-defined identifiers (enum
/// constants, message type names). Implemented by Celly.Protobuf's ProtoTypeRegistry; the core
/// package has no protobuf dependency.
/// </summary>
public interface ITypeProvider
{
    /// <summary>The CEL type for a struct name (well-known protos map to wrapper/timestamp/… types), or null.</summary>
    CelType? FindStructType(string name);

    /// <summary>The CEL type of a field, or null when the type or field is unknown.</summary>
    CelType? FindStructFieldType(string messageName, string fieldName);

    /// <summary>Provider identifiers: enum constants (int values) and message type names (type values).</summary>
    CelValue? FindIdent(string name);

    /// <summary>Constructs a message. Field values are pre-evaluated; returns ErrorValue on failure.</summary>
    CelValue NewValue(string messageName, IReadOnlyList<KeyValuePair<string, CelValue>> fields);
}

/// <summary>A structured value supporting field access (protobuf messages).</summary>
public interface IStructValue
{
    CelValue GetField(string name);

    /// <summary>has() semantics: BoolValue, or ErrorValue for unknown fields.</summary>
    CelValue HasField(string name);
}

/// <summary>The no-op provider used when no protobuf (or other) type support is registered.</summary>
public sealed class EmptyTypeProvider : ITypeProvider
{
    public static readonly EmptyTypeProvider Instance = new();

    public CelType? FindStructType(string name) => null;

    public CelType? FindStructFieldType(string messageName, string fieldName) => null;

    public CelValue? FindIdent(string name) => null;

    public CelValue NewValue(string messageName, IReadOnlyList<KeyValuePair<string, CelValue>> fields) =>
        new ErrorValue($"unknown type: '{messageName}'");
}
