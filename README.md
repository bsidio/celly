# Celly

**A native C#/.NET implementation of Google's [Common Expression Language (CEL)](https://github.com/cel-expr/cel-spec).**

Celly is written from scratch in pure managed C# — no WASM shims, no Go-compiled artifacts, no native library bindings.

> ✅ **Conformance: 2,456 / 2,456 (100%)** of the official cel-spec conformance suite
> (30 testdata files, pinned @ [`59505c1`](https://github.com/cel-expr/cel-spec/commit/59505c14f3187e6eb9684fbd3d07146f614c6148)), verified in CI on Linux, Windows, and macOS.

**📖 Documentation: [bsid.io/celly](https://bsid.io/celly/)** — user guide plus a deep-dive internals track explaining how the lexer, parser, macros, type checker, evaluator, and protobuf integration work.

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

Use `Celly` alone to evaluate expressions over .NET dictionaries/lists/primitives; add `Celly.Protobuf` when your data is protobuf messages.

## Feature checklist

- **Full standard library**: operators with CEL semantics (checked int64/uint64 arithmetic, cross-type numeric comparisons, IEEE doubles), `size`/`in`/indexing, all type conversions with Go-compatible formatting, RE2-semantics `matches()` (via .NET's linear-time NonBacktracking engine), nanosecond-precision timestamps/durations with IANA timezone accessors
- **Macros**: `has`, `all`, `exists`, `exists_one`, `map`, `filter` (+ two-variable comprehensions)
- **Type checker**: gradual typing with `dyn`, parameterized-type unification, container (`C.name`) resolution, comprehension-variable shadowing per spec
- **Optionals**: `optional.of/ofNonZeroValue/none`, `a.?b`, `a[?b]`, `[?e]`, `{?k: v}`, `orValue`, `optMap`/`optFlatMap`, full optional chaining
- **Extension libraries** (opt-in via `CelEnvSettings.Libraries`): strings (incl. `format` and `strings.quote`), math, encoders (base64), bindings (`cel.bind`), block (`cel.block`), two-var comprehensions, proto2 extensions (`proto.getExt/hasExt`), networking (`net.IP`/`net.CIDR`)
- **Protobuf**: message construction with WKT collapse (wrappers → primitives, Struct/Value/ListValue → map/dyn/list), Any pack/unpack with bytewise fallback, presence-aware field-wise message equality, proto2 extension fields, **strong-enum mode** (`ProtoTypeRegistry.FromFiles(strongEnums: true, …)`) where enums are distinct named types — an opt-in the reference Go implementation doesn't offer
- **Plain .NET types**: `NativeTypeProvider` gives your own classes/records/enums full CEL semantics — construction, typed field access via the checker, `has()` presence, snake_case field aliases, `Nullable<T>` as wrappers — no protobuf required
- **First-class ASTs**: traversal/inspection tools (`AstTools`), lossless conversion to/from the canonical `cel.expr` protos (`ParsedExpr`/`CheckedExpr` incl. type & reference maps) for caching and cross-implementation interop, and a precedence-aware unparser (AST → source, macros restored to original form)
- **Thread safety**: `CelEnv` and `CelProgram` are immutable; programs are safe for concurrent evaluation

```csharp
// Extensions are opt-in libraries:
var env = CelEnv.Create(new CelEnvSettings
{
    Libraries = [Celly.Extensions.StringsLibrary.Instance, Celly.Extensions.MathLibrary.Instance],
});
env.Compile("'%d apples'.format([math.greatest(3, 7, 5)])").Eval(); // "7 apples"
```

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
