using Celly.Checking;
using Celly.Types;
using Celly.Values;
using Xunit;

namespace Celly.Tests.Stress;

/// <summary>
/// Validates that a single compiled <see cref="CelProgram"/> is safe to evaluate from many
/// threads at once — including through the lazily-materialized concatenation rope, whose
/// flatten step uses a deliberate benign data race.
/// </summary>
public class ConcurrencyTests
{
    [Fact]
    public void OneProgramManyThreadsDistinctData()
    {
        var env = CelEnv.Create(new CelEnvSettings
        {
            Declarations = [new VariableDecl("n", CelType.Int)],
        });
        // Comprehension exercises the rope (map builds a list via repeated '+ [x]').
        var program = env.Compile("[0, 1, 2, 3, 4].map(x, x + n).filter(x, x % 2 == 0)");

        const int threads = 32;
        const int perThread = 5_000;
        Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
        {
            var n = (long)t;
            var bindings = new Dictionary<string, object?> { ["n"] = n };
            // Expected: [0+n, 2+n, 4+n] filtered to evens after adding n.
            var expected = new[] { 0L, 1, 2, 3, 4 }.Select(x => x + n).Where(x => x % 2 == 0).ToList();
            for (var i = 0; i < perThread; i++)
            {
                var result = program.Eval(bindings);
                var list = Assert.IsAssignableFrom<ListValue>(result);
                Assert.Equal(expected, list.Elements.Select(e => ((IntValue)e).Value));
            }
        });
    }

    [Fact]
    public void SharedRopeValueMaterializesConsistentlyUnderContention()
    {
        // One program yields a list value; many threads read (materialize) the same result
        // graph concurrently. The rope's lazy flatten must produce identical contents every time.
        var program = CelEnv.Default.Compile("[1, 2] + [3, 4] + [5, 6] + [7, 8]");
        var shared = Assert.IsAssignableFrom<ListValue>(program.Eval());

        long[] Snapshot() => [.. shared.Elements.Select(e => ((IntValue)e).Value)];

        var results = new long[64][];
        Parallel.For(0, 64, i => results[i] = Snapshot());
        foreach (var snapshot in results)
        {
            Assert.Equal([1L, 2, 3, 4, 5, 6, 7, 8], snapshot);
        }
    }

    [Fact]
    public void ConcurrentCompileAndEvalOnSharedEnv()
    {
        // CelEnv is immutable; compiling and evaluating from many threads must not corrupt it.
        var env = CelEnv.Create(new CelEnvSettings
        {
            Declarations = [new VariableDecl("x", CelType.Int)],
        });

        Parallel.For(0, 5_000, i =>
        {
            var program = env.Compile($"x + {i}");
            var result = program.Eval(new Dictionary<string, object?> { ["x"] = 100L });
            Assert.Equal(100L + i, ((IntValue)result).Value);
        });
    }
}
