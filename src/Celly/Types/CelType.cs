namespace Celly.Types;

public enum CelTypeKind
{
    Dyn,
    Null,
    Bool,
    Int,
    Uint,
    Double,
    String,
    Bytes,
    List,
    Map,
    Struct,
    Type,
    TypeParam,
    Opaque,
    Timestamp,
    Duration,
    Error,
    Unknown,
}

/// <summary>
/// The single CEL type model, shared by the checker and the runtime (a value's runtime type is a
/// <see cref="CelType"/>, and <c>type(x)</c> yields it as a first-class value). Primitive types are
/// singletons so identity comparisons like <c>type(1) == int</c> hold structurally.
/// </summary>
public class CelType
{
    private static readonly IReadOnlyList<CelType> NoParams = [];

    public CelType(CelTypeKind kind, string name, IReadOnlyList<CelType>? parameters = null)
    {
        Kind = kind;
        Name = name;
        Parameters = parameters ?? NoParams;
    }

    public CelTypeKind Kind { get; }

    /// <summary>The runtime type name (what <c>string(type(x))</c>-style comparisons key on).</summary>
    public string Name { get; }

    public IReadOnlyList<CelType> Parameters { get; }

    // ---- singletons ------------------------------------------------------------------------------

    public static readonly CelType Dyn = new(CelTypeKind.Dyn, "dyn");
    public static readonly CelType Null = new(CelTypeKind.Null, "null_type");
    public static readonly CelType Bool = new(CelTypeKind.Bool, "bool");
    public static readonly CelType Int = new(CelTypeKind.Int, "int");
    public static readonly CelType Uint = new(CelTypeKind.Uint, "uint");
    public static readonly CelType Double = new(CelTypeKind.Double, "double");
    public static readonly CelType String = new(CelTypeKind.String, "string");
    public static readonly CelType Bytes = new(CelTypeKind.Bytes, "bytes");
    public static readonly CelType Timestamp = new(CelTypeKind.Timestamp, "google.protobuf.Timestamp");
    public static readonly CelType Duration = new(CelTypeKind.Duration, "google.protobuf.Duration");
    public static readonly CelType Error = new(CelTypeKind.Error, "error");
    public static readonly CelType Unknown = new(CelTypeKind.Unknown, "unknown");

    /// <summary>The type of type values themselves.</summary>
    public static readonly CelType TypeType = new(CelTypeKind.Type, "type");

    /// <summary>Unparameterized list/map types (the runtime type of every list/map value).</summary>
    public static readonly CelType ListDyn = new(CelTypeKind.List, "list", [Dyn]);
    public static readonly CelType MapDyn = new(CelTypeKind.Map, "map", [Dyn, Dyn]);

    public static readonly CelType OptionalDyn = new(CelTypeKind.Opaque, "optional_type", [Dyn]);

    // ---- constructors ----------------------------------------------------------------------------

    public static CelType List(CelType element) => new(CelTypeKind.List, "list", [element]);

    public static CelType Map(CelType key, CelType value) => new(CelTypeKind.Map, "map", [key, value]);

    public static CelType Struct(string messageName) => new(CelTypeKind.Struct, messageName);

    public static CelType TypeParam(string name) => new(CelTypeKind.TypeParam, name);

    public static CelType Optional(CelType inner) => new(CelTypeKind.Opaque, "optional_type", [inner]);

    public static CelType Opaque(string name, params CelType[] parameters) =>
        new(CelTypeKind.Opaque, name, parameters);

    // ---- equality --------------------------------------------------------------------------------

    /// <summary>
    /// Runtime type equality: kind and name (parameters intentionally ignored — the runtime type of
    /// every list is <c>list</c>). The checker performs structural comparisons separately.
    /// </summary>
    public bool RuntimeEquals(CelType other) => Kind == other.Kind && Name == other.Name;

    public override string ToString() =>
        Parameters.Count == 0 ? Name : $"{Name}({string.Join(", ", Parameters)})";
}
