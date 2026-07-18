# Celly

**A native C#/.NET implementation of Google's [Common Expression Language (CEL)](https://github.com/cel-expr/cel-spec).**

Celly is written from scratch in pure managed C# — no WASM shims, no Go-compiled artifacts, no native library bindings.

> **Conformance: 2,456 / 2,456 (100%)** of the official cel-spec conformance suite
> (30 testdata files, pinned @ [`59505c1`](https://github.com/cel-expr/cel-spec/commit/59505c14f3187e6eb9684fbd3d07146f614c6148)), verified in CI on Linux, Windows, and macOS.

**Status: 1.0 — stable public API** (snapshot-tested), 100% conformance, fuzz-hardened.

**Performance:** the fastest .NET CEL implementation — ~1.2× faster than Cel.NET and ~6–9× faster than TELUS `Cel`, allocating far less. In the same class as the reference Go implementation (within ~1.2× of cel-go on simple expressions; *faster* on comprehension-heavy ones). See [Performance](https://bsid.io/celly/performance/).

**Documentation: [bsid.io/celly](https://bsid.io/celly/)** — user guide plus a deep-dive internals track explaining how the lexer, parser, macros, type checker, evaluator, and protobuf integration work.

## Why Celly?

CEL is the expression language behind Kubernetes admission policies (ValidatingAdmissionPolicy), Envoy RBAC, Google Cloud IAM conditions, gRPC protovalidate, and more. Official implementations exist for Go, C++, Java, and Rust — but not .NET. Celly fills that gap with a conformance-verified native implementation.

```csharp
using Celly;
using Celly.Checking;
using Celly.Types;

var env = CelEnv.Create(new CelEnvSettings
{
    Declarations =
    [
        new VariableDecl("request", CelType.Map(CelType.String, CelType.Dyn)),
    ],
});

var program = env.Compile("request.user.startsWith('admin-') && size(request.groups) > 0");

var result = program.Eval(new Dictionary<string, object?>
{
    ["request"] = new Dictionary<string, object?>
    {
        ["user"] = "admin-alice",
        ["groups"] = new[] { "ops" },
    },
});
// result => BoolValue.True
```

## Packages

| Package | Contents | Dependencies |
|---|---|---|
| `Celly` | Lexer, parser, macros, type checker, evaluator, standard library, all extension libraries | **None** |
| `Celly.Protobuf` | Protobuf types: message construction & field access, well-known types (Timestamp/Duration/Struct/Any/wrappers), enums (legacy int and strong modes), proto2 extensions | `Celly`, `Google.Protobuf` |
| `Celly.Protovalidate` | [protovalidate](https://github.com/bufbuild/protovalidate) for .NET: validate protobuf messages against `buf.validate` rules | `Celly`, `Celly.Protobuf`, `Google.Protobuf` |

Use `Celly` alone to evaluate expressions over .NET dictionaries/lists/primitives; add `Celly.Protobuf` when your data is protobuf messages.

### protovalidate for .NET

`Celly.Protovalidate` is a from-scratch .NET implementation of [protovalidate](https://github.com/bufbuild/protovalidate) — the successor to protoc-gen-validate — powered by the Celly CEL engine. It validates protobuf messages against the `buf.validate` rules declared in their `.proto` (standard rules like `string.email`/`int32.gte`, `required`, `repeated`/`map` rules, and custom `cel` expressions), returning structured `Violation`s.

```csharp
using Celly.Protovalidate;

var validator = new Validator(Person.Descriptor.File);   // compiles the rules once
foreach (var violation in validator.Validate(person))    // reuse across messages, thread-safe
{
    Console.WriteLine($"{violation.RuleId}: {violation.Message}");
}
```

**It passes buf's official conformance suite 2,872 / 2,872 (100%)** — the same corpus the Go/Java/Python/C++ runtimes verify against. See [`tests/Celly.Protovalidate.Conformance`](tests/Celly.Protovalidate.Conformance).

## Feature checklist

- **Full standard library**: operators with CEL semantics (checked int64/uint64 arithmetic, cross-type numeric comparisons, IEEE doubles), `size`/`in`/indexing, all type conversions with Go-compatible formatting, RE2-semantics `matches()` (via .NET's linear-time NonBacktracking engine), nanosecond-precision timestamps/durations with IANA timezone accessors
- **Macros**: `has`, `all`, `exists`, `exists_one`, `map`, `filter` (+ two-variable comprehensions)
- **Type checker**: gradual typing with `dyn`, parameterized-type unification, container (`C.name`) resolution, comprehension-variable shadowing per spec
- **Optionals**: `optional.of/ofNonZeroValue/none`, `a.?b`, `a[?b]`, `[?e]`, `{?k: v}`, `orValue`, `optMap`/`optFlatMap`, full optional chaining
- **Extension libraries** (opt-in via `CelEnvSettings.Libraries`): strings (incl. `format` and `strings.quote`), math, encoders (base64), bindings (`cel.bind`), block (`cel.block`), two-var comprehensions, proto2 extensions (`proto.getExt/hasExt`), networking (`net.IP`/`net.CIDR`)
- **Protobuf**: message construction with WKT collapse (wrappers → primitives, Struct/Value/ListValue → map/dyn/list), Any pack/unpack with bytewise fallback, presence-aware field-wise message equality, proto2 extension fields, **strong-enum mode** (`ProtoTypeRegistry.FromFiles(strongEnums: true, …)`) where enums are distinct named types — an opt-in the reference Go implementation doesn't offer
- **Plain .NET types**: `NativeTypeProvider` gives your own classes/records/enums full CEL semantics — construction, typed field access via the checker, `has()` presence, snake_case field aliases, `Nullable<T>` as wrappers — no protobuf required
- **First-class ASTs**: traversal/inspection tools (`AstTools`), lossless conversion to/from the canonical `cel.expr` protos (`ParsedExpr`/`CheckedExpr` incl. type & reference maps) for caching and cross-implementation interop, and a precedence-aware unparser (AST → source, macros restored to original form)
- **Untrusted-input safety**: a runtime evaluation budget (`EvalLimits` — iteration cap + `CancellationToken`) *and* static, pre-evaluation cost estimation (`CelEnv.EstimateCost`, cel-go-modelled) to reject expensive or unbounded expressions at ingest
- **Thread safety**: `CelEnv` and `CelProgram` are immutable; programs are safe for concurrent evaluation

## Enabling extensions

Extensions are **opt-in** — the same model as cel-go (`ext.Strings()`, `cel.OptionalTypes()`,
…). A bare environment can't evaluate `optional.of(...)`, `'x'.substring(1)`, or
`math.greatest(...)` because those functions aren't registered. Add just the libraries you
need:

```csharp
using Celly;
using Celly.Extensions;

var env = CelEnv.Create(new CelEnvSettings
{
    Libraries = [StringsLibrary.Instance, MathLibrary.Instance],
});
env.Compile("'%d apples'.format([math.greatest(3, 7, 5)])").Eval(); // "7 apples"
```

Or turn on the **full feature set** (all nine libraries + protobuf type support) — this has
no per-eval cost, since library loading is a one-time compile step:

```csharp
using Celly;
using Celly.Extensions;
using Celly.Protobuf;

var registry = ProtoTypeRegistry.FromFiles(/* your proto descriptors */);
var env = CelEnv.Create(new CelEnvSettings
{
    TypeProvider = registry,   // protobuf messages, WKTs, Any, enums, wrappers
    Adapter = registry,
    Libraries =
    [
        OptionalsLibrary.Instance,            // ?., [?], optional.of/orValue/optMap
        StringsLibrary.Instance,              // substring, format, indexOf, join, ...
        MathLibrary.Instance,                 // math.greatest/least, bitAnd, sqrt, ...
        EncodersLibrary.Instance,             // base64.encode/decode
        BindingsLibrary.Instance,             // cel.bind
        BlockLibrary.Instance,                // cel.block
        TwoVarComprehensionsLibrary.Instance, // all/exists with (key, value)
        ProtosLibrary.Instance,               // proto.getExt/hasExt
        NetworkLibrary.Instance,              // ip(), cidr()
    ],
});
```

| Library | Provides | Conformance file |
|---|---|---|
| `OptionalsLibrary` | `?.`, `[?]`, `optional.of/none/orValue/optMap/optFlatMap` | `optionals` |
| `StringsLibrary` | `charAt`, `indexOf`, `substring`, `replace`, `split`, `join`, `format`, `quote`, `lowerAscii`/`upperAscii`, `reverse` | `string_ext` |
| `MathLibrary` | `math.greatest/least`, `ceil/floor/round/trunc`, `abs/sign`, `sqrt`, bitwise, `isNaN/isInf/isFinite` | `math_ext` |
| `EncodersLibrary` | `base64.encode/decode` | `encoders_ext` |
| `BindingsLibrary` | `cel.bind` | `bindings_ext` |
| `BlockLibrary` | `cel.block` (optimizer form) | `block_ext` |
| `TwoVarComprehensionsLibrary` | `all/exists/existsOne/transformList/transformMap` with two vars | `macros2` |
| `ProtosLibrary` | `proto.getExt/hasExt` | `proto2_ext` |
| `NetworkLibrary` | `net.IP`, `net.CIDR` | `network_ext` |

See the [extensions guide](https://bsid.io/celly/guide/extensions/) for examples of each.

## Enabling protobuf

Protobuf support is a **separate setting from extensions** — enabling extension libraries
does *not* turn on protobuf, and vice-versa. If you construct messages (`MyMessage{...}`),
read message fields, or use wrappers / enums / `Any`, register a `ProtoTypeRegistry` on
**both** `TypeProvider` and `Adapter`:

```csharp
using Celly;
using Celly.Protobuf;

// One descriptor pulls in its whole dependency graph (nested types, enums, extensions).
var registry = ProtoTypeRegistry.FromFiles(MyMessage.Descriptor.File);

var env = CelEnv.Create(new CelEnvSettings
{
    Container    = "my.pkg",     // so expressions can write MyMessage{...} unqualified
    TypeProvider = registry,     // message construction, field types, enums, wrappers, Any
    Adapter      = registry,     // adapts IMessage values passed in activations
    // Libraries = [ ... ],      // extensions are separate — add them here too if needed
});
```

Without `TypeProvider`/`Adapter`, every proto/wrapper/enum expression fails with
"unknown type" — which is exactly why running the conformance suite with only `Libraries`
set (extensions but no protobuf) fails all ~600 protobuf tests. To enable **everything** at
once, see the [full configuration](https://bsid.io/celly/internals/conformance/#the-full-configuration-a-runner-must-use).

## Building

Requires the .NET 8 SDK. `protoc` is only needed to refresh vendored conformance data.

```bash
dotnet build Celly.sln
dotnet test                          # unit + conformance suites
tools/vendor-conformance.sh          # re-vendor cel-spec protos/testdata (pinned commit)
```

## Conformance methodology

Celly vendors the official suite (30 `SimpleTestFile` textprotos, pre-encoded to binary at vendoring time since Google.Protobuf C# has no textproto parser). Each of the 2,456 conformance cases is an individual xUnit test. `testdata/known-failures.txt` is a ratcheting skip-list — a listed test that starts passing fails CI until the list is updated — and it is now **empty**.

The suite's `strong_*` enum sections re-run shared expressions under the alternate strong-enum semantics they document (mutually exclusive with the default legacy sections); the harness runs those sections with Celly's strong-enum mode enabled, so both semantics are fully verified.

## License

Apache-2.0 (matching the CEL ecosystem).
