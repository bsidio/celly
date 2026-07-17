# Performance

Celly is designed for the policy-engine pattern: **compile an expression once, evaluate it
many times**. The numbers below focus on that steady-state eval cost, with an
apples-to-apples comparison against [Cel.NET](https://github.com/rayokota/cel.net) (the
other native .NET CEL library) on the same machine and runtime.

## Results

*net8.0, Apple Silicon, BenchmarkDotNet short job. Reproduce with*
`dotnet run -c Release --project tests/Celly.Benchmarks`.

### Evaluation (pre-compiled program, per call)

| Workload | Celly | Cel.NET | Speedup | Celly alloc | Cel.NET alloc |
|---|--:|--:|--:|--:|--:|
| Simple `x + 1 > 3 && name.startsWith('h')` | **126 ns** | 150 ns | 1.2× | 472 B | 416 B |
| Comprehension `items.filter(…).map(…).exists(…)` (100 elems) | **14.7 µs** | 32.4 µs | **2.2×** | **41 KB** | 105 KB |

Celly is faster on both, and markedly so on comprehension-heavy expressions — the workload
where the [rope-based list concatenation](internals/values.md) turns the
`@result + [element]` accumulation pattern from O(n²) into O(n).

### Celly pipeline costs

| Stage | Time | Allocated | Notes |
|---|--:|--:|---|
| Parse | 1.1 µs | 5.0 KB | lex + build AST |
| Compile (parse + plan) | 1.5 µs | 6.6 KB | what you pay once per expression |
| Check | 8.6 µs | 48 KB | optional; type unification is allocation-heavy |

Checking is the most expensive stage — but it runs **once at ingest**, never per
evaluation, so it's off the hot path. Parse + plan (compile) is what you pay to turn a rule
string into a reusable `CelProgram`.

## How to read this

- **Evaluate, don't recompile.** Keep the `CelProgram` from `Compile` and reuse it. The
  eval numbers assume this; recompiling per call would dominate everything.
- **Check at ingest, not per request.** Validate and type-check a rule when it's saved;
  store it (optionally as a [CheckedExpr blob](guide/ast.md)); evaluate the compiled
  program thereafter.
- **Programs are thread-safe.** One compiled program serves all threads — no per-thread
  compilation.

## Methodology & honesty notes

- Both libraries evaluate the **identical expression** with the **same bound data**, each
  pre-compiled in `[GlobalSetup]`, so only steady-state eval is measured.
- Cel.NET returns a native `bool`; Celly returns a `CelValue` (a small extra allocation
  the simple-case alloc figures reflect — Celly is a hair heavier there, faster in time).
- These are microbenchmarks on one machine; treat the **ratios** as the signal, not the
  absolute nanoseconds. Your expressions, data shapes, and hardware will differ — the
  benchmark project is in the repo so you can measure your own workload.
- No comparison against cel-go is shown: it runs on a different runtime (Go), so any number
  would measure the runtimes as much as the implementations.

## If you need more speed

The `IInterpretable` plan tree is a deliberate seam. Celly does no bytecode/IL compilation
today because a virtual-dispatch tree walk is easy to verify against 2,456 conformance
tests and CEL expressions are short. If a workload proves eval-bound, an optimizing backend
(constant folding beyond literals, IL emission, or expression-tree compilation) slots in at
that seam without touching the parser, checker, or value model.
