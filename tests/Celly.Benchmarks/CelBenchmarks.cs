using BenchmarkDotNet.Attributes;
using Celly;
using Celly.Checking;
using Celly.Extensions;
using Celly.Types;
using Celly.Values;
using Cel.Checker;
using Cel.Tools;

namespace Celly.Benchmarks;

/// <summary>
/// Absolute parse/check/plan/eval costs for Celly, plus an apples-to-apples eval comparison
/// against Cel.NET (rayokota) on the same runtime and expression. The comparison isolates the
/// steady-state cost that matters for policy engines: a pre-compiled program evaluated repeatedly.
///
/// The environment has ALL extension libraries enabled (as a real deployment and the conformance
/// runner do) so the numbers reflect a fully-loaded env — and one benchmark evaluates an
/// extension-heavy expression (strings + math) directly.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkDotNet.Configs.BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class CelBenchmarks
{
    // Representative expressions, from trivial to comprehension-heavy to extension-heavy.
    private const string Simple = "x + 1 > 3 && name.startsWith('h')";
    private const string Comprehension = "items.filter(i, i % 2 == 0).map(i, i * i).exists(i, i > 100)";
    private const string Extensions =
        "name.upperAscii().substring(0, 2) + items.map(i, string(i)).join(',') + string(math.greatest(items))";

    private static readonly CelLibrary[] AllLibraries =
    [
        OptionalsLibrary.Instance, StringsLibrary.Instance, MathLibrary.Instance,
        EncodersLibrary.Instance, BindingsLibrary.Instance, BlockLibrary.Instance,
        TwoVarComprehensionsLibrary.Instance, ProtosLibrary.Instance, NetworkLibrary.Instance,
    ];

    // ---- Celly setup: all libraries enabled ----
    private static readonly CelEnv CellyEnv = CelEnv.Create(new CelEnvSettings
    {
        Libraries = AllLibraries,
        Declarations =
        [
            new VariableDecl("x", CelType.Int),
            new VariableDecl("name", CelType.String),
            new VariableDecl("items", CelType.List(CelType.Int)),
        ],
    });

    // Bare env (no libraries) — to measure the per-eval cost of a fully-loaded env vs a minimal one.
    private static readonly CelEnv BareEnv = CelEnv.Create(new CelEnvSettings
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
    private CelProgram _cellyExtensions = null!;
    private CelProgram _cellySimpleBare = null!;

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
        _cellyExtensions = CellyEnv.Compile(Extensions);
        _cellySimpleBare = BareEnv.Compile(Simple);

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

    // ---- eval: extension-heavy expression (strings + math); Celly vs bare env ----
    // (No Cel.NET row: it doesn't ship the math extension, so the expression won't compile there.)

    [BenchmarkCategory("eval-extensions"), Benchmark]
    public CelValue Celly_Eval_Extensions() => _cellyExtensions.Eval(_args);

    // ---- does enabling all 9 libraries slow down core eval? (same expr, loaded vs bare env) ----

    [BenchmarkCategory("libs-overhead"), Benchmark(Baseline = true)]
    public CelValue Celly_Eval_Simple_AllLibs() => _cellySimple.Eval(_args);

    [BenchmarkCategory("libs-overhead"), Benchmark]
    public CelValue Celly_Eval_Simple_BareEnv() => _cellySimpleBare.Eval(_args);
}
