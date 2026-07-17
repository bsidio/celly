# Plain .NET Types (POCOs)

`NativeTypeProvider` gives your own classes, records, structs, and enums full CEL message
semantics — construction, typed field access, `has()` presence, and field-wise equality —
with **no protobuf involved**. It's the front door for the majority of .NET apps whose
data lives in ordinary objects.

```csharp
using Celly;
using Celly.Checking;
using Celly.Providers;
using Celly.Types;

public enum Tier { Free, Pro, Enterprise }
public sealed record Address(string City, string Country);

public sealed class User
{
    public string Name { get; set; } = "";
    public long Age { get; set; }
    public Tier Tier { get; set; }
    public int? LoginCount { get; set; }
    public Address? HomeAddress { get; set; }
    public List<string> Roles { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
}

var provider = NativeTypeProvider.FromTypes(typeof(User));   // Address & Tier auto-register

var env = CelEnv.Create(new CelEnvSettings
{
    Container = "MyApp.Models",       // optional: lets expressions use short type names
    TypeProvider = provider,
    Adapter = provider,
    Declarations = [new VariableDecl("user", CelType.Struct("MyApp.Models.User"))],
});

var program = env.Compile("""
    user.age >= 18 &&
    user.tier == Tier.Pro &&
    'admin' in user.roles &&
    user.created_at < timestamp('2030-01-01T00:00:00Z')
    """);

program.Eval(new Dictionary<string, object?> { ["user"] = myUser });
```

## Field names

Both the verbatim property name and its snake_case form work: `user.FirstName` and
`user.first_name` resolve to the same property. Acronym runs collapse sensibly
(`HTTPServer` → `http_server`).

## Type mapping

| .NET | CEL |
|---|---|
| `long`, `int`, `short`, `sbyte` | `int` (range-checked on write-back) |
| `ulong`, `uint`, `ushort`, `byte` | `uint` |
| `double`, `float`, `decimal` | `double` (decimal is lossy — by design) |
| `bool`, `string`, `byte[]` | `bool`, `string`, `bytes` |
| `DateTimeOffset`, `DateTime` | `timestamp` (DateTime treated as UTC) |
| `TimeSpan` | `duration` |
| `Guid` | `string` |
| enums | `int`, with constants resolvable as `My.Ns.Tier.Pro` |
| `T?` (`Nullable<T>`) | wrapper — reads as the value or `null`, accepts `null` on construction |
| `List<T>`, `T[]`, `IEnumerable<T>` | `list(T)` |
| `Dictionary<K, V>` | `map(K, V)` |
| registered class/record/struct | struct type (its CLR full name) |
| anything else | `dyn` |

Property types that are themselves classes/records/enums are **registered automatically**,
recursively — `FromTypes(typeof(User))` above pulled in `Address` and `Tier`.

## Construction

```javascript
MyApp.Models.Address{city: 'Porto', country: 'PT'}     // records: primary constructor
MyApp.Models.User{name: 'zoe', roles: ['x'], ?login_count: maybeCount}  // classes: props
```

Records construct through their primary constructor (parameters matched by name,
case-insensitively); mutable classes construct via the parameterless constructor +
property sets. Unknown fields and type mismatches are **checker errors** when you check,
and runtime errors when you don't.

## Presence (`has()`)

Proto3-style semantics per mapped type: `null` → false; scalars must be non-default
(`0`, `""`, `false` are "absent"); collections must be non-empty; `Nullable<T>` follows
`HasValue`. Pair with [optionals](extensions.md#optionals) for graceful defaults:
`user.?home_address.?city.orValue('unknown')`.

## Equality

Registered objects compare **field-wise with CEL semantics** (like protobuf messages):
same registered type, all mapped properties equal — with IEEE NaN behavior, not
`object.Equals`.

!!! warning "Trimming / Native AOT"
    `NativeTypeProvider` is reflection-based and marked `[RequiresUnreferencedCode]` /
    `[RequiresDynamicCode]`. The rest of the core remains AOT-compatible; you only take
    the reflection dependency if you use this provider. Protobuf and dictionary-based
    evaluation stay AOT-safe.
