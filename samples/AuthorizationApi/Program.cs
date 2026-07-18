// CEL as an authorization policy language in ASP.NET Core.
//
// Each route carries a CEL policy string. A middleware compiles policies once at startup and
// evaluates the matching one per request against a { auth, resource, request } context — the same
// pattern as Envoy RBAC, Kubernetes ValidatingAdmissionPolicy, and IAM conditions. Policies are
// DATA: swap them from config without recompiling the app.
//
//   dotnet run --project samples/AuthorizationApi
//   then, in another terminal:
//     curl -s localhost:5080/public
//     curl -s localhost:5080/docs        -H 'x-user: lee'  -H 'x-groups: staff'
//     curl -s localhost:5080/admin       -H 'x-user: sam'  -H 'x-groups: admin,staff'
//     curl -s localhost:5080/orders/sam  -H 'x-user: sam'  -H 'x-groups: staff'   # owner access
//     curl -s localhost:5080/orders/mo   -H 'x-user: sam'  -H 'x-groups: staff'   # 403

using Celly;
using Celly.Checking;
using Celly.Extensions;
using Celly.Types;
using Celly.Values;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
var app = builder.Build();

// The policy engine: one immutable CelEnv, policies compiled once.
var policies = new PolicyEngine();

// A route's policy sees three variables. `true` means "no restriction".
app.MapGet("/public", () => "anyone can read this")
    .WithPolicy(policies, "true");

// Any authenticated user in the 'staff' or 'admin' group.
app.MapGet("/docs", () => "internal docs")
    .WithPolicy(policies, "auth.groups.exists(g, g in ['staff', 'admin'])");

// Admins only.
app.MapGet("/admin", () => "admin console")
    .WithPolicy(policies, "'admin' in auth.groups");

// Owner-or-admin: the resource owner (from the route) or any admin. `resource.owner` is supplied
// by the route below; `auth.subject` comes from the request.
app.MapGet("/orders/{owner}", (string owner) => $"order details for {owner}")
    .WithPolicy(policies, "resource.owner == auth.subject || 'admin' in auth.groups");

app.Run("http://localhost:5080");


// ---------------------------------------------------------------------------
// A tiny policy engine + endpoint helper. In a real app this would be middleware
// or an IAuthorizationHandler; kept inline here so the whole flow is on one screen.
// ---------------------------------------------------------------------------

sealed class PolicyEngine
{
    private readonly CelEnv _env = CelEnv.Create(new CelEnvSettings
    {
        Libraries = [StringsLibrary.Instance],
        Declarations =
        [
            new VariableDecl("auth", CelType.Map(CelType.String, CelType.Dyn)),      // who is calling
            new VariableDecl("resource", CelType.Map(CelType.String, CelType.Dyn)),  // what they're accessing
            new VariableDecl("request", CelType.Map(CelType.String, CelType.Dyn)),   // method, path, ...
        ],
    });

    public CelProgram Compile(string policy) => _env.Compile(policy);

    // Evaluate a compiled policy. Any error (bad field, wrong type) FAILS CLOSED → deny.
    public bool Allows(CelProgram policy, HttpContext http, IReadOnlyDictionary<string, object?> resource) =>
        policy.Eval(new Dictionary<string, object?>
        {
            ["auth"] = new Dictionary<string, object?>
            {
                ["subject"] = http.Request.Headers["x-user"].FirstOrDefault() ?? "",
                ["groups"] = (http.Request.Headers["x-groups"].FirstOrDefault() ?? "")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            },
            ["resource"] = resource,
            ["request"] = new Dictionary<string, object?>
            {
                ["method"] = http.Request.Method,
                ["path"] = http.Request.Path.Value ?? "",
            },
        }) is BoolValue { Value: true };
}

static class EndpointPolicyExtensions
{
    // Attach a CEL policy to an endpoint: compile it now, enforce it on each request.
    public static RouteHandlerBuilder WithPolicy(
        this RouteHandlerBuilder builder, PolicyEngine engine, string policy)
    {
        var compiled = engine.Compile(policy);   // fail fast at startup on a bad policy
        return builder.AddEndpointFilter(async (ctx, next) =>
        {
            // `resource.owner` = the {owner} route value when present, so owner-based rules work.
            var resource = new Dictionary<string, object?>
            {
                ["owner"] = ctx.HttpContext.Request.RouteValues.TryGetValue("owner", out var o) ? o : null,
            };

            return engine.Allows(compiled, ctx.HttpContext, resource)
                ? await next(ctx)
                : Results.StatusCode(StatusCodes.Status403Forbidden);
        });
    }
}
