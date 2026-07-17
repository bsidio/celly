using System.Globalization;
using System.Text;
using Celly.Values;

namespace Celly.Stdlib;

/// <summary>The standard type-conversion functions (langdef.md "Standard definitions").</summary>
public static class Conversions
{
    private const double TwoPow63 = 9223372036854775808.0;
    private const double TwoPow64 = 18446744073709551616.0;

    public static CelValue ToBool(CelValue value) => value switch
    {
        BoolValue => value,
        // Go strconv.ParseBool set: 1, t, T, TRUE, true, True, 0, f, F, FALSE, false, False.
        StringValue s => s.Value switch
        {
            "1" or "t" or "T" or "TRUE" or "true" or "True" => BoolValue.True,
            "0" or "f" or "F" or "FALSE" or "false" or "False" => BoolValue.False,
            _ => new ErrorValue($"cannot convert string to bool: '{s.Value}'"),
        },
        _ => ErrorValue.NoSuchOverload(),
    };

    public static CelValue ToBytes(CelValue value) => value switch
    {
        BytesValue => value,
        StringValue s => BytesValue.Of(Encoding.UTF8.GetBytes(s.Value)),
        _ => ErrorValue.NoSuchOverload(),
    };

    public static CelValue ToDouble(CelValue value) => value switch
    {
        DoubleValue => value,
        IntValue i => DoubleValue.Of(i.Value),
        UintValue u => DoubleValue.Of(u.Value),
        StringValue s => ParseDouble(s.Value),
        _ => ErrorValue.NoSuchOverload(),
    };

    private static CelValue ParseDouble(string s)
    {
        // Go ParseFloat compatibility: no whitespace, optional sign, inf/infinity/nan spellings.
        var body = s.StartsWith('+') || s.StartsWith('-') ? s[1..] : s;
        if (body.Equals("inf", StringComparison.OrdinalIgnoreCase)
            || body.Equals("infinity", StringComparison.OrdinalIgnoreCase))
        {
            return DoubleValue.Of(s.StartsWith('-') ? double.NegativeInfinity : double.PositiveInfinity);
        }

        if (body.Equals("nan", StringComparison.OrdinalIgnoreCase))
        {
            return DoubleValue.Of(double.NaN);
        }

        const NumberStyles styles = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent;
        if (double.TryParse(s, styles, CultureInfo.InvariantCulture, out var d))
        {
            return DoubleValue.Of(d);
        }

        return new ErrorValue($"cannot convert string to double: '{s}'");
    }

    public static CelValue ToInt(CelValue value) => value switch
    {
        IntValue => value,
        UintValue u => u.Value > long.MaxValue ? RangeError() : IntValue.Of((long)u.Value),
        DoubleValue d => DoubleToInt(d.Value),
        StringValue s => long.TryParse(s.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var i)
            ? IntValue.Of(i)
            : new ErrorValue($"cannot convert string to int: '{s.Value}'"),
        TimestampValue ts => IntValue.Of(ts.Data.Seconds),
        // Strong-enum mode only (legacy enums already are ints).
        EnumValue e => IntValue.Of(e.Number),
        _ => ErrorValue.NoSuchOverload(),
    };

    private static CelValue DoubleToInt(double d)
    {
        // Truncation toward zero; error outside the range. Both bounds are exclusive per the
        // conformance suite: int(-9223372036854775808.0) is a range error even though the value
        // is representable — matching cel-go's canonical behavior.
        if (double.IsNaN(d) || d >= TwoPow63 || d <= -TwoPow63)
        {
            return RangeError();
        }

        return IntValue.Of((long)d);
    }

    public static CelValue ToUint(CelValue value) => value switch
    {
        UintValue => value,
        IntValue i => i.Value < 0 ? RangeError() : UintValue.Of((ulong)i.Value),
        DoubleValue d => DoubleToUint(d.Value),
        StringValue s => ulong.TryParse(s.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var u)
            ? UintValue.Of(u)
            : new ErrorValue($"cannot convert string to uint: '{s.Value}'"),
        _ => ErrorValue.NoSuchOverload(),
    };

    private static CelValue DoubleToUint(double d)
    {
        // Any negative (except -0.0, which compares == 0) is a range error, matching cel-go.
        if (double.IsNaN(d) || d >= TwoPow64 || d < 0.0)
        {
            return RangeError();
        }

        return UintValue.Of((ulong)d);
    }

    public static CelValue ToString(CelValue value) => value switch
    {
        StringValue => value,
        BoolValue b => StringValue.Of(b.Value ? "true" : "false"),
        IntValue i => StringValue.Of(i.Value.ToString(CultureInfo.InvariantCulture)),
        UintValue u => StringValue.Of(u.Value.ToString(CultureInfo.InvariantCulture)),
        DoubleValue d => StringValue.Of(GoDoubleFormatter.Format(d.Value)),
        BytesValue b => b.DecodeUtf8(),
        TimestampValue ts => StringValue.Of(Rfc3339.Format(ts.Data)),
        DurationValue d => StringValue.Of(TimeFunctions.FormatDuration(d.Data)),
        _ => ErrorValue.NoSuchOverload(),
    };

    private static ErrorValue RangeError() => new("range error");
}
