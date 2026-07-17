using System.Globalization;
using Celly.Values;
using Xunit;

namespace Celly.Tests.Stress;

/// <summary>
/// CEL parsing and formatting must be culture-invariant: a comma-decimal locale (de-DE) or the
/// dotted/dotless-i locale (tr-TR) must not change how numbers parse, how doubles stringify, or
/// how ASCII-case functions behave. Runs a battery under each hostile culture and asserts
/// identical results to the invariant baseline.
/// </summary>
public class CultureTests
{
    private static readonly (string Expr, string Expected)[] Cases =
    [
        ("string(1.5)", "1.5"),                 // NOT "1,5" under de-DE
        ("string(1234.5)", "1234.5"),
        ("string(1000000.0)", "1e+06"),         // Go 'g' scientific threshold, culture-independent
        ("string(100000.0)", "100000"),         // just below the threshold — plain
        ("string(double('3.14'))", "3.14"),     // parse must accept '.', not ','
        ("string(-0.5)", "-0.5"),
        ("string(123)", "123"),
        ("string(1u)", "1"),
        ("'HELLO'.lowerAscii()", "hello"),      // ASCII case, not Turkish 'i'
        ("'istanbul'.upperAscii()", "ISTANBUL"),
        ("'İ'.lowerAscii()", "İ"),              // non-ASCII untouched by *Ascii funcs
        ("string(timestamp('2009-02-13T23:31:30Z'))", "2009-02-13T23:31:30Z"),
        ("string(duration('1h30m'))", "5400s"),
    ];

    public static IEnumerable<object[]> Cultures() =>
    [
        ["de-DE"], ["tr-TR"], ["fr-FR"], ["ar-SA"], [""],
    ];

    [Theory]
    [MemberData(nameof(Cultures))]
    public void ResultsAreCultureInvariant(string cultureName)
    {
        var culture = cultureName.Length == 0 ? CultureInfo.InvariantCulture : new CultureInfo(cultureName);
        var original = CultureInfo.CurrentCulture;
        var originalUi = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;

            var env = CelEnv.Create(new CelEnvSettings
            {
                Libraries = [Celly.Extensions.StringsLibrary.Instance],
            });

            foreach (var (expr, expected) in Cases)
            {
                var result = env.Compile(expr).Eval();
                var actual = Assert.IsType<StringValue>(result).Value;
                Assert.Equal(expected, actual);
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
            CultureInfo.CurrentUICulture = originalUi;
        }
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public void NumericParsingIsCultureInvariant(string cultureName)
    {
        var culture = cultureName.Length == 0 ? CultureInfo.InvariantCulture : new CultureInfo(cultureName);
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = culture;
            var env = CelEnv.Default;

            // Literal parsing: the dot is always the decimal separator.
            Assert.Equal(1.5, ((DoubleValue)env.Compile("1.5").Eval()).Value);
            Assert.Equal(1000.0, ((DoubleValue)env.Compile("1e3").Eval()).Value);
            // A comma is a list/arg separator, never part of a number.
            var list = (ListValue)env.Compile("[1.5, 2.5]").Eval();
            Assert.Equal(2, list.Elements.Count);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
