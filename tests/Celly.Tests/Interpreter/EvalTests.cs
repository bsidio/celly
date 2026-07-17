using Celly.Values;
using Xunit;

namespace Celly.Tests.Interpreter;

public class EvalTests
{
    private static CelValue Eval(string expression, Dictionary<string, object?>? bindings = null)
    {
        var program = CelEnv.Default.Compile(expression);
        return bindings is null ? program.Eval() : program.Eval(bindings);
    }

    private static void AssertBool(string expression, bool expected) =>
        Assert.Equal(expected, Assert.IsType<BoolValue>(Eval(expression)).Value);

    private static void AssertInt(string expression, long expected) =>
        Assert.Equal(expected, Assert.IsType<IntValue>(Eval(expression)).Value);

    private static void AssertDouble(string expression, double expected) =>
        Assert.Equal(expected, Assert.IsType<DoubleValue>(Eval(expression)).Value);

    private static string AssertError(string expression)
    {
        var value = Eval(expression);
        return Assert.IsType<ErrorValue>(value).Message;
    }

    // ---- arithmetic ------------------------------------------------------------------------------

    [Theory]
    [InlineData("1 + 2", 3L)]
    [InlineData("10 - 3", 7L)]
    [InlineData("6 * 7", 42L)]
    [InlineData("17 / 5", 3L)]
    [InlineData("-17 / 5", -3L)] // truncation toward zero
    [InlineData("17 % 5", 2L)]
    [InlineData("-17 % 5", -2L)] // sign of dividend
    [InlineData("17 % -5", 2L)]
    [InlineData("-(3 + 4)", -7L)]
    [InlineData("9223372036854775807 - 1", 9223372036854775806L)]
    [InlineData("-9223372036854775808 + 1", -9223372036854775807L)]
    public void IntArithmetic(string expression, long expected) => AssertInt(expression, expected);

    [Theory]
    [InlineData("9223372036854775807 + 1")]
    [InlineData("-9223372036854775808 - 1")]
    [InlineData("9223372036854775807 * 2")]
    [InlineData("-9223372036854775808 / -1")]
    [InlineData("-9223372036854775808 % -1")]
    [InlineData("-(-9223372036854775808)")]
    public void IntOverflow(string expression) => Assert.Equal("return error for overflow", AssertError(expression));

    [Theory]
    [InlineData("1 / 0", "divide by zero")]
    [InlineData("1 % 0", "modulus by zero")]
    [InlineData("1u / 0u", "divide by zero")]
    [InlineData("1u % 0u", "modulus by zero")]
    public void DivideByZero(string expression, string message) => Assert.Equal(message, AssertError(expression));

    [Theory]
    [InlineData("18446744073709551615u - 1u", 18446744073709551614UL)]
    [InlineData("2u + 3u", 5UL)]
    [InlineData("6u * 7u", 42UL)]
    public void UintArithmetic(string expression, ulong expected) =>
        Assert.Equal(expected, Assert.IsType<UintValue>(Eval(expression)).Value);

    [Fact]
    public void UintOverflow()
    {
        Assert.Equal("return error for overflow", AssertError("18446744073709551615u + 1u"));
        Assert.Equal("return error for overflow", AssertError("0u - 1u"));
    }

    [Theory]
    [InlineData("1.5 + 2.5", 4.0)]
    [InlineData("1.0 / 0.0", double.PositiveInfinity)] // IEEE, no error
    [InlineData("-1.0 / 0.0", double.NegativeInfinity)]
    [InlineData("-(1.5)", -1.5)]
    public void DoubleArithmetic(string expression, double expected) => AssertDouble(expression, expected);

    [Fact]
    public void MixedTypeArithmeticIsNoSuchOverload()
    {
        Assert.Equal("no such overload", AssertError("1 + 1.0"));
        Assert.Equal("no such overload", AssertError("1 + 1u"));
        Assert.Equal("no such overload", AssertError("1.0 % 2.0"));
        Assert.Equal("no such overload", AssertError("-1u"));
        Assert.Equal("no such overload", AssertError("\"a\" - \"b\""));
    }

    [Theory]
    [InlineData("\"foo\" + \"bar\"", "foobar")]
    public void StringConcat(string expression, string expected) =>
        Assert.Equal(expected, Assert.IsType<StringValue>(Eval(expression)).Value);

    [Fact]
    public void ListConcat() => AssertBool("[1, 2] + [3] == [1, 2, 3]", true);

    [Fact]
    public void BytesConcat() => AssertBool("b\"ab\" + b\"cd\" == b\"abcd\"", true);

    // ---- comparisons (including required cross-type numeric) --------------------------------------

    [Theory]
    [InlineData("1 < 2", true)]
    [InlineData("2 <= 2", true)]
    [InlineData("3 > 2", true)]
    [InlineData("2 >= 3", false)]
    [InlineData("\"a\" < \"b\"", true)]
    [InlineData("b\"a\" < b\"b\"", true)]
    [InlineData("false < true", true)]
    [InlineData("1u < 2u", true)]
    [InlineData("1.0 < 1.5", true)]
    // cross-type numeric comparisons on the shared number line
    [InlineData("-1 < 1u", true)]
    [InlineData("1 >= 18446744073709551615u", false)]
    [InlineData("18446744073709551615u > 1", true)]
    [InlineData("1 < 1.1", true)]
    [InlineData("1u < 1.1", true)]
    [InlineData("2.0 == 2", true)]
    [InlineData("2.0 == 2u", true)]
    [InlineData("1 == 1u", true)]
    // Boundary semantics match cel-go exactly (the conformance suite codifies them): doubles
    // beyond ±2^63 order strictly, but IN-range comparisons cast int→double, which is lossy
    // above 2^53 — so int64max compares EQUAL to 2^63.0 ("lossy" per the suite's own naming).
    [InlineData("9223372036854775807 < 9223372036854777856.0", true)] // next double above 2^63
    [InlineData("9223372036854775807 >= 9223372036854775808.0", true)] // lossy boundary equality
    [InlineData("9007199254740993 == 9007199254740992.0", true)] // lossy above 2^53, per cel-go
    [InlineData("9007199254740993 > 9007199254740992.0", false)]
    public void Comparisons(string expression, bool expected) => AssertBool(expression, expected);

    [Theory]
    [InlineData("0.0/0.0 == 0.0/0.0", false)] // NaN != NaN
    [InlineData("0.0/0.0 != 0.0/0.0", true)]
    [InlineData("-0.0 == 0.0", true)]
    public void DoubleEdgeEquality(string expression, bool expected) => AssertBool(expression, expected);

    [Fact]
    public void NanOrderingIsError() => Assert.Contains("NaN", AssertError("1.0 < 0.0/0.0"));

    [Theory]
    [InlineData("1 == \"1\"", false)] // mismatched types are unequal, not errors
    [InlineData("1 != \"1\"", true)]
    [InlineData("[1] == [1.0]", true)] // deep numeric-aware equality
    [InlineData("[1, 2] == [1]", false)]
    [InlineData("{1: \"a\"} == {1u: \"a\"}", true)] // numerically equal keys are the same key
    [InlineData("{\"a\": 1} == {\"a\": 1.0}", true)]
    [InlineData("null == null", true)]
    [InlineData("null == 0", false)]
    [InlineData("type(1) == int", true)]
    [InlineData("type(1u) == uint", true)]
    [InlineData("type(\"\") == string", true)]
    [InlineData("type([]) == list", true)]
    [InlineData("type({}) == map", true)]
    [InlineData("type(null) == null_type", true)]
    [InlineData("type(type(1)) == type", true)]
    [InlineData("type(1) == type(2)", true)]
    [InlineData("type(1) == uint", false)]
    [InlineData("dyn(1) == 1", true)]
    public void Equality(string expression, bool expected) => AssertBool(expression, expected);

    // ---- logic: commutative error absorption -------------------------------------------------------

    [Theory]
    [InlineData("true && true", true)]
    [InlineData("true && false", false)]
    [InlineData("false && (1 / 0 == 0)", false)] // short-circuit
    [InlineData("(1 / 0 == 0) && false", false)] // commutative absorption
    [InlineData("true || (1 / 0 == 0)", true)]
    [InlineData("(1 / 0 == 0) || true", true)]
    [InlineData("false || false", false)]
    public void LogicAbsorption(string expression, bool expected) => AssertBool(expression, expected);

    [Theory]
    [InlineData("true && (1 / 0 == 0)")]
    [InlineData("(1 / 0 == 0) && true")]
    [InlineData("false || (1 / 0 == 0)")]
    [InlineData("1 && true")]
    public void LogicErrors(string expression) => AssertError(expression);

    [Theory]
    [InlineData("true ? 1 : 2", 1L)]
    [InlineData("false ? 1 : 2", 2L)]
    [InlineData("true ? 1 : (1 / 0)", 1L)] // untaken branch never evaluates
    [InlineData("false ? (1 / 0) : 2", 2L)]
    public void Ternary(string expression, long expected) => AssertInt(expression, expected);

    [Fact]
    public void TernaryConditionError() => AssertError("(1 / 0 == 0) ? 1 : 2");

    [Fact]
    public void NotOperator()
    {
        AssertBool("!true", false);
        AssertBool("!false", true);
        Assert.Equal("no such overload", AssertError("!1"));
    }

    // ---- lists, maps, indexing ----------------------------------------------------------------------

    [Theory]
    [InlineData("[1, 2, 3][0]", 1L)]
    [InlineData("[1, 2, 3][2]", 3L)]
    [InlineData("[1, 2, 3][1u]", 2L)]
    [InlineData("[1, 2, 3][1.0]", 2L)] // integral double index
    [InlineData("{\"a\": 1, \"b\": 2}[\"b\"]", 2L)]
    [InlineData("{1: 10}[1u]", 10L)] // cross-numeric key lookup
    [InlineData("{1u: 10}[1]", 10L)]
    [InlineData("{2: 10}[2.0]", 10L)]
    [InlineData("size([1, 2, 3])", 3L)]
    [InlineData("size(\"hello\")", 5L)]
    [InlineData("size(\"héllo\")", 5L)]
    [InlineData("size(\"h😀llo\")", 5L)] // code points, not UTF-16 units
    [InlineData("size(b\"abc\")", 3L)]
    [InlineData("size({\"a\": 1})", 1L)]
    [InlineData("\"hello\".size()", 5L)]
    [InlineData("[1, 2].size()", 2L)]
    public void Aggregates(string expression, long expected) => AssertInt(expression, expected);

    [Theory]
    [InlineData("[1, 2][5]", "index out of range: 5")]
    [InlineData("[1, 2][-1]", "index out of range: -1")]
    [InlineData("{\"a\": 1}[\"missing\"]", "no such key: missing")]
    [InlineData("{\"a\": 1, \"a\": 2}", "Failed with repeated key")]
    [InlineData("{1: 1, 1u: 2}", "Failed with repeated key")]
    public void AggregateErrors(string expression, string message) => Assert.Equal(message, AssertError(expression));

    [Theory]
    [InlineData("1 in [1, 2]", true)]
    [InlineData("3 in [1, 2]", false)]
    [InlineData("1.0 in [1]", true)] // numeric-aware membership
    [InlineData("\"a\" in {\"a\": 1}", true)]
    [InlineData("\"b\" in {\"a\": 1}", false)]
    [InlineData("1u in {1: 1}", true)]
    [InlineData("\"x\" in {1: 1}", false)] // wrong key type: false, not error
    public void InOperator(string expression, bool expected) => AssertBool(expression, expected);

    [Fact]
    public void HasOnMaps()
    {
        AssertBool("has({\"a\": 1}.a)", true);
        AssertBool("has({\"a\": 1}.b)", false);
    }

    // ---- macros / comprehensions ----------------------------------------------------------------------

    [Theory]
    [InlineData("[1, 2, 3].all(x, x > 0)", true)]
    [InlineData("[1, -2, 3].all(x, x > 0)", false)]
    [InlineData("[].all(x, x > 0)", true)]
    [InlineData("[1, 2, 3].exists(x, x == 2)", true)]
    [InlineData("[1, 2, 3].exists(x, x == 9)", false)]
    [InlineData("[].exists(x, true)", false)]
    [InlineData("[1, 2, 2].exists_one(x, x == 1)", true)]
    [InlineData("[1, 2, 2].exists_one(x, x == 2)", false)]
    [InlineData("[1, 2, 3].filter(x, x > 1) == [2, 3]", true)]
    [InlineData("[1, 2].map(x, x * 10) == [10, 20]", true)]
    [InlineData("[1, 2, 3].map(x, x > 1, x * 10) == [20, 30]", true)]
    [InlineData("{\"a\": 1, \"b\": 2}.all(k, k == \"a\" || k == \"b\")", true)]
    [InlineData("{\"a\": 1}.map(k, k) == [\"a\"]", true)]
    // error absorption inside quantifiers, mirroring || / && semantics: a determining
    // element (true for exists, false for all) wins even when another element errors
    [InlineData("[1, 2, 3].exists(x, x / (x - 2) > 0 || x == 2)", true)]
    [InlineData("[0, 2].all(x, 4 / x > 0 && x != 2)", false)]
    public void Comprehensions(string expression, bool expected) => AssertBool(expression, expected);

    [Fact]
    public void ComprehensionOverNonIterable() => AssertError("(1).all(x, true)");

    [Fact]
    public void QuantifierErrorSurvivesWithoutDeterminingElement() =>
        // No element makes all() false, so the division error must surface.
        AssertError("[0, 2, 4].all(x, 4 / x > 0)");

    [Fact]
    public void NestedComprehensionsShareNothing() =>
        AssertBool("[1, 2].all(x, [1, 2].exists(y, y == x))", true);

    // ---- variables -------------------------------------------------------------------------------------

    [Fact]
    public void Variables()
    {
        var bindings = new Dictionary<string, object?> { ["x"] = 41L, ["name"] = "cel" };
        Assert.Equal(42L, Assert.IsType<IntValue>(Eval("x + 1", bindings)).Value);
        AssertBoolWith("name == \"cel\"", bindings, true);
    }

    [Fact]
    public void DottedVariableNames()
    {
        // Parse-only maybe-attribute: "x.y" may be a variable named "x.y".
        var bindings = new Dictionary<string, object?> { ["x.y"] = 15L };
        Assert.Equal(15L, Assert.IsType<IntValue>(Eval("x.y", bindings)).Value);
    }

    [Fact]
    public void ContainerResolution()
    {
        var env = CelEnv.Create(new CelEnvSettings { Container = "a.b" });
        var program = env.Program(env.Parse("v").Ast!);
        // Container a.b resolves v as a.b.v, then a.v, then v — longest match wins.
        Assert.Equal(
            1L,
            Assert.IsType<IntValue>(program.Eval(new Dictionary<string, object?> { ["a.b.v"] = 1L, ["v"] = 3L })).Value);
        Assert.Equal(
            2L,
            Assert.IsType<IntValue>(program.Eval(new Dictionary<string, object?> { ["a.v"] = 2L, ["v"] = 3L })).Value);
        Assert.Equal(
            3L,
            Assert.IsType<IntValue>(program.Eval(new Dictionary<string, object?> { ["v"] = 3L })).Value);
    }

    [Fact]
    public void MissingVariableIsError() =>
        Assert.Equal("no such attribute(s): missing", AssertError("missing"));

    [Fact]
    public void MapVariableFieldSelection()
    {
        var bindings = new Dictionary<string, object?>
        {
            ["req"] = new Dictionary<string, object?> { ["user"] = "alice", ["age"] = 30L },
        };
        AssertBoolWith("req.user == \"alice\" && req.age >= 18", bindings, true);
        AssertBoolWith("has(req.user) && !has(req.email)", bindings, true);
    }

    private static void AssertBoolWith(string expression, Dictionary<string, object?> bindings, bool expected) =>
        Assert.Equal(expected, Assert.IsType<BoolValue>(Eval(expression, bindings)).Value);
}
