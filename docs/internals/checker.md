# Type Checker

The checker (`src/Celly/Checking/`) walks the AST once and computes a type for every node,
or reports why it can't. CEL is **gradually typed**: `dyn` opts out of checking locally,
and unchecked programs still evaluate — so the checker is a quality gate, not a
prerequisite.

## One type model for everything

`CelType` serves both worlds. The checker sees parameterized types — `list(int)`,
`map(string, dyn)`, type parameters like `A` — while the runtime sees the same objects as
value types (`type(x)`). Primitives are singletons, so structural equality doubles as
reference equality for them.

## Declarations

The environment supplies what names mean:

- **Variables**: `VariableDecl(name, type)` — including qualified names (`"a.b.c"`).
- **Functions**: `FunctionDecl(name, overloads)`, each `OverloadDecl` with an id, argument
  types, result type, and receiver-style flag. `StandardDecls.cs` declares the ~150
  standard overloads — homogeneous signatures per spec (cross-type numeric comparison is a
  *runtime* capability reached through `dyn`, not a declared overload).
- Standard **type identifiers** (`int`, `list`, …) are variables of type `type(T)`.

## Overload resolution with unification

For a call, the checker collects candidate overloads (matching name, arity, receiver
style) and tries each:

1. **Fresh-rename** the overload's type parameters for this call site (`A` → `%p17`), so
   two calls to `size(list(A))` don't interfere.
2. Check each argument with `IsAssignable(paramType, argType)`, which **binds** type
   parameters as it goes (unification): matching `list(A) := list(int)` binds `A := int`.
3. On failure, **roll back** the bindings (a snapshot/restore around each attempt — a
   failed candidate must not leak speculative bindings) and try the next overload.
4. If several overloads match, join their result types.

`IsAssignable` in one screen:

```
target is unbound param  →  bind target := from (occurs check)     ✓
from   is unbound param  →  bind from := target                    ✓
either side is dyn/error →  ✓            (gradual typing)
both are type(...)       →  ✓            (type values inter-compare)
null → message/wrapper/timestamp/duration/optional                 ✓
wrapper(T) ↔ T           →  ✓            (proto wrapper interop)
enum ↔ int               →  ✓            (strong-enum mode)
otherwise: same kind+name+arity, parameters pairwise assignable
```

### Empty aggregates get fresh parameters

`[]` types as `list(%p)` with a *fresh* parameter, not `list(dyn)`. So in `[] + [1, 2]`,
the `add_list(list(A), list(A))` overload unifies `%p := int` and the whole expression
deduces `list(int)`. This is why comprehension results stay precise:
`[1, 2].map(x, x * 2)` is `list(int)`, because the accumulator starts as `[]`.

### Joins prefer the more general type

Mixed aggregates join: `[1, 'a']` → `list(dyn)`. When both sides are compatible, the join
keeps the *more general* one — `[wrapper(int), int]` joins to `list(wrapper(int))` (a
wrapper admits null; int doesn't), and `optional(dyn)` beats `optional(int)`.

## Name resolution

References resolve through the **container** (namespace) longest-first: in container
`a.b`, the name `v` tries `a.b.v`, `a.v`, `v`. For a dotted chain `x.y.z` the checker
first asks whether the *whole* chain is a declared variable (again longest-first), and
only then treats it as field selections. Precedence rules that matter:

1. **Comprehension variables shadow everything** (the chain's root is checked against
   enclosing comprehension scopes first).
2. A leading dot (`.x.y`) skips comprehension scopes and container prefixes — absolute.
3. Provider identifiers (enum constants, message type names) resolve after declared
   variables at each candidate name.

Field selection then types by operand kind: `map(K, V)` → `V`; a `Struct` type asks the
provider for the field's type (undefined field = error); `dyn` stays `dyn`; optionals
chain (`optional(T).field` → `optional(dyn)`).

## Comprehensions

The range type determines the iteration variable: `list(E)` → `E`, `map(K, V)` → `K`
(or `K, V` for two-variable forms), `dyn` → `dyn`. The accumulator is declared in a nested
scope with its init's type; the loop step must remain assignable to it (the shared
substitution makes `@result + [t]` refine an empty-list accumulator's fresh parameter).

## Output

On success, the AST is annotated with `TypeMap: exprId → CelType` (fully resolved;
leftover unbound parameters collapse to `dyn`). The conformance suite's `type_deduction`
file asserts these deduced types verbatim — 47 tests of nothing but this machinery.

Next: [Planner & Evaluator](evaluator.md).
