using Celly.Ast;
using Xunit;

namespace Celly.Tests.Ast;

public class AstToolsTests
{
    private static CelAbstractSyntax Parse(string expression)
    {
        var result = CelEnv.Default.Parse(expression);
        Assert.NotNull(result.Ast);
        return result.Ast!;
    }

    [Fact]
    public void ReferencedVariablesFindsFreeRoots()
    {
        var ast = Parse("a.b + c && list.exists(x, x > threshold)");
        var vars = AstTools.ReferencedVariables(ast.Expr);
        Assert.Equal(["a", "c", "list", "threshold"], vars.Order());
    }

    [Fact]
    public void ComprehensionVariablesAreNotFree()
    {
        var ast = Parse("[1, 2].map(x, x * factor)");
        var vars = AstTools.ReferencedVariables(ast.Expr);
        Assert.DoesNotContain("x", vars);
        Assert.Contains("factor", vars);
    }

    [Fact]
    public void CalledFunctionsIncludesOperators()
    {
        var functions = AstTools.CalledFunctions(Parse("size(x) + 1 == 2").Expr);
        Assert.Contains("size", functions);
        Assert.Contains("_+_", functions);
        Assert.Contains("_==_", functions);
    }

    [Fact]
    public void DescendantsVisitsEveryNode()
    {
        var ast = Parse("{'k': [1, f(2)]}");
        var kinds = AstTools.DescendantsAndSelf(ast.Expr).Select(e => e.GetType().Name).ToList();
        Assert.Contains("MapExpr", kinds);
        Assert.Contains("ListExpr", kinds);
        Assert.Contains("CallExpr", kinds);
        Assert.Contains("ConstExpr", kinds);
    }
}

public class UnparserTests
{
    private static string RoundTrip(string expression)
    {
        var ast = CelEnv.Default.Parse(expression).Ast!;
        return Unparser.Unparse(ast);
    }

    [Theory]
    [InlineData("1 + 2 * 3")]
    [InlineData("(1 + 2) * 3")]
    [InlineData("a && b || c")]
    [InlineData("(a || b) && c")]
    [InlineData("a ? b : c")]
    [InlineData("(a ? b : c) ? d : e")]
    [InlineData("!x")]
    [InlineData("-(a + b)")]
    [InlineData("x.y.z")]
    [InlineData("x[0]")]
    [InlineData("f(1, 2)")]
    [InlineData("x.f(1)")]
    [InlineData("[1, 2, 3]")]
    [InlineData("{\"k\": 1}")]
    [InlineData("Msg{f: 1}")]
    [InlineData("has(m.f)")]
    [InlineData("[1, 2].all(x, x > 0)")]
    [InlineData("[1, 2].map(x, x * 2)")]
    [InlineData("m.filter(k, m[k] > 1)")]
    [InlineData("a.?b")]
    [InlineData("a[?0]")]
    [InlineData("[?a, b]")]
    [InlineData("{?\"k\": v}")]
    [InlineData("timestamp(\"2024-01-01T00:00:00Z\")")]
    [InlineData("b\"abc\"")]
    [InlineData("\"quo\\\"te\"")]
    [InlineData("1u + 2u")]
    [InlineData("1.5 / 2.0")]
    [InlineData("a in [1, 2]")]
    [InlineData("m.`content-type`")]
    public void UnparseReparseIsStable(string expression)
    {
        // unparse(parse(e)) must reparse to a structurally identical AST.
        var once = RoundTrip(expression);
        var reparsed = CelEnv.Default.Parse(once).Ast!;
        Assert.Equal(
            ExprFormatter.Format(CelEnv.Default.Parse(expression).Ast!.Expr),
            ExprFormatter.Format(reparsed.Expr));

        // And unparsing again is a fixed point.
        Assert.Equal(once, Unparser.Unparse(reparsed));
    }

    [Fact]
    public void MacrosRenderInOriginalForm()
    {
        Assert.Equal("[1, 2].all(x, x > 0)", RoundTrip("[1, 2].all(x, x > 0)"));
        Assert.Equal("has(m.f)", RoundTrip("has(m.f)"));
    }

    [Fact]
    public void PrecedenceParenthesizationIsMinimal()
    {
        Assert.Equal("1 + 2 * 3", RoundTrip("1 + (2 * 3)"));      // redundant parens dropped
        Assert.Equal("(1 + 2) * 3", RoundTrip("(1 + 2) * 3"));    // required parens kept
        Assert.Equal("a - (b - c)", RoundTrip("a - (b - c)"));    // right-assoc grouping kept
    }
}
