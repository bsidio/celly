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

    // Int↔double and uint↔double compare exactly like cel-go's compareDoubleInt/compareDoubleUint:
    // out-of-range doubles order strictly, and IN-range comparisons cast the integer to double.
    // That cast is lossy above 2^53 — deliberately so: the conformance suite codifies this
    // (e.g. "gte_dyn_int_big_lossy_double": 9223372036854775807 >= 9223372036854775808.0 is true).

    public static int? Compare(long a, double b)
    {
        if (double.IsNaN(b))
        {
            return null;
        }

        if (b > TwoPow63)
        {
            return -1;
        }

        if (b < -TwoPow63)
        {
            return 1;
        }

        return ((double)a).CompareTo(b);
    }

    public static int? Compare(double a, long b) => -Compare(b, a);

    public static int? Compare(ulong a, double b)
    {
        if (double.IsNaN(b))
        {
            return null;
        }

        if (b > TwoPow64)
        {
            return -1;
        }

        if (b < 0)
        {
            return 1;
        }

        return ((double)a).CompareTo(b);
    }

    public static int? Compare(double a, ulong b) => -Compare(b, a);
}
