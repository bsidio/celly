using Celly.Types;

namespace Celly.Values;

/// <summary>An evaluation error as a value. Error messages follow cel-go's canonical texts.</summary>
public sealed class ErrorValue(string message) : CelValue
{
    public string Message { get; } = message;

    public override CelType Type => CelType.Error;

    public override bool EqualTo(CelValue other) => false;

    public override object? ToNative() => new InvalidOperationException(Message);

    public override string ToString() => $"error: {Message}";

    public static ErrorValue NoSuchOverload() => new("no such overload");

    public static ErrorValue DivideByZero() => new("divide by zero");

    public static ErrorValue ModulusByZero() => new("modulus by zero");

    public static ErrorValue Overflow() => new("return error for overflow");

    public static ErrorValue NoSuchKey(CelValue key) => new($"no such key: {key.ToNative()}");

    public static ErrorValue NoSuchAttribute(string name) => new($"no such attribute(s): {name}");

    public static ErrorValue IndexOutOfRange(long index) => new($"index out of range: {index}");
}

/// <summary>
/// An unknown value: the set of expression ids whose attributes were declared unknown. Produced by
/// partial evaluation (M6); merged, not erroring, through logic operators.
/// </summary>
public sealed class UnknownValue(IReadOnlyList<long> exprIds) : CelValue
{
    public IReadOnlyList<long> ExprIds { get; } = exprIds;

    public override CelType Type => CelType.Unknown;

    public override bool EqualTo(CelValue other) => false;

    public override object? ToNative() => ExprIds;

    public static UnknownValue Merge(UnknownValue a, UnknownValue b)
    {
        var ids = new List<long>(a.ExprIds);
        foreach (var id in b.ExprIds)
        {
            if (!ids.Contains(id))
            {
                ids.Add(id);
            }
        }

        return new UnknownValue(ids);
    }
}
