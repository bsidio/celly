using BenchmarkDotNet.Attributes;
using Celly;
using Celly.Ast;
using Celly.Values;

namespace Celly.Benchmarks;

[MemoryDiagnoser]
public class CelBenchmarks
{
    private static readonly CelEnv Env = CelEnv.Create(new CelEnvSettings
    {
        Declarations =
        [
            new Checking.VariableDecl("x", Types.CelType.Int),
            new Checking.VariableDecl("name", Types.CelType.String),
            new Checking.VariableDecl("items", Types.CelType.List(Types.CelType.Int)),
        ],
    });

    private const string SimpleExpr = "x + 1 > 3 && name.startsWith('h')";
    private const string ComprehensionExpr = "items.filter(i, i % 2 == 0).map(i, i * i).exists(i, i > 100)";

    private readonly CelProgram _simpleProgram = Env.Compile(SimpleExpr);
    private readonly CelProgram _comprehensionProgram = Env.Compile(ComprehensionExpr);
    private readonly Dictionary<string, object?> _bindings = new()
    {
        ["x"] = 42L,
        ["name"] = "hello",
        ["items"] = Enumerable.Range(0, 100).Select(i => (long)i).ToList(),
    };

    [Benchmark]
    public Parsing.ParseResult Parse() => Env.Parse(SimpleExpr);

    [Benchmark]
    public object Check()
    {
        var ast = Env.Parse(SimpleExpr).Ast!;
        return Env.Check(ast);
    }

    [Benchmark]
    public CelProgram Plan() => Env.Compile(SimpleExpr);

    [Benchmark]
    public CelValue EvalSimple() => _simpleProgram.Eval(_bindings);

    [Benchmark]
    public CelValue EvalComprehension() => _comprehensionProgram.Eval(_bindings);
}
