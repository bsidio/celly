# Getting Started

## Install

```bash
dotnet add package Celly
```

## Evaluate an expression

```csharp
using Celly;

var program = CelEnv.Default.Compile("1 + 2 * 3 == 7");
var result = program.Eval();   // BoolValue.True
```

`Compile` parses the expression and plans it into a reusable `CelProgram`. Programs are immutable and safe to evaluate concurrently from many threads.

## Bind variables

Variables come from an **activation** — a bag of named values supplied at evaluation time. The easiest form is a dictionary; plain .NET types are adapted automatically:

```csharp
var program = CelEnv.Default.Compile("age >= 18 && name.startsWith('A')");

var result = program.Eval(new Dictionary<string, object?>
{
    ["age"] = 21L,          // long   -> int
    ["name"] = "Alice",     // string -> string
});
// BoolValue.True
```

| .NET type | CEL type |
|---|---|
| `bool` | `bool` |
| `int`, `long`, `short`, `sbyte` | `int` (64-bit signed) |
| `uint`, `ulong`, `ushort`, `byte` | `uint` (64-bit unsigned) |
| `double`, `float` | `double` |
| `string` | `string` |
| `byte[]` | `bytes` |
| `IDictionary` | `map` |
| `IEnumerable` | `list` |
| `null` | `null` |

## Use the type checker

By default `Compile` only parses. Declaring variable types lets the **checker** reject bad expressions before they ever run:

```csharp
using Celly.Checking;
using Celly.Types;

var env = CelEnv.Create(new CelEnvSettings
{
    Declarations =
    [
        new VariableDecl("age", CelType.Int),
        new VariableDecl("name", CelType.String),
    ],
});

var parsed = env.Parse("age + name");        // parses fine...
var check = env.Check(parsed.Ast!);          // ...but doesn't type-check
// check.Issues[0]: found no matching overload for '_+_' applied to '(int, string)'
```

## Inspect results

Every evaluation returns a `CelValue`. Pattern match to consume it:

```csharp
using Celly.Values;

var value = program.Eval(bindings);
var verdict = value switch
{
    BoolValue b => b.Value,
    ErrorValue e => throw new InvalidOperationException(e.Message),
    _ => throw new InvalidOperationException($"expected bool, got {value.Type}"),
};
```

!!! note "Errors are values"
    CEL evaluation never throws for expression-level failures (division by zero, missing keys,
    overflow). Those come back as `ErrorValue` — see [Errors & Absorption](guide/errors.md)
    for why that design matters.

## Enable extensions

The standard environment covers the CEL spec. Optionals, `format`, math helpers, and the rest are opt-in libraries:

```csharp
using Celly.Extensions;

var env = CelEnv.Create(new CelEnvSettings
{
    Libraries = [OptionalsLibrary.Instance, StringsLibrary.Instance, MathLibrary.Instance],
});

env.Compile("optional.of(5).orValue(0) == math.greatest(1, 5, 3)").Eval();  // true
```

See [Extension Libraries](guide/extensions.md) for the full catalog.
