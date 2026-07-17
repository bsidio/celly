using Celly.Types;

namespace Celly.Checking;

/// <summary>A declared variable (or enum constant / type ident) visible to the checker.</summary>
public sealed class VariableDecl(string name, CelType type)
{
    public string Name { get; } = name;

    public CelType Type { get; } = type;
}

/// <summary>One typed signature of a function.</summary>
public sealed class OverloadDecl(string id, IReadOnlyList<CelType> argTypes, CelType resultType, bool isInstance = false)
{
    public string Id { get; } = id;

    public IReadOnlyList<CelType> ArgTypes { get; } = argTypes;

    public CelType ResultType { get; } = resultType;

    /// <summary>Receiver-style overload (first arg type is the receiver).</summary>
    public bool IsInstance { get; } = isInstance;
}

public sealed class FunctionDecl(string name, IReadOnlyList<OverloadDecl> overloads)
{
    public string Name { get; } = name;

    public IReadOnlyList<OverloadDecl> Overloads { get; } = overloads;
}

/// <summary>Lexical scopes of variable declarations plus the function table.</summary>
public sealed class TypeEnv
{
    private readonly TypeEnv? _parent;
    private readonly Dictionary<string, VariableDecl> _variables = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FunctionDecl>? _functions;

    public TypeEnv(Dictionary<string, FunctionDecl> functions) => _functions = functions;

    private TypeEnv(TypeEnv parent)
    {
        _parent = parent;
        _functions = null;
    }

    public bool IsRoot => _parent is null;

    public TypeEnv NewScope() => new(this);

    public void AddVariable(VariableDecl decl) => _variables[decl.Name] = decl;

    public VariableDecl? FindVariable(string name) =>
        _variables.TryGetValue(name, out var decl) ? decl : _parent?.FindVariable(name);

    /// <summary>Looks up a name in comprehension scopes only (excludes root declarations).</summary>
    public VariableDecl? FindScopedVariable(string name)
    {
        var env = this;
        while (!env.IsRoot)
        {
            if (env._variables.TryGetValue(name, out var decl))
            {
                return decl;
            }

            env = env._parent!;
        }

        return null;
    }

    /// <summary>Looks up a name in root declarations only (absolute references skip scopes).</summary>
    public VariableDecl? FindRootVariable(string name)
    {
        var env = this;
        while (!env.IsRoot)
        {
            env = env._parent!;
        }

        return env._variables.TryGetValue(name, out var decl) ? decl : null;
    }

    public FunctionDecl? FindFunction(string name)
    {
        var env = this;
        while (env._parent is not null)
        {
            env = env._parent;
        }

        return env._functions!.TryGetValue(name, out var fn) ? fn : null;
    }
}
