# The CEL Language

A working reference for CEL as Celly implements it. The authoritative definition is
[cel-spec langdef.md](https://github.com/cel-expr/cel-spec/blob/master/doc/langdef.md); Celly
tracks it exactly (verified by conformance).

## Types

CEL has a small, fixed type universe:

| Type | Literals | Notes |
|---|---|---|
| `int` | `42`, `-7`, `0x1F` | 64-bit signed; arithmetic overflow is an **error**, not wraparound |
| `uint` | `42u`, `0xFFu` | 64-bit unsigned; the `u` suffix is required |
| `double` | `1.5`, `.5`, `1e3`, `-0.0` | IEEE-754; `1.0/0.0` is `+Inf`, `0.0/0.0` is `NaN` (no error) |
| `bool` | `true`, `false` | |
| `string` | `"hi"`, `'hi'`, `r"raw\n"`, `"""multi"""` | Unicode; `size()` counts **code points**, not UTF-16 units |
| `bytes` | `b"abc"`, `b"\xff"` | octet sequences |
| `list` | `[1, 2, 3]` | heterogeneous allowed (typed `list(dyn)` then) |
| `map` | `{"k": 1, 2: true}` | keys: `int`/`uint`/`string`/`bool`; `1` and `1u` are the *same* key |
| `null_type` | `null` | |
| `type` | `int`, `string`, … | types are first-class: `type(1) == int` |
| timestamp/duration | `timestamp('2024-01-01T00:00:00Z')`, `duration('90s')` | nanosecond precision |

### Numeric behavior worth knowing

```javascript
1 + 1.0            // ERROR: no such overload — arithmetic is NOT cross-type
1 < 1.5            // true  — comparisons and equality ARE cross-type
1 == 1.0           // true  — one number line for int/uint/double
9223372036854775807 + 1   // ERROR: return error for overflow (checked arithmetic)
-17 / 5            // -3    — integer division truncates toward zero
1u - 2u            // ERROR: unsigned underflow
```

## Operators (highest to lowest precedence)

```
()  .  []            member access, indexing, calls
!   -                unary
*   /   %
+   -
<   <=  >  >=  ==  !=  in
&&
||
?:                   ternary (right-associative)
```

`in` tests membership: `2 in [1, 2]`, `'k' in {'k': 1}` (map keys).

## Field selection and presence

```javascript
msg.field              // map key lookup or protobuf field access
has(msg.field)         // presence test: map key exists / proto field set
msg.`weird-name`       // backtick-escaped identifiers for non-identifier keys
```

Selecting a missing map key is an **error**; `has()` is the safe test. (Or use
[optionals](extensions.md#optionals): `msg.?field.orValue(default)`.)

## Macros (comprehensions)

CEL has no loops. Instead, seven macros expand into bounded comprehensions:

```javascript
has(m.f)                          // presence
list.all(x, x > 0)                // every element satisfies
list.exists(x, x == target)       // any element satisfies
list.exists_one(x, p(x))          // exactly one
list.map(x, x * 2)                // transform
list.map(x, x > 0, x * 2)         // filter + transform
list.filter(x, x > 0)             // keep matching
```

Maps iterate over their **keys**: `{'a': 1}.all(k, k != '')`.

Quantifiers are *error-absorbing* like `&&`/`||`: `[1, 0].exists(x, 1/x > 0)` is `true`
even though one element errors — see [Errors & Absorption](errors.md).

## Name resolution and containers

An environment can have a **container** (namespace), e.g. `mycompany.rules`. A reference `x`
then resolves by trying `mycompany.rules.x`, `mycompany.x`, `x` — longest first. A leading dot
forces absolute resolution: `.x` only ever means top-level `x`.

Comprehension variables shadow everything: in `list.exists(y, y.z == 0)`, `y` is always the
iteration variable even if a variable literally named `"y.z"` is bound.

## Timestamps and durations

```javascript
timestamp('2009-02-13T23:31:30Z').getFullYear()        // 2009
timestamp('2009-02-13T23:31:30Z').getHours('-08:00')   // 15 (timezone-aware)
timestamp('2009-02-13T23:31:30Z') + duration('1h')     // arithmetic
duration('1h30m').getMinutes()                          // 90 (whole-unit conversion)
```

Gotchas the spec mandates (Celly matches them exactly): `getMonth()` is **0-based**,
`getDate()` is 1-based, `getDayOfMonth()` is 0-based, `getDayOfWeek()` has 0 = Sunday.

## Conversions

Explicit, never implicit: `int('42')`, `string(3.14)`, `double('1e3')`, `uint(7)`,
`bytes('abc')`, `bool('true')`, `timestamp(1234567890)`, `duration('90s')`, `dyn(x)`, `type(x)`.

Out-of-range conversions are errors: `int(1e99)`, `uint(-1)`.
