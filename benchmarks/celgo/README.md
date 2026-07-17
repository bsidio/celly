# cel-go comparison benchmark

Measures the reference Go implementation ([cel-go](https://github.com/google/cel-go))
on the same two expressions the .NET benchmarks use, so the results in
[docs/performance.md](../../docs/performance.md) are reproducible.

```bash
go test -run=XXX -bench=. -benchmem -benchtime=3s -count=3
```

Note this is a **cross-runtime** comparison (Go vs .NET); read the caveats in the
performance docs. cel-go's `Program.Eval` returns `(ref.Val, EvalDetails, error)` — the
benchmark uses the standard fast path and ignores details.
