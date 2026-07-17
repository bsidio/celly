using System.Globalization;

namespace Celly.Ast;

public enum ConstantKind
{
    Null,
    Bool,
    Int,
    Uint,
    Double,
    String,
    Bytes,
}

/// <summary>A literal constant value in the AST (shape-compatible with <c>cel.expr.Constant</c>).</summary>
public readonly struct CelConstant
{
    private readonly long _int;
    private readonly ulong _uint;
    private readonly double _double;
    private readonly object? _ref; // string or byte[]

    private CelConstant(ConstantKind kind, long i = 0, ulong u = 0, double d = 0, object? r = null)
    {
        Kind = kind;
        _int = i;
        _uint = u;
        _double = d;
        _ref = r;
    }

    public ConstantKind Kind { get; }

    public bool BoolValue => _int != 0;

    public long IntValue => _int;

    public ulong UintValue => _uint;

    public double DoubleValue => _double;

    public string StringValue => (string)_ref!;

    public byte[] BytesValue => (byte[])_ref!;

    public static readonly CelConstant Null = new(ConstantKind.Null);
    public static readonly CelConstant True = new(ConstantKind.Bool, i: 1);
    public static readonly CelConstant False = new(ConstantKind.Bool, i: 0);

    public static CelConstant Of(bool value) => value ? True : False;

    public static CelConstant Of(long value) => new(ConstantKind.Int, i: value);

    public static CelConstant OfUint(ulong value) => new(ConstantKind.Uint, u: value);

    public static CelConstant Of(double value) => new(ConstantKind.Double, d: value);

    public static CelConstant Of(string value) => new(ConstantKind.String, r: value);

    public static CelConstant Of(byte[] value) => new(ConstantKind.Bytes, r: value);

    public override string ToString() => Kind switch
    {
        ConstantKind.Null => "null",
        ConstantKind.Bool => BoolValue ? "true" : "false",
        ConstantKind.Int => IntValue.ToString(CultureInfo.InvariantCulture),
        ConstantKind.Uint => UintValue.ToString(CultureInfo.InvariantCulture) + "u",
        ConstantKind.Double => DoubleValue.ToString("R", CultureInfo.InvariantCulture),
        ConstantKind.String => "\"" + StringValue + "\"",
        ConstantKind.Bytes => "b\"" + Convert.ToHexString(BytesValue) + "\"",
        _ => "?",
    };
}
