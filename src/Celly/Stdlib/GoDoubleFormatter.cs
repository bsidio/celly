using System.Globalization;
using System.Text;

namespace Celly.Stdlib;

/// <summary>
/// Formats doubles the way Go's <c>strconv.FormatFloat(f, 'f', -1, 64)</c> does — shortest
/// round-trip digits in PLAIN decimal notation (never scientific), which is what cel-go's
/// <c>string(double)</c> produces. .NET's shortest round-trip ("R") switches to scientific
/// notation for large/small magnitudes, so the exponent is expanded here.
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

        var shortest = value.ToString("R", CultureInfo.InvariantCulture);
        var eIndex = shortest.IndexOfAny(['e', 'E']);
        if (eIndex < 0)
        {
            return shortest;
        }

        // Expand mantissa±exponent into plain decimal.
        var mantissa = shortest[..eIndex];
        var exponent = int.Parse(shortest[(eIndex + 1)..], CultureInfo.InvariantCulture);
        var negative = mantissa.StartsWith('-');
        if (negative)
        {
            mantissa = mantissa[1..];
        }

        var dot = mantissa.IndexOf('.');
        var digits = dot < 0 ? mantissa : mantissa.Remove(dot, 1);
        var pointPos = (dot < 0 ? mantissa.Length : dot) + exponent;

        var sb = new StringBuilder();
        if (negative)
        {
            sb.Append('-');
        }

        if (pointPos <= 0)
        {
            sb.Append("0.").Append('0', -pointPos).Append(digits.TrimEnd('0') is { Length: > 0 } d ? d : "0");
        }
        else if (pointPos >= digits.Length)
        {
            sb.Append(digits).Append('0', pointPos - digits.Length);
        }
        else
        {
            sb.Append(digits, 0, pointPos).Append('.').Append(digits, pointPos, digits.Length - pointPos);
        }

        return sb.ToString();
    }
}
