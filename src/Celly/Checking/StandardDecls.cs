using Celly.Common;
using Celly.Types;

namespace Celly.Checking;

/// <summary>
/// The standard CEL environment declarations (langdef.md "Standard environment"). Signatures are
/// homogeneous per the spec — cross-type numeric comparison is a runtime capability reached
/// through dyn, not a declared overload set.
/// </summary>
public static class StandardDecls
{
    private static readonly CelType A = CelType.TypeParam("A");
    private static readonly CelType B = CelType.TypeParam("B");
    private static readonly CelType Bool = CelType.Bool;
    private static readonly CelType Int = CelType.Int;
    private static readonly CelType Uint = CelType.Uint;
    private static readonly CelType Dbl = CelType.Double;
    private static readonly CelType Str = CelType.String;
    private static readonly CelType Byt = CelType.Bytes;
    private static readonly CelType Ts = CelType.Timestamp;
    private static readonly CelType Dur = CelType.Duration;
    private static readonly CelType Dyn = CelType.Dyn;

    private static CelType ListOf(CelType t) => CelType.List(t);

    private static CelType MapOf(CelType k, CelType v) => CelType.Map(k, v);

    private static CelType TypeOf(CelType t) => new(CelTypeKind.Type, "type", [t]);

    public static Dictionary<string, FunctionDecl> CreateFunctions()
    {
        var functions = new Dictionary<string, FunctionDecl>(StringComparer.Ordinal);

        void Fn(string name, params OverloadDecl[] overloads) =>
            functions[name] = new FunctionDecl(name, overloads);

        OverloadDecl Global(string id, CelType[] args, CelType result) => new(id, args, result);
        OverloadDecl Member(string id, CelType[] args, CelType result) => new(id, args, result, isInstance: true);

        // Logic.
        Fn(Operators.Conditional, Global("conditional", [Bool, A, A], A));
        Fn(Operators.LogicalAnd, Global("logical_and", [Bool, Bool], Bool));
        Fn(Operators.LogicalOr, Global("logical_or", [Bool, Bool], Bool));
        Fn(Operators.LogicalNot, Global("logical_not", [Bool], Bool));
        Fn(Operators.NotStrictlyFalse, Global("not_strictly_false", [Bool], Bool));

        // Equality.
        Fn(Operators.Equals, Global("equals", [A, A], Bool));
        Fn(Operators.NotEquals, Global("not_equals", [A, A], Bool));

        // Ordering (homogeneous).
        foreach (var (op, prefix) in ((string, string)[])
        [
            (Operators.Less, "less"),
            (Operators.LessEquals, "less_equals"),
            (Operators.Greater, "greater"),
            (Operators.GreaterEquals, "greater_equals"),
        ])
        {
            Fn(
                op,
                Global($"{prefix}_bool", [Bool, Bool], Bool),
                Global($"{prefix}_int64", [Int, Int], Bool),
                Global($"{prefix}_uint64", [Uint, Uint], Bool),
                Global($"{prefix}_double", [Dbl, Dbl], Bool),
                Global($"{prefix}_string", [Str, Str], Bool),
                Global($"{prefix}_bytes", [Byt, Byt], Bool),
                Global($"{prefix}_timestamp", [Ts, Ts], Bool),
                Global($"{prefix}_duration", [Dur, Dur], Bool));
        }

        // Arithmetic.
        Fn(
            Operators.Add,
            Global("add_int64", [Int, Int], Int),
            Global("add_uint64", [Uint, Uint], Uint),
            Global("add_double", [Dbl, Dbl], Dbl),
            Global("add_string", [Str, Str], Str),
            Global("add_bytes", [Byt, Byt], Byt),
            Global("add_list", [ListOf(A), ListOf(A)], ListOf(A)),
            Global("add_timestamp_duration", [Ts, Dur], Ts),
            Global("add_duration_timestamp", [Dur, Ts], Ts),
            Global("add_duration_duration", [Dur, Dur], Dur));
        Fn(
            Operators.Subtract,
            Global("subtract_int64", [Int, Int], Int),
            Global("subtract_uint64", [Uint, Uint], Uint),
            Global("subtract_double", [Dbl, Dbl], Dbl),
            Global("subtract_timestamp_timestamp", [Ts, Ts], Dur),
            Global("subtract_timestamp_duration", [Ts, Dur], Ts),
            Global("subtract_duration_duration", [Dur, Dur], Dur));
        Fn(
            Operators.Multiply,
            Global("multiply_int64", [Int, Int], Int),
            Global("multiply_uint64", [Uint, Uint], Uint),
            Global("multiply_double", [Dbl, Dbl], Dbl));
        Fn(
            Operators.Divide,
            Global("divide_int64", [Int, Int], Int),
            Global("divide_uint64", [Uint, Uint], Uint),
            Global("divide_double", [Dbl, Dbl], Dbl));
        Fn(
            Operators.Modulo,
            Global("modulo_int64", [Int, Int], Int),
            Global("modulo_uint64", [Uint, Uint], Uint));
        Fn(
            Operators.Negate,
            Global("negate_int64", [Int], Int),
            Global("negate_double", [Dbl], Dbl));

        // Index and membership.
        Fn(
            Operators.Index,
            Global("index_list", [ListOf(A), Int], A),
            Global("index_map", [MapOf(A, B), A], B));
        Fn(
            Operators.In,
            Global("in_list", [A, ListOf(A)], Bool),
            Global("in_map", [A, MapOf(A, B)], Bool));

        // size.
        Fn(
            "size",
            Global("size_string", [Str], Int),
            Global("size_bytes", [Byt], Int),
            Global("size_list", [ListOf(A)], Int),
            Global("size_map", [MapOf(A, B)], Int),
            Member("string_size", [Str], Int),
            Member("bytes_size", [Byt], Int),
            Member("list_size", [ListOf(A)], Int),
            Member("map_size", [MapOf(A, B)], Int));

        // Reflection.
        Fn("type", Global("type", [A], TypeOf(A)));
        Fn("dyn", Global("to_dyn", [A], Dyn));

        // Conversions.
        Fn(
            "bool",
            Global("bool_to_bool", [Bool], Bool),
            Global("string_to_bool", [Str], Bool));
        Fn(
            "bytes",
            Global("bytes_to_bytes", [Byt], Byt),
            Global("string_to_bytes", [Str], Byt));
        Fn(
            "double",
            Global("double_to_double", [Dbl], Dbl),
            Global("int64_to_double", [Int], Dbl),
            Global("uint64_to_double", [Uint], Dbl),
            Global("string_to_double", [Str], Dbl));
        Fn(
            "int",
            Global("int64_to_int64", [Int], Int),
            Global("uint64_to_int64", [Uint], Int),
            Global("double_to_int64", [Dbl], Int),
            Global("string_to_int64", [Str], Int),
            Global("timestamp_to_int64", [Ts], Int));
        Fn(
            "uint",
            Global("uint64_to_uint64", [Uint], Uint),
            Global("int64_to_uint64", [Int], Uint),
            Global("double_to_uint64", [Dbl], Uint),
            Global("string_to_uint64", [Str], Uint));
        Fn(
            "string",
            Global("string_to_string", [Str], Str),
            Global("bool_to_string", [Bool], Str),
            Global("int64_to_string", [Int], Str),
            Global("uint64_to_string", [Uint], Str),
            Global("double_to_string", [Dbl], Str),
            Global("bytes_to_string", [Byt], Str),
            Global("timestamp_to_string", [Ts], Str),
            Global("duration_to_string", [Dur], Str));
        Fn(
            "timestamp",
            Global("timestamp_to_timestamp", [Ts], Ts),
            Global("string_to_timestamp", [Str], Ts),
            Global("int64_to_timestamp", [Int], Ts));
        Fn(
            "duration",
            Global("duration_to_duration", [Dur], Dur),
            Global("string_to_duration", [Str], Dur));

        // String tests.
        Fn("contains", Member("contains_string", [Str, Str], Bool));
        Fn("startsWith", Member("starts_with_string", [Str, Str], Bool));
        Fn("endsWith", Member("ends_with_string", [Str, Str], Bool));
        Fn(
            "matches",
            Global("matches", [Str, Str], Bool),
            Member("matches_string", [Str, Str], Bool));

        // Temporal accessors.
        foreach (var (name, hasDurationForm) in ((string, bool)[])
        [
            ("getFullYear", false), ("getMonth", false), ("getDate", false), ("getDayOfMonth", false),
            ("getDayOfWeek", false), ("getDayOfYear", false),
            ("getHours", true), ("getMinutes", true), ("getSeconds", true), ("getMilliseconds", true),
        ])
        {
            var overloads = new List<OverloadDecl>
            {
                Member($"timestamp_{name}", [Ts], Int),
                Member($"timestamp_{name}_with_tz", [Ts, Str], Int),
            };
            if (hasDurationForm)
            {
                overloads.Add(Member($"duration_{name}", [Dur], Int));
            }

            Fn(name, [.. overloads]);
        }

        return functions;
    }

    /// <summary>Standard identifier declarations: the type identifiers.</summary>
    public static IEnumerable<VariableDecl> CreateIdents()
    {
        yield return new VariableDecl("bool", TypeOf(Bool));
        yield return new VariableDecl("int", TypeOf(Int));
        yield return new VariableDecl("uint", TypeOf(Uint));
        yield return new VariableDecl("double", TypeOf(Dbl));
        yield return new VariableDecl("string", TypeOf(Str));
        yield return new VariableDecl("bytes", TypeOf(Byt));
        yield return new VariableDecl("list", TypeOf(CelType.ListDyn));
        yield return new VariableDecl("map", TypeOf(CelType.MapDyn));
        yield return new VariableDecl("null_type", TypeOf(CelType.Null));
        yield return new VariableDecl("type", TypeOf(CelType.TypeType));
        yield return new VariableDecl("google.protobuf.Timestamp", TypeOf(Ts));
        yield return new VariableDecl("google.protobuf.Duration", TypeOf(Dur));
    }

}
