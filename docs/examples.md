# Examples & Recipes

Complete, runnable scenarios. Every snippet uses only public API; expected results are
shown in comments.

## 1. Request authorization policy

The classic CEL use case: gate an action on attributes of the request.

```csharp
using Celly;
using Celly.Checking;
using Celly.Types;
using Celly.Values;

var env = CelEnv.Create(new CelEnvSettings
{
    Declarations =
    [
        new VariableDecl("request", CelType.Map(CelType.String, CelType.Dyn)),
        new VariableDecl("resource", CelType.Map(CelType.String, CelType.Dyn)),
    ],
});

// Validate the rule ONCE at admin time (parse + check), store it, reuse it.
const string rule = """
    request.user.role in ['admin', 'editor'] &&
    resource.owner == request.user.name ||
    request.user.role == 'superadmin'
    """;

var parsed = env.Parse(rule);
if (parsed.Ast is null || env.Check(parsed.Ast).HasErrors)
{
    throw new ArgumentException("invalid policy rule");
}

var program = env.Program(parsed.Ast);   // compile once…

// …evaluate per request (thread-safe, allocation-light):
bool Allowed(string name, string role, string owner)
{
    var result = program.Eval(new Dictionary<string, object?>
    {
        ["request"] = new Dictionary<string, object?>
        {
            ["user"] = new Dictionary<string, object?> { ["name"] = name, ["role"] = role },
        },
        ["resource"] = new Dictionary<string, object?> { ["owner"] = owner },
    });
    return result is BoolValue { Value: true };   // errors fail closed
}

Allowed("amy", "editor", "amy");     // true  (role ok + owns the resource)
Allowed("amy", "editor", "bob");     // false (not the owner)
Allowed("sam", "superadmin", "bob"); // true  (superadmin bypass)
```

## 2. Feature-flag targeting

```csharp
var env = CelEnv.Create(new CelEnvSettings
{
    Declarations =
    [
        new VariableDecl("user", CelType.Map(CelType.String, CelType.Dyn)),
        new VariableDecl("now", CelType.Timestamp),
    ],
});

var flag = env.Compile("""
    user.plan == 'enterprise' ||
    user.email.endsWith('@example.com') ||
    (user.beta_opt_in && now < timestamp('2026-09-01T00:00:00Z'))
    """);

var enabled = flag.Eval(new Dictionary<string, object?>
{
    ["user"] = new Dictionary<string, object?>
    {
        ["plan"] = "free", ["email"] = "dev@example.com", ["beta_opt_in"] = false,
    },
    ["now"] = Celly.Values.TimestampValue.Of(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 0),
});
// BoolValue.True — matched the email domain clause
```

## 3. Input validation (protovalidate style)

One rule per field; collect violations instead of stopping at the first.

```csharp
var env = CelEnv.Create(new CelEnvSettings
{
    Declarations = [new VariableDecl("this", CelType.Map(CelType.String, CelType.Dyn))],
    Libraries = [Celly.Extensions.StringsLibrary.Instance],
});

var rules = new (string Field, string Rule, string Message)[]
{
    ("email",    "this.email.matches('^[^@]+@[^@]+$')",      "must be a valid email"),
    ("age",      "this.age >= 13 && this.age < 130",          "must be between 13 and 129"),
    ("username", "this.username.size() >= 3 && this.username.matches('^[a-z0-9_]+$')",
                 "3+ chars, lowercase alphanumeric"),
    ("tags",     "this.tags.all(t, t.size() <= 20) && size(this.tags) <= 5",
                 "at most 5 tags of at most 20 chars"),
};

var programs = rules.Select(r => (r.Field, r.Message, Program: env.Compile(r.Rule))).ToList();

var subject = new Dictionary<string, object?>
{
    ["this"] = new Dictionary<string, object?>
    {
        ["email"] = "not-an-email",
        ["age"] = 25L,
        ["username"] = "amy_9",
        ["tags"] = new[] { "a", "way-too-long-tag-name-over-twenty-chars" },
    },
};

var violations = programs
    .Where(p => p.Program.Eval(subject) is not BoolValue { Value: true })
    .Select(p => $"{p.Field}: {p.Message}")
    .ToList();
// ["email: must be a valid email", "tags: at most 5 tags of at most 20 chars"]
```

## 4. Comprehension cookbook

```javascript
// Filtering + shaping
orders.filter(o, o.status == 'open').map(o, o.id)          // ids of open orders
prices.filter(p, p > 0).size() == prices.size()             // all positive?
items.exists(i, i.sku == wanted)                             // containment by field
users.exists_one(u, u.is_primary)                            // exactly one primary

// Maps iterate over KEYS
{'a': 1, 'b': 2}.all(k, k in allowed_keys)
labels.filter(k, labels[k] == 'prod')                        // keys whose value matches

// Two-variable forms (TwoVarComprehensionsLibrary)
scores.all(i, v, v >= scores[0])                             // list: index, value
labels.transformMap(k, v, v.lowerAscii())                    // map over values
inventory.transformMapEntry(sku, count, {count: sku})        // invert a map

// Aggregation via chaining (no fold operator needed for common cases)
size(items.filter(i, i.qty > 0))                             // count matching
items.map(i, i.price).exists(p, p > limit)                   // any over projection

// Nested data
teams.all(t, t.members.exists(m, m.role == 'lead'))
matrix.map(row, row.map(cell, cell * 2))                     // nested map
```

## 5. Timestamps: expiry, windows, business hours

```javascript
// Token expiry
now < token.issued_at + duration('15m')

// Within a date window
now >= campaign.start && now < campaign.end

// Business hours in a specific timezone (getHours is tz-aware)
now.getHours('America/New_York') >= 9 && now.getHours('America/New_York') < 17 &&
now.getDayOfWeek('America/New_York') in [1, 2, 3, 4, 5]      // Mon..Fri (0 = Sunday)

// Age in days
(now - user.created_at) > duration('720h')                    // older than 30 days
```

```csharp
// Binding 'now' from .NET:
["now"] = TimestampValue.Of(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 0)
```

## 6. Optionals: tolerant reads over ragged data

```csharp
var env = CelEnv.Create(new CelEnvSettings
{
    Declarations = [new VariableDecl("cfg", CelType.Map(CelType.String, CelType.Dyn))],
    Libraries = [Celly.Extensions.OptionalsLibrary.Instance],
});

// Missing anywhere along the chain → the default, never an error:
var timeout = env.Compile("cfg.?server.?timeout_ms.orValue(5000)");

timeout.Eval(new Dictionary<string, object?> { ["cfg"] = new Dictionary<string, object?>() });
// IntValue 5000

// First present wins:
env.Compile("cfg.?override.or(cfg.?fallback).orValue('none')");

// Optional entries: include keys conditionally
env.Compile("{'always': 1, ?'sometimes': cfg.?extra}");   // key absent when cfg.extra missing
```

## 7. String formatting & manipulation

```csharp
var env = CelEnv.Create(new CelEnvSettings
{
    Libraries = [Celly.Extensions.StringsLibrary.Instance],
});
```

```javascript
'%s used %d of %d credits (%.1f%%)'.format([user, used, quota, 100.0 * used / quota])
// "amy used 42 of 100 credits (42.0%)"

'Hello, %s!'.format([name.substring(0, 1).upperAscii() + name.substring(1)])
path.split('/').filter(seg, seg != '')                     // path segments
parts.join('-')                                            // reassemble
header.trim().lowerAscii() == 'application/json'
```

## 8. Custom functions: domain vocabulary

Give rule authors domain-specific verbs while keeping expressions simple:

```csharp
using Celly.Stdlib;

var env = CelEnv.Create(new CelEnvSettings
{
    Declarations = [new VariableDecl("card", CelType.Map(CelType.String, CelType.Dyn))],
    ConfigureFunctions = registry =>
    {
        registry.Register("luhnValid", args => args is [StringValue s]
            ? BoolValue.Of(LuhnCheck(s.Value))            // your .NET code
            : ErrorValue.NoSuchOverload());
        registry.Register("riskScore", args => args is [StringValue country]
            ? IntValue.Of(RiskTable.GetValueOrDefault(country.Value, 50))
            : ErrorValue.NoSuchOverload());
    },
    FunctionDeclarations =
    [
        new FunctionDecl("luhnValid", [new OverloadDecl("luhn_string", [CelType.String], CelType.Bool)]),
        new FunctionDecl("riskScore", [new OverloadDecl("risk_string", [CelType.String], CelType.Int)]),
    ],
});

var screen = env.Compile("luhnValid(card.number) && riskScore(card.country) < 70");
```

## 9. A miniature rule engine

Many named rules, compiled once, evaluated together with shared bindings:

```csharp
public sealed class RuleEngine
{
    private readonly List<(string Name, CelProgram Program)> _rules = [];
    private readonly CelEnv _env;

    public RuleEngine(CelEnv env) => _env = env;

    public IReadOnlyList<string> AddRule(string name, string expression)
    {
        var parsed = _env.Parse(expression);
        if (parsed.Ast is null)
        {
            return [.. parsed.Issues.Select(i => i.ToString())];   // reject at ingest
        }

        var check = _env.Check(parsed.Ast);
        if (check.HasErrors)
        {
            return [.. check.Issues.Select(i => i.ToString())];
        }

        _rules.Add((name, _env.Program(parsed.Ast)));
        return [];
    }

    /// <summary>Names of all rules that fired. Errors count as "did not fire" (fail closed).</summary>
    public IEnumerable<string> Evaluate(IReadOnlyDictionary<string, object?> facts) =>
        _rules.Where(r => r.Program.Eval(facts) is BoolValue { Value: true }).Select(r => r.Name);
}
```

## 10. Caching compiled policies as protos

Skip re-parsing on every process start: check once, store the `CheckedExpr` bytes, and
rehydrate anywhere (including from ASTs produced by cel-go!).

```csharp
using Celly.Protobuf;
using Google.Protobuf;

// Ingest: validate + serialize
var ast = env.Parse(ruleText).Ast!;
if (env.Check(ast).HasErrors) throw new ArgumentException("bad rule");
byte[] blob = AstConverter.ToCheckedExpr(ast).ToByteArray();
database.Save(ruleId, blob);

// Startup: rehydrate + plan (no parsing, no checking)
var restored = AstConverter.FromCheckedExpr(Cel.Expr.CheckedExpr.Parser.ParseFrom(blob));
var program = env.Program(restored);
```

## 11. Protobuf messages end to end

```csharp
using Celly.Protobuf;

var registry = ProtoTypeRegistry.FromFiles(Order.Descriptor.File);
var env = CelEnv.Create(new CelEnvSettings
{
    Container = "shop.v1",                    // Order's proto package
    TypeProvider = registry,
    Adapter = registry,
    Declarations = [new VariableDecl("order", CelType.Struct("shop.v1.Order"))],
});

var program = env.Compile("""
    order.status == OrderStatus.PAID &&
    order.items.all(i, i.quantity > 0) &&
    has(order.shipping_address) &&
    order.total.units < 10000
    """);

var result = program.Eval(new Dictionary<string, object?>
{
    ["order"] = myOrderMessage,               // an IMessage — adapted automatically
});
```

## 12. Handling untrusted expressions safely

The full defensive pattern for user-supplied rules:

```csharp
public static (CelProgram? Program, string? Error) TryCompile(CelEnv env, string text)
{
    if (text.Length > 10_000)
    {
        return (null, "expression too long");
    }

    var parsed = env.Parse(text);                       // bounded recursion: hostile
    if (parsed.Ast is null)                             // nesting is a parse error,
    {                                                   // never a crash
        return (null, parsed.Issues[0].ToString());
    }

    var check = env.Check(parsed.Ast);                  // reject type errors and
    if (check.HasErrors)                                // references to undeclared
    {                                                   // variables/functions
        return (null, check.Issues[0].ToString());
    }

    // Optional: enforce a variable allow-list on top of declarations
    var refs = Celly.Ast.AstTools.ReferencedVariables(parsed.Ast.Expr);
    if (refs.Except(["request", "resource"]).Any())
    {
        return (null, "expression references unknown variables");
    }

    return (env.Program(parsed.Ast), null);
}

// At eval time: errors are values; decide your failure posture explicitly.
var outcome = program.Eval(facts) switch
{
    BoolValue b => b.Value,
    ErrorValue e => false,      // fail closed (log e.Message)
    _ => false,                 // non-bool result: treat as policy bug
};
```
