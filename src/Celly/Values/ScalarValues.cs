using Celly.Types;

namespace Celly.Values;

public sealed class NullValue : CelValue
{
    public static readonly NullValue Instance = new();

    private NullValue()
    {
    }

    public override CelType Type => CelType.Null;

    public override bool EqualTo(CelValue other) => other is NullValue;

    public override object? ToNative() => null;

    public override string ToString() => "null";
}

public sealed class BoolValue : CelValue, IComparableValue
{
    public static readonly BoolValue True = new(true);
    public static readonly BoolValue False = new(false);

    private BoolValue(bool value) => Value = value;

    public bool Value { get; }

    public static BoolValue Of(bool value) => value ? True : False;

    public override CelType Type => CelType.Bool;

    public override bool EqualTo(CelValue other) => other is BoolValue b && b.Value == Value;

    public override object ToNative() => Value;

    public CelValue CompareTo(CelValue other) =>
        other is BoolValue b ? IntValue.OfComparison(Value.CompareTo(b.Value)) : ErrorValue.NoSuchOverload();

    public override string ToString() => Value ? "true" : "false";
}

public sealed class IntValue : CelValue, IComparableValue, IAdder, ISubtractor, IMultiplier, IDivider, IModder, INegater
{
    private static readonly IntValue[] Cache = CreateCache();

    public IntValue(long value) => Value = value;

    public long Value { get; }

    public static IntValue Of(long value) =>
        value is >= -1 and <= 64 ? Cache[value + 1] : new IntValue(value);

    internal static IntValue OfComparison(int cmp) => Of(cmp < 0 ? -1 : cmp > 0 ? 1 : 0);

    private static IntValue[] CreateCache()
    {
        var cache = new IntValue[66];
        for (var i = 0; i < cache.Length; i++)
        {
            cache[i] = new IntValue(i - 1);
        }

        return cache;
    }

    public override CelType Type => CelType.Int;

    public override bool EqualTo(CelValue other) => other switch
    {
        IntValue i => Value == i.Value,
        UintValue u => NumericCompare.Compare(Value, u.Value) == 0,
        DoubleValue d => NumericCompare.Compare(Value, d.Value) == 0,
        _ => false,
    };

    public override object ToNative() => Value;

    public CelValue CompareTo(CelValue other)
    {
        int? cmp = other switch
        {
            IntValue i => NumericCompare.Compare(Value, i.Value),
            UintValue u => NumericCompare.Compare(Value, u.Value),
            DoubleValue d => NumericCompare.Compare(Value, d.Value),
            _ => null,
        };
        if (cmp is null)
        {
            return other is DoubleValue
                ? new ErrorValue("NaN values cannot be ordered")
                : ErrorValue.NoSuchOverload();
        }

        return OfComparison(cmp.Value);
    }

    public CelValue Add(CelValue other)
    {
        if (other is not IntValue rhs)
        {
            return ErrorValue.NoSuchOverload();
        }

        try
        {
            return Of(checked(Value + rhs.Value));
        }
        catch (OverflowException)
        {
            return ErrorValue.Overflow();
        }
    }

    public CelValue Subtract(CelValue other)
    {
        if (other is not IntValue rhs)
        {
            return ErrorValue.NoSuchOverload();
        }

        try
        {
            return Of(checked(Value - rhs.Value));
        }
        catch (OverflowException)
        {
            return ErrorValue.Overflow();
        }
    }

    public CelValue Multiply(CelValue other)
    {
        if (other is not IntValue rhs)
        {
            return ErrorValue.NoSuchOverload();
        }

        try
        {
            return Of(checked(Value * rhs.Value));
        }
        catch (OverflowException)
        {
            return ErrorValue.Overflow();
        }
    }

    public CelValue Divide(CelValue other)
    {
        if (other is not IntValue rhs)
        {
            return ErrorValue.NoSuchOverload();
        }

        if (rhs.Value == 0)
        {
            return ErrorValue.DivideByZero();
        }

        if (Value == long.MinValue && rhs.Value == -1)
        {
            return ErrorValue.Overflow();
        }

        return Of(Value / rhs.Value);
    }

    public CelValue Modulo(CelValue other)
    {
        if (other is not IntValue rhs)
        {
            return ErrorValue.NoSuchOverload();
        }

        if (rhs.Value == 0)
        {
            return ErrorValue.ModulusByZero();
        }

        if (Value == long.MinValue && rhs.Value == -1)
        {
            return ErrorValue.Overflow();
        }

        return Of(Value % rhs.Value);
    }

    public CelValue Negate() =>
        Value == long.MinValue ? ErrorValue.Overflow() : Of(-Value);

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public sealed class UintValue(ulong value) : CelValue, IComparableValue, IAdder, ISubtractor, IMultiplier, IDivider, IModder
{
    public ulong Value { get; } = value;

    public static UintValue Of(ulong value) => new(value);

    public override CelType Type => CelType.Uint;

    public override bool EqualTo(CelValue other) => other switch
    {
        UintValue u => Value == u.Value,
        IntValue i => NumericCompare.Compare(Value, i.Value) == 0,
        DoubleValue d => NumericCompare.Compare(Value, d.Value) == 0,
        _ => false,
    };

    public override object ToNative() => Value;

    public CelValue CompareTo(CelValue other)
    {
        int? cmp = other switch
        {
            UintValue u => NumericCompare.Compare(Value, u.Value),
            IntValue i => NumericCompare.Compare(Value, i.Value),
            DoubleValue d => NumericCompare.Compare(Value, d.Value),
            _ => null,
        };
        if (cmp is null)
        {
            return other is DoubleValue
                ? new ErrorValue("NaN values cannot be ordered")
                : ErrorValue.NoSuchOverload();
        }

        return IntValue.OfComparison(cmp.Value);
    }

    public CelValue Add(CelValue other)
    {
        if (other is not UintValue rhs)
        {
            return ErrorValue.NoSuchOverload();
        }

        try
        {
            return Of(checked(Value + rhs.Value));
        }
        catch (OverflowException)
        {
            return ErrorValue.Overflow();
        }
    }

    public CelValue Subtract(CelValue other)
    {
        if (other is not UintValue rhs)
        {
            return ErrorValue.NoSuchOverload();
        }

        try
        {
            return Of(checked(Value - rhs.Value));
        }
        catch (OverflowException)
        {
            return ErrorValue.Overflow();
        }
    }

    public CelValue Multiply(CelValue other)
    {
        if (other is not UintValue rhs)
        {
            return ErrorValue.NoSuchOverload();
        }

        try
        {
            return Of(checked(Value * rhs.Value));
        }
        catch (OverflowException)
        {
            return ErrorValue.Overflow();
        }
    }

    public CelValue Divide(CelValue other)
    {
        if (other is not UintValue rhs)
        {
            return ErrorValue.NoSuchOverload();
        }

        return rhs.Value == 0 ? ErrorValue.DivideByZero() : Of(Value / rhs.Value);
    }

    public CelValue Modulo(CelValue other)
    {
        if (other is not UintValue rhs)
        {
            return ErrorValue.NoSuchOverload();
        }

        return rhs.Value == 0 ? ErrorValue.ModulusByZero() : Of(Value % rhs.Value);
    }

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + "u";
}

public sealed class DoubleValue(double value) : CelValue, IComparableValue, IAdder, ISubtractor, IMultiplier, IDivider, INegater
{
    public double Value { get; } = value;

    public static DoubleValue Of(double value) => new(value);

    public override CelType Type => CelType.Double;

    public override bool EqualTo(CelValue other) => other switch
    {
        // IEEE ==: NaN equals nothing, -0.0 == 0.0.
        DoubleValue d => Value == d.Value,
        IntValue i => NumericCompare.Compare(Value, i.Value) == 0,
        UintValue u => NumericCompare.Compare(Value, u.Value) == 0,
        _ => false,
    };

    public override object ToNative() => Value;

    public CelValue CompareTo(CelValue other)
    {
        int? cmp = other switch
        {
            DoubleValue d => NumericCompare.Compare(Value, d.Value),
            IntValue i => NumericCompare.Compare(Value, i.Value),
            UintValue u => NumericCompare.Compare(Value, u.Value),
            _ => null,
        };
        if (cmp is null)
        {
            return other is DoubleValue or IntValue or UintValue
                ? new ErrorValue("NaN values cannot be ordered")
                : ErrorValue.NoSuchOverload();
        }

        return IntValue.OfComparison(cmp.Value);
    }

    public CelValue Add(CelValue other) =>
        other is DoubleValue rhs ? Of(Value + rhs.Value) : ErrorValue.NoSuchOverload();

    public CelValue Subtract(CelValue other) =>
        other is DoubleValue rhs ? Of(Value - rhs.Value) : ErrorValue.NoSuchOverload();

    public CelValue Multiply(CelValue other) =>
        other is DoubleValue rhs ? Of(Value * rhs.Value) : ErrorValue.NoSuchOverload();

    public CelValue Divide(CelValue other) =>
        other is DoubleValue rhs ? Of(Value / rhs.Value) : ErrorValue.NoSuchOverload();

    public CelValue Negate() => Of(-Value);

    public override string ToString() => Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>A first-class type value (the result of <c>type(x)</c>).</summary>
public sealed class TypeValue(CelType value) : CelValue
{
    public CelType Value { get; } = value;

    public override CelType Type => CelType.TypeType;

    public override bool EqualTo(CelValue other) => other is TypeValue t && t.Value.RuntimeEquals(Value);

    public override object ToNative() => Value;

    public override string ToString() => Value.Name;
}
