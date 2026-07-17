using Celly.Stdlib;
using Celly.Values;
using Xunit;

namespace Celly.Tests.Stdlib;

public class GoDoubleFormatterTests
{
    [Theory]
    [InlineData(0.0, "0")]
    [InlineData(-0.0, "-0")]
    [InlineData(1.0, "1")]
    [InlineData(1.5, "1.5")]
    [InlineData(-2.25, "-2.25")]
    // Go's 'g' shortest format: scientific for exp < -4 or exp >= 6 (verified against cel-go).
    [InlineData(1e6, "1e+06")]
    [InlineData(1e5, "100000")]
    [InlineData(123456.0, "123456")]
    [InlineData(1e23, "1e+23")]
    [InlineData(1.2345e25, "1.2345e+25")]
    [InlineData(0.0001, "0.0001")]
    [InlineData(1e-5, "1e-05")]
    [InlineData(1e-7, "1e-07")]
    [InlineData(1.5e-9, "1.5e-09")]
    [InlineData(double.MaxValue, "1.7976931348623157e+308")]
    [InlineData(double.PositiveInfinity, "+Inf")]
    [InlineData(double.NegativeInfinity, "-Inf")]
    [InlineData(double.NaN, "NaN")]
    [InlineData(0.1, "0.1")]
    [InlineData(1.0 / 3.0, "0.3333333333333333")]
    public void FormatsLikeGoStrconv(double value, string expected) =>
        Assert.Equal(expected, GoDoubleFormatter.Format(value));
}

public class Rfc3339Tests
{
    [Theory]
    [InlineData("2009-02-13T23:31:30Z", 1234567890L, 0)]
    [InlineData("2009-02-13T23:31:30.123456789Z", 1234567890L, 123456789)]
    [InlineData("2009-02-13T23:31:30.5Z", 1234567890L, 500000000)]
    [InlineData("1970-01-01T00:00:00Z", 0L, 0)]
    [InlineData("1969-12-31T23:59:59Z", -1L, 0)]
    [InlineData("0001-01-01T00:00:00Z", -62135596800L, 0)]
    [InlineData("9999-12-31T23:59:59.999999999Z", 253402300799L, 999999999)]
    [InlineData("2009-02-14T00:31:30+01:00", 1234567890L, 0)] // offset applied
    [InlineData("2009-02-13T15:31:30-08:00", 1234567890L, 0)]
    public void ParsesValidTimestamps(string input, long seconds, int nanos)
    {
        var parsed = Rfc3339.Parse(input);
        Assert.NotNull(parsed);
        Assert.Equal(seconds, parsed.Value.Seconds);
        Assert.Equal(nanos, parsed.Value.Nanos);
    }

    [Theory]
    [InlineData("2009-02-13T23:31:30")] // no offset
    [InlineData("2009-02-30T00:00:00Z")] // invalid day
    [InlineData("2009-13-01T00:00:00Z")] // invalid month
    [InlineData("2009-02-13T24:00:00Z")] // invalid hour
    [InlineData("2009-02-13 23:31:30Z")] // space separator
    [InlineData("10000-01-01T00:00:00Z")] // beyond year 9999
    [InlineData("2009-02-13T23:31:30.1234567891Z")] // 10 fraction digits
    [InlineData("garbage")]
    public void RejectsInvalidTimestamps(string input) => Assert.Null(Rfc3339.Parse(input));

    [Theory]
    [InlineData(1234567890L, 0, "2009-02-13T23:31:30Z")]
    [InlineData(1234567890L, 123000000, "2009-02-13T23:31:30.123Z")]
    [InlineData(1234567890L, 123456789, "2009-02-13T23:31:30.123456789Z")]
    [InlineData(-62135596800L, 0, "0001-01-01T00:00:00Z")]
    public void FormatsRoundTrip(long seconds, int nanos, string expected) =>
        Assert.Equal(expected, Rfc3339.Format(new CelTimestampData(seconds, nanos)));

    [Fact]
    public void LeapYearHandling()
    {
        Assert.NotNull(Rfc3339.Parse("2000-02-29T00:00:00Z")); // 400-year leap
        Assert.Null(Rfc3339.Parse("1900-02-29T00:00:00Z")); // 100-year non-leap
        Assert.NotNull(Rfc3339.Parse("2024-02-29T00:00:00Z"));
        Assert.Null(Rfc3339.Parse("2023-02-29T00:00:00Z"));
    }
}

public class DurationTests
{
    [Theory]
    [InlineData("1h", 3600L, 0)]
    [InlineData("1h5m", 3900L, 0)]
    [InlineData("2.5s", 2L, 500000000)]
    [InlineData("-2.5s", -2L, -500000000)]
    [InlineData("1000ms", 1L, 0)]
    [InlineData("1ns", 0L, 1)]
    [InlineData("1us", 0L, 1000)]
    [InlineData("1µs", 0L, 1000)]
    [InlineData("0", 0L, 0)]
    [InlineData("1.000000001s", 1L, 1)]
    public void ParsesGoDurations(string input, long seconds, int nanos)
    {
        var result = TimeFunctions.ParseDuration(input);
        var duration = Assert.IsType<DurationValue>(result);
        Assert.Equal(seconds, duration.Data.Seconds);
        Assert.Equal(nanos, duration.Data.Nanos);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("s")]
    [InlineData("1d")] // days not a Go unit
    [InlineData("1 h")]
    public void RejectsInvalidDurations(string input) => Assert.IsType<ErrorValue>(TimeFunctions.ParseDuration(input));

    [Theory]
    [InlineData(3600L, 0, "3600s")]
    [InlineData(1L, 500000000, "1.5s")]
    [InlineData(-1L, -500000000, "-1.5s")]
    [InlineData(0L, 1, "0.000000001s")]
    [InlineData(0L, 0, "0s")]
    public void FormatsDurations(long seconds, int nanos, string expected) =>
        Assert.Equal(expected, TimeFunctions.FormatDuration(new CelDurationData(seconds, nanos)));

    [Fact]
    public void DurationRangeIsInt64Nanos()
    {
        // ~292 years fits; the full timestamp-difference range does not.
        Assert.IsType<DurationValue>(DurationValue.OfNanos(long.MaxValue));
        Assert.IsType<ErrorValue>(DurationValue.OfNanos((System.Numerics.BigInteger)long.MaxValue + 1));
    }
}

public class StdlibEvalTests
{
    private static CelValue Eval(string expression) => CelEnv.Default.Compile(expression).Eval();

    private static void AssertBool(string expression, bool expected) =>
        Assert.Equal(expected, Assert.IsType<BoolValue>(Eval(expression)).Value);

    [Theory]
    [InlineData("string(123) == '123'", true)]
    [InlineData("string(1.5) == '1.5'", true)]
    [InlineData("string(1e23) == '1e+23'", true)]
    [InlineData("string(7u) == '7'", true)]
    [InlineData("string(true) == 'true'", true)]
    [InlineData("string(b'abc') == 'abc'", true)]
    [InlineData("int('42') == 42", true)]
    [InlineData("int(2.9) == 2", true)]
    [InlineData("int(-2.9) == -2", true)]
    [InlineData("uint('42') == 42u", true)]
    [InlineData("double('1.5') == 1.5", true)]
    [InlineData("double('-Inf') <= -1e308", true)]
    [InlineData("bool('true') && !bool('0')", true)]
    [InlineData("bytes('abc') == b'abc'", true)]
    [InlineData("'hello'.contains('ell')", true)]
    [InlineData("'hello'.startsWith('he')", true)]
    [InlineData("'hello'.endsWith('lo')", true)]
    [InlineData("'hello'.matches('l+')", true)]
    [InlineData("matches('hello', '^h.*o$')", true)]
    [InlineData("'hello'.matches('^e')", false)]
    [InlineData("timestamp('2009-02-13T23:31:30Z').getFullYear() == 2009", true)]
    [InlineData("timestamp('2009-02-13T23:31:30Z').getMonth() == 1", true)] // 0-based
    [InlineData("timestamp('2009-02-13T23:31:30Z').getDate() == 13", true)] // 1-based
    [InlineData("timestamp('2009-02-13T23:31:30Z').getDayOfMonth() == 12", true)] // 0-based
    [InlineData("timestamp('2009-02-13T23:31:30Z').getDayOfWeek() == 5", true)] // Friday
    [InlineData("timestamp('2009-02-13T23:31:30Z').getDayOfYear() == 43", true)] // 0-based
    [InlineData("timestamp('2009-02-13T23:31:30Z').getHours() == 23", true)]
    [InlineData("timestamp('2009-02-13T23:31:30Z').getHours('-08:00') == 15", true)]
    [InlineData("timestamp('2009-02-13T23:31:30Z').getMinutes() == 31", true)]
    [InlineData("duration('90s').getMinutes() == 1", true)] // whole-unit conversion
    [InlineData("duration('123.321s').getMilliseconds() == 321", true)] // ms component
    [InlineData("timestamp('2009-02-13T23:31:30Z') + duration('60s') == timestamp('2009-02-13T23:32:30Z')", true)]
    [InlineData("timestamp('2009-02-13T23:31:30Z') - timestamp('2009-02-13T23:30:30Z') == duration('1m')", true)]
    [InlineData("duration('1h') + duration('30m') == duration('90m')", true)]
    [InlineData("duration('1h') > duration('59m')", true)]
    [InlineData("timestamp('2010-01-01T00:00:00Z') > timestamp('2009-01-01T00:00:00Z')", true)]
    [InlineData("int(timestamp('1970-01-01T00:16:40Z')) == 1000", true)]
    [InlineData("timestamp(1234567890) == timestamp('2009-02-13T23:31:30Z')", true)]
    public void StandardLibrary(string expression, bool expected) => AssertBool(expression, expected);

    [Theory]
    [InlineData("int(1e99)")]
    [InlineData("int(0.0/0.0)")]
    [InlineData("uint(-1)")]
    [InlineData("uint(-0.5)")]
    [InlineData("int('abc')")]
    [InlineData("int(' 1')")] // Go rejects whitespace
    [InlineData("uint('-1')")]
    [InlineData("bool('yes')")]
    [InlineData("string(b'\\xff')")] // invalid UTF-8
    [InlineData("timestamp('bogus')")]
    [InlineData("duration('bogus')")]
    [InlineData("timestamp('0000-12-31T23:59:59Z')")] // below range
    [InlineData("timestamp('9999-12-31T23:59:59Z') + duration('1s')")] // overflow
    [InlineData("'a'.matches('(unclosed')")]
    public void StdlibErrors(string expression) => Assert.IsType<ErrorValue>(Eval(expression));

    [Fact]
    public void EscapedIdentifiers()
    {
        AssertBool("{'foo.txt': 32}.`foo.txt` == 32", true);
        AssertBool("has({'content-type': 'json'}.`content-type`)", true);
    }
}
