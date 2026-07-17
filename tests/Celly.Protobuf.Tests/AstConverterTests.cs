using Celly;
using Celly.Ast;
using Google.Protobuf;
using Celly.Checking;
using Celly.Protobuf;
using Celly.Types;
using Celly.Values;
using Xunit;

namespace Celly.Protobuf.Tests;

public class AstConverterTests
{
    [Theory]
    [InlineData("1 + 2 * 3")]
    [InlineData("a && b ? 'x' : 'y'")]
    [InlineData("[1, 2u, 3.5, \"s\", b\"b\", true, null]")]
    [InlineData("{\"k\": [1, 2], 3: {4: 5}}")]
    [InlineData("Msg{f: 1, g: [2]}")]
    [InlineData("[1, 2].all(x, x > 0)")]
    [InlineData("has(m.f) && m.exists(k, m[k] > 1)")]
    [InlineData("a.?b.orValue([?c])")]
    [InlineData("x.startsWith('a') || x.matches('b+')")]
    public void ParsedExprRoundTripsLosslessly(string expression)
    {
        var ast = CelEnv.Default.Parse(expression).Ast!;

        var proto = AstConverter.ToParsedExpr(ast);
        var restored = AstConverter.FromParsedExpr(proto);

        // Structure identical (formatter includes every node in evaluation order).
        Assert.Equal(ExprFormatter.Format(ast.Expr), ExprFormatter.Format(restored.Expr));

        // Ids and positions preserved.
        Assert.Equal(ast.Expr.Id, restored.Expr.Id);
        Assert.Equal(ast.SourceInfo.Positions, restored.SourceInfo.Positions);
        Assert.Equal(ast.SourceInfo.MacroCalls.Count, restored.SourceInfo.MacroCalls.Count);

        // Proto → native → proto is a fixed point (expression part; proto equality).
        Assert.Equal(proto.Expr, AstConverter.ToParsedExpr(restored).Expr);
    }

    [Fact]
    public void DeserializedAstEvaluates()
    {
        var ast = CelEnv.Default.Parse("[1, 2, 3].filter(x, x > n).size()").Ast!;
        var bytes = AstConverter.ToParsedExpr(ast).ToByteArray();   // serialize…

        var parsed = Cel.Expr.ParsedExpr.Parser.ParseFrom(bytes);   // …ship/store/reload…
        var restored = AstConverter.FromParsedExpr(parsed);
        var program = CelEnv.Default.Program(restored);             // …and evaluate

        var result = program.Eval(new Dictionary<string, object?> { ["n"] = 1L });
        Assert.Equal(2L, Assert.IsType<IntValue>(result).Value);
    }

    [Fact]
    public void CheckedExprCarriesTypesAndReferences()
    {
        var env = CelEnv.Create(new CelEnvSettings
        {
            Declarations = [new VariableDecl("x", CelType.Int)],
        });
        var ast = env.Parse("x + 1 > 2").Ast!;
        Assert.False(env.Check(ast).HasErrors);

        var checkedExpr = AstConverter.ToCheckedExpr(ast);

        // The root deduces bool; the type map covers every node.
        Assert.Equal(Cel.Expr.Type.Types.PrimitiveType.Bool, checkedExpr.TypeMap[ast.Expr.Id].Primitive);
        Assert.True(checkedExpr.TypeMap.Count >= 5);

        // References include the resolved variable and the matched overloads.
        Assert.Contains(checkedExpr.ReferenceMap.Values, r => r.Name == "x");
        Assert.Contains(checkedExpr.ReferenceMap.Values, r => r.OverloadId.Contains("add_int64"));

        // And it all comes back.
        var restored = AstConverter.FromCheckedExpr(checkedExpr);
        Assert.True(restored.IsChecked);
        Assert.Equal(CelTypeKind.Bool, restored.TypeMap![restored.Expr.Id].Kind);
        Assert.Contains(restored.ReferenceMap!.Values, r => r.OverloadIds.Contains("add_int64"));
    }

    [Fact]
    public void TypeConversionRoundTrips()
    {
        CelType[] types =
        [
            CelType.Int, CelType.String, CelType.Dyn, CelType.Null,
            CelType.List(CelType.Int),
            CelType.Map(CelType.String, CelType.List(CelType.Double)),
            CelType.Struct("my.pkg.Msg"),
            CelType.TypeParam("A"),
            CelType.Timestamp, CelType.Duration,
            CelType.Opaque("wrapper", CelType.Int),
            new CelType(CelTypeKind.Type, "type", [CelType.Bool]),
        ];
        foreach (var type in types)
        {
            var roundTripped = TypeConverter.ToCelType(AstConverter.ToProtoType(type));
            Assert.True(
                TypeSubstitution.StructuralEquals(type, roundTripped),
                $"{type} round-tripped to {roundTripped}");
        }
    }
}
