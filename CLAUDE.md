# CLAUDE.md — Celly

Celly is a from-scratch, **pure managed C#** implementation of Google's Common Expression Language (CEL, https://github.com/cel-expr/cel-spec). No native bindings, no WASM shims, no Go-compiled artifacts — native .NET processing only.

Full plan: `docs/PLAN.md`. Spec reference: `docs/langdef-notes.md` (gotchas) and the vendored protos in `proto/`.

## Build & test

```bash
export PATH="$HOME/.dotnet:$PATH"        # SDK installed via dotnet-install to ~/.dotnet
dotnet build Celly.sln
dotnet test tests/Celly.Tests             # unit tests (fast inner loop)
dotnet test tests/Celly.Conformance       # official cel-spec suite (binpb-driven)
tools/vendor-conformance.sh               # refresh vendored cel-spec protos + testdata (needs protoc)
```

## Repository layout

- `src/Celly` — core package. **ZERO package dependencies.** Namespaces: `Celly` (Env/Program API), `Celly.Common`, `Celly.Ast`, `Celly.Parsing`, `Celly.Types`, `Celly.Checking`, `Celly.Values`, `Celly.Interpreter`, `Celly.Stdlib`, `Celly.Extensions`, `Celly.Providers`.
- `src/Celly.Protobuf` — Google.Protobuf integration (`ProtoTypeRegistry`, WKT adapter, `AstConverter`, `ValueConverter`). All proto-specific behavior lives behind `Celly.Providers.ITypeRegistry`.
- `tests/Celly.Conformance` — runs the official suite from `testdata/*.binpb` (`cel.expr.conformance.test.SimpleTestFile`). One xUnit case per test.
- `testdata/known-failures.txt` — **ratcheting skip-list**: listed+failing ⇒ skipped; listed+passing ⇒ test FAILURE (you must remove the line). CI stays green; progress is monotonic. When you make tests pass, remove them from this file in the same commit.
- `proto/` + `testdata/*.binpb` — vendored from cel-spec at a pinned commit (see `tools/vendor-conformance.sh`). Never hand-edit; refresh via the script.

## Hard rules

- Core (`src/Celly`) must never gain a dependency on Google.Protobuf or any other package.
- Errors and unknowns are **values** (`ErrorValue`, `UnknownValue`), never exceptions across evaluator boundaries. Exceptions from `checked` arithmetic are caught at the dispatch point and converted.
- Overload IDs in `Celly.Common.Overloads` must match cel-spec exactly — the checker reference map and runtime dispatch key on them.
- One `CelType` model shared by checker and runtime; primitives are singletons (`type(1) == int` relies on it).
- Parse binary-op chains **iteratively** (spec requires ≥32 chained `||`/`&&`/args, ≥24 ternaries, ≥12 nesting without failure).

## Spec gotchas (verified against cel-spec @ 2026-07, commit 59505c1)

- Cross-type numeric comparisons (int/uint/double on one number line) are REQUIRED at runtime. Never compare via long↔double casts (precision loss above 2^53) — port cel-go's algorithms.
- `size(string)`, `charAt`, `indexOf`, `substring` are **Unicode code-point** based; .NET strings are UTF-16. `string.CompareOrdinal` mis-sorts surrogate pairs vs code points — use rune-wise compare when surrogates present.
- Timestamps/durations need **nanosecond** precision; .NET ticks are 100ns → custom `(long Seconds, int Nanos)` structs, hand-written RFC 3339. Timestamp range 0001-01-01..9999-12-31T23:59:59.999999999Z.
- Temporal accessors: `getMonth()` 0-based, `getDate()` 1-based, `getDayOfMonth()` 0-based, `getDayOfWeek()` 0=Sunday.
- `long.MinValue` literal only parses because unary `-` folds into the literal. Same folding gives `-0.0`.
- `checked` doesn't cover everything: special-case `long.MinValue / -1`, `long.MinValue % -1`, `-long.MinValue` → error values.
- NaN: `x != x` is true; all orderings false. Conformance matcher: any NaN matches any NaN. `-0.0 == 0.0` is true (use `==` semantics, not `double.Equals`).
- Map keys: numerically-equal int/uint are the SAME key; integral-double index lookups find int keys; duplicate keys in a map literal are an error.
- `string(double)` must match Go `strconv.FormatFloat` presentation (`GoDoubleFormatter`), not .NET `1E+23` style.
- `matches()` = RE2 semantics → `RegexOptions.NonBacktracking | CultureInvariant` behind `IRegexEngine`; translate `(?P<name>` → `(?<name>`. No native RE2 binding, ever.
- `int(enum)` conversion was REMOVED from the spec (Oct 2025) — do not implement.
- Optionals (`?.`, `[?]`, `{?k: v}`, `optional.of/none/orValue/optMap/optFlatMap`) are extensions but have official conformance files — required for complete support.
- `&&`/`||` are commutative error-absorbing: a determining operand (false for &&, true for ||) wins even if the other side is error/unknown. Unknown beats error when merging.
- Canonical protos are package `cel.expr`; `google.api.expr.v1alpha1` is deleted legacy — never reference it.
- Google.Protobuf C# has NO textproto parser — conformance textprotos are converted to `.binpb` by `tools/vendor-conformance.sh` using `protoc --encode`. Don't try to parse textproto at test runtime.

## Milestones (tracked in the session task list)

M0 skeleton+pipeline → M1 lexer/parser/macros → M2 values+evaluator (parse-only) → M3 stdlib complete → M4 checker → M5 Celly.Protobuf → M6 optionals/unknowns/extensions (all 29 conformance files green) → M7 hardening+packaging. Per-milestone conformance targets are in `docs/PLAN.md`.
