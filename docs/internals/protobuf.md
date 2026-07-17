# Protobuf Integration

CEL's data model *is* protobuf's: int64/uint64/double scalars, `Timestamp`/`Duration`,
message construction, enum semantics. Yet many .NET users just want dictionaries. Celly
resolves the tension with a seam: the core defines small interfaces, `Celly.Protobuf`
implements them, and the core never references `Google.Protobuf`.

## The seam

```csharp
// src/Celly/Providers/ — the core side
public interface ITypeProvider
{
    CelType? FindStructType(string name);                       // message name -> CEL type
    CelType? FindStructFieldType(string messageName, string fieldName);
    CelValue? FindIdent(string name);                           // enum constants, type names
    CelValue NewValue(string messageName, IReadOnlyList<KeyValuePair<string, CelValue>> fields);
}

public interface IStructValue          // implemented by message values
{
    CelValue GetField(string name);
    CelValue HasField(string name);
}
```

The checker calls the provider for message/field types; the planner resolves struct
literals and enum constants through it; `SelectEval` handles any `IStructValue`. Swap the
provider and you could back CEL structs with anything.

## ProtoTypeRegistry

`ProtoTypeRegistry.FromFiles(descriptors…)` walks each `FileDescriptor` — dependencies,
nested messages, nested enums, extensions — and indexes:

- message full name → `MessageDescriptor` (construction, field lookup)
- enum constant full name → value (`"pkg.Color.RED"` → `1`)
- `(extendee, extension full name)` → `FieldDescriptor` (backtick selectors, `proto.getExt`)
- a `TypeRegistry` + `ExtensionRegistry` for unpacking `Any` payloads *with extensions intact*

## Well-known types collapse

The registry's adaption layer (`AdaptMessage`) means you never see a WKT message as a
message: `Int32Value` → `int`/`null`, `Timestamp` → CEL timestamp, `Struct` → map, `Value`
→ whatever it holds, `ListValue` → list, `Any` → recursively unpacked. Construction runs
the same mapping in reverse — building `google.protobuf.Int32Value{value: 5}` in an
expression yields the CEL int `5`.

Three hard-won details:

- **C# maps wrapper-typed fields to nullable primitives** (`int?`), not wrapper messages —
  both reflection directions must speak boxed primitives. (The Go and Java APIs don't do
  this; it's the .NET-specific trap of this milestone.)
- **Unset fields**: wrapper/`Value`/`Any` fields read as `null`; other message fields read
  as their default instance (so `TestAllTypes{}.single_nested_message.bb` is `0`, not an
  error).
- **Null assignment**: `null` unsets message-kind fields, is *pruned* from repeated/map
  slots of message-kind element types (but retained for `Any`/`Value`, which represent
  null natively), and errors for `Struct`/`ListValue`/repeated/map targets.

## Any, including the bytewise fallback

Packing chooses the right wrapper for scalars (`int` → `Int64Value`, map → `Struct`, …).
Unpacking parses with the extension registry so proto2 extensions survive. If the
`type_url` is empty or unknown, the `Any` **stays wrapped** — and message equality then
compares `type_url` + raw bytes, which is exactly the "bytewise fallback" behavior the
conformance suite specifies for unresolvable payloads.

## Message equality

Field-wise CEL equality: same descriptor, presence must match for explicit-presence
fields, then each set field's adapted values compare with `EqualTo`. Deliberately *not*
`proto.Equal` — proto compares doubles bitwise (NaN == NaN), while CEL requires IEEE
semantics inside messages too. The suite tests both directions of this.

## Enums, twice

**Legacy mode** (default, spec-standard): enums are `int`. Constants resolve to
`IntValue`; enum-typed fields read/write ints; proto2 (closed) enums reject undefined
values on *field assignment* with a range error.

**Strong mode** (`FromFiles(strongEnums: true, …)`): enums are distinct named types.
`EnumValue` carries `(type, number)`; `type(Color.RED) == pkg.Color`; per-enum conversion
functions `Color(1)` / `Color('RED')` come from `CreateEnumConversionLibrary()`; `int ↔
enum` assignability lets ints still flow into enum fields; `int(enum)` returns. The
conformance suite's `strong_*` sections specify this mode, and Celly is (to our knowledge)
the only implementation that passes them.

Next: [Conformance Testing](conformance.md).
