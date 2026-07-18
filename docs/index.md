# Celly

**A native C#/.NET implementation of Google's [Common Expression Language (CEL)](https://github.com/cel-expr/cel-spec).**

Celly is written from scratch in pure managed C# — no WASM shims, no Go-compiled artifacts, no native library bindings.

!!! success "Conformance: 2,456 / 2,456 (100%)"
    Every test in the official cel-spec conformance suite passes, verified in CI on Linux,
    Windows, and macOS. See [Conformance Testing](internals/conformance.md) for how this is
    enforced against regressions.

## What is CEL?

CEL is a small, non-Turing-complete expression language designed for evaluating **untrusted expressions safely and fast**. You embed it where users or configuration need to express conditions:

- Kubernetes admission policies (`ValidatingAdmissionPolicy`)
- Envoy RBAC rules
- Google Cloud IAM conditions
- gRPC message validation (protovalidate)
- Feature flags, alert routing, row-level security — anywhere a "rule" is data

A CEL expression looks like this:

```javascript
request.user.startsWith('admin-') &&
resource.labels.env in ['staging', 'prod'] &&
size(request.groups) > 0
```

CEL guarantees **termination** (no loops or recursion — only bounded comprehensions over data), **isolation** (expressions can only touch what you bind), and **gradual typing** (an optional compile-time type checker catches errors before evaluation).

## Packages

| Package | Contents | Dependencies |
|---|---|---|
| [`Celly`](https://www.nuget.org/packages/Celly) | Parser, type checker, evaluator, standard library, all extension libraries | **None** |
| [`Celly.Protobuf`](https://www.nuget.org/packages/Celly.Protobuf) | Protobuf message types, well-known types, enums, Any, proto2 extensions | `Celly`, `Google.Protobuf` |
| [`Celly.Protovalidate`](https://www.nuget.org/packages/Celly.Protovalidate) | [protovalidate](https://github.com/bufbuild/protovalidate) for .NET — validate protobuf messages against `buf.validate` rules | `Celly`, `Celly.Protobuf` |

```bash
dotnet add package Celly
dotnet add package Celly.Protobuf        # only if your data is protobuf messages
dotnet add package Celly.Protovalidate   # protobuf validation with buf.validate rules
```

## Where to go next

- **[Getting Started](getting-started.md)** — install, evaluate your first expression, bind variables
- **[Examples & Recipes](examples.md)** — twelve complete scenarios: authorization, validation, feature flags, rule engines, policy caching, and more
- **User Guide** — the [CEL language](guide/language.md), the [API](guide/api.md), [extensions](guide/extensions.md), [protobuf](guide/protobuf.md)
- **Internals** — a guided tour of how the implementation actually works, starting with the
  [Architecture Overview](internals/architecture.md). Written to be readable top-to-bottom.
