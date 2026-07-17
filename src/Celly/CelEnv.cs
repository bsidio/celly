using Celly.Ast;
using Celly.Interpreter;
using Celly.Parsing;
using Celly.Providers;
using Celly.Stdlib;
using Celly.Values;

namespace Celly;

/// <summary>Configuration for a <see cref="CelEnv"/>.</summary>
public sealed class CelEnvSettings
{
    /// <summary>The namespace container for name resolution (spec C.name rules), e.g. "a.b".</summary>
    public string Container { get; init; } = string.Empty;

    /// <summary>Disables parse-time macro expansion (has/all/exists/…).</summary>
    public bool DisableMacros { get; init; }

    /// <summary>Enables optional syntax (a.?b, [?e], {?k: v}); on by default.</summary>
    public bool EnableOptionalSyntax { get; init; } = true;

    /// <summary>Adapter for native .NET values in activations.</summary>
    public ITypeAdapter Adapter { get; init; } = NativeTypeAdapter.Instance;

    /// <summary>Additional/replacement runtime functions, applied over the standard registry.</summary>
    public Action<FunctionRegistry>? ConfigureFunctions { get; init; }

    /// <summary>Variable declarations visible to the type checker.</summary>
    public IReadOnlyList<Checking.VariableDecl> Declarations { get; init; } = [];

    /// <summary>Additional function declarations visible to the type checker.</summary>
    public IReadOnlyList<Checking.FunctionDecl> FunctionDeclarations { get; init; } = [];
}

/// <summary>
/// An immutable, thread-safe CEL environment: parse (and from M4, type-check) expressions, then
/// plan them into reusable <see cref="CelProgram"/>s.
/// </summary>
public sealed class CelEnv
{
    private readonly ParserOptions _parserOptions;
    private readonly FunctionRegistry _functions;

    private CelEnv(CelEnvSettings settings)
    {
        Settings = settings;
        _parserOptions = new ParserOptions
        {
            EnableOptionalSyntax = settings.EnableOptionalSyntax,
            Macros = settings.DisableMacros ? [] : StandardMacros.All,
        };
        _functions = StandardFunctions.CreateRegistry();
        settings.ConfigureFunctions?.Invoke(_functions);
    }

    public CelEnvSettings Settings { get; }

    public static readonly CelEnv Default = Create(new CelEnvSettings());

    public static CelEnv Create(CelEnvSettings? settings = null) => new(settings ?? new CelEnvSettings());

    public ParseResult Parse(string expression) => CelParser.Parse(expression, _parserOptions);

    /// <summary>Type-checks a parsed AST; on success annotates it with the deduced type map.</summary>
    public Checking.CheckResult Check(CelAbstractSyntax ast)
    {
        var functions = Checking.StandardDecls.CreateFunctions();
        foreach (var fn in Settings.FunctionDeclarations)
        {
            functions[fn.Name] = functions.TryGetValue(fn.Name, out var existing)
                ? new Checking.FunctionDecl(fn.Name, [.. existing.Overloads, .. fn.Overloads])
                : fn;
        }

        var env = new Checking.TypeEnv(functions);
        foreach (var ident in Checking.StandardDecls.CreateIdents())
        {
            env.AddVariable(ident);
        }

        foreach (var decl in Settings.Declarations)
        {
            env.AddVariable(decl);
        }

        var checker = new Checking.Checker(env, Settings.Container, ast.SourceInfo);
        var result = checker.Check(ast.Expr);
        if (!result.HasErrors)
        {
            ast.TypeMap = result.TypeMap;
        }

        return result;
    }

    public CelProgram Program(CelAbstractSyntax ast)
    {
        var planner = new Planner(_functions, Settings.Container);
        return new CelProgram(planner.Plan(ast.Expr), Settings.Adapter);
    }

    /// <summary>Parses and plans in one step; throws <see cref="CelParseException"/> on syntax errors.</summary>
    public CelProgram Compile(string expression)
    {
        var result = Parse(expression);
        if (result.Ast is null)
        {
            throw new CelParseException(result);
        }

        return Program(result.Ast);
    }
}

public sealed class CelParseException(ParseResult result)
    : Exception(string.Join("\n", result.Issues.Select(i => i.ToString())))
{
    public ParseResult Result { get; } = result;
}

/// <summary>A planned expression, safe for concurrent evaluation against different activations.</summary>
public sealed class CelProgram
{
    private readonly IInterpretable _interpretable;
    private readonly ITypeAdapter _adapter;

    internal CelProgram(IInterpretable interpretable, ITypeAdapter adapter)
    {
        _interpretable = interpretable;
        _adapter = adapter;
    }

    public CelValue Eval(IActivation activation) => _interpretable.Eval(activation);

    public CelValue Eval(IReadOnlyDictionary<string, object?> bindings) =>
        Eval(new MapActivation(bindings, _adapter));

    public CelValue Eval() => Eval(EmptyActivation.Instance);
}
