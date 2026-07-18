# Extension Libraries

Everything beyond the core spec is an opt-in `CelLibrary`. A library bundles three things
that register together: **parse-time macros**, **runtime functions**, and **checker
declarations** (see [internals](../internals/architecture.md) for why all three exist).

```csharp
using Celly.Extensions;

var env = CelEnv.Create(new CelEnvSettings
{
    Libraries =
    [
        OptionalsLibrary.Instance,
        StringsLibrary.Instance,
        MathLibrary.Instance,
        EncodersLibrary.Instance,
        BindingsLibrary.Instance,
        TwoVarComprehensionsLibrary.Instance,
        NetworkLibrary.Instance,
    ],
});
```

!!! tip "Enabling everything (e.g. to run the conformance suite)"
    Extensions are opt-in — a bare environment can't evaluate `optional.of(...)`,
    `'x'.substring(1)`, `math.greatest(...)` etc., because those functions aren't registered.
    This is the same model as cel-go (`ext.Strings()`, `cel.OptionalTypes()`, …). To turn on
    the full feature set, add every library plus protobuf support:

    ```csharp
    using Celly.Extensions;
    using Celly.Protobuf;

    var registry = ProtoTypeRegistry.FromFiles(/* your proto descriptors */);
    var env = CelEnv.Create(new CelEnvSettings
    {
        TypeProvider = registry,
        Adapter = registry,
        Libraries =
        [
            OptionalsLibrary.Instance, StringsLibrary.Instance, MathLibrary.Instance,
            EncodersLibrary.Instance, BindingsLibrary.Instance, BlockLibrary.Instance,
            TwoVarComprehensionsLibrary.Instance, ProtosLibrary.Instance, NetworkLibrary.Instance,
        ],
    });
    ```

    See [Conformance Testing](../internals/conformance.md#extensions-and-proto-support-are-opt-in-a-runner-must-enable-them)
    for the exact configuration the test suite uses (and why running without it reports a
    misleadingly low pass rate).

## Optionals

First-class "maybe a value" without null-tripping errors:

```javascript
optional.of(5)                    // optional(5)
optional.none()                   // empty
optional.ofNonZeroValue('')       // empty ('' is a zero value)

m.?key                            // optional field select: none if missing
list[?10]                         // optional index: none if out of range
opt.hasValue()                    // bool
opt.value()                       // unwrap (error if none)
opt.orValue('default')            // unwrap with fallback
optA.or(optB)                     // first non-empty
opt.optMap(v, v * 2)              // transform if present
[?opt1, 3]                        // list literal: include only if present
{?'k': opt}                       // map/struct entry: include only if present
```

Chaining works through plain selects too: `optional.of(m).a.b.orValue(x)` — a missing
step anywhere collapses to `none` instead of erroring.

## Strings

Code-point-correct helpers on `string` (from the cel-spec strings extension):

```javascript
'hello'.charAt(1)                 // 'e'
'hello mellow'.indexOf('ello', 2) // 7
['a', 'b'].join('-')              // 'a-b'
'hello'.replace('l', 'L', 1)      // 'heLlo'
'a,b,c'.split(',', 2)             // ['a', 'b,c']
'tacocat'.substring(1, 4)         // 'aco'
'  x  '.trim()                    // 'x'
'TacoCat'.lowerAscii()            // 'tacocat'
'foo'.reverse()                   // 'oof'
strings.quote('a"b')              // '"a\"b"'

'%s scored %.2f (%d percent)'.format(['amy', 9.5, 95])
// printf-style: %s %d %f %e %x %X %o %b %% with optional precision
```

## Math

```javascript
math.greatest(1, 5, 3)            // 5 (variadic, mixed numerics allowed)
math.least([2, 1.5, 3u])          // 1.5
math.ceil(1.2)  math.floor(1.8)  math.round(1.5)  math.trunc(-1.9)
math.abs(-5)    math.sign(-3.0)   math.sqrt(2)
math.isNaN(x)   math.isInf(x)     math.isFinite(x)
math.bitAnd(5, 3)  math.bitOr(...)  math.bitXor(...)  math.bitNot(x)
math.bitShiftLeft(1, 4)           // 16; shifts >= 64 yield 0, negative shift errors
```

## Bindings & Block

```javascript
cel.bind(v, expensive(), v + v)   // evaluate once, use many times
```

`cel.block` (+ `cel.index`) is the optimizer-internal form used by conformance tests —
supported, but you'd normally never write it by hand.

## Two-variable comprehensions

Index+value over lists, key+value over maps:

```javascript
[1, 2, 3].all(i, v, v > i)                      // index, value
{'a': 1}.exists(k, v, k == 'a' && v == 1)       // key, value
[10, 20].transformList(i, v, v + i)             // [10, 21]
{'a': 1}.transformMap(k, v, v * 2)              // {'a': 2}
{'a': 1}.transformMapEntry(k, v, {v: k})        // invert: {1: 'a'}
```

## Encoders

```javascript
base64.encode(b'hello')           // 'aGVsbG8='
base64.decode('aGVsbG8')          // b'hello' (missing padding tolerated)
```

## Proto2 extensions

```javascript
proto.getExt(msg, my.pkg.int32_ext)     // read an extension field
proto.hasExt(msg, my.pkg.int32_ext)     // presence
msg.`my.pkg.int32_ext`                  // equivalent backtick form
```

## Networking

Opaque `net.IP` and `net.CIDR` types with Go-compatible semantics:

```javascript
ip('192.168.0.1').isGlobalUnicast()
isIP('not-an-ip')                        // false
cidr('192.168.0.0/24').containsIP('192.168.0.9')
cidr('192.168.0.1/24').masked()          // cidr('192.168.0.0/24')
ip('::ffff:c0a8:1') == ip('192.168.0.1') // true — IPv4-mapped normalizes
type(ip('::1')) == net.IP
```
