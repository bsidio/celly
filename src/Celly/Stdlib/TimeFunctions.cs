using System.Globalization;
using System.Numerics;
using Celly.Values;

namespace Celly.Stdlib;

/// <summary>Timestamp/Duration conversions and accessors (langdef.md "Timestamps and Durations").</summary>
public static class TimeFunctions
{
    // ---- duration(string): Go time.ParseDuration compatible ------------------------------------

    private static readonly (string Suffix, long Nanos)[] Units =
    [
        ("ns", 1L),
        ("us", 1_000L),
        ("µs", 1_000L),
        ("μs", 1_000L),
        ("ms", 1_000_000L),
        ("s", 1_000_000_000L),
        ("m", 60_000_000_000L),
        ("h", 3_600_000_000_000L),
    ];

    public static CelValue ParseDuration(string s)
    {
        // Go grammar: [-+]? ( number unit )+ where number = digits[.digits]; "0" alone is valid.
        var original = s;
        var negative = false;
        if (s.StartsWith('-') || s.StartsWith('+'))
        {
            negative = s[0] == '-';
            s = s[1..];
        }

        if (s == "0")
        {
            return DurationValue.Of(0, 0);
        }

        if (s.Length == 0)
        {
            return Invalid(original);
        }

        BigInteger totalNanos = 0;
        var i = 0;
        while (i < s.Length)
        {
            var numStart = i;
            while (i < s.Length && char.IsAsciiDigit(s[i]))
            {
                i++;
            }

            var intDigits = s[numStart..i];
            var fracDigits = string.Empty;
            if (i < s.Length && s[i] == '.')
            {
                i++;
                var fracStart = i;
                while (i < s.Length && char.IsAsciiDigit(s[i]))
                {
                    i++;
                }

                fracDigits = s[fracStart..i];
            }

            if (intDigits.Length == 0 && fracDigits.Length == 0)
            {
                return Invalid(original);
            }

            // Two-letter units are listed before "s"/"m"/"h", so ordering resolves prefixes.
            var matched = Units.FirstOrDefault(u => s.AsSpan(i).StartsWith(u.Suffix, StringComparison.Ordinal));
            if (matched.Suffix is null)
            {
                return Invalid(original);
            }

            i += matched.Suffix.Length;

            var whole = intDigits.Length == 0 ? BigInteger.Zero : BigInteger.Parse(intDigits, CultureInfo.InvariantCulture);
            totalNanos += whole * matched.Nanos;
            if (fracDigits.Length > 0)
            {
                // fraction * unitNanos with truncation, computed exactly in integers.
                var frac = BigInteger.Parse(fracDigits, CultureInfo.InvariantCulture);
                var scale = BigInteger.Pow(10, fracDigits.Length);
                totalNanos += frac * matched.Nanos / scale;
            }
        }

        if (negative)
        {
            totalNanos = -totalNanos;
        }

        return DurationValue.OfNanos(totalNanos);
    }

    private static ErrorValue Invalid(string s) => new($"invalid duration: '{s}'");

    /// <summary>string(duration): decimal seconds with trailing-zero-trimmed fraction + "s" (cel-go format).</summary>
    public static string FormatDuration(CelDurationData d)
    {
        var negative = d.Seconds < 0 || d.Nanos < 0;
        var seconds = Math.Abs(d.Seconds);
        var nanos = Math.Abs(d.Nanos);
        var result = seconds.ToString(CultureInfo.InvariantCulture);
        if (nanos > 0)
        {
            result += "." + nanos.ToString("D9", CultureInfo.InvariantCulture).TrimEnd('0');
        }

        return (negative ? "-" : string.Empty) + result + "s";
    }

    public static CelValue ParseTimestamp(string s)
    {
        var parsed = Rfc3339.Parse(s);
        return parsed is { } data
            ? new TimestampValue(data)
            : new ErrorValue($"cannot parse timestamp: '{s}'");
    }

    // ---- accessors -------------------------------------------------------------------------------

    public static CelValue TimestampAccessor(TimestampValue ts, string function, string? timezone)
    {
        var offsetSeconds = 0L;
        if (timezone is not null && timezone.Length > 0 && !string.Equals(timezone, "UTC", StringComparison.Ordinal))
        {
            var resolved = ResolveOffsetSeconds(timezone, ts.Data.Seconds);
            if (resolved is null)
            {
                return new ErrorValue($"unknown timezone: '{timezone}'");
            }

            offsetSeconds = resolved.Value;
        }

        var local = ts.Data.Seconds + offsetSeconds;
        var (year, month, day, hour, minute, second) = Rfc3339.EpochSecondsToCivil(local);
        var days = Math.DivRem(local, 86400, out var rem);
        if (rem < 0)
        {
            days--;
        }

        return function switch
        {
            "getFullYear" => IntValue.Of(year),
            "getMonth" => IntValue.Of(month - 1), // 0-based
            "getDate" => IntValue.Of(day), // 1-based
            "getDayOfMonth" => IntValue.Of(day - 1), // 0-based
            "getDayOfWeek" => IntValue.Of((int)(((days + 4) % 7 + 7) % 7)), // epoch day 0 = Thursday; 0 = Sunday
            "getDayOfYear" => IntValue.Of(days - Rfc3339.DaysFromCivil(year, 1, 1)), // 0-based
            "getHours" => IntValue.Of(hour),
            "getMinutes" => IntValue.Of(minute),
            "getSeconds" => IntValue.Of(second),
            "getMilliseconds" => IntValue.Of(ts.Data.Nanos / 1_000_000),
            _ => ErrorValue.NoSuchOverload(),
        };
    }

    public static CelValue DurationAccessor(DurationValue d, string function)
    {
        var totalNanos = d.TotalNanos;
        return function switch
        {
            "getHours" => IntValue.Of((long)(totalNanos / 3_600_000_000_000L)),
            "getMinutes" => IntValue.Of((long)(totalNanos / 60_000_000_000L)),
            "getSeconds" => IntValue.Of((long)(totalNanos / 1_000_000_000L)),
            // Milliseconds is the sub-second COMPONENT, unlike the whole-unit accessors above.
            "getMilliseconds" => IntValue.Of((long)(totalNanos % 1_000_000_000 / 1_000_000)),
            _ => ErrorValue.NoSuchOverload(),
        };
    }

    /// <summary>
    /// Resolves a timezone argument to a UTC offset (in seconds) at the given instant:
    /// fixed "(+|-)HH:MM" forms directly, otherwise IANA names via TimeZoneInfo.
    /// </summary>
    private static long? ResolveOffsetSeconds(string timezone, long epochSeconds)
    {
        // Fixed offsets: "(+|-)HH:MM" and the unsigned "HH:MM" form.
        if (timezone.StartsWith('+') || timezone.StartsWith('-') || (timezone.Length > 0 && char.IsAsciiDigit(timezone[0])))
        {
            var sign = timezone[0] == '-' ? -1 : 1;
            var body = timezone[0] is '+' or '-' ? timezone[1..] : timezone;
            var colon = body.IndexOf(':');
            if (colon > 0
                && int.TryParse(body[..colon], NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
                && int.TryParse(body[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
                && hours <= 23 && minutes <= 59)
            {
                return sign * (hours * 3600L + minutes * 60L);
            }

            return null;
        }


        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(timezone);
            // Clamp to DateTimeOffset's range for offset lookup; fine for accessor purposes.
            var clamped = Math.Clamp(epochSeconds, -62135596800, 253402300799);
            var instant = DateTimeOffset.FromUnixTimeSeconds(clamped);
            return (long)tz.GetUtcOffset(instant).TotalSeconds;
        }
        catch (TimeZoneNotFoundException)
        {
            return null;
        }
        catch (InvalidTimeZoneException)
        {
            return null;
        }
    }
}
