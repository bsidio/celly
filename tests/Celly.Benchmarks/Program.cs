using System.Diagnostics;
using BenchmarkDotNet.Running;
using Celly;
using Celly.Checking;
using Celly.Interpreter;
using Celly.Types;
using Celly.Values;

// `dotnet run -c Release -- throughput` runs a sustained multi-core load measurement.
// Force Server GC for a server-representative profile: DOTNET_gcServer=1.
// Anything else runs the BenchmarkDotNet suite.
if (args.Length > 0 && args[0] == "throughput")
{
    Throughput.Run();
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

internal static class Throughput
{
    public static void Run()
    {
        var env = CelEnv.Create(new CelEnvSettings
        {
            Declarations =
            [
                new VariableDecl("x", CelType.Int),
                new VariableDecl("name", CelType.String),
                new VariableDecl("items", CelType.List(CelType.Int)),
            ],
        });

        var simple = env.Compile("x + 1 > 3 && name.startsWith('h')");
        var comprehension = env.Compile("items.filter(i, i % 2 == 0).map(i, i * i).exists(i, i > 100)");

        // Pre-adapted, immutable activation shared across threads — isolates eval throughput from
        // the caller's per-request dictionary allocation (which would otherwise dominate at 50M).
        var items = ListValue.Of([.. Enumerable.Range(0, 100).Select(i => (CelValue)IntValue.Of(i))]);
        var activation = new ValueActivation(new Dictionary<string, CelValue>
        {
            ["x"] = IntValue.Of(42),
            ["name"] = StringValue.Of("hello"),
            ["items"] = items,
        });

        Console.WriteLine($"Server GC: {System.Runtime.GCSettings.IsServerGC} | Cores: {Environment.ProcessorCount}");
        Console.WriteLine("(shared pre-adapted activation — measures eval throughput + scaling)\n");

        // Thread-count sweep reveals the scaling curve (isolating hardware heterogeneity/turbo
        // from any serialization — this eval path has no locks).
        Console.WriteLine("[simple] scaling sweep:");
        foreach (var threads in new[] { 1, 2, 4, 8, Environment.ProcessorCount })
        {
            var perSec = Sweep(simple, activation, threads, 20_000_000);
            Console.WriteLine($"  {threads,2} threads : {perSec / 1e6,6:F2} M/s   ({perSec / threads / 1e6:F2} M/s/thread)");
        }

        Console.WriteLine();
        Measure("simple", simple, activation, 50_000_000);
        Measure("comprehension", comprehension, activation, 5_000_000);
    }

    private static double Sweep(CelProgram program, IActivation activation, int threads, long total)
    {
        var perThread = total / threads;
        for (var i = 0; i < 50_000; i++)
        {
            program.Eval(activation);
        }

        var sw = Stopwatch.StartNew();
        Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, _ =>
        {
            for (long i = 0; i < perThread; i++)
            {
                program.Eval(activation);
            }
        });
        sw.Stop();
        return perThread * threads / sw.Elapsed.TotalSeconds;
    }

    private static void Measure(string label, CelProgram program, IActivation activation, long total)
    {
        var threads = Environment.ProcessorCount;
        var perThread = total / threads;

        // Warm up.
        for (var i = 0; i < 100_000; i++)
        {
            program.Eval(activation);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var gen0Before = GC.CollectionCount(0);
        var gen1Before = GC.CollectionCount(1);
        var gen2Before = GC.CollectionCount(2);
        var memBefore = GC.GetTotalMemory(true);

        var sw = Stopwatch.StartNew();
        Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
        {
            for (long i = 0; i < perThread; i++)
            {
                program.Eval(activation);
            }
        });
        sw.Stop();

        var done = perThread * threads;
        var gen2After = GC.CollectionCount(2);
        var memAfterLive = GC.GetTotalMemory(false);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var memAfter = GC.GetTotalMemory(true);

        var perSec = done / sw.Elapsed.TotalSeconds;
        Console.WriteLine($"[{label}]");
        Console.WriteLine($"  {done:N0} evals across {threads} threads in {sw.Elapsed.TotalSeconds:F2}s");
        Console.WriteLine($"  throughput : {perSec / 1e6:F2} M evals/sec");
        Console.WriteLine($"  GC gen0    : {GC.CollectionCount(0) - gen0Before:N0}  gen1: {GC.CollectionCount(1) - gen1Before:N0}  gen2: {gen2After - gen2Before:N0}");
        Console.WriteLine($"  memory     : before {memBefore / 1024.0 / 1024.0:F1}MB  live-peak {memAfterLive / 1024.0 / 1024.0:F1}MB  after-GC {memAfter / 1024.0 / 1024.0:F1}MB");
        Console.WriteLine();
    }
}
