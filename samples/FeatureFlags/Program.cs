// Feature flags with CEL — the canonical use case.
//
// A flag's targeting rule is a CEL boolean expression over a "user" context. Rules are DATA
// (strings from a config store), compiled once, then evaluated per user. This is exactly how
// LaunchDarkly-style targeting, Envoy RBAC, and IAM conditions work — CEL is the rule language.
//
//   dotnet run --project samples/FeatureFlags

using Celly;
using Celly.Checking;
using Celly.Extensions;
using Celly.Types;
using Celly.Values;

// One environment, shared and immutable. The "user" context is a map of dyn — the shape rules
// can see. StringsLibrary gives rules startsWith / matches / etc.
var env = CelEnv.Create(new CelEnvSettings
{
    Libraries = [StringsLibrary.Instance],
    Declarations = [new VariableDecl("user", CelType.Map(CelType.String, CelType.Dyn))],
});

// Flags with their targeting rules (as they'd arrive from a config service).
var flags = new (string Name, string Rule)[]
{
    ("new-dashboard",   "user.plan == 'pro' && user.country in ['US', 'CA']"),
    ("beta-search",     "user.email.endsWith('@acme.com') || 'beta' in user.groups"),
    ("dark-mode",       "true"),                                  // on for everyone
    ("holiday-banner",  "user.signupYear >= 2024 && user.plan != 'free'"),
};

// Compile every rule once at startup. A bad rule is caught here, not per request.
var compiled = new List<(string Name, CelProgram Program)>();
foreach (var (name, rule) in flags)
{
    try
    {
        compiled.Add((name, env.Compile(rule)));
    }
    catch (CelParseException ex)
    {
        Console.WriteLine($"! flag '{name}' has an invalid rule and was skipped: {ex.Message}");
    }
}

// Some users to evaluate against.
var users = new[]
{
    Ctx(email: "sam@acme.com",  plan: "pro",  country: "US", signupYear: 2025, groups: ["beta"]),
    Ctx(email: "lee@gmail.com", plan: "free", country: "GB", signupYear: 2021, groups: []),
    Ctx(email: "mo@acme.com",   plan: "team", country: "CA", signupYear: 2024, groups: ["ops"]),
};

foreach (var user in users)
{
    Console.WriteLine($"\n{user["email"]}  (plan={user["plan"]}, country={user["country"]}):");
    foreach (var (name, program) in compiled)
    {
        Console.WriteLine($"  {(IsOn(program, user) ? "✅" : "  ")} {name}");
    }
}

// Evaluate one flag for one user. Errors (e.g. a rule references a missing field) FAIL CLOSED —
// the flag is treated as off — which is the safe default for gating features.
static bool IsOn(CelProgram program, IReadOnlyDictionary<string, object?> user) =>
    program.Eval(new Dictionary<string, object?> { ["user"] = user }) is BoolValue { Value: true };

static Dictionary<string, object?> Ctx(
    string email, string plan, string country, long signupYear, string[] groups) => new()
{
    ["email"] = email,
    ["plan"] = plan,
    ["country"] = country,
    ["signupYear"] = signupYear,
    ["groups"] = groups,
};
