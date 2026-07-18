# Protobuf Validation (protovalidate)

`Celly.Protovalidate` is a from-scratch .NET implementation of
[protovalidate](https://github.com/bufbuild/protovalidate) — buf's successor to
protoc-gen-validate — powered by the Celly CEL engine. It validates protobuf messages against the
`buf.validate` rules declared in their `.proto`, returning structured violations.

```bash
dotnet add package Celly.Protovalidate
```

!!! success "100% conformant"
    Verified against buf's **official conformance suite: 2,872 / 2,872** — the same corpus the
    Go, Java, Python, and C++ runtimes are tested against.

## Declaring rules

Rules live in the `.proto`, exactly as with any other protovalidate runtime:

```protobuf
syntax = "proto3";
import "buf/validate/validate.proto";

message Person {
  option (buf.validate.message).cel = {
    id: "adult_needs_email"
    expression: "this.age < 18 || this.email != ''"
  };

  string name = 1 [(buf.validate.field).string.min_len = 1];
  string email = 2 [(buf.validate.field).string.email = true];
  int32 age = 3 [(buf.validate.field).int32 = {gte: 0, lte: 150}];
  repeated string roles = 4 [(buf.validate.field).repeated.min_items = 1];
  map<string, int32> scores = 5 [(buf.validate.field).map.values.int32.gte = 0];
}
```

## Validating

Build one `Validator` per set of message descriptors and reuse it — the rules' CEL is compiled
once, and the validator is safe for concurrent use.

```csharp
using Celly.Protovalidate;

var validator = new Validator(Person.Descriptor.File);

var person = new Person { Name = "", Email = "nope", Age = 200 };
foreach (var violation in validator.Validate(person))
{
    Console.WriteLine($"{violation.RuleId}: {violation.Message}");
    // string.min_len, string.email, int32.gte_lte, repeated.min_items, ...
}
```

`Validate` returns **all** violations (not just the first), each a `buf.validate.Violation` with:

- `RuleId` — the machine-readable rule id (`string.email`, `int32.gte_lte`, `required`, or your
  custom rule's id).
- `Field` / `Rule` — `FieldPath`s locating the offending field and the rule that failed (with
  repeated indices and map-key subscripts).
- `Message` — the human-readable message.
- `ForKey` — true when the violation is on a map key rather than a value.

An empty result means the message is valid.

## What's supported

Everything in the conformance suite:

- **Standard rules** for every scalar type, `bytes`, `string` (including the format helpers
  `email`, `hostname`, `ip`, `ip_prefix`, `uri`, `uri_ref`, `host_and_port`, `uuid`, `address`),
  `enum` (`const`/`in`/`not_in`/`defined_only`), `repeated`, `map`, and the well-known types
  (`Timestamp`, `Duration`, `Any`, wrappers, `FieldMask`).
- **`required`** and **`ignore`** (`IGNORE_ALWAYS` / `IGNORE_IF_ZERO_VALUE` / presence-aware
  default) across proto2, proto3, and editions (including delimited/group fields).
- **Field-, message-, and oneof-level** custom rules (`cel` and `cel_expression`), plus
  **user-defined predefined rules** (proto2 extensions on the standard rule messages).
- **Compile-time checks**: a rule whose type doesn't match its field (e.g. `timestamp` rules on a
  `string`) is reported as a validation/compile error rather than silently passing.

Rules that reference `now` (e.g. `timestamp.lt_now`, `timestamp.within`) are evaluated against the
current time.

## How it works

The standard rules are themselves **embedded CEL** in `buf/validate/validate.proto` — protovalidate
ships the expression for each rule as a proto option. `Celly.Protovalidate` reads those expressions
via reflection and evaluates them on Celly's [100%-conformant CEL engine](../internals/conformance.md),
binding `this` (the field value), `rule`/`rules` (the rule config), and `now`. That's why the
implementation is small and tracks the spec closely: your CEL engine does the work, and the rules
come from the proto.

## See also

- The [Protobuf Support](protobuf.md) guide — the `Celly.Protobuf` layer this builds on.
- [Bounding untrusted evaluation](errors.md#bounding-untrusted-evaluation) — the evaluation budget
  and static cost estimation, useful when validating messages carrying untrusted CEL.
