using Celly.Checking;
using Celly.Common;
using Celly.Types;
using Celly.Values;
using Xunit;

namespace Celly.Tests.Stress;

public class SoakTests
{
    /// <summary>
    /// Evaluates a compiled program a few million times and asserts memory does not grow
    /// unbounded — catches per-eval leaks (retained references, growing caches, un-reset state).
    /// </summary>
    [Fact]
    public void SustainedEvaluationDoesNotLeak()
    {
        var env = CelEnv.Create(new CelEnvSettings
        {
            Declarations = [new VariableDecl("x", CelType.Int), new VariableDecl("s", CelType.String)],
            Libraries = [Celly.Extensions.StringsLibrary.Instance],
        });
        var program = env.Compile("[1, 2, 3].map(i, i + x).filter(i, i > 1).size() + s.size()");

        void Run(int iterations)
        {
            for (var i = 0; i < iterations; i++)
            {
                var result = program.Eval(new Dictionary<string, object?> { ["x"] = (long)i, ["s"] = "hello" });
                // Force the lazy rope to materialize each time.
                _ = ((IntValue)result).Value;
            }
        }

        Run(50_000); // warm up JIT + caches
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetTotalMemory(forceFullCollection: true);

        Run(2_000_000);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var after = GC.GetTotalMemory(forceFullCollection: true);

        var growthMb = (after - before) / (1024.0 * 1024.0);
        Assert.True(growthMb < 5.0, $"retained memory grew {growthMb:F1} MB over 2M evals — possible leak");
    }

    /// <summary>Matches() compiles patterns into a bounded cache; churning distinct patterns must not leak.</summary>
    [Fact]
    public void RegexCacheIsBounded()
    {
        var env = CelEnv.Create(new CelEnvSettings { Declarations = [new VariableDecl("s", CelType.String)] });

        GC.Collect();
        var before = GC.GetTotalMemory(true);
        for (var i = 0; i < 10_000; i++)
        {
            // A distinct pattern each time — the bounded LRU must evict, not grow forever.
            var program = env.Compile($"s.matches('a{i}b+')");
            _ = program.Eval(new Dictionary<string, object?> { ["s"] = "aaabbb" });
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var after = GC.GetTotalMemory(true);
        Assert.True((after - before) / (1024.0 * 1024.0) < 20.0, "regex cache appears unbounded");
    }
}

public class OperatorsTests
{
    // Covers the public Operators.FindBinary lookup (part of the stable API surface).
    [Theory]
    [InlineData("+", Operators.Add)]
    [InlineData("-", Operators.Subtract)]
    [InlineData("*", Operators.Multiply)]
    [InlineData("<", Operators.Less)]
    [InlineData("==", Operators.Equals)]
    [InlineData("&&", Operators.LogicalAnd)]
    [InlineData("||", Operators.LogicalOr)]
    [InlineData("in", Operators.In)]
    public void FindBinaryMapsSymbols(string symbol, string expected) =>
        Assert.Equal(expected, Operators.FindBinary(symbol));
}
