# Differential fuzzer (Celly vs cel-go)

The strongest correctness oracle in the project. The conformance suite checks fixed,
hand-written cases; this generates **random, type-correct CEL expressions**, evaluates each
in both Celly (.NET) and the reference [cel-go](https://github.com/google/cel-go) (Go), and
flags any result that differs. It finds *wrong answers*, not just crashes.

## Running

Requires the .NET SDK and the `go` toolchain on PATH.

```bash
cd tools/differential
dotnet run -c Release -- [count] [seed]      # default: 20000 expressions, seed 1
```

Exit code 0 = full agreement; 1 = divergences (printed with the offending expression and
both results); 2 = harness error.

## How it works

- **`Generator.cs`** builds typed expressions over a fixed variable set (int/uint/double/
  bool/string/list/map), so both engines actually evaluate rather than mostly type-erroring.
  It deliberately favors the bug-prone areas: cross-type numeric comparison, `int64.MinValue`
  arithmetic, uint overflow, division/modulo by zero, conversions, and comprehensions.
- **`Program.cs`** generates the expressions, evaluates them in Celly, shells out to the Go
  program for the cel-go results, and compares.
- **`celgo/main.go`** evaluates the same expressions against the same fixed activation.

## The comparison contract

Results are reduced to a canonical string that **both normalizers must produce identically**:

- errors match errors — `ERROR` (messages differ between implementations by design)
- doubles compare by **IEEE bit pattern** (`d:<hex>`), so `NaN`, `±Inf`, and `-0.0` are
  exact and language-independent
- strings compare as base64, maps are key-sorted, lists element-wise

A `PANIC` (cel-go) or `EXCEPTION` (Celly) token marks an engine that threw — itself a
finding.

## Status

0 divergences across 65,000+ generated expressions (seeds 1–4). Re-run with new seeds to
extend coverage.
