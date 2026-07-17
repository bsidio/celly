# Celly

**A native C#/.NET implementation of Google's [Common Expression Language (CEL)](https://github.com/cel-expr/cel-spec).**

Celly is written from scratch in pure managed C# — no WASM shims, no Go-compiled artifacts, no native library bindings. It aims for *complete* spec support, proven by the official cel-spec conformance suite (all 29 test files).

> ⚠️ **Status: pre-release, under active development.** See [Roadmap](#roadmap) for current progress.

## Why Celly?

CEL is the expression language behind Kubernetes admission policies (ValidatingAdmissionPolicy), Envoy RBAC, Google Cloud IAM conditions, gRPC protovalidate, and more. Official implementations exist for Go, C++, Java, and Rust — but not .NET. Existing .NET options are partial ports without conformance coverage. Celly's goal is a first-class, conformance-passing native implementation.

```csharp
var env = CelEnv.NewEnv(
    CelEnvOptions.Variable("request", CelType.Map(CelType.String, CelType.Dyn)));

var result = env.Compile("request.user.startsWith('admin-') && size(request.groups) > 0");
var program = env.Program(result.Ast);

CelValue output = program.Eval(Activation.Of(new Dictionary<string, object>
{
    ["request"] = new Dictionary<string, object>
    {
        ["user"] = "admin-alice",
        ["groups"] = new[] { "ops" },
    },
}));
// output => BoolValue.True
```

## Packages

| Package | Contents | Dependencies |
|---|---|---|
| `Celly` | Lexer, parser, macros, type checker, evaluator, standard library, extensions (strings, math, encoders, lists, sets, bindings, optionals, …) | **None** |
| `Celly.Protobuf` | Protobuf type support: message construction & field access, well-known types (Timestamp/Duration/Struct/Any/wrappers), enums, proto2 extensions, AST ↔ `cel.expr` proto conversion | `Celly`, `Google.Protobuf` |

Use `Celly` alone to evaluate expressions over .NET dictionaries/lists/primitives; add `Celly.Protobuf` when your data is protobuf messages or you need full spec semantics for proto-defined types.

## Design highlights

- **Hand-written lexer & recursive-descent parser** — zero build-time dependencies, precise error positions, spec capacity guarantees.
- **cel-go-shaped API**: immutable, thread-safe `CelEnv` → `Compile` (parse + typecheck) → `CelProgram` → `Eval(activation)`. Programs are reusable and safe for concurrent evaluation.
- **Plan-based interpreter**: the AST compiles to an interpretable tree with overload pre-binding, constant folding, and compiled-regex caching.
- **Spec-faithful semantics**: cross-type numeric comparisons, commutative error-absorbing `&&`/`||`, Unicode code-point string operations, nanosecond-precision timestamps/durations, Go-compatible `string(double)` formatting.
- **RE2-compatible `matches()`** via `System.Text.RegularExpressions` `NonBacktracking` mode (linear-time guarantee), pluggable through `IRegexEngine`.
- **Errors as values**: evaluation never throws for CEL-level errors; unknowns and partial evaluation are supported for attribute-driven use cases.
- **Conformance-first testing**: the official cel-spec test suite runs in CI from day one with a ratcheting known-failures list — progress is monotonic and verifiable.

## Building

Requires the .NET 8 SDK. `protoc` is only needed to refresh vendored conformance data.

```bash
dotnet build Celly.sln
dotnet test                          # unit + conformance suites
tools/vendor-conformance.sh          # re-vendor cel-spec protos/testdata (pinned commit)
```

## Conformance

Celly vendors the official suite (29 `SimpleTestFile` textprotos, pre-encoded to binary at vendoring time since Google.Protobuf C# has no textproto parser). Each conformance test is an individual xUnit case. `testdata/known-failures.txt` tracks not-yet-passing tests: a listed test that *starts passing* fails CI until the list is updated, so the pass rate can only go up.

## Roadmap

| Milestone | Scope | Conformance files green |
|---|---|---|
| M0 | Skeleton, CI, conformance pipeline | — (harness operational) |
| M1 | Lexer, parser, macros | — (parser goldens) |
| M2 | Value model, evaluator (parse-only mode) | basic, logic, integer_math, fp_math, lists, parse, plumbing |
| M3 | Complete standard library | string, timestamps, macros, conversions*, comparisons, fields* |
| M4 | Type checker | type_deduction, namespace + checked re-runs |
| M5 | `Celly.Protobuf` | proto2, proto3, proto2_ext, enums, wrappers, dynamic |
| M6 | Optionals, unknowns, extensions | **all 29 files** |
| M7 | Hardening, benchmarks, NuGet release | — |

See [docs/PLAN.md](docs/PLAN.md) for the full architecture and implementation plan.

## License

TBD (Apache-2.0 intended, matching the CEL ecosystem).
