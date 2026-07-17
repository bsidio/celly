using Celly.Types;

namespace Celly.Values;

/// <summary>
/// Nanosecond-precision instant: seconds since Unix epoch + nanos in [0, 999999999].
/// .NET ticks are 100ns, so DateTimeOffset cannot back this losslessly.
/// Valid range: 0001-01-01T00:00:00Z .. 9999-12-31T23:59:59.999999999Z.
/// </summary>
public readonly record struct CelTimestampData(long Seconds, int Nanos)
{
    public const long MinSeconds = -62135596800; // 0001-01-01T00:00:00Z
    public const long MaxSeconds = 253402300799; // 9999-12-31T23:59:59Z

    public bool InRange => Seconds is >= MinSeconds and <= MaxSeconds && Nanos is >= 0 and <= 999_999_999;
}

/// <summary>
/// Nanosecond-precision span: seconds and nanos share sign. The CEL duration range is what an
/// int64 nanosecond count can represent (~±292 years) — narrower than proto's ±10000 years, and
/// what the conformance suite's overflow tests expect.
/// </summary>
public readonly record struct CelDurationData(long Seconds, int Nanos)
{
    public bool InRange =>
        Nanos is > -1_000_000_000 and < 1_000_000_000
        && (Seconds == 0 || Nanos == 0 || Math.Sign(Seconds) == Math.Sign(Nanos))
        && FitsInt64Nanos(Seconds, Nanos);

    private static bool FitsInt64Nanos(long seconds, int nanos)
    {
        var total = (System.Numerics.BigInteger)seconds * 1_000_000_000 + nanos;
        return total >= long.MinValue && total <= long.MaxValue;
    }
}

public sealed class TimestampValue(CelTimestampData data) : CelValue, IComparableValue, IAdder, ISubtractor
{
    public CelTimestampData Data { get; } = data;

    public static CelValue Of(long seconds, int nanos)
    {
        var data = new CelTimestampData(seconds, nanos);
        return data.InRange ? new TimestampValue(data) : new ErrorValue("timestamp overflow");
    }

    public override CelType Type => CelType.Timestamp;

    public override bool EqualTo(CelValue other) => other is TimestampValue t && t.Data == Data;

    public override object ToNative() => Data;

    public CelValue CompareTo(CelValue other)
    {
        if (other is not TimestampValue t)
        {
            return ErrorValue.NoSuchOverload();
        }

        var cmp = Data.Seconds != t.Data.Seconds
            ? Data.Seconds.CompareTo(t.Data.Seconds)
            : Data.Nanos.CompareTo(t.Data.Nanos);
        return IntValue.OfComparison(cmp);
    }

    public CelValue Add(CelValue other) => other switch
    {
        DurationValue d => TimeArithmetic.AddTimestampDuration(Data, d.Data),
        _ => ErrorValue.NoSuchOverload(),
    };

    public CelValue Subtract(CelValue other) => other switch
    {
        TimestampValue t => TimeArithmetic.SubtractTimestamps(Data, t.Data),
        DurationValue d => TimeArithmetic.AddTimestampDuration(Data, new CelDurationData(-d.Data.Seconds, -d.Data.Nanos)),
        _ => ErrorValue.NoSuchOverload(),
    };

    public override string ToString() => Stdlib.Rfc3339.Format(Data);
}

public sealed class DurationValue(CelDurationData data) : CelValue, IComparableValue, IAdder, ISubtractor, INegater
{
    public CelDurationData Data { get; } = data;

    public static CelValue Of(long seconds, int nanos)
    {
        var data = new CelDurationData(seconds, nanos);
        return data.InRange ? new DurationValue(data) : new ErrorValue("duration overflow");
    }

    public static CelValue OfNanos(System.Numerics.BigInteger totalNanos)
    {
        var seconds = (long)(totalNanos / 1_000_000_000);
        var nanos = (int)(totalNanos % 1_000_000_000);
        return Of(seconds, nanos);
    }

    public System.Numerics.BigInteger TotalNanos =>
        (System.Numerics.BigInteger)Data.Seconds * 1_000_000_000 + Data.Nanos;

    public override CelType Type => CelType.Duration;

    public override bool EqualTo(CelValue other) => other is DurationValue d && d.Data == Data;

    public override object ToNative() => Data;

    public CelValue CompareTo(CelValue other)
    {
        if (other is not DurationValue d)
        {
            return ErrorValue.NoSuchOverload();
        }

        return IntValue.OfComparison(TotalNanos.CompareTo(d.TotalNanos));
    }

    public CelValue Add(CelValue other) => other switch
    {
        DurationValue d => OfNanos(TotalNanos + d.TotalNanos),
        TimestampValue t => TimeArithmetic.AddTimestampDuration(t.Data, Data),
        _ => ErrorValue.NoSuchOverload(),
    };

    public CelValue Subtract(CelValue other) => other switch
    {
        DurationValue d => OfNanos(TotalNanos - d.TotalNanos),
        _ => ErrorValue.NoSuchOverload(),
    };

    public CelValue Negate() => OfNanos(-TotalNanos);

    public override string ToString() => Stdlib.TimeFunctions.FormatDuration(Data);
}

internal static class TimeArithmetic
{
    public static CelValue AddTimestampDuration(CelTimestampData ts, CelDurationData d)
    {
        var seconds = ts.Seconds + d.Seconds; // |values| ≪ long.MaxValue: no wraparound possible
        var nanos = ts.Nanos + d.Nanos;
        if (nanos >= 1_000_000_000)
        {
            seconds++;
            nanos -= 1_000_000_000;
        }
        else if (nanos < 0)
        {
            seconds--;
            nanos += 1_000_000_000;
        }

        return TimestampValue.Of(seconds, nanos);
    }

    public static CelValue SubtractTimestamps(CelTimestampData a, CelTimestampData b) =>
        DurationValue.OfNanos(
            ((System.Numerics.BigInteger)a.Seconds - b.Seconds) * 1_000_000_000 + (a.Nanos - b.Nanos));
}
