namespace Celly.Values;

/// <summary>
/// Precise cross-type numeric comparison: int, uint, and double are compared as points on a single
/// number line, without long↔double casts that lose precision above 2^53.
/// Returns -1/0/1, or null when unordered (any NaN operand).
/// </summary>
public static class NumericCompare
{
    private const double TwoPow63 = 9223372036854775808.0; // 2^63
    private const double TwoPow64 = 18446744073709551616.0; // 2^64

    public static int Compare(long a, long b) => a.CompareTo(b);

    public static int Compare(ulong a, ulong b) => a.CompareTo(b);

    public static int Compare(long a, ulong b)
    {
        if (a < 0)
        {
            return -1;
        }

        return ((ulong)a).CompareTo(b);
    }

    public static int Compare(ulong a, long b) => -Compare(b, a);

    public static int? Compare(double a, double b)
    {
        if (double.IsNaN(a) || double.IsNaN(b))
        {
            return null;
        }

        return a.CompareTo(b);
    }

    public static int? Compare(long a, double b)
    {
        if (double.IsNaN(b))
        {
            return null;
        }

        if (b >= TwoPow63)
        {
            return -1;
        }

        if (b < -TwoPow63)
        {
            return 1;
        }

        // |b| < 2^63: the truncation is exact (any double >= 2^52 in magnitude is integral).
        var truncated = (long)b;
        if (a != truncated)
        {
            return a < truncated ? -1 : 1;
        }

        var fraction = b - truncated;
        return fraction > 0 ? -1 : fraction < 0 ? 1 : 0;
    }

    public static int? Compare(double a, long b) => -Compare(b, a);

    public static int? Compare(ulong a, double b)
    {
        if (double.IsNaN(b))
        {
            return null;
        }

        if (b >= TwoPow64)
        {
            return -1;
        }

        if (b < 0)
        {
            return 1;
        }

        var truncated = (ulong)b;
        if (a != truncated)
        {
            return a < truncated ? -1 : 1;
        }

        var fraction = b - truncated;
        return fraction > 0 ? -1 : fraction < 0 ? 1 : 0;
    }

    public static int? Compare(double a, ulong b) => -Compare(b, a);
}
