# Macros

CEL has no loops — yet `[1,2,3].all(x, x > 0)` clearly iterates. The trick: `all` is not a
function. It's a **macro**, rewritten at parse time into a `ComprehensionExpr` — the one
AST node that can iterate, and it can only iterate over data you already have. That's how
CEL keeps its termination guarantee.

## The comprehension node

```
ComprehensionExpr {
    IterVar        // loop variable name
    IterVar2       // optional second variable (two-var comprehensions)
    IterRange      // what to iterate (list elements / map keys)
    AccuVar        // accumulator variable name
    AccuInit       // accumulator's initial value
    LoopCondition  // checked before each step; false stops the loop
    LoopStep       // new accumulator value each iteration
    Result         // final expression over the accumulator
}
```

Evaluation ([`ComprehensionEval`](evaluator.md)) is: eval `IterRange`; bind `AccuVar` to
`AccuInit`; for each element while `LoopCondition` holds, rebind the accumulator to
`LoopStep`; finally yield `Result`.

## The standard expansions

`src/Celly/Parsing/Macro.cs` registers seven macros. The accumulator is named `@result` —
the `@` guarantees no collision, because `@` can't appear in a source identifier.

`list.all(x, p)` expands to:

```
fold(x, list,
     @result = true,                            // init
     @not_strictly_false(@result),              // condition — enables early exit
     @result && p,                              // step
     @result)                                   // result
```

`exists` is the dual (`false`, `||`, early exit on `true`). `exists_one` counts matches
(`@result + 1` under a ternary) and compares to 1 — no early exit, by spec.
`map`/`filter` accumulate a list: `@result + [t]`.

Two details are load-bearing:

- **`@not_strictly_false`** returns `true` for anything except literal `false` — including
  errors. That's what lets `all` keep scanning past an erroring element to find a `false`
  that decides the answer ([error absorption](../guide/errors.md)).
- **Steps use `&&`/`||` themselves**, so absorption inside quantifiers isn't a special
  case — it falls out of the logical operators' semantics.

`has(e.f)` is simpler: it rewrites to a `SelectExpr` with `TestOnly = true`, which the
evaluator interprets as a presence check instead of a value fetch.

## How expansion is wired

The parser owns a registry keyed by `(function name, arg count, receiver-style?)`. When a
call site matches, the macro's expander runs with an `IMacroContext` (fresh node ids +
error reporting) and returns a replacement `Expr`. Extension libraries add their own
macros through the same registry — that's how `cel.bind`, `optMap`, two-variable
comprehensions, and `math.greatest` work:

- **`cel.bind(v, init, expr)`** → a comprehension over an *empty* list whose accumulator
  is the bound variable: `fold(#unused, [], v = init, false, v, expr)`. The loop never
  runs; the machinery is reused purely for scoping.
- **`optMap`** builds `hasValue() ? optional.of(bind(v, value(), f)) : optional.none()`.
- **`math.greatest(a, b, c)`** is variadic — a var-arg macro normalizes any arity into
  one call: `math.@max([a, b, c])`.
- **`cel.block([e0, e1], r)`** (optimizer form) rewrites `cel.index(i)` references into
  variables and nests bind-comprehensions — a genuine AST-to-AST transform.
- A macro can **decline** by returning null — `cel.bind` only fires when the receiver is
  literally the identifier `cel`, so your variable named `cel` still works.

## Scoping and shadowing

Comprehension variables introduce real lexical scopes. Per the spec (and its 2026
clarification), they **shadow namespaced resolution**: in `[{'z': 0}].exists(y, y.z == 0)`,
`y.z` is a field access on the loop variable even if a top-level variable named `"y.z"`
exists. Both the checker and the parse-only planner track in-scope comprehension variables
to enforce this; absolute references (`.y`) escape the comprehension scopes entirely.

Next: [The Value Model](values.md).
