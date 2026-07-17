# Performance

Celly is designed for the policy-engine pattern: **compile an expression once, evaluate it
many times**. The numbers below focus on that steady-state eval cost, compared against both
the other native .NET CEL library ([Cel.NET](https://github.com/rayokota/cel.net)) and the
reference Go implementation ([cel-go](https://github.com/google/cel-go)).

## Results

*Apple Silicon, same machine. .NET on net8.0 via BenchmarkDotNet (short job); cel-go 0.29
via `go test -bench` (go1.26). Reproduce: `dotnet run -c Release --project
tests/Celly.Benchmarks` and `go test -bench=.` in `benchmarks/celgo/`.*

### Evaluation (pre-compiled program, per call)

| Workload | **Celly** | cel-go *(reference)* | Cel.NET |
|---|--:|--:|--:|
| Simple `x + 1 > 3 && name.startsWith('h')` | 130 ns | **107 ns** | 153 ns |
| Comprehension `items.filter(…).map(…).exists(…)` (100 elems) | **15.1 µs** | 22.5 µs | 33.1 µs |

Two things stand out:

- **On simple expressions, Celly is within ~1.2× of the Go reference** and faster than the
  other .NET library — a good place to be for a managed implementation measured against
  native Go.
- **On comprehension-heavy expressions, Celly is the fastest of the three — faster than
  cel-go itself** (15.1 µs vs 22.5 µs, ~1.5×). The [rope-based list
  concatenation](internals/values.md) turns the `@result + [element]` accumulation pattern
  from O(n²) into O(n), which is exactly where comprehension cost concentrates.

Allocation differs by runtime and isn't directly comparable across the Go/.NET boundary:
cel-go's escape analysis keeps simple-eval allocation very low (16 B), while Celly and
Cel.NET allocate a `CelValue` result (472 B / 416 B). Within .NET, Celly allocates ~2.6×
less than Cel.NET on the comprehension workload (41 KB vs 105 KB).

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
  absolute nanoseconds. Your expressions, data shapes, and hardware will differ — both
  benchmark projects are in the repo (`tests/Celly.Benchmarks`, `benchmarks/celgo`) so you
  can measure your own workload.
- **The cel-go comparison crosses runtimes** (Go vs .NET/CoreCLR): it measures the runtimes
  as much as the implementations. A sub-2× gap on a ~100 ns operation can be entirely GC
  model, allocation strategy, and JIT-vs-AOT differences. Read it as "Celly is in the same
  performance class as the reference implementation," not as a controlled head-to-head.
- cel-go's `Program.Eval` returns `(ref.Val, EvalDetails, error)`; the benchmark uses the
  standard fast path and ignores details, matching typical usage.

## If you need more speed

The `IInterpretable` plan tree is a deliberate seam. Celly does no bytecode/IL compilation
today because a virtual-dispatch tree walk is easy to verify against 2,456 conformance
tests and CEL expressions are short. If a workload proves eval-bound, an optimizing backend
(constant folding beyond literals, IL emission, or expression-tree compilation) slots in at
that seam without touching the parser, checker, or value model.
