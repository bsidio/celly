# Protobuf Support

CEL's type system is defined in terms of protobuf. The `Celly.Protobuf` package plugs
protobuf messages into the core through the provider seam (`ITypeProvider`/`ITypeAdapter`).

## Setup

```csharp
using Celly;
using Celly.Protobuf;

var registry = ProtoTypeRegistry.FromFiles(MyMessage.Descriptor.File);

var env = CelEnv.Create(new CelEnvSettings
{
    Container = "my.pkg",          // so expressions can say MyMessage{...} unqualified
    TypeProvider = registry,       // message construction, field types, enum constants
    Adapter = registry,            // adapts IMessage values in activations
});
```

Registering a `FileDescriptor` pulls in its dependencies, nested types, enums, and
extensions recursively. Well-known types are always available.

## What you can do

```javascript
// Construct messages (field types are checked at compile time when the checker runs)
MyMessage{name: 'x', count: 3}

// Read fields — unset message fields read as their default instance
msg.sub_message.leaf_value

// Presence
has(msg.name)              // proto3 scalar: non-default; proto2/message: set

// Enums are ints (legacy mode, the CEL default)
msg.status == my.pkg.Status.ACTIVE
```

## Well-known type mappings

Well-known protobuf types *collapse into CEL-native values* — you never see the wrapper
message itself:

| Proto type | CEL view |
|---|---|
| `Int32Value` … `BytesValue` (wrappers) | the primitive, or `null` when unset |
| `Timestamp` / `Duration` | CEL timestamp / duration |
| `Struct` | `map(string, dyn)` |
| `Value` | the dynamic value (`null`, `double`, `string`, `bool`, `map`, `list`) |
| `ListValue` | `list(dyn)` |
| `Any` | unpacked automatically via the registry; unresolvable Any compares bytewise |
| `FieldMask` / `Empty` | JSON forms (`"a,b"` / `{}`) when converted through `Value` |

Null-assignment rules mirror the spec: `MyMessage{wrapper_field: null}` leaves the field
unset; `null` in a repeated/map slot of message-kind type is pruned; assigning `null` to a
`Struct`- or `ListValue`-typed field is an error (their CEL types are map/list, which
reject null).

## Message equality

`m1 == m2` is **field-wise CEL equality**: presence-aware, and NaN inside a message is
unequal to itself (IEEE semantics) — deliberately *not* `proto.Equal`, which compares
doubles bitwise. This is what the conformance suite requires.

## Strong-enum mode

By default (and per the CEL spec) proto enums are just `int`s. Celly also offers **strong
enums** — enums as distinct named types — which the conformance suite's `strong_*` sections
document:

```csharp
var registry = ProtoTypeRegistry.FromFiles(strongEnums: true, MyMessage.Descriptor.File);
var env = CelEnv.Create(new CelEnvSettings
{
    TypeProvider = registry,
    Adapter = registry,
    Libraries = [registry.CreateEnumConversionLibrary()],  // EnumName(int|string) conversions
});
```

```javascript
// strong mode:
type(Status.ACTIVE) == my.pkg.Status     // enums have their own type
Status(1)                                 // int  -> enum conversion
Status('ACTIVE')                          // name -> enum conversion
int(Status.ACTIVE)                        // enum -> int (restored in strong mode)
msg.status == Status.ACTIVE               // field values are enum-typed
```

Choose one mode per environment; they are intentionally different semantics.

## Extensions (proto2)

Extension fields are addressed by their full name — via the `ProtosLibrary` macros or
backtick selectors:

```javascript
proto.getExt(msg, my.pkg.my_extension)
has(msg.`my.pkg.my_extension`)
```
