# Planner & Evaluator

Celly doesn't interpret the AST directly. A **planner** compiles it once into a tree of
`IInterpretable` objects; evaluation walks that tree. The split pays for itself when a
program is evaluated many times (the normal case for policies): name-resolution decisions,
constant materialization, and macro structure are all settled at plan time.

## The interpretable tree

```csharp
public interface IInterpretable
{
    CelValue Eval(IActivation activation);
}
```

One small class per behavior (`src/Celly/Interpreter/Interpretables.cs`):

| Node | Built from | Eval behavior |
|---|---|---|
| `ConstEval` | literals | returns a pre-materialized `CelValue` |
| `IdentEval` | identifiers | tries container-qualified candidates against the activation |
| `ScopeIdentEval` | comprehension vars | direct single-name lookup (shadowing) |
| `SelectEval` | `a.b` | qualified-variable candidates first, then field access |
| `TestOnlySelectEval` | `has(a.b)` | presence check |
| `CallEval` | function calls | strict arg eval → dispatch |
| `AndEval` / `OrEval` / `ConditionalEval` / `NotStrictlyFalseEval` | logic | non-strict (below) |
| `ListEval` / `MapEval` / `StructEval` | aggregates | build values; optional-entry handling |
| `ComprehensionEval` | macros | the fold loop |

## Strict calls, error/unknown protocol

`CallEval` evaluates all arguments first, then:

1. any **unknowns** → merge them and return (partial evaluation wins over errors),
2. else any **error** → return the first one,
3. else invoke the function implementation (a `Func<CelValue[], CelValue>` from the
   registry, resolved *at plan time*).

Exceptions from implementations are caught at this single point and converted to
`ErrorValue` — the evaluator itself never throws for expression-level failures.

## The non-strict four

`&&`, `||`, `?:`, and `@not_strictly_false` get dedicated nodes because their semantics
are about *not* evaluating or *not* propagating:

```csharp
// OrEval, abbreviated — commutative absorption in ~10 lines:
var l = left.Eval(activation);
if (l is BoolValue { Value: true }) return BoolValue.True;   // short-circuit
var r = right.Eval(activation);
if (r is BoolValue { Value: true }) return BoolValue.True;   // absorption: r decides
if (l is BoolValue && r is BoolValue) return BoolValue.False;
return MergeNonBool(l, r);  // unknown ∪ unknown → merged; unknown > error; else error
```

The ternary is strict only in its condition — untaken branches never run.

## Comprehensions and scoping

`ComprehensionEval` implements the fold from [Macros](macros.md). Scoping uses
`ScopedActivation` — a one-name binding layered over the parent activation:

```
activation ▸ ScopedActivation(@result = accu) ▸ ScopedActivation(x = element)
```

The accumulator cell is *mutable* across iterations (rebound each step); the iteration
variable is rewritten per element. Early exit falls out of the loop condition: `exists`
stops as soon as `@not_strictly_false(!@result)` is false, i.e. once a `true` lands.

Map iteration yields keys; two-variable comprehensions bind `(index, value)` for lists and
`(key, value)` for maps via `IterVar2`.

## Parse-only name resolution ("maybe attributes")

Here's the subtlest part of the evaluator. In checked mode, the checker decides what
`a.b.c` means. In **parse-only** mode nobody has — it could be:

- a variable literally named `"a.b.c"` (bindings may use qualified names),
- variable `"a.b"` with field `.c`, or variable `"a"` with `.b.c`,
- any of those prefixed by the container (`ns.a.b.c`, …).

The planner therefore gives `SelectEval` the full candidate list (longest first,
container-expanded). At eval, the first candidate bound in the activation wins; only then
does it fall back to evaluating the operand and selecting the field. Three refinements:

1. **Comprehension variables shadow candidates** — the planner tracks in-scope variable
   names while planning loop bodies, so `y.z` inside `exists(y, …)` compiles to a plain
   field select with *no* candidate list.
2. **Absolute references** (`.y`) unwrap all `ScopedActivation` layers before looking up.
3. **Static fallbacks**: if a candidate names a standard type ident (`int`,
   `google.protobuf.Duration`) or a provider ident (enum constant, message type), the
   planner resolves it to a constant at plan time and `IdentEval`/`SelectEval` fall back
   to it after activation misses.

The same candidate logic makes namespaced *functions* work: `optional.of(x)` is
receiver-syntax on the identifier `optional`, but the planner sees a registered function
named `"optional.of"` and compiles a plain global call.

## What's deliberately not here

No bytecode, no expression-tree/IL compilation, no constant-folding pass beyond literal
materialization. A tree of virtual `Eval` calls is straightforward to verify against a
2,456-case suite, and CEL expressions are short — tree-walk overhead is rarely the
bottleneck. The `IInterpretable` boundary is where an optimizing backend would slot in
without touching anything upstream.

Next: [Protobuf Integration](protobuf.md).
