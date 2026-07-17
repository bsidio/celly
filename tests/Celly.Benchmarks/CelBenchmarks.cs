using BenchmarkDotNet.Attributes;
using Celly;
using Celly.Checking;
using Celly.Types;
using Celly.Values;
using Cel.Checker;
using Cel.Tools;

namespace Celly.Benchmarks;

/// <summary>
/// Absolute parse/check/plan/eval costs for Celly, plus an apples-to-apples eval comparison
/// against Cel.NET (rayokota) on the same runtime and expression. The comparison isolates the
/// steady-state cost that matters for policy engines: a pre-compiled program evaluated repeatedly.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkDotNet.Configs.BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class CelBenchmarks
{
    // Representative expressions, from trivial to comprehension-heavy.
    private const string Simple = "x + 1 > 3 && name.startsWith('h')";
    private const string Comprehension = "items.filter(i, i % 2 == 0).map(i, i * i).exists(i, i > 100)";

    // ---- Celly setup ----
    private static readonly CelEnv CellyEnv = CelEnv.Create(new CelEnvSettings
    {
        Declarations =
        [
            new VariableDecl("x", CelType.Int),
            new VariableDecl("name", CelType.String),
            new VariableDecl("items", CelType.List(CelType.Int)),
        ],
    });

    private CelProgram _cellySimple = null!;
    private CelProgram _cellyComprehension = null!;

    // ---- Cel.NET setup ----
    private Script _celnetSimple = null!;
    private Script _celnetComprehension = null!;

    private Dictionary<string, object?> _args = null!;
    private Dictionary<string, object> _celnetArgs = null!;

    [GlobalSetup]
    public void Setup()
    {
        _cellySimple = CellyEnv.Compile(Simple);
        _cellyComprehension = CellyEnv.Compile(Comprehension);

        var host = ScriptHost.NewBuilder().Build();
        _celnetSimple = host.BuildScript(Simple)
            .WithDeclarations(Decls.NewVar("x", Decls.Int), Decls.NewVar("name", Decls.String))
            .Build();
        _celnetComprehension = host.BuildScript(Comprehension)
            .WithDeclarations(Decls.NewVar("items", Decls.NewListType(Decls.Int)))
            .Build();

        var items = Enumerable.Range(0, 100).Select(i => (long)i).ToList();
        _args = new Dictionary<string, object?> { ["x"] = 42L, ["name"] = "hello", ["items"] = items };
        _celnetArgs = new Dictionary<string, object> { ["x"] = 42L, ["name"] = "hello", ["items"] = items };
    }

    // ---- Celly-only pipeline costs ----

    [BenchmarkCategory("pipeline"), Benchmark]
    public object Celly_Parse() => CellyEnv.Parse(Simple);

    [BenchmarkCategory("pipeline"), Benchmark]
    public object Celly_Check() => CellyEnv.Check(CellyEnv.Parse(Simple).Ast!);

    [BenchmarkCategory("pipeline"), Benchmark]
    public object Celly_Compile() => CellyEnv.Compile(Simple);

    // ---- eval: simple expression ----

    [BenchmarkCategory("eval-simple"), Benchmark(Baseline = true)]
    public CelValue Celly_Eval_Simple() => _cellySimple.Eval(_args);

    [BenchmarkCategory("eval-simple"), Benchmark]
    public bool CelNet_Eval_Simple() => _celnetSimple.Execute<bool>(_celnetArgs);

    // ---- eval: comprehension-heavy expression ----

    [BenchmarkCategory("eval-comprehension"), Benchmark(Baseline = true)]
    public CelValue Celly_Eval_Comprehension() => _cellyComprehension.Eval(_args);

    [BenchmarkCategory("eval-comprehension"), Benchmark]
    public bool CelNet_Eval_Comprehension() => _celnetComprehension.Execute<bool>(_celnetArgs);
}
