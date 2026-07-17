# Protobuf Support

CEL's type system is defined in terms of protobuf. The `Celly.Protobuf` package plugs
protobuf messages into the core through the provider seam (`ITypeProvider`/`ITypeAdapter`).

!!! warning "Protobuf support is a separate step from extension libraries"
    Enabling **extension libraries** (`Libraries = [StringsLibrary.Instance, …]`) and
    enabling **protobuf type support** (`TypeProvider`/`Adapter`) are **two independent
    settings**. Adding the extension libraries does *not* turn on protobuf, and vice-versa.
    If you construct messages (`MyMessage{...}`), read message fields, or use wrappers /
    enums / `Any`, you **must** set `TypeProvider` and `Adapter` — otherwise every one of
    those evaluations fails with "unknown type". (Enabling extensions but not protobuf is
    the single most common misconfiguration; it makes all proto tests fail while everything
    else works.)

## Setup

Two lines turn it on — a registry built from your message descriptors, wired into both
`TypeProvider` (construction, field types, enums) and `Adapter` (adapting `IMessage` values
you pass in):

```csharp
using Celly;
using Celly.Protobuf;

// 1. Build a registry from your proto message types. One descriptor pulls in its whole
//    dependency graph — nested types, enums, extensions — recursively. WKTs are always on.
var registry = ProtoTypeRegistry.FromFiles(
    MyMessage.Descriptor.File,
    AnotherMessage.Descriptor.File);

// 2. Wire it into BOTH TypeProvider and Adapter.
var env = CelEnv.Create(new CelEnvSettings
{
    Container = "my.pkg",          // so expressions can say MyMessage{...} unqualified
    TypeProvider = registry,       // message construction, field types, enum constants
    Adapter = registry,            // adapts IMessage values in activations

    // Extensions are separate — add them here if you also want strings/math/optionals/etc.:
    // Libraries = [StringsLibrary.Instance, MathLibrary.Instance, OptionalsLibrary.Instance],
});
```

That's it — `MyMessage{...}` construction, `msg.field` access, `has(msg.field)`, wrappers,
`Any`, and enum constants now all work. To enable *everything* (all extension libraries
**and** protobuf) in one env, see the
[full configuration](../internals/conformance.md#the-full-configuration-a-runner-must-use).

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
