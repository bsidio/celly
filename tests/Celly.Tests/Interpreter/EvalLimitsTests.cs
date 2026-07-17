using System.Diagnostics;
using Celly.Checking;
using Celly.Interpreter;
using Celly.Types;
using Celly.Values;
using Xunit;

namespace Celly.Tests.Interpreter;

public class EvalLimitsTests
{
    private static CelProgram Compile(string expression, EvalLimits? limits = null) =>
        CelEnv.Create(new CelEnvSettings
        {
            Declarations = [new VariableDecl("r", CelType.List(CelType.Int))],
            EvalLimits = limits ?? EvalLimits.None,
        }).Compile(expression);

    private static IReadOnlyList<long> Range(int n) => [.. Enumerable.Range(0, n).Select(i => (long)i)];

    // ---- the bomb must abort -----------------------------------------------------------------------

    [Fact]
    public void QuadraticBombAbortsWithinBudget()
    {
        // r.map(x, r.map(y, y)) over a 5000-element list is 25,000,000 inner iterations —
        // unbounded, that's seconds of CPU and ~gigabytes. With a budget it must abort fast.
        var program = Compile("r.map(x, r.map(y, y)).size()", new EvalLimits { MaxIterations = 1_000_000 });
        var bindings = new Dictionary<string, object?> { ["r"] = Range(5000) };

        var sw = Stopwatch.StartNew();
        var result = program.Eval(bindings);
        sw.Stop();

        var error = Assert.IsType<ErrorValue>(result);
        Assert.Contains("iteration budget", error.Message);
        Assert.True(sw.ElapsedMilliseconds < 2000, $"budget abort took {sw.ElapsedMilliseconds}ms — too slow");
    }

    [Fact]
    public void BudgetCountsAcrossNestedComprehensions()
    {
        // 100 outer x 100 inner = 10,000 iterations. A 5,000 budget must trip; 20,000 must pass.
        var over = Compile("r.map(x, r.exists(y, y == x)).size()", new EvalLimits { MaxIterations = 5_000 });
        var under = Compile("r.map(x, r.exists(y, y == x)).size()", new EvalLimits { MaxIterations = 20_000 });
        var bindings = new Dictionary<string, object?> { ["r"] = Range(100) };

        Assert.IsType<ErrorValue>(over.Eval(bindings));
        Assert.Equal(100L, Assert.IsType<IntValue>(under.Eval(bindings)).Value);
    }

    [Fact]
    public void WithinBudgetSucceeds()
    {
        var program = Compile("r.filter(x, x % 2 == 0).size()", new EvalLimits { MaxIterations = 1000 });
        Assert.Equal(50L, Assert.IsType<IntValue>(program.Eval(new Dictionary<string, object?> { ["r"] = Range(100) })).Value);
    }

    [Fact]
    public void UnlimitedByDefault()
    {
        // No limits configured: a modestly large comprehension still runs.
        var program = Compile("r.map(x, x * 2).size()");
        Assert.Equal(10_000L, Assert.IsType<IntValue>(program.Eval(new Dictionary<string, object?> { ["r"] = Range(10_000) })).Value);
    }

    // ---- per-call override -------------------------------------------------------------------------

    [Fact]
    public void PerCallLimitsOverrideEnvDefault()
    {
        var program = Compile("r.map(x, x).size()");  // env: unlimited
        var bindings = new Dictionary<string, object?> { ["r"] = Range(1000) };

        // A tight per-call budget aborts the same compiled program…
        Assert.IsType<ErrorValue>(program.Eval(bindings, new EvalLimits { MaxIterations = 100 }));
        // …while the default (unlimited) call succeeds.
        Assert.Equal(1000L, Assert.IsType<IntValue>(program.Eval(bindings)).Value);
    }

    // ---- cancellation ------------------------------------------------------------------------------

    [Fact]
    public void CancellationTokenAborts()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var program = Compile("r.map(x, r.map(y, y)).size()");
        var result = program.Eval(
            new Dictionary<string, object?> { ["r"] = Range(5000) },
            new EvalLimits { CancellationToken = cts.Token });
        Assert.Contains("cancelled", Assert.IsType<ErrorValue>(result).Message);
    }

    // ---- concurrency safety of the budget ----------------------------------------------------------

    [Fact]
    public void BudgetIsPerEvalNotShared()
    {
        // Each Eval gets a fresh iteration count — concurrent evals of one program must not
        // exhaust a shared counter.
        var program = Compile("r.map(x, x).size()", new EvalLimits { MaxIterations = 200 });
        var bindings = new Dictionary<string, object?> { ["r"] = Range(100) };

        Parallel.For(0, 200, _ =>
            Assert.Equal(100L, Assert.IsType<IntValue>(program.Eval(bindings)).Value));
    }
}
