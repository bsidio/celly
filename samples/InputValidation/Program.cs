// Data validation with CEL — rules as data, all violations collected.
//
// Each rule pairs a CEL boolean (the invariant that must hold) with the message to report when
// it doesn't. Rules live outside the code, so product/ops can add constraints without a redeploy.
// This is the shape of protovalidate, JSON-Schema-with-CEL, and admission policies.
//
//   dotnet run --project samples/InputValidation

using Celly;
using Celly.Checking;
using Celly.Extensions;
using Celly.Types;
using Celly.Values;

// The object under validation is exposed as `input`. StringsLibrary provides matches() for the
// email pattern; the whole rule set is compiled once.
var env = CelEnv.Create(new CelEnvSettings
{
    Libraries = [StringsLibrary.Instance],
    Declarations = [new VariableDecl("input", CelType.Map(CelType.String, CelType.Dyn))],
});

// A validator is a (rule, message) pair. `input` is the candidate object.
var rules = new (string Expr, string Message)[]
{
    ("size(input.username) >= 3",                         "username must be at least 3 characters"),
    ("input.username.matches('^[a-zA-Z0-9_]+$')",         "username may only contain letters, digits, underscore"),
    ("input.email.matches('^[^@]+@[^@]+[.][^@]+$')",      "email is not a valid address"),
    ("input.age >= 18",                                   "must be 18 or older"),
    ("input.age < 150",                                   "age is out of range"),
    ("size(input.tags) <= 5",                             "at most 5 tags allowed"),
    ("input.tags.all(t, size(t) <= 20)",                  "each tag must be 20 characters or fewer"),
};

var validators = rules.Select(r => (r.Message, Program: env.Compile(r.Expr))).ToList();

// Two candidate objects: one valid, one that breaks several rules.
var good = Obj(username: "sam_92", email: "sam@acme.com", age: 29, tags: ["ops", "eng"]);
var bad = Obj(username: "x!", email: "not-an-email", age: 15, tags: ["a", "b", "c", "d", "e", "f"]);

foreach (var (label, obj) in new[] { ("VALID candidate", good), ("INVALID candidate", bad) })
{
    Console.WriteLine($"\n== {label} ==");
    var violations = Validate(validators, obj);
    if (violations.Count == 0)
    {
        Console.WriteLine("  ✅ passes all rules");
    }
    else
    {
        foreach (var v in violations)
        {
            Console.WriteLine($"  ❌ {v}");
        }
    }
}

// Run every rule and collect ALL failures (not just the first) — the useful UX for form validation.
// A rule that errors (missing field, wrong type) is itself reported as a violation: fail closed.
static List<string> Validate(
    List<(string Message, CelProgram Program)> validators, IReadOnlyDictionary<string, object?> input)
{
    var bindings = new Dictionary<string, object?> { ["input"] = input };
    var violations = new List<string>();
    foreach (var (message, program) in validators)
    {
        switch (program.Eval(bindings))
        {
            case BoolValue { Value: true }:
                break;                                  // invariant holds
            case ErrorValue err:
                violations.Add($"{message} (rule error: {err.Message})");
                break;
            default:
                violations.Add(message);                // invariant violated
                break;
        }
    }

    return violations;
}

static Dictionary<string, object?> Obj(string username, string email, long age, string[] tags) => new()
{
    ["username"] = username,
    ["email"] = email,
    ["age"] = age,
    ["tags"] = tags,
};
