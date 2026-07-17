using System.Diagnostics;
using Celly.Checking;
using Celly.Interpreter;
using Celly.Types;
using Celly.Values;
using Xunit;

namespace Celly.Tests.Stress;

/// <summary>
/// Evaluation over large collections must remain correct, terminate in bounded time, and allocate
/// proportionally (not pathologically) — the rope keeps comprehension accumulation O(n).
/// </summary>
public class LargeDataTests
{
    private static CelProgram Compile(string expression) =>
        CelEnv.Create(new CelEnvSettings
        {
            Declarations = [new VariableDecl("big", CelType.List(CelType.Int))],
        }).Compile(expression);

    private static ValueActivation Data(int n) => new(new Dictionary<string, CelValue>
    {
        ["big"] = ListValue.Of([.. Enumerable.Range(0, n).Select(i => (CelValue)IntValue.Of(i))]),
    });

    [Fact]
    public void MapFilterOverHundredThousandElements()
    {
        var program = Compile("big.filter(x, x % 2 == 0).map(x, x * 2).size()");
        var sw = Stopwatch.StartNew();
        var result = program.Eval(Data(100_000));
        sw.Stop();

        Assert.Equal(50_000L, ((IntValue)result).Value);
        Assert.True(sw.ElapsedMilliseconds < 2000, $"100k-element comprehension took {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void ComprehensionAccumulationIsLinearNotQuadratic()
    {
        // The rope makes `@result + [x]` O(1); a full map over the list is O(n). Doubling n should
        // roughly double time — NOT quadruple it (which the old copy-on-append behavior would).
        var program = Compile("big.map(x, x + 1).size()");

        double Time(int n)
        {
            program.Eval(Data(n)); // warm
            var sw = Stopwatch.StartNew();
            for (var r = 0; r < 20; r++)
            {
                program.Eval(Data(n));
            }

            return sw.Elapsed.TotalMilliseconds;
        }

        var t50k = Time(50_000);
        var t100k = Time(100_000);
        // Linear ⇒ ~2×. Allow generous headroom for noise/GC; quadratic would be ~4×+.
        Assert.True(t100k < t50k * 3.0, $"scaling looks super-linear: 50k={t50k:F0}ms, 100k={t100k:F0}ms (ratio {t100k / t50k:F1})");
    }

    [Fact]
    public void ExistsShortCircuitsOverLargeList()
    {
        // exists should stop at the first match — near-instant even on a huge list when the match
        // is at the front.
        var program = Compile("big.exists(x, x == 0)");
        var sw = Stopwatch.StartNew();
        var result = program.Eval(Data(1_000_000));
        sw.Stop();
        Assert.True(((BoolValue)result).Value);
        Assert.True(sw.ElapsedMilliseconds < 500, $"early-exit exists took {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void NestedComprehensionOverLargeDataRespectsBudget()
    {
        // 10k × 10k = 100M inner iterations — the budget must cut it off. A modest budget keeps
        // the abort fast: the guard's value is bounding cost, so it should trip early.
        var env = CelEnv.Create(new CelEnvSettings
        {
            Declarations = [new VariableDecl("big", CelType.List(CelType.Int))],
            EvalLimits = new EvalLimits { MaxIterations = 500_000 },
        });
        var program = env.Compile("big.map(x, big.filter(y, y > x).size()).size()");

        var sw = Stopwatch.StartNew();
        var result = program.Eval(Data(10_000));
        sw.Stop();

        Assert.Contains("iteration budget", Assert.IsType<ErrorValue>(result).Message);
        Assert.True(sw.ElapsedMilliseconds < 3000, $"budget abort took {sw.ElapsedMilliseconds}ms on large data");
    }

    [Fact]
    public void LargeMapValueLookup()
    {
        // Build and index a large map inside an expression.
        var env = CelEnv.Create(new CelEnvSettings
        {
            Declarations = [new VariableDecl("keys", CelType.List(CelType.Int))],
        });
        var program = env.Compile("keys.map(k, k).filter(k, k == 9999).size()");
        var data = new ValueActivation(new Dictionary<string, CelValue>
        {
            ["keys"] = ListValue.Of([.. Enumerable.Range(0, 50_000).Select(i => (CelValue)IntValue.Of(i))]),
        });
        Assert.Equal(1L, ((IntValue)program.Eval(data)).Value);
    }
}
