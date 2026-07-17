# Conformance Testing

"Complete support" is a claim you have to be able to *prove*. Celly's proof is the
official cel-spec conformance suite — the same corpus cel-go, cel-cpp, and cel-java verify
against — wired in from the project's very first commit.

## The suite

`github.com/cel-expr/cel-spec` ships 30 textproto files under `tests/simple/testdata/`,
each a `cel.expr.conformance.test.SimpleTestFile` containing sections of tests. A test
gives an expression plus its environment (`container`, `type_env` declarations, variable
`bindings`, flags like `disable_check`/`check_only`) and the expected outcome: a value, an
eval error, or a deduced type. 2,456 tests in total, covering everything from `basic` to
`proto2_ext` to `string_ext`.

## The textproto problem

Google.Protobuf for C# has **no textproto parser** — only Go/C++/Java/Python do. Rather
than write one (weeks of correctness risk in the test *infrastructure*), Celly converts at
vendoring time:

```bash
tools/vendor-conformance.sh
#  clones cel-spec at a PINNED commit (59505c1)
#  copies the canonical cel.expr protos into proto/
#  for each testdata/*.textproto:
#    protoc --encode=cel.expr.conformance.test.SimpleTestFile ... > testdata/NAME.binpb
```

The binary `.binpb` files are checked in; the test run just parses them with the generated
`SimpleTestFile` parser. `protoc` is only needed when refreshing the vendored data.

## The harness

`tests/Celly.Conformance/ConformanceHarness.cs` executes one test exactly as a user would:

1. Build a `CelEnv`: container + `type_env` declarations + all extension libraries + the
   `TestAllTypes` proto registry.
2. Parse. (Parse failure is acceptable only if the test expects an error.)
3. Check, unless `disable_check`. For `check_only` tests, compare the deduced root type
   against `typed_result.deduced_type` and stop.
4. Evaluate with the test's bindings.
5. Match the result — strictly.

**Strict matching** matters: `1u` must produce a *uint*, not an int that happens to equal
1 (CEL equality would say they're equal; the matcher doesn't). Doubles compare bitwise so
`-0.0 ≠ 0.0`, except any NaN matches any NaN. Maps compare order-agnostically. Messages
compare field-wise.

Each of the 2,456 cases is an individual xUnit `[Theory]` case — runnable, filterable,
and individually reported in CI.

## The ratchet

`testdata/known-failures.txt` is the mechanism that made incremental development honest:

- A listed test that **fails** → reported as an expected failure; CI stays green.
- A listed test that **passes** → the build **fails** until the entry is removed.

So the pass count could only ever go up, CI was green from the first milestone (when 100%
of tests were expected failures), and every milestone's exit criterion was "these files
leave the list." Today the list is **empty** — which means the ratchet now guards the full
suite against any regression: one newly-failing test anywhere fails CI.

The pass-rate history, for the record:

| Milestone | Passing | Newly green |
|---|---|---|
| M2 evaluator | 1,058 | basic, logic, integer_math, fp_math, lists, plumbing |
| M3 stdlib | 1,231 | string, timestamps, macros, conversions, fields, comparisons* |
| M4 checker | 1,252 | namespace, type_deduction* |
| M5 protobuf | 1,854 | proto2, proto3, dynamic, wrappers, enums(legacy), parse |
| M6 extensions | 2,438 | optionals, string_ext, math_ext, macros2, all *_ext files |
| strong enums | **2,456 (100%)** | enums(strong) |

## Extensions and proto support are opt-in — a runner must enable them

The suite has separate files (`string_ext`, `math_ext`, `optionals`, `wrappers`, `proto2`,
…) because those features are **opt-in**, exactly as in every CEL implementation: cel-go
makes you call `ext.Strings()`, `cel.OptionalTypes()`, and register your proto types; Celly
makes you add the libraries to `CelEnvSettings.Libraries` and pass a `ProtoTypeRegistry`.
The conformance harness enables all of them (see `ConformanceHarness.Run`) — that is the
*correct* way to run the suite, and it is what cel-go/cel-java/cel-cpp do too.

Running Celly **without** enabling those features fails every extension and proto test —
not because the implementation lacks them, but because the host didn't turn them on. You
can see this yourself: `CELLY_BARE=1 dotnet test tests/Celly.Conformance` strips the
libraries and registry and drops the pass rate to ~1,300/2,456, with `wrappers`, every
`*_ext` file, and `optionals` (95.7%) failing. That number reflects a bare configuration,
not the implementation's capability. (It also demonstrates the result matcher is strict:
it produces ~1,150 genuine failures when features are disabled — a matcher that trivially
passed everything would still report 100% in bare mode.)

## The strong-enum caveat, stated plainly

The suite's `enums` file contains `legacy_*` and `strong_*` sections that run the **same
expressions with mutually exclusive expected results** — they document two alternative
enum semantics. No single configuration can pass both; cel-go's conformance runner
*skips* the strong sections. Celly instead implements strong enums as a real opt-in mode
and the harness runs each section under the semantics it documents. That's how 100% is
achieved — and the methodology is stated here and in the README rather than buried.

## Regenerating

```bash
CELLY_CONFORMANCE_REPORT=/tmp/report.txt dotnet test tests/Celly.Conformance
grep '^FAIL' /tmp/report.txt   # should print nothing
```

Report mode records per-case outcomes without asserting — it's how the known-failures
list was regenerated after each milestone.
