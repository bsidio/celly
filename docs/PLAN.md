# Celly — Native CEL (Common Expression Language) for .NET

> **Status (2026-07-17): COMPLETE.** All milestones shipped; conformance is 2456/2456 (100%)
> with an empty known-failures ratchet. Beyond this plan, a strong-enum mode
> (`ProtoTypeRegistry.FromFiles(strongEnums: true, …)`) was added so the suite's strong_*
> enum sections pass under the semantics they document. This file is preserved as the
> original design document.

## Context

Build a from-scratch, **pure managed C#** implementation of Google's Common Expression Language (CEL, github.com/cel-expr/cel-spec — formerly google/cel-spec) as a NuGet package family with complete spec support, validated against the official conformance suite. No WASM shims, no Go-compiled artifacts, no native bindings — native .NET processing throughout.

The field is open: existing .NET options (`Cel` by TELUS, `Cel.NET` port of cel-java) are partial; no conformance-passing native implementation exists. Official implementations are cel-go/cel-cpp/cel-java/cel-rust only.

**Confirmed decisions:**
- Hand-written lexer + recursive-descent parser (no ANTLR)
- Split packages: `Celly` core (zero dependencies) + `Celly.Protobuf` (Google.Protobuf integration)
- net8.0+ only
- xUnit unit tests + official cel-spec conformance suite (29 textproto files, `cel.expr.conformance.test.SimpleTestFile`)
- `matches()` via `System.Text.RegularExpressions` with `RegexOptions.NonBacktracking` (linear-time RE2-like guarantees, pure managed), behind a pluggable `IRegexEngine`
- Public API modeled on cel-go: `CelEnv` → `Compile` → `CelProgram` → `Eval(activation)`

## Solution layout

```
celly/
├── Celly.sln
├── Directory.Build.props            # net8.0, nullable, TreatWarningsAsErrors, package metadata
├── src/
│   ├── Celly/                       # core package — ZERO dependencies
│   └── Celly.Protobuf/              # Celly + Google.Protobuf
├── tests/
│   ├── Celly.Tests/                 # xUnit unit tests
│   ├── Celly.Protobuf.Tests/
│   ├── Celly.Conformance/           # official suite runner
│   └── Celly.Benchmarks/            # BenchmarkDotNet
├── proto/                           # vendored cel-spec protos (pinned tag)
├── testdata/                        # vendored conformance data as .binpb + known-failures.txt
└── tools/vendor-conformance.sh      # clone cel-spec → protoc --encode textproto→binpb
```

## Core architecture (namespaces in `Celly`)

| Namespace | Contents |
|---|---|
| `Celly` | `CelEnv` (immutable/thread-safe; `Extend/Parse/Check/Compile/Program`), `CelProgram`, `CompileResult`+`Issues`, `IActivation`/`Activation` |
| `Celly.Common` | `Source`, `Location`, `CelIssue`, `Operators` table, `Overloads` (spec overload-id constants — must match cel-spec exactly) |
| `Celly.Ast` | Native AST mirroring `cel.expr` syntax shape: `ConstExpr, IdentExpr, SelectExpr(TestOnly), CallExpr, ListExpr, StructExpr, MapExpr, ComprehensionExpr(+IterVar2)`; `SourceInfo` with macro-call map |
| `Celly.Parsing` | `Lexer`, `Parser` (recursive descent; **iterative** for binary-op chains so spec capacity minimums — 32 chained ops, 24 ternaries, 12 nesting — don't blow the stack; MaxRecursionDepth default 250), `IMacro`/`MacroExpander` |
| `Celly.Types` | Single unified `CelType` model shared by checker AND runtime (`ListType(T)`, `MapType(K,V)`, `TypeParamType`, `StructTypeRef`, `DynType`, `OptionalType(T)`, singletons for primitives) |
| `Celly.Checking` | `Checker`, type unification (`TypeSubstitution`), `StandardDecls` (~250 overloads), container `C.name` resolution, outputs type map + reference map |
| `Celly.Values` | `CelValue` hierarchy + trait interfaces |
| `Celly.Interpreter` | `Planner` (AST → `IInterpretable` tree, overload pre-binding from reference map, constant folding), comprehension eval, attribute resolution, partial activations/unknowns |
| `Celly.Stdlib` | Standard functions incl. `Rfc3339` (hand-written, 0–9 fractional digits) and `GoDoubleFormatter` |
| `Celly.Extensions` | Opt-in `ICelLibrary` per extension: Strings (incl. `format`, `quote`), Math, Encoders, Lists, Sets, Bindings (`cel.bind`), TwoVarComprehensions, Regex, Optionals |
| `Celly.Providers` | **Core↔protobuf seam**: `ITypeProvider` (`FindStructType/FindStructFieldType/FindIdent/NewValue`), `ITypeAdapter`, `ITypeRegistry`; core ships `NativeTypeRegistry` (primitives, IDictionary, IEnumerable) |

**`Celly.Protobuf`**: `ProtoTypeRegistry : ITypeRegistry` (from descriptors), lazy `ProtoMessageValue/ProtoListValue/ProtoMapValue`, `WellKnownTypeAdapter` (wrappers→primitives, Struct/Value/ListValue, Any unpack, Timestamp/Duration), `AstConverter` (native AST ↔ `cel.expr.ParsedExpr/CheckedExpr`), `ValueConverter` (`cel.expr.Value` ↔ `CelValue`), proto2 extensions runtime (`proto.getExt/hasExt`).

## Key design decisions (.NET-specific)

1. **Value model**: `abstract class CelValue` with sealed subclasses (`IntValue(long)`, `UintValue(ulong)`, `DoubleValue`, `BoolValue`, `StringValue`, `BytesValue(ReadOnlyMemory<byte>)`, `NullValue`, `TypeValue`, `TimestampValue`, `DurationValue`, `ListValue`/`MapValue` bases, `ErrorValue`, `UnknownValue`, `OptionalValue`). Trait interfaces drive dispatch (`IComparableValue`, `ISizedValue`, `IIndexer`, `IAdder`, …). **Errors/unknowns are values, not exceptions** — required for `&&`/`||`/`?:` absorption semantics. Singleton caches (True/False/Null, small ints) mitigate boxing.
2. **Strings are code-point based** (CEL `size`, `charAt`, `indexOf`, `substring`): `StringValue` caches `HasSupplementary`/`CodePointLength`; fast path when no surrogates; iterate via `Rune`. **Comparison trap**: `string.CompareOrdinal` sorts UTF-16 units, not code points — use rune-wise compare when surrogates present. `string(bytes)` rejects invalid UTF-8 (`UTF8Encoding(false, true)`); lexer rejects `\u` surrogates/invalid code points.
3. **Timestamp/Duration**: custom structs `(long Seconds, int Nanos)` — .NET ticks are 100ns but the spec needs nanosecond precision. Hand-written RFC 3339 parse/format. Range checks (0001-01-01..9999-12-31.999999999 / ±315,576,000,000s) → `ErrorValue`. IANA timezones via `TimeZoneInfo.FindSystemTimeZoneById`; fixed offsets `(+|-)HH:MM` parsed directly. Note spec quirks: `getMonth()` 0-based, `getDate()` 1-based, `getDayOfMonth()` 0-based.
4. **Numerics**: `checked` arithmetic → `ErrorValue`; explicit handling for `long.MinValue / -1`, `% -1`, `-long.MinValue`. Parser folds unary `-` into literals (int64.MinValue, `-0.0`). **Cross-type numeric comparison is required at runtime** (int/uint/double on one number line) — port cel-go's compare algorithms exactly; never cast long↔double (precision loss > 2^53). NaN: `!=` is true, all orderings false; conformance matcher treats any-NaN==any-NaN. Map keys: int/uint numerically equal are the same key (custom `IEqualityComparer<CelValue>`); integral-double lookups find int keys. `string(double)` must match Go's `strconv.FormatFloat` → `GoDoubleFormatter` wraps .NET shortest-round-trip digits in Go presentation.
5. **Conformance harness**: Google.Protobuf C# has **no textproto parser** → `tools/vendor-conformance.sh` converts the 29 textprotos to `.binpb` via `protoc --encode=cel.expr.conformance.test.SimpleTestFile` at vendoring time (pinned cel-spec tag); binaries checked in; protoc needed only for refresh. Test projects codegen `cel.expr.*` + `TestAllTypes` (proto2+proto3) via `Grpc.Tools`. Runner: one xUnit `[Theory]` case per test (file/section/name); builds env from `type_env`+`container`, honors `disable_check`/`disable_macros`/`check_only`; proto-aware result comparer (NaN-tolerant, unordered maps). **Ratcheting `known-failures.txt`**: listed+failing ⇒ skip-reported; listed+passing ⇒ CI failure (forces monotonic progress; CI green from day one).
6. **Interpreter is plan-based** (cel-go style): plan-time overload binding, constant folding, compiled-regex caching (bounded LRU for dynamic patterns). Comprehension node with accumulator in scoped activation, `iterVar2` support, iteration budget. Attribute "maybe" resolution for parse-only namespace semantics (`a.b.c` as qualified var vs field selects, longest-first per container).
7. **Optionals** (`?.`, `[?]`, `{?k: v}`, `optional.of/none/ofNonZeroValue`, `orValue/or`, `optMap/optFlatMap`) are technically an extension but have official conformance files — implement as `OptionalsLibrary` + parser support. Same for `cel.bind`, two-var comprehensions (macros2), `cel.@block`.
8. **Spec currency notes**: `int(enum)` conversion removed Oct 2025 — don't add it. Canonical protos are `cel.expr` (google.api.expr.v1alpha1 is deleted legacy). Strings extension is spec'd in `doc/extensions/strings.md`.

## Milestones (each ends with conformance files removed from known-failures.txt)

- **M0 — Skeleton & pipeline** (~1 wk): sln/projects/CI (GitHub Actions), vendor script, pinned protos + 29 .binpb, Grpc.Tools codegen validated (incl. proto2 TestAllTypes — de-risk early), runner enumerating all tests as known-failures. *CI green, 0 passing.*
- **M1 — Lexer/Parser/Macros** (~2–3 wks): full lexis (all escape forms, raw/triple/bytes strings, hex/uint literals, reserved words), grammar with trailing commas, literal `-` folding, macro expansion to comprehension AST, optional syntax, error positions/recovery. Golden AST tests ported from cel-go parser test table.
- **M2 — Values + planner + evaluator (parse-only dyn mode)** (~3 wks): value hierarchy, traits, cross-type numerics, absorption semantics, comprehensions, activations, arithmetic/logic/comparison/index/`in`/`size`, `CelEnv`/`CelProgram` API. *Green: basic, logic, integer_math, fp_math, lists, parse, plumbing, most comparisons.*
- **M3 — Complete stdlib** (~2 wks): all conversions (Go-compatible formatting), `matches()` engine, string receivers, timestamp/duration structs + RFC3339 + accessors + temporal arithmetic. *Green: string, timestamps, macros, conversions (non-proto), fields (map cases), rest of comparisons.*
- **M4 — Type checker** (~2–3 wks): unification, standard decls, container resolution, type/reference maps, checked planning; harness flips to check-enabled. *Green: type_deduction, namespace; checked re-run of all prior files.*
- **M5 — Celly.Protobuf** (~2–3 wks): ProtoTypeRegistry, message construction/presence semantics, WKT adapter, enums, Any, AstConverter, proto2 extensions. *Green: proto2, proto3, proto2_ext, enums, wrappers, dynamic, remaining fields/conversions.*
- **M6 — Optionals, unknowns, extensions** (~2–3 wks): OptionalsLibrary, partial activations + attribute patterns + unknown merging, strings ext (incl. `format`), math, encoders, sets, lists, bindings, two-var comprehensions, regex ext, network ext, cel.@block. *Green: **all 29 files**, known-failures.txt empty.*
- **M7 — Hardening & packaging** (~1–2 wks): BenchmarkDotNet suite, lexer/parser fuzzing, PublicApiAnalyzers, XML docs, README/samples, SourceLink, deterministic builds, AOT/trimming audit (core is reflection-free), NuGet publish.

Total ≈ 16–20 engineer-weeks; each milestone is independently shippable progress with green CI.

## Top risks & mitigations

1. Surrogate/code-point string semantics → fast-path design day one; astral-plane test corpus.
2. RE2 vs .NET regex gaps → NonBacktracking + small pattern-translation pass (`(?P<name>` → `(?<name>`), pluggable `IRegexEngine`, documented deviations.
3. Go double formatting → `GoDoubleFormatter` + ported Go strconv test vectors.
4. Cross-type compare precision → port cel-go algorithms; property tests vs BigInteger reference.
5. `checked` blind spots (`MinValue/-1` etc.) → explicit special cases; integer_math conformance gates.
6. IANA tz on Windows → fixed-offset parser covers conformance; CI matrix incl. windows-latest.
7. Proto2 TestAllTypes C# codegen (groups/extensions) → validate in M0 before any impl work.
8. Unknowns fidelity → mirror cel-go AttributePattern design; schedule after absorption machinery proven (M6).
9. Conformance data drift → pinned tag + re-encode check in CI.
10. Class-based value allocation perf → singleton caches + plan-time folding now; struct-wrapper experiment deferred (internal repr, not API break).

## Verification

- `dotnet test` — unit suites (lexer goldens, parser AST goldens, checker deductions, eval tables, numeric property tests, RFC3339/double-format vectors, surrogate corpus).
- Conformance: `dotnet test tests/Celly.Conformance` — pass-rate ratchet via known-failures.txt; milestone exit = named files green; final bar = all 29 files, empty skip-list.
- Interop: AST↔proto round-trip on every parser golden; Value converter round-trips.
- `dotnet pack` both packages; benchmark suite informational in CI.

## First execution step (upon approval)

`git init`, M0 scaffolding: solution + 6 projects, Directory.Build.props, vendor script + pinned cel-spec checkout, protoc conversion of testdata, Grpc.Tools codegen smoke test, conformance runner walking skeleton, GitHub Actions workflow.
