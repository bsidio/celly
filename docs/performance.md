# Performance

Celly is designed for the policy-engine pattern: **compile an expression once, evaluate it
many times**. The numbers below focus on that steady-state eval cost, compared against the
other native .NET CEL libraries ([Cel.NET](https://github.com/rayokota/cel.net) and
[TELUS `Cel`](https://github.com/telus-labs/cel-net)) and the reference Go implementation
([cel-go](https://github.com/google/cel-go)).

## Results

*Apple Silicon, same machine. .NET on net8.0 via BenchmarkDotNet (short job); cel-go 0.29
via `go test -bench` (go1.26). Reproduce: `dotnet run -c Release --project
tests/Celly.Benchmarks` and `go test -bench=.` in `benchmarks/celgo/`.*

### Evaluation (pre-compiled program, per call)

| Workload | **Celly** | cel-go *(reference)* | Cel.NET | TELUS `Cel` |
|---|--:|--:|--:|--:|
| Simple `x + 1 > 3 && name.startsWith('h')` | 129 ns | **107 ns** | 152 ns | 1,112 ns |
| Comprehension `items.filter(…).map(…).exists(…)` (100 elems) | **14.8 µs** | 22.5 µs | 32.0 µs | 81.8 µs |
| Extension-heavy `upperAscii/substring/join/string/math.greatest` | **11.6 µs** | 25.9 µs | — † | — † |

† Neither Cel.NET nor TELUS `Cel` ships the strings/math extensions, so the expression
won't compile there.

**Celly is the fastest .NET CEL implementation of the three** — ~1.2× faster than Cel.NET
and ~6–9× faster than TELUS `Cel` on these workloads, allocating far less in every case. Note
that TELUS `Cel`'s "compiles to a delegate" design is *not* a JIT/IL fast path — the delegate
wraps its interpreter, so it's actually the slowest of the three. Against the native Go
reference, Celly is within ~1.2× on simple expressions and faster on comprehension-heavy ones.

All benchmarks run with **all nine extension libraries enabled** (as a real deployment and
the conformance runner do). That has **no measurable per-eval cost** — a simple expression
evaluates in 125 ns whether the env has zero libraries or all nine (library loading is a
one-time compile cost; the eval hot path only touches the functions the expression uses).

Three things stand out:

- **On simple expressions, Celly is within ~1.2× of the Go reference** and faster than the
  other .NET library — a good place to be for a managed implementation measured against
  native Go.
- **On comprehension-heavy expressions, Celly is the fastest of the three — faster than
  cel-go itself** (15.1 µs vs 22.5 µs, ~1.5×). The [rope-based list
  concatenation](internals/values.md) turns the `@result + [element]` accumulation pattern
  from O(n²) into O(n), which is exactly where comprehension cost concentrates.
- **On extension-heavy expressions (strings + math), Celly is ~2.2× faster than cel-go**
  (11.6 µs vs 25.9 µs) — the extension functions ride the same tree-walk hot path with no
  dispatch penalty.

Allocation differs by runtime and isn't directly comparable across the Go/.NET boundary:
cel-go's escape analysis keeps simple-eval allocation very low (16 B), while Celly and
Cel.NET allocate a `CelValue` result (472 B / 416 B). Within .NET, Celly allocates the least
of the three — ~2.6× less than Cel.NET and ~6× less than TELUS `Cel` on the comprehension
workload (41 KB vs 105 KB vs 249 KB).

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

## Scale, memory & GC

Measured on the same laptop under **Server GC** (`DOTNET_gcServer=1`), sustained parallel
load (`tests/Celly.Benchmarks -- throughput`):

| Workload | Aggregate throughput | Gen2 collections | Memory after load |
|---|--:|--:|--:|
| Simple (50M evals, 12 threads) | ~7–10 M evals/sec | ~1–2 | **returns to baseline** |
| Comprehension (5M evals, 12 threads) | ~150 K evals/sec | ~1–3 | **returns to baseline** |

What these say:

- **No leaks.** After tens of millions of evaluations, forcing a full GC returns memory to
  its ~0.2 MB baseline. The soak tests assert this as a CI gate.
- **No promotion pathology under Server GC.** Gen2 collections stay in the low single
  digits across millions of evals — allocations are short-lived and die in Gen0, nothing
  accumulates. (Under the default *Workstation* GC, high-allocation parallel load will
  promote-then-collect more; use Server GC for server workloads.)
- **No lock contention.** The eval path has no locks; a compiled program is safe to share
  across threads (verified by the concurrency tests).

**On multi-core scaling, be realistic.** Simple-eval throughput scales from 1→2 threads
but flattens beyond a few cores on this heterogeneous-core laptop (P-cores downclock as a
cluster; E-cores are slower; and simple eval is allocation-bound — `CallEval` allocates a
small argument array per call). The practical takeaway: single-process throughput
(millions of simple evals/sec, ~150 K comprehension evals/sec) is **orders of magnitude
above what a policy workload demands**, and you scale horizontally (more processes/pods) in
production anyway. If a single process ever became eval-bound, reducing per-call allocation
behind the `IInterpretable` seam is the lever.

## If you need more speed

The `IInterpretable` plan tree is a deliberate seam. Celly does no bytecode/IL compilation
today because a virtual-dispatch tree walk is easy to verify against 2,456 conformance
tests and CEL expressions are short. If a workload proves eval-bound, an optimizing backend
(constant folding beyond literals, IL emission, or expression-tree compilation) slots in at
that seam without touching the parser, checker, or value model.
