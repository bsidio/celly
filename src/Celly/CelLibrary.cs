using Celly.Checking;
using Celly.Parsing;
using Celly.Stdlib;

namespace Celly;

/// <summary>
/// An opt-in CEL extension library: parse-time macros, runtime functions, and checker
/// declarations registered together (strings, math, optionals, …).
/// </summary>
public sealed class CelLibrary
{
    public required string Name { get; init; }

    public IReadOnlyList<Macro> Macros { get; init; } = [];

    public Action<FunctionRegistry>? Functions { get; init; }

    public IReadOnlyList<FunctionDecl> FunctionDecls { get; init; } = [];

    public IReadOnlyList<VariableDecl> VariableDecls { get; init; } = [];
}
