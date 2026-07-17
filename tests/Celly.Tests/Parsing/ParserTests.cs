using Celly.Ast;
using Celly.Parsing;
using Xunit;

namespace Celly.Tests.Parsing;

public class ParserTests
{
    private static string ParseToString(string expression)
    {
        var result = CelParser.Parse(expression);
        Assert.True(result.Ast is not null, $"parse failed: {string.Join("; ", result.Issues)}");
        return ExprFormatter.Format(result.Ast!.Expr);
    }

    private static string ParseError(string expression)
    {
        var result = CelParser.Parse(expression);
        Assert.True(result.HasErrors, $"expected parse failure, got: {(result.Ast is null ? "?" : ExprFormatter.Format(result.Ast.Expr))}");
        return result.Issues[0].Message;
    }

    // ---- literals, identifiers ------------------------------------------------------------------

    [Theory]
    [InlineData("42", "42")]
    [InlineData("0x10", "16")]
    [InlineData("-1", "-1")]
    [InlineData("--1", "1")]
    [InlineData("- -1", "1")]
    [InlineData("-9223372036854775808", "-9223372036854775808")]
    [InlineData("-0x8000000000000000", "-9223372036854775808")]
    [InlineData("9223372036854775807", "9223372036854775807")]
    [InlineData("7u", "7u")]
    [InlineData("1.5", "1.5")]
    [InlineData(".5", "0.5")]
    [InlineData("-.5", "-0.5")]
    [InlineData("-2.3e+1", "-23.0")]
    [InlineData("-0.0", "-0.0")]
    [InlineData("0.0", "0.0")]
    [InlineData("\"hi\"", "\"hi\"")]
    [InlineData("b\"abc\"", "b\"616263\"")]
    [InlineData("true", "true")]
    [InlineData("false", "false")]
    [InlineData("null", "null")]
    [InlineData("x", "x")]
    [InlineData(".x", ".x")]
    [InlineData("(7)", "7")]
    public void Literals(string source, string expected) => Assert.Equal(expected, ParseToString(source));

    [Theory]
    [InlineData("9223372036854775808")]
    [InlineData("-9223372036854775809")]
    [InlineData("0x8000000000000000")]
    [InlineData("-0x8000000000000001")]
    public void IntLiteralOverflow(string source) =>
        Assert.Contains("out of range", ParseError(source));

    // ---- operators and precedence ----------------------------------------------------------------

    [Theory]
    [InlineData("a + b - c", "_-_(_+_(a, b), c)")]
    [InlineData("1 + 2 * 3", "_+_(1, _*_(2, 3))")]
    [InlineData("(1 + 2) * 3", "_*_(_+_(1, 2), 3)")]
    [InlineData("a / b % c", "_%_(_/_(a, b), c)")]
    [InlineData("a && b || c && d", "_||_(_&&_(a, b), _&&_(c, d))")]
    [InlineData("a || b || c", "_||_(_||_(a, b), c)")]
    [InlineData("a ? b : c ? d : e", "_?_:_(a, b, _?_:_(c, d, e))")]
    [InlineData("(a ? b : c) ? d : e", "_?_:_(_?_:_(a, b, c), d, e)")]
    [InlineData("a < b < c", "_<_(_<_(a, b), c)")]
    [InlineData("a <= b", "_<=_(a, b)")]
    [InlineData("a >= b", "_>=_(a, b)")]
    [InlineData("a == b != c", "_!=_(_==_(a, b), c)")]
    [InlineData("x in [1, 2]", "@in(x, [1, 2])")]
    [InlineData("a + b < c", "_<_(_+_(a, b), c)")]
    [InlineData("a < b && c", "_&&_(_<_(a, b), c)")]
    [InlineData("!true", "!_(true)")]
    [InlineData("!!true", "true")]
    [InlineData("!!!x", "!_(x)")]
    [InlineData("-a", "-_(a)")]
    [InlineData("--a", "a")]
    [InlineData("-(1 + 2)", "-_(_+_(1, 2))")]
    [InlineData("-x[0]", "-_(_[_](x, 0))")]
    public void Operators(string source, string expected) => Assert.Equal(expected, ParseToString(source));

    // ---- member access, calls, indexing ----------------------------------------------------------

    [Theory]
    [InlineData("a.b.c", "a.b.c")]
    [InlineData("a.b(c)", "a.b(c)")]
    [InlineData("a.b(c, d)", "a.b(c, d)")]
    [InlineData("f()", "f()")]
    [InlineData("f(1)", "f(1)")]
    [InlineData(".f(1)", ".f(1)")]
    [InlineData("size(x)", "size(x)")]
    [InlineData("a[0]", "_[_](a, 0)")]
    [InlineData("a[0][1]", "_[_](_[_](a, 0), 1)")]
    [InlineData("a.b[0].c", "_[_](a.b, 0).c")]
    [InlineData("[1, 2][0]", "_[_]([1, 2], 0)")]
    [InlineData("\"s\".size()", "\"s\".size()")]
    [InlineData("1.a", "1.a")]
    [InlineData("-1.a", "-1.a")]
    [InlineData("a.while", "a.while")] // reserved words are legal selectors
    [InlineData("a.while(b)", "a.while(b)")]
    [InlineData("a.import.if", "a.import.if")]
    public void MembersAndCalls(string source, string expected) => Assert.Equal(expected, ParseToString(source));

    // ---- aggregates ------------------------------------------------------------------------------

    [Theory]
    [InlineData("[]", "[]")]
    [InlineData("[,]", "[]")]
    [InlineData("[1]", "[1]")]
    [InlineData("[1, 2, 3]", "[1, 2, 3]")]
    [InlineData("[1, 2,]", "[1, 2]")]
    [InlineData("{}", "{}")]
    [InlineData("{,}", "{}")]
    [InlineData("{\"k\": \"v\"}", "{\"k\": \"v\"}")]
    [InlineData("{1: 2, 3: 4,}", "{1: 2, 3: 4}")]
    [InlineData("Msg{}", "Msg{}")]
    [InlineData("Msg{f: 1}", "Msg{f: 1}")]
    [InlineData("Msg{f: 1, g: 2,}", "Msg{f: 1, g: 2}")]
    [InlineData("pkg.Msg{f: 1}", "pkg.Msg{f: 1}")]
    [InlineData(".pkg.Msg{f: 1}", ".pkg.Msg{f: 1}")]
    [InlineData("while.for{f: 1}", "while.for{f: 1}")] // SELECTOR segments admit reserved words
    [InlineData("[[1], [2]]", "[[1], [2]]")]
    [InlineData("{\"a\": [1], \"b\": {\"c\": 2}}", "{\"a\": [1], \"b\": {\"c\": 2}}")]
    public void Aggregates(string source, string expected) => Assert.Equal(expected, ParseToString(source));

    // ---- macros ----------------------------------------------------------------------------------

    [Theory]
    [InlineData(
        "has(m.f)",
        "m.f~test-only~")]
    [InlineData(
        "has(a.b.c)",
        "a.b.c~test-only~")]
    [InlineData(
        "e.all(x, x > 0)",
        "fold(x, e, @result, true, @not_strictly_false(@result), _&&_(@result, _>_(x, 0)), @result)")]
    [InlineData(
        "e.exists(x, x > 0)",
        "fold(x, e, @result, false, @not_strictly_false(!_(@result)), _||_(@result, _>_(x, 0)), @result)")]
    [InlineData(
        "e.exists_one(x, x > 0)",
        "fold(x, e, @result, 0, true, _?_:_(_>_(x, 0), _+_(@result, 1), @result), _==_(@result, 1))")]
    [InlineData(
        "e.map(x, x * 2)",
        "fold(x, e, @result, [], true, _+_(@result, [_*_(x, 2)]), @result)")]
    [InlineData(
        "e.map(x, x > 0, x * 2)",
        "fold(x, e, @result, [], true, _?_:_(_>_(x, 0), _+_(@result, [_*_(x, 2)]), @result), @result)")]
    [InlineData(
        "e.filter(x, x > 0)",
        "fold(x, e, @result, [], true, _?_:_(_>_(x, 0), _+_(@result, [x]), @result), @result)")]
    [InlineData(
        "[1, 2].all(x, [3, 4].all(y, x < y))",
        "fold(x, [1, 2], @result, true, @not_strictly_false(@result), _&&_(@result, fold(y, [3, 4], @result, true, @not_strictly_false(@result), _&&_(@result, _<_(x, y)), @result)), @result)")]
    [InlineData("x.has(y)", "x.has(y)")] // has is not a receiver macro
    [InlineData("all(x, y)", "all(x, y)")] // all is not a global macro
    public void Macros(string source, string expected) => Assert.Equal(expected, ParseToString(source));

    [Theory]
    [InlineData("has(a)", "invalid argument to has() macro")]
    [InlineData("has(m[0])", "invalid argument to has() macro")]
    [InlineData("has(has(m.f))", "invalid argument to has() macro")]
    [InlineData("e.all(x.y, true)", "argument must be a simple name")]
    [InlineData("e.exists(1, true)", "argument must be a simple name")]
    [InlineData("e.map(x + 1, x)", "argument is not an identifier")]
    [InlineData("e.filter(1, true)", "argument is not an identifier")]
    public void MacroErrors(string source, string expectedMessage) =>
        Assert.Contains(expectedMessage, ParseError(source));

    // ---- optional syntax -------------------------------------------------------------------------

    [Theory]
    [InlineData("a.?b", "_?._(a, \"b\")")]
    [InlineData("a.?b.?c", "_?._(_?._(a, \"b\"), \"c\")")]
    [InlineData("a[?b]", "_[?_](a, b)")]
    [InlineData("[?a, b]", "[?a, b]")]
    [InlineData("[a, ?b]", "[a, ?b]")]
    [InlineData("{?\"k\": v}", "{?\"k\": v}")]
    [InlineData("Msg{?f: v}", "Msg{?f: v}")]
    public void OptionalSyntax(string source, string expected) => Assert.Equal(expected, ParseToString(source));

    [Theory]
    [InlineData("a.?b")]
    [InlineData("a[?b]")]
    [InlineData("[?a]")]
    [InlineData("{?\"k\": v}")]
    [InlineData("Msg{?f: v}")]
    public void OptionalSyntaxCanBeDisabled(string source)
    {
        var result = CelParser.Parse(source, new ParserOptions { EnableOptionalSyntax = false });
        Assert.True(result.HasErrors);
    }

    // ---- errors ----------------------------------------------------------------------------------

    [Theory]
    [InlineData("1 +")]
    [InlineData("[1, 2")]
    [InlineData("(a")]
    [InlineData("a.")]
    [InlineData("a ? b")]
    [InlineData("a ? b :")]
    [InlineData("f(1, 2,)")] // no trailing comma in call args
    [InlineData("f(1)(2)")]
    [InlineData("{1: }")]
    [InlineData("{1}")]
    [InlineData("Msg{f}")]
    [InlineData("while")]
    [InlineData("import")]
    [InlineData("a b")]
    [InlineData("")]
    [InlineData("in")]
    public void ParseErrors(string source) => ParseError(source);

    [Fact]
    public void ReservedIdentifierMessage() => Assert.Equal("reserved identifier: while", ParseError("while"));

    // ---- capacity requirements (spec: 32 repetitions, 24 chains, 12 nesting minimums) -------------

    [Fact]
    public void ChainedOperatorsAtSpecCapacity()
    {
        ParseToString(string.Join(" && ", Enumerable.Repeat("x", 33)));
        ParseToString(string.Join(" || ", Enumerable.Repeat("x", 33)));
        ParseToString(string.Join(" + ", Enumerable.Repeat("1", 33)));
        ParseToString("f(" + string.Join(", ", Enumerable.Repeat("1", 32)) + ")");
        ParseToString("[" + string.Join(", ", Enumerable.Repeat("1", 32)) + "]");
        ParseToString(string.Join(" < ", Enumerable.Repeat("x", 25)));
    }

    [Fact]
    public void NestedTernariesAtSpecCapacity()
    {
        // Ternaries chain through the else branch (right-associative); a bare ternary in the
        // truthy branch is a grammar error, so nesting via ':' is the canonical deep form.
        var expr = "x";
        for (var i = 0; i < 24; i++)
        {
            expr = $"c ? y : {expr}";
        }

        ParseToString(expr);

        // A ternary in the truthy branch requires parentheses.
        ParseError("a ? b ? c : d : e");
    }

    [Fact]
    public void RecursiveNestingAtSpecCapacity()
    {
        ParseToString(new string('(', 12) + "x" + new string(')', 12));
        ParseToString(new string('[', 12) + "x" + new string(']', 12));
        ParseToString(string.Concat(Enumerable.Repeat("f(", 12)) + "x" + new string(')', 12));
        ParseToString(string.Concat(Enumerable.Repeat("{1: ", 12)) + "x" + new string('}', 12));
    }

    [Fact]
    public void RecursionLimitEnforced()
    {
        // Depending on available stack, either the depth limit or the stack-headroom probe trips
        // first; both must surface as a clean parse error (never a crash).
        var deep = new string('(', 300) + "x" + new string(')', 300);
        var message = ParseError(deep);
        Assert.True(
            message.Contains("recursion limit") || message.Contains("exceeds available stack"),
            $"unexpected error message: {message}");

        var wayTooDeep = new string('(', 10_000) + "x" + new string(')', 10_000);
        ParseError(wayTooDeep);
    }

    [Fact]
    public void MacroCallsAreRecordedInSourceInfo()
    {
        var result = CelParser.Parse("[1].all(x, x > 0)");
        var macroCalls = result.Ast!.SourceInfo.MacroCalls;
        var call = Assert.IsType<CallExpr>(Assert.Single(macroCalls).Value);
        Assert.Equal("all", call.Function);
        Assert.NotNull(call.Target);
    }
}
