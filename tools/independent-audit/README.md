# Independent conformance audit

A second, deliberately-independent verification of the 100% conformance claim — built to
answer a specific challenge: *"your repo's test harness / vendored data is the rigged part."*

It shares **nothing** with `tests/Celly.Conformance`:

| | This audit | `tests/Celly.Conformance` |
|---|---|---|
| Test data | textprotos → binary via **Python `text_format`** | via `protoc --encode` |
| Celly | the **1.1.0 NuGet package** | local project reference |
| Harness | fresh single-file console | xUnit |

It is the same method the accusatory "45%" report used (Python conversion + a custom
harness against the NuGet package) — but **configured correctly**: all extension libraries
and protobuf type support enabled, exactly as every CEL conformance runner does.

## Run it

```bash
# 1. Generate Python protobuf bindings (self-consistent protoc + runtime):
pip install grpcio-tools
cd <cel-spec checkout @ 59505c1>
python -m grpc_tools.protoc --proto_path=proto --python_out=<out>/pygen \
    proto/cel/expr/conformance/test/simple.proto \
    proto/cel/expr/conformance/proto2/test_all_types.proto \
    proto/cel/expr/conformance/proto2/test_all_types_extensions.proto \
    proto/cel/expr/conformance/proto3/test_all_types.proto

# 2. Convert every textproto to .binpb via Python text_format (the report's step 1):
python tools/independent-audit/convert_textproto_to_pb.py <cel-spec>/tests/simple/testdata <out>/binpb

# 3. Run Celly 1.1.0 (from NuGet) against it, all features enabled:
dotnet run -c Release --project tools/independent-audit -- <out>/binpb
```

## Result

**2,454 / 2,456 (99.9%)** — every extension file at 100% (optionals 70/70, string_ext
216/216, math_ext 199/199, wrappers 36/36, proto2 118/118, …). This independently confirms
the 100% claim and refutes the "45%, extensions 100% failure" report, whose numbers came
from running Celly with extensions and proto support *disabled* (reproducible with
`CELLY_BARE=1 dotnet test tests/Celly.Conformance`).

### The 2 "failures" are a Python `text_format` defect, not a Celly bug

Both are `parse/bytes_literals/..._unescaped_punctuation`, and the discrepancy is in the
**expected value**, not Celly's output:

| | expected bytes for `b''' ? \" \' ` '''` |
|---|---|
| Python `text_format` (this audit) | `20 5c 3f …` — kept the backslash (`\?` → `\` `?`, **10 bytes**) |
| `protoc` (repo) | `20 3f …` — resolved `\?` → `?` (**9 bytes**) |
| **cel-go** (reference) evaluates the expr to | `20 3f …` (**9 bytes**) |
| **Celly** evaluates the expr to | `20 3f …` (**9 bytes**) — matches cel-go |

Celly's output matches the reference implementation exactly; Python's *converted expected
value* is the outlier, because its `text_format` mis-handles the `\?` escape (the known
cel-spec textformat ambiguity, PR #512). So with correctly-decoded data (protoc, as the
repo suite uses) Celly passes all 2,456. The report's Python-based data pipeline had this
same corruption baked in.
