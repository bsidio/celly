using System.Globalization;
using System.Text;
using Celly.Values;

namespace Celly.Stdlib;

/// <summary>
/// RFC 3339 timestamp parsing and formatting with full nanosecond precision (0–9 fractional
/// digits). Hand-written because DateTimeOffset's "o" round-trip format is fixed at 7 digits
/// (100ns ticks) and cannot represent nanos.
/// </summary>
public static class Rfc3339
{
    /// <summary>Parses an RFC 3339 timestamp; returns null when malformed or out of the CEL range.</summary>
    public static CelTimestampData? Parse(string s)
    {
        // yyyy-MM-ddTHH:mm:ss[.fraction](Z|±HH:MM)
        if (s.Length < 20)
        {
            return null;
        }

        if (!TryDigits(s, 0, 4, out var year) || s[4] != '-'
            || !TryDigits(s, 5, 2, out var month) || s[7] != '-'
            || !TryDigits(s, 8, 2, out var day)
            || (s[10] != 'T' && s[10] != 't')
            || !TryDigits(s, 11, 2, out var hour) || s[13] != ':'
            || !TryDigits(s, 14, 2, out var minute) || s[16] != ':'
            || !TryDigits(s, 17, 2, out var second))
        {
            return null;
        }

        var pos = 19;
        var nanos = 0;
        if (pos < s.Length && s[pos] == '.')
        {
            pos++;
            var start = pos;
            while (pos < s.Length && char.IsAsciiDigit(s[pos]))
            {
                pos++;
            }

            var digits = pos - start;
            if (digits is < 1 or > 9)
            {
                return null;
            }

            for (var i = start; i < pos; i++)
            {
                nanos = nanos * 10 + (s[i] - '0');
            }

            for (var i = digits; i < 9; i++)
            {
                nanos *= 10;
            }
        }

        if (pos >= s.Length)
        {
            return null;
        }

        long offsetSeconds;
        if (s[pos] is 'Z' or 'z')
        {
            offsetSeconds = 0;
            pos++;
        }
        else if (s[pos] is '+' or '-')
        {
            var sign = s[pos] == '-' ? -1 : 1;
            pos++;
            if (pos + 5 > s.Length
                || !TryDigits(s, pos, 2, out var offH) || s[pos + 2] != ':'
                || !TryDigits(s, pos + 3, 2, out var offM)
                || offH > 23 || offM > 59)
            {
                return null;
            }

            offsetSeconds = sign * (offH * 3600L + offM * 60L);
            pos += 5;
        }
        else
        {
            return null;
        }

        if (pos != s.Length)
        {
            return null;
        }

        if (month is < 1 or > 12 || day < 1 || day > DaysInMonth(year, month)
            || hour > 23 || minute > 59 || second > 59 || year < 1 || year > 9999)
        {
            return null;
        }

        var epochSeconds = CivilToEpochSeconds(year, month, day, hour, minute, second) - offsetSeconds;
        var data = new CelTimestampData(epochSeconds, nanos);
        return data.InRange ? data : null;
    }

    public static string Format(CelTimestampData ts)
    {
        var (year, month, day, hour, minute, second) = EpochSecondsToCivil(ts.Seconds);
        var sb = new StringBuilder(30);
        sb.Append(year.ToString("D4", CultureInfo.InvariantCulture)).Append('-')
          .Append(month.ToString("D2", CultureInfo.InvariantCulture)).Append('-')
          .Append(day.ToString("D2", CultureInfo.InvariantCulture)).Append('T')
          .Append(hour.ToString("D2", CultureInfo.InvariantCulture)).Append(':')
          .Append(minute.ToString("D2", CultureInfo.InvariantCulture)).Append(':')
          .Append(second.ToString("D2", CultureInfo.InvariantCulture));
        if (ts.Nanos > 0)
        {
            var frac = ts.Nanos.ToString("D9", CultureInfo.InvariantCulture).TrimEnd('0');
            sb.Append('.').Append(frac);
        }

        return sb.Append('Z').ToString();
    }

    // ---- civil-time math (proleptic Gregorian; Howard Hinnant's algorithms) ----------------------

    public static long CivilToEpochSeconds(int year, int month, int day, int hour, int minute, int second) =>
        DaysFromCivil(year, month, day) * 86400L + hour * 3600L + minute * 60L + second;

    public static (int Year, int Month, int Day, int Hour, int Minute, int Second) EpochSecondsToCivil(long seconds)
    {
        var days = Math.DivRem(seconds, 86400, out var rem);
        if (rem < 0)
        {
            days--;
            rem += 86400;
        }

        var (y, m, d) = CivilFromDays(days);
        return (y, m, d, (int)(rem / 3600), (int)(rem % 3600 / 60), (int)(rem % 60));
    }

    public static long DaysFromCivil(int y, int m, int d)
    {
        y -= m <= 2 ? 1 : 0;
        var era = (y >= 0 ? y : y - 399) / 400;
        var yoe = y - era * 400;
        var doy = (153 * (m + (m > 2 ? -3 : 9)) + 2) / 5 + d - 1;
        var doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
        return era * 146097L + doe - 719468;
    }

    public static (int Year, int Month, int Day) CivilFromDays(long z)
    {
        z += 719468;
        var era = (z >= 0 ? z : z - 146096) / 146097;
        var doe = z - era * 146097;
        var yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365;
        var y = yoe + era * 400;
        var doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
        var mp = (5 * doy + 2) / 153;
        var d = (int)(doy - (153 * mp + 2) / 5 + 1);
        var m = (int)(mp + (mp < 10 ? 3 : -9));
        return ((int)(y + (m <= 2 ? 1 : 0)), m, d);
    }

    public static bool IsLeapYear(int year) => year % 4 == 0 && (year % 100 != 0 || year % 400 == 0);

    public static int DaysInMonth(int year, int month) => month switch
    {
        1 or 3 or 5 or 7 or 8 or 10 or 12 => 31,
        4 or 6 or 9 or 11 => 30,
        2 => IsLeapYear(year) ? 29 : 28,
        _ => 0,
    };

    private static bool TryDigits(string s, int start, int count, out int value)
    {
        value = 0;
        if (start + count > s.Length)
        {
            return false;
        }

        for (var i = start; i < start + count; i++)
        {
            if (!char.IsAsciiDigit(s[i]))
            {
                return false;
            }

            value = value * 10 + (s[i] - '0');
        }

        return true;
    }
}
