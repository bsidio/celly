# Celly samples

Runnable examples of using Celly (native CEL for .NET) in the patterns people actually reach
for it: **policies and rules as data**, compiled once and evaluated many times. Each sample is a
standalone project referencing the core `Celly` package via a project reference.

| Sample | What it shows | Run |
|---|---|---|
| [`FeatureFlags`](FeatureFlags) | Feature-flag targeting rules over a user context — the canonical CEL use case. | `dotnet run --project samples/FeatureFlags` |
| [`InputValidation`](InputValidation) | Data validation as `(rule, message)` pairs; collects **all** violations, fails closed on rule errors. | `dotnet run --project samples/InputValidation` |
| [`AuthorizationApi`](AuthorizationApi) | ASP.NET Core minimal API where each route carries a CEL authorization policy (`auth`/`resource`/`request` context), enforced by an endpoint filter. | `dotnet run --project samples/AuthorizationApi` |

## The pattern they share

1. **Build one immutable `CelEnv`** with the variable shape rules can see and the extension
   libraries they may use.
2. **Compile each rule once** (at startup / on config load) — a bad rule is caught there, not
   per request.
3. **Evaluate the compiled `CelProgram` per input.** Programs are thread-safe, so one compiled
   rule serves all requests concurrently.
4. **Fail closed.** A rule that errors (missing field, wrong type) is treated as "deny" /
   "invalid" — the safe default. See the [Errors & Absorption guide](../docs/guide/errors.md).

For untrusted rules, add an [evaluation budget and static cost
estimate](../docs/guide/errors.md#bounding-untrusted-evaluation) so a hostile expression is
rejected or aborted instead of burning CPU.

> The samples are intentionally **not** part of `Celly.sln` — they're standalone so the core
> build and CI stay lean. Open one directly or `dotnet run --project samples/<name>`.
