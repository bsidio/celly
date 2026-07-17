using Celly.Checking;
using Celly.Types;
using Xunit;

namespace Celly.Tests.Checking;

public class CheckerTests
{
    private static CheckResult Check(string expression, CelEnvSettings? settings = null)
    {
        var env = CelEnv.Create(settings);
        var parsed = env.Parse(expression);
        Assert.NotNull(parsed.Ast);
        return env.Check(parsed.Ast!);
    }

    private static CelType TypeOf(string expression, CelEnvSettings? settings = null)
    {
        var env = CelEnv.Create(settings);
        var parsed = env.Parse(expression);
        Assert.NotNull(parsed.Ast);
        var result = env.Check(parsed.Ast!);
        Assert.False(result.HasErrors, string.Join("; ", result.Issues));
        return result.TypeOf(parsed.Ast!.Expr);
    }

    private static void AssertType(string expression, CelType expected, CelEnvSettings? settings = null) =>
        Assert.True(
            TypeSubstitution.StructuralEquals(expected, TypeOf(expression, settings)),
            $"expected {expected}, got {TypeOf(expression, settings)}");

    [Fact]
    public void Literals()
    {
        AssertType("1", CelType.Int);
        AssertType("1u", CelType.Uint);
        AssertType("1.5", CelType.Double);
        AssertType("'s'", CelType.String);
        AssertType("b's'", CelType.Bytes);
        AssertType("true", CelType.Bool);
        AssertType("null", CelType.Null);
    }

    [Fact]
    public void Operators()
    {
        AssertType("1 + 2", CelType.Int);
        AssertType("1u + 2u", CelType.Uint);
        AssertType("1.0 + 2.0", CelType.Double);
        AssertType("'a' + 'b'", CelType.String);
        AssertType("1 < 2", CelType.Bool);
        AssertType("1 == 2", CelType.Bool);
        AssertType("true && false", CelType.Bool);
        AssertType("true ? 1 : 2", CelType.Int);
        AssertType("-(1)", CelType.Int);
        AssertType("!true", CelType.Bool);
    }

    [Fact]
    public void Aggregates()
    {
        AssertType("[1, 2]", CelType.List(CelType.Int));
        AssertType("[]", CelType.List(CelType.Dyn));
        AssertType("[1, 'a']", CelType.List(CelType.Dyn));
        AssertType("{'k': 1}", CelType.Map(CelType.String, CelType.Int));
        AssertType("{}", CelType.Map(CelType.Dyn, CelType.Dyn));
        AssertType("[1, 2][0]", CelType.Int);
        AssertType("{'k': 1.5}['k']", CelType.Double);
        AssertType("size([1])", CelType.Int);
        AssertType("1 in [1]", CelType.Bool);
    }

    [Fact]
    public void EmptyListUnifiesThroughConcat() =>
        // [] gets a fresh type parameter that unifies with list(int) via add_list.
        AssertType("[] + [1, 2]", CelType.List(CelType.Int));

    [Fact]
    public void Comprehensions()
    {
        AssertType("[1, 2].all(x, x > 0)", CelType.Bool);
        AssertType("[1, 2].map(x, x * 2)", CelType.List(CelType.Int));
        AssertType("[1, 2].filter(x, x > 1)", CelType.List(CelType.Int));
        AssertType("[1, 2].exists_one(x, x == 1)", CelType.Bool);
        AssertType("{'a': 1}.all(k, k != '')", CelType.Bool);
        AssertType("[1.5].map(x, x)", CelType.List(CelType.Double));
    }

    [Fact]
    public void TypeValues()
    {
        AssertType("type(1)", new CelType(CelTypeKind.Type, "type", [CelType.Int]));
        AssertType("type(1) == int", CelType.Bool);
        AssertType("type(0.0) != type(0)", CelType.Bool); // differing type params still compare
        AssertType("dyn(1)", CelType.Dyn);
    }

    [Fact]
    public void DeclaredVariables()
    {
        var settings = new CelEnvSettings
        {
            Declarations = [new VariableDecl("x", CelType.Int), new VariableDecl("names", CelType.List(CelType.String))],
        };
        AssertType("x + 1", CelType.Int, settings);
        AssertType("names[0]", CelType.String, settings);
        AssertType("names.map(n, n + '!')", CelType.List(CelType.String), settings);
    }

    [Fact]
    public void ContainerResolution()
    {
        var settings = new CelEnvSettings
        {
            Container = "a.b",
            Declarations = [new VariableDecl("a.b.v", CelType.Int), new VariableDecl("v", CelType.String)],
        };
        AssertType("v", CelType.Int, settings); // a.b.v wins over v
        AssertType(".v", CelType.String, settings); // absolute
    }

    [Fact]
    public void ComprehensionVariablesShadowQualifiedNames()
    {
        var settings = new CelEnvSettings
        {
            Declarations = [new VariableDecl("y.z", CelType.String)],
        };
        // Inside the comprehension, y is the iteration variable — y.z selects its field.
        AssertType("[{'z': 0}].exists(y, y.z == 0)", CelType.Bool, settings);
    }

    [Fact]
    public void Errors()
    {
        Assert.True(Check("undeclared_var").HasErrors);
        Assert.True(Check("1 + 1.0").HasErrors);
        Assert.True(Check("1 + 'a'").HasErrors);
        Assert.True(Check("1 < 'a'").HasErrors);
        Assert.True(Check("'a'.startsWith(1)").HasErrors);
        Assert.True(Check("size(1)").HasErrors);
        Assert.True(Check("unknownFn(1)").HasErrors);
        Assert.False(Check("[].all(e, e > 0) && [].all(e, e != '')").HasErrors); // params independent per call
        Assert.True(Check("true ? 1 : 'a'").HasErrors); // ternary branches must unify
        Assert.False(Check("true ? 1 : dyn('a')").HasErrors); // dyn absorbs
    }

    [Fact]
    public void ErrorMessages()
    {
        var result = Check("missing_var");
        Assert.Contains("undeclared reference to 'missing_var'", result.Issues[0].Message);

        result = Check("1 + 1.0");
        Assert.Contains("found no matching overload for '_+_' applied to '(int, double)'", result.Issues[0].Message);
    }

    [Fact]
    public void StdlibSignatures()
    {
        AssertType("string(1)", CelType.String);
        AssertType("int('1')", CelType.Int);
        AssertType("duration('1h')", CelType.Duration);
        AssertType("timestamp('2009-02-13T23:31:30Z')", CelType.Timestamp);
        AssertType("timestamp('2009-02-13T23:31:30Z') - timestamp('2009-02-13T23:31:30Z')", CelType.Duration);
        AssertType("timestamp('2009-02-13T23:31:30Z').getFullYear()", CelType.Int);
        AssertType("duration('1h').getHours()", CelType.Int);
        AssertType("'abc'.matches('b')", CelType.Bool);
        AssertType("matches('abc', 'b')", CelType.Bool);
        AssertType("'abc'.size()", CelType.Int);
    }
}
