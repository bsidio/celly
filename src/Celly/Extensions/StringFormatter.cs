using System.Globalization;
using System.Text;
using Celly.Stdlib;
using Celly.Values;

namespace Celly.Extensions;

/// <summary>
/// string.format() clause handling: %s %d %f %e %x %X %o %b %% with optional precision for
/// %f/%e, mirroring cel-go's formatting extension.
/// </summary>
public static class StringFormatter
{
    public static CelValue Format(string template, IReadOnlyList<CelValue> args)
    {
        var sb = new StringBuilder(template.Length);
        var argIndex = 0;
        var i = 0;
        while (i < template.Length)
        {
            var c = template[i];
            if (c != '%')
            {
                sb.Append(c);
                i++;
                continue;
            }

            i++;
            if (i >= template.Length)
            {
                return new ErrorValue("could not parse formatting clause: unterminated clause");
            }

            if (template[i] == '%')
            {
                sb.Append('%');
                i++;
                continue;
            }

            var precision = -1;
            if (template[i] == '.')
            {
                i++;
                var start = i;
                while (i < template.Length && char.IsAsciiDigit(template[i]))
                {
                    i++;
                }

                if (i == start || i >= template.Length)
                {
                    return new ErrorValue("could not parse formatting clause: invalid precision");
                }

                if (!int.TryParse(template[start..i], NumberStyles.None, CultureInfo.InvariantCulture, out precision)
                    || precision > 512)
                {
                    // Cap precision: hostile "%.999999999f" must not allocate unbounded output.
                    return new ErrorValue("could not parse formatting clause: precision out of range");
                }
            }

            var verb = template[i];
            i++;
            if (argIndex >= args.Count)
            {
                return new ErrorValue($"index {argIndex} out of range");
            }

            var arg = args[argIndex++];
            var formatted = verb switch
            {
                's' => FormatValue(arg),
                'd' => FormatDecimal(arg),
                'f' => FormatFixed(arg, precision < 0 ? 6 : precision),
                'e' => FormatScientific(arg, precision < 0 ? 6 : precision),
                'x' => FormatHex(arg, upper: false),
                'X' => FormatHex(arg, upper: true),
                'o' => FormatOctal(arg),
                'b' => FormatBinary(arg),
                _ => new ErrorValue($"could not parse formatting clause: unrecognized formatting clause: '{verb}'"),
            };
            if (formatted is ErrorValue error)
            {
                return error;
            }

            sb.Append(((StringValue)formatted).Value);
        }

        return StringValue.Of(sb.ToString());
    }

    private static CelValue FormatValue(CelValue value)
    {
        switch (value)
        {
            case StringValue s:
                return s;
            case BoolValue b:
                return StringValue.Of(b.Value ? "true" : "false");
            case IntValue i:
                return StringValue.Of(i.Value.ToString(CultureInfo.InvariantCulture));
            case UintValue u:
                return StringValue.Of(u.Value.ToString(CultureInfo.InvariantCulture));
            case DoubleValue d:
                // format() spells infinities out, unlike string(double)'s +Inf/-Inf.
                return StringValue.Of(double.IsPositiveInfinity(d.Value) ? "Infinity"
                    : double.IsNegativeInfinity(d.Value) ? "-Infinity"
                    : GoDoubleFormatter.Format(d.Value));
            case BytesValue by:
                return StringValue.Of(Encoding.UTF8.GetString(by.Span));
            case NullValue:
                return StringValue.Of("null");
            case TypeValue t:
                return StringValue.Of(t.Value.Name);
            case TimestampValue ts:
                return StringValue.Of(Rfc3339.Format(ts.Data));
            case DurationValue dur:
                return StringValue.Of(TimeFunctions.FormatDuration(dur.Data));
            case OptionalValue opt:
                if (!opt.HasValue)
                {
                    return StringValue.Of("optional.none()");
                }

                return FormatValue(opt.Value) is StringValue inner
                    ? StringValue.Of($"optional.of({inner.Value})")
                    : new ErrorValue("could not format optional value");
            case ListValue list:
            {
                var parts = new List<string>(list.Elements.Count);
                foreach (var element in list.Elements)
                {
                    var formatted = FormatValue(element);
                    if (formatted is ErrorValue error)
                    {
                        return error;
                    }

                    parts.Add(((StringValue)formatted).Value);
                }

                return StringValue.Of("[" + string.Join(", ", parts) + "]");
            }

            case MapValue map:
            {
                var entries = new List<(string Key, string Value)>(map.Count);
                foreach (var key in map.Keys)
                {
                    map.TryGet(key, out var entryValue);
                    var keyFormatted = FormatValue(key);
                    if (keyFormatted is ErrorValue keyError)
                    {
                        return keyError;
                    }

                    var valueFormatted = FormatValue(entryValue);
                    if (valueFormatted is ErrorValue valueError)
                    {
                        return valueError;
                    }

                    entries.Add((((StringValue)keyFormatted).Value, ((StringValue)valueFormatted).Value));
                }

                entries.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
                return StringValue.Of("{" + string.Join(", ", entries.Select(e => $"{e.Key}: {e.Value}")) + "}");
            }

            default:
                return new ErrorValue($"could not convert argument of type {value.Type.Name} to string");
        }
    }

    private static CelValue FormatDecimal(CelValue value) => value switch
    {
        IntValue i => StringValue.Of(i.Value.ToString(CultureInfo.InvariantCulture)),
        UintValue u => StringValue.Of(u.Value.ToString(CultureInfo.InvariantCulture)),
        // Non-finite doubles are allowed through %d with their spelled-out names.
        DoubleValue d when double.IsNaN(d.Value) => StringValue.Of("NaN"),
        DoubleValue d when double.IsPositiveInfinity(d.Value) => StringValue.Of("Infinity"),
        DoubleValue d when double.IsNegativeInfinity(d.Value) => StringValue.Of("-Infinity"),
        _ => new ErrorValue("error during formatting: integer clause can only be used on integers"),
    };

    private static CelValue FormatFixed(CelValue value, int precision)
    {
        double d;
        switch (value)
        {
            case DoubleValue dv:
                d = dv.Value;
                break;
            case IntValue iv:
                d = iv.Value;
                break;
            case UintValue uv:
                d = uv.Value;
                break;
            default:
                return new ErrorValue("error during formatting: fixed-point clause can only be used on numbers");
        }

        if (double.IsNaN(d))
        {
            return StringValue.Of("NaN");
        }

        if (double.IsInfinity(d))
        {
            return StringValue.Of(d > 0 ? "Infinity" : "-Infinity");
        }

        return StringValue.Of(d.ToString("F" + precision, CultureInfo.InvariantCulture));
    }

    private static CelValue FormatScientific(CelValue value, int precision)
    {
        double d;
        switch (value)
        {
            case DoubleValue dv:
                d = dv.Value;
                break;
            case IntValue iv:
                d = iv.Value;
                break;
            case UintValue uv:
                d = uv.Value;
                break;
            default:
                return new ErrorValue("error during formatting: scientific clause can only be used on numbers");
        }

        if (double.IsNaN(d))
        {
            return StringValue.Of("NaN");
        }

        if (double.IsInfinity(d))
        {
            return StringValue.Of(d > 0 ? "Infinity" : "-Infinity");
        }

        // Go-style %e: lowercase 'e', two-or-more exponent digits.
        var pattern = "0." + new string('0', Math.Max(precision, 0)) + "e+00";
        if (precision == 0)
        {
            pattern = "0e+00";
        }

        return StringValue.Of(d.ToString(pattern, CultureInfo.InvariantCulture));
    }

    private static CelValue FormatHex(CelValue value, bool upper) => value switch
    {
        IntValue i => StringValue.Of(HexOfLong(i.Value, upper)),
        UintValue u => StringValue.Of(upper
            ? u.Value.ToString("X", CultureInfo.InvariantCulture)
            : u.Value.ToString("x", CultureInfo.InvariantCulture)),
        StringValue s => StringValue.Of(HexOfBytes(Encoding.UTF8.GetBytes(s.Value), upper)),
        BytesValue by => StringValue.Of(HexOfBytes(by.Span, upper)),
        _ => new ErrorValue("error during formatting: only integers, byte buffers, and strings can be formatted as hex"),
    };

    private static string HexOfLong(long value, bool upper)
    {
        // Negative ints format as -<magnitude-hex>, matching Go's %x.
        var magnitude = value < 0 ? (ulong)(-(value + 1)) + 1 : (ulong)value;
        var hex = magnitude.ToString(upper ? "X" : "x", CultureInfo.InvariantCulture);
        return value < 0 ? "-" + hex : hex;
    }

    private static string HexOfBytes(ReadOnlySpan<byte> bytes, bool upper)
    {
        var hex = Convert.ToHexString(bytes);
        return upper ? hex : hex.ToLowerInvariant();
    }

    private static CelValue FormatOctal(CelValue value) => value switch
    {
        IntValue i => StringValue.Of(SignedInBase(i.Value, 8)),
        UintValue u => StringValue.Of(ConvertUnsigned(u.Value, 8)),
        _ => new ErrorValue("error during formatting: octal clause can only be used on integers"),
    };

    private static CelValue FormatBinary(CelValue value) => value switch
    {
        BoolValue b => StringValue.Of(b.Value ? "1" : "0"),
        IntValue i => StringValue.Of(SignedInBase(i.Value, 2)),
        UintValue u => StringValue.Of(ConvertUnsigned(u.Value, 2)),
        _ => new ErrorValue("error during formatting: only integers and bools can be formatted as binary"),
    };

    private static string SignedInBase(long value, int toBase)
    {
        // Magnitude-safe for long.MinValue.
        var magnitude = value < 0 ? (ulong)(-(value + 1)) + 1 : (ulong)value;
        return (value < 0 ? "-" : string.Empty) + ConvertUnsigned(magnitude, toBase);
    }

    private static string ConvertUnsigned(ulong value, int toBase)
    {
        if (value == 0)
        {
            return "0";
        }

        var sb = new StringBuilder();
        while (value > 0)
        {
            var digit = (int)(value % (ulong)toBase);
            sb.Insert(0, (char)(digit < 10 ? '0' + digit : 'a' + digit - 10));
            value /= (ulong)toBase;
        }

        return sb.ToString();
    }
}
