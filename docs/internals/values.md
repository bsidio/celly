# The Value Model

Everything the evaluator touches is a `CelValue` (`src/Celly/Values/`). This page covers
the hierarchy, the capability-interface dispatch pattern, and the numeric semantics that
took the most care to get exactly right.

## The hierarchy

```
CelValue (abstract)
├─ NullValue, BoolValue, IntValue(long), UintValue(ulong), DoubleValue(double)
├─ StringValue, BytesValue
├─ TimestampValue, DurationValue        // custom seconds+nanos structs (see below)
├─ ListValue, MapValue
├─ TypeValue(CelType)                   // types are first-class: type(1) == int
├─ OptionalValue                        // optionals extension
├─ EnumValue                            // strong-enum mode only
├─ ErrorValue, UnknownValue             // failures/partials AS VALUES
└─ ProtoMessageValue                    // in Celly.Protobuf, via the provider seam
```

Base contract: `Type` (the runtime `CelType`), `EqualTo(other)` (CEL equality), and
`ToNative()`. Singletons keep allocation down: `BoolValue.True/False`,
`NullValue.Instance`, small-int cache.

## Capability interfaces drive dispatch

Operators don't switch over value kinds. Each value implements the capabilities it has:

```csharp
public interface IAdder        { CelValue Add(CelValue other); }
public interface IComparableValue { CelValue CompareTo(CelValue other); }
public interface IIndexerValue { CelValue Get(CelValue index); }
public interface ISizedValue   { CelValue Size(); }
// … ISubtractor, IMultiplier, IDivider, IModder, INegater, IContainsTester, IIterableValue
```

The `_+_` implementation is then one line: *if lhs is `IAdder`, call `Add(rhs)`; else "no
such overload"*. `IntValue.Add` accepts only another `IntValue` — which is precisely CEL's
rule that arithmetic is never cross-type. Adding a new value type (say, `net.IP` in the
networking extension) means implementing interfaces, not editing operator code.

## Equality vs. ordering — two different beasts

**Equality** (`EqualTo`) never errors: mismatched types are simply `false`, and the three
numeric types compare on one number line (`1 == 1.0 == 1u`). Lists and maps recurse
element-wise. NaN equals nothing, `-0.0 == 0.0` (IEEE `==`, deliberately not
`double.Equals`).

**Ordering** (`CompareTo`) can error: `1 < 'a'` is "no such overload", and ordering
against NaN is "NaN values cannot be ordered".

### Cross-type numeric comparison, exactly like cel-go

`NumericCompare.cs` implements the shared number line. The rules (which the conformance
suite pins down to the bit):

- A double **outside** `[−2⁶³, 2⁶³]` (or `[0, 2⁶⁴]` vs uint) orders strictly against any
  int — no precision issues possible.
- A double **inside** the range compares by casting the integer to double — which is
  *lossy above 2⁵³*, and that's canonical: the suite has tests literally named `*_lossy`
  asserting `9223372036854775807 >= 9223372036854775808.0` is **true** (both sides land on
  2⁶³ as doubles). An implementation with mathematically-exact comparison *fails*
  conformance. We know because Celly's first version was exact.

### Checked arithmetic and its blind spots

Int/uint arithmetic runs in `checked` blocks; `OverflowException` becomes
`ErrorValue("return error for overflow")`. Three cases C#'s `checked` doesn't reliably
cover are handled explicitly: `long.MinValue / -1`, `long.MinValue % -1`, and
`-long.MinValue`.

## Strings: code points, not UTF-16

CEL strings are sequences of Unicode **code points**; .NET strings are UTF-16 with
surrogate pairs. `StringValue` caches its code-point length lazily; `size("h😀llo")` is 5.

The subtle trap is **ordering**: `string.CompareOrdinal` sorts UTF-16 *units*, and high
surrogates (0xD800+) sort below `U+E000`–`U+FFFF` even though the code points they encode
(≥ `U+10000`) sort above. Celly compares ordinally until it hits a difference involving a
surrogate, then switches to rune-wise comparison — fast path for ASCII, correct for
astral-plane text.

## Time: custom structs, not TimeSpan

.NET ticks are 100 ns; CEL requires **nanosecond** precision. So:

```csharp
readonly record struct CelTimestampData(long Seconds, int Nanos);  // 0001-01-01 .. 9999-12-31
readonly record struct CelDurationData(long Seconds, int Nanos);   // range = int64 nanoseconds
```

RFC 3339 parsing/formatting is hand-written (`Stdlib/Rfc3339.cs`, using Howard Hinnant's
civil-date algorithms) because `DateTimeOffset`'s round-trip format caps at 7 fractional
digits. Note the duration range: it's what an int64 *nanosecond count* holds (~±292 years)
— narrower than proto's ±10,000 years — because that's what conformance requires
(`timestamp_max - timestamp_min` must overflow).

## Maps: numeric key identity

`{1: 'a'}` and `{1u: 'a'}` have the **same key**. `MapValue` uses a custom equality
comparer that hashes int and uint onto the same line, and lookups normalize integral
doubles (`m[1.0]` finds key `1`). Building a literal with duplicate keys — even
`{1: x, 1u: y}` — is an error.

## Errors and unknowns

`ErrorValue` carries a message (cel-go's canonical texts: "divide by zero",
"no such key: …", "return error for overflow"). `UnknownValue` carries expression ids for
partial evaluation. Both being values — not exceptions — is what lets `&&`/`||` implement
[absorption](../guide/errors.md) with ordinary control flow, and it keeps the eval hot
path free of try/catch.

Next: [Type Checker](checker.md).
