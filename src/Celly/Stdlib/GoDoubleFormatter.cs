using System.Globalization;
using System.Text;

namespace Celly.Stdlib;

/// <summary>
/// Formats doubles the way Go's <c>strconv.FormatFloat(f, 'g', -1, 64)</c> does — shortest
/// round-trip digits, using plain decimal for moderate magnitudes and scientific notation for
/// large/small ones. This is exactly what cel-go's <c>string(double)</c> produces (verified by
/// the differential fuzzer), so it must match Go's 'g' presentation rule precisely, not .NET's.
///
/// Go's shortest-mode rule: with the value written as <c>0.digits × 10^dp</c>, let
/// <c>exp = dp - 1</c>. Use scientific when <c>exp &lt; -4 || exp &gt;= 6</c>, else plain decimal.
/// </summary>
public static class GoDoubleFormatter
{
    public static string Format(double value)
    {
        if (double.IsNaN(value))
        {
            return "NaN";
        }

        if (double.IsPositiveInfinity(value))
        {
            return "+Inf";
        }

        if (double.IsNegativeInfinity(value))
        {
            return "-Inf";
        }

        if (value == 0.0)
        {
            return double.IsNegative(value) ? "-0" : "0";
        }

        // Reduce .NET's shortest round-trip to canonical (significant digits, decimal-point pos).
        var (negative, digits, dp) = Decompose(value);
        var exp = dp - 1;
        var body = exp is < -4 or >= 6 ? Scientific(digits, exp) : Plain(digits, dp);
        return negative ? "-" + body : body;
    }

    /// <summary>Splits into sign, significant digits (no leading/trailing zeros), and dp = point position.</summary>
    private static (bool Negative, string Digits, int Dp) Decompose(double value)
    {
        var s = value.ToString("R", CultureInfo.InvariantCulture);
        var negative = s.StartsWith('-');
        if (negative)
        {
            s = s[1..];
        }

        var exp10 = 0;
        var eIndex = s.IndexOfAny(['e', 'E']);
        if (eIndex >= 0)
        {
            exp10 = int.Parse(s[(eIndex + 1)..], CultureInfo.InvariantCulture);
            s = s[..eIndex];
        }

        var dot = s.IndexOf('.');
        var intLen = dot < 0 ? s.Length : dot;
        var allDigits = dot < 0 ? s : s.Remove(dot, 1);
        var dp = intLen + exp10;

        // Strip leading zeros (shifting dp left with each).
        var lead = 0;
        while (lead < allDigits.Length - 1 && allDigits[lead] == '0')
        {
            lead++;
            dp--;
        }

        allDigits = allDigits[lead..].TrimEnd('0');
        if (allDigits.Length == 0)
        {
            allDigits = "0";
        }

        return (negative, allDigits, dp);
    }

    private static string Plain(string digits, int dp)
    {
        var sb = new StringBuilder(digits.Length + 4);
        if (dp <= 0)
        {
            sb.Append("0.").Append('0', -dp).Append(digits);
        }
        else if (dp >= digits.Length)
        {
            sb.Append(digits).Append('0', dp - digits.Length);
        }
        else
        {
            sb.Append(digits, 0, dp).Append('.').Append(digits, dp, digits.Length - dp);
        }

        return sb.ToString();
    }

    private static string Scientific(string digits, int exp)
    {
        var sb = new StringBuilder(digits.Length + 6);
        sb.Append(digits[0]);
        if (digits.Length > 1)
        {
            sb.Append('.').Append(digits, 1, digits.Length - 1);
        }

        // Go's %e: lowercase 'e', explicit sign, at least two exponent digits.
        sb.Append('e').Append(exp < 0 ? '-' : '+').Append(Math.Abs(exp).ToString("00", CultureInfo.InvariantCulture));
        return sb.ToString();
    }
}
