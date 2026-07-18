# Errors & Absorption

CEL's error model is one of its most distinctive design points, and understanding it
explains a lot of Celly's internals.

## Errors are values

Evaluation never throws for expression-level failures. Instead, errors **flow** through
evaluation as `ErrorValue`s:

```javascript
1 / 0                     // error: divide by zero
{'a': 1}['b']             // error: no such key: b
9223372036854775807 + 1   // error: return error for overflow
[1, 2][5]                 // error: index out of range: 5
```

Most operations are **strict**: if any argument is an error, the result is that error.
`size(1/0)` is the divide-by-zero error, not a size error.

## Commutative absorption in `&&` and `||`

The logical operators are special: they can succeed even when one side fails, as long as
the *other* side determines the answer — **regardless of order**:

```javascript
false && (1/0 == 0)       // false  (short-circuit, familiar)
(1/0 == 0) && false       // false  (absorption — most languages would fail here!)
(1/0 == 0) || true        // true
true && (1/0 == 0)        // error  (nothing determines the result)
```

Think of `&&`/`||` as *commutative*: CEL is free to evaluate either side first, so an error
on one side only surfaces if the other side can't settle the outcome. The ternary
`c ? a : b` is strict **only in the condition** — the untaken branch is never evaluated.

## Absorption inside macros

Quantifier macros expand into `&&`/`||` chains, so they inherit absorption:

```javascript
[1, 2, 3].exists(x, x/(x-2) > 0 || x == 2)   // true — element 2 divides by zero,
                                              // but another element proves 'exists'
[0, 2, 4].all(x, 4/x > 0)                     // error — no element disproves 'all',
                                              // so the division error surfaces
```

The rule of thumb: **a determining element wins; otherwise the error survives.**

## Why this design?

CEL expressions guard admission to systems. If `a || b` failed whenever `a` errored, every
policy would need defensive `has()` checks in front of every field access, and rule
authors would get it wrong. Absorption makes policies robust by default while still
surfacing errors when the result genuinely depends on the failed branch.

## Practical guidance

- Test for presence with `has(m.f)` or reach for [optionals](extensions.md#optionals)
  (`m.?f.orValue(x)`) rather than relying on absorption.
- When consuming results, always handle the `ErrorValue` case — it *is* the API for
  runtime failures:

```csharp
var value = program.Eval(activation);
if (value is ErrorValue err)
{
    logger.LogWarning("policy evaluation failed: {Message}", err.Message);
    return Decision.Deny; // fail closed
}
```

- Unknown values (`UnknownValue`, from partial evaluation) merge through the same
  machinery: unknown beats error, and two unknowns merge their attribute sets.

## Bounding untrusted evaluation

CEL always terminates, but a comprehension over large data can still be expensive —
`range.map(x, range.map(y, …))` is quadratic. When you evaluate **untrusted** expressions,
set an evaluation budget so a hostile expression aborts with an error instead of burning
CPU and memory:

```csharp
using Celly.Interpreter;

var env = CelEnv.Create(new CelEnvSettings
{
    // Default budget for every Eval in this environment:
    EvalLimits = new EvalLimits { MaxIterations = 1_000_000 },
});

var program = env.Compile(untrustedRule);
var result = program.Eval(bindings);
if (result is ErrorValue e && e.Message.Contains("iteration budget"))
{
    // rejected: too expensive
}
```

`MaxIterations` counts **total** comprehension iterations across the whole evaluation (not
per loop), so nested-comprehension bombs trip it. You can also pass limits per call —
`program.Eval(bindings, new EvalLimits { MaxIterations = 50_000 })` — or supply a
`CancellationToken` to abort long evaluations from another thread. The budget is per-`Eval`,
so it's safe to evaluate one compiled program concurrently. Leave `EvalLimits` unset
(the default) for trusted, first-party expressions — there's zero overhead on that path.

## Rejecting expensive expressions *before* they run

`EvalLimits` is a runtime backstop — it aborts a bomb mid-flight. For untrusted expressions
you often want to reject the expensive ones **at ingest**, before spending anything, the way
Kubernetes bounds admission-policy cost. `CelEnv.EstimateCost` does a static, worst-case cost
analysis over a **checked** AST (the compile-time complement to `EvalLimits`):

```csharp
using Celly.Checking;

var env = CelEnv.Create(new CelEnvSettings
{
    Declarations = [new VariableDecl("items", CelType.List(CelType.Int))],
});

var ast = env.Parse(untrustedRule).Ast!;
var check = env.Check(ast);                 // cost estimation needs types
if (check.HasErrors) { /* reject: doesn't type-check */ }

CostEstimate cost = env.EstimateCost(ast);
if (cost.IsUnbounded || cost.Max > 1_000_000)
{
    // reject before evaluating — too expensive, or unbounded on unconstrained input
}
```

The estimate is a saturating `[Min, Max]` range in abstract operation units. A comprehension
multiplies its body cost by the size of the range it iterates, so nested comprehensions
compound multiplicatively — and iterating an input variable whose size you haven't bounded
produces an **unbounded** estimate (`cost.IsUnbounded`). That unbounded result is itself the
signal: "this rule's cost depends on input size you don't control."

To turn "unbounded" into a real number, supply an `ICostEstimator` that bounds input sizes —
e.g. "the `items` list has at most 100 elements":

```csharp
sealed class SizeHints : ICostEstimator
{
    public SizeEstimate? EstimateSize(string? path, CelType type) => path switch
    {
        "items"  => new SizeEstimate(0, 100),    // ≤ 100 elements
        _        => null,                        // fall back to unbounded
    };
}

var bounded = env.EstimateCost(ast, new SizeHints());   // now finite → threshold it
```

Use both layers together: `EstimateCost` at ingest to reject rules whose worst case is too
high (or unbounded), and `EvalLimits` at run time as the belt-and-braces backstop against
whatever slips through (a rule whose cost is fine on typical data but pathological on a
specific input). The estimator is modelled on cel-go's — same structure (comprehension ranges
drive the multiplicative blow-up, size-dependent ops like `matches`/`contains`/concatenation
scale with operand size) — so cost bounds transfer intuitively between the two runtimes.
