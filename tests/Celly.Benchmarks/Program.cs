using BenchmarkDotNet.Running;

// Benchmarks are added per-milestone; see docs/PLAN.md (M7).
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
