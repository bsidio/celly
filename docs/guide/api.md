# Environments & Programs

The public API follows cel-go's proven shape:

```
CelEnv  ──Parse──▶  AST  ──Check──▶  typed AST  ──Program──▶  CelProgram  ──Eval──▶  CelValue
```

## CelEnv

An immutable, thread-safe environment. Create once, reuse everywhere:

```csharp
var env = CelEnv.Create(new CelEnvSettings
{
    Container = "acme.policies",              // namespace for name resolution
    Declarations = [ /* variables for the checker */ ],
    FunctionDeclarations = [ /* custom function signatures */ ],
    Libraries = [ /* extension libraries */ ],
    TypeProvider = registry,                  // protobuf support (Celly.Protobuf)
    Adapter = registry,                       // native-value adaption
    EnableOptionalSyntax = true,              // a.?b, [?e], {?k: v} (default on)
    DisableMacros = false,                    // has/all/exists/... (default on)
});
```

### The three phases, separately or together

```csharp
// Separately — full control, inspect issues at each stage:
ParseResult parsed = env.Parse("x + 1");
if (parsed.Ast is null) { /* parsed.Issues has positions + messages */ }

CheckResult checked_ = env.Check(parsed.Ast);   // optional; annotates the AST with types
if (checked_.HasErrors) { /* checked_.Issues */ }

CelProgram program = env.Program(parsed.Ast);

// Or in one step (parse + plan; throws CelParseException on syntax errors):
CelProgram program2 = env.Compile("x + 1");
```

!!! tip "Check is optional but recommended"
    Evaluation works on unchecked ASTs (everything is `dyn` at runtime). Checking catches
    type errors early and validates variable references against declarations. For
    user-supplied expressions, parse → check → reject before you ever store the rule.

## Activations

An `IActivation` supplies variable values at eval time:

```csharp
public interface IActivation
{
    bool TryFind(string name, out CelValue value);
}
```

Three built-in forms:

```csharp
program.Eval();                                  // EmptyActivation — no variables
program.Eval(dictionary);                        // MapActivation — lazily adapts .NET values
program.Eval(new ValueActivation(celValues));    // pre-adapted CelValues, zero conversion
```

Implement `IActivation` yourself to resolve variables lazily from a request context,
database row, etc. — `TryFind` is only called for names the expression actually references.

## Custom functions

Register a runtime implementation plus (optionally) a checker signature:

```csharp
using Celly.Checking;
using Celly.Stdlib;
using Celly.Types;
using Celly.Values;

var env = CelEnv.Create(new CelEnvSettings
{
    ConfigureFunctions = registry =>
        registry.Register("shout", args => args is [StringValue s]
            ? StringValue.Of(s.Value.ToUpperInvariant() + "!")
            : ErrorValue.NoSuchOverload()),
    FunctionDeclarations =
    [
        new FunctionDecl("shout", [new OverloadDecl("shout_string", [CelType.String], CelType.String)]),
    ],
});

env.Compile("shout('hello')").Eval();   // "HELLO!"
```

Function implementations receive **already-evaluated** arguments (`CelValue[]`) and must
return a `CelValue` — return `ErrorValue`, never throw, for expression-level failures.
Receiver-style calls (`x.f(y)`) dispatch to the same name with the receiver as `args[0]`.

## Threading model

- `CelEnv`, `CelProgram`: immutable after construction; share freely across threads.
- `Eval` allocates its own evaluation state; concurrent `Eval` calls on one program are safe.
- Activations are read during eval; a `MapActivation` memoizes adaption per instance, so
  reuse one activation per logical evaluation context, not across unrelated evaluations.
