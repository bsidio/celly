using Celly.Ast;
using Celly.Common;
using Celly.Types;

namespace Celly.Checking;

/// <summary>The result of a check: type/reference maps on success, issues otherwise.</summary>
public sealed class CheckResult
{
    internal CheckResult(IReadOnlyDictionary<long, CelType>? typeMap, IReadOnlyList<CelIssue> issues)
    {
        TypeMap = typeMap;
        Issues = issues;
    }

    public IReadOnlyDictionary<long, CelType>? TypeMap { get; }

    public IReadOnlyList<CelIssue> Issues { get; }

    public bool HasErrors => TypeMap is null;

    /// <summary>The deduced type of the whole expression (requires success).</summary>
    public CelType TypeOf(Expr expr) => TypeMap![expr.Id];
}

/// <summary>
/// The CEL type checker: gradual typing with dyn, type-parameter unification for parameterized
/// overloads, and container-based (C.name) resolution of identifiers and functions.
/// </summary>
public sealed class Checker
{
    private readonly TypeEnv _env;
    private readonly string _container;
    private readonly SourceInfo _sourceInfo;
    private readonly Providers.ITypeProvider _provider;
    private readonly ErrorReporter _reporter = new();
    private readonly TypeSubstitution _substitution = new();
    private readonly Dictionary<long, CelType> _typeMap = [];
    private readonly Dictionary<long, string> _identReferences = [];
    private int _freshParamCounter;

    public Checker(TypeEnv env, string container, SourceInfo sourceInfo, Providers.ITypeProvider? provider = null)
    {
        _env = env;
        _container = container;
        _sourceInfo = sourceInfo;
        _provider = provider ?? Providers.EmptyTypeProvider.Instance;
    }

    public CheckResult Check(Expr expr)
    {
        var scope = _env.NewScope();
        CheckExpr(expr, scope);
        if (_reporter.HasErrors)
        {
            return new CheckResult(null, _reporter.Issues);
        }

        // Finalize: resolve substitutions; unbound parameters become dyn.
        var final = _typeMap.ToDictionary(kv => kv.Key, kv => _substitution.Finalize(kv.Value));
        return new CheckResult(final, _reporter.Issues);
    }

    private CelType CheckExpr(Expr expr, TypeEnv scope)
    {
        var type = expr switch
        {
            ConstExpr c => c.Value.Kind switch
            {
                ConstantKind.Null => CelType.Null,
                ConstantKind.Bool => CelType.Bool,
                ConstantKind.Int => CelType.Int,
                ConstantKind.Uint => CelType.Uint,
                ConstantKind.Double => CelType.Double,
                ConstantKind.String => CelType.String,
                ConstantKind.Bytes => CelType.Bytes,
                _ => CelType.Error,
            },
            IdentExpr ident => CheckIdent(ident, scope),
            SelectExpr select => CheckSelect(select, scope),
            CallExpr call => CheckCall(call, scope),
            ListExpr list => CheckList(list, scope),
            MapExpr map => CheckMap(map, scope),
            StructExpr st => CheckStruct(st, scope),
            ComprehensionExpr comp => CheckComprehension(comp, scope),
            _ => CelType.Error,
        };

        _typeMap[expr.Id] = type;
        return type;
    }

    private CelType FreshTypeParam() => CelType.TypeParam($"%p{++_freshParamCounter}");

    private IReadOnlyList<string> CandidateNames(string name)
    {
        if (name.StartsWith('.'))
        {
            return [name[1..]];
        }

        if (_container.Length == 0)
        {
            return [name];
        }

        var candidates = new List<string>();
        var prefix = _container;
        while (true)
        {
            candidates.Add(prefix + "." + name);
            var lastDot = prefix.LastIndexOf('.');
            if (lastDot < 0)
            {
                break;
            }

            prefix = prefix[..lastDot];
        }

        candidates.Add(name);
        return candidates;
    }

    private CelType CheckIdent(IdentExpr ident, TypeEnv scope)
    {
        var absolute = ident.Name.StartsWith('.');

        // Comprehension variables shadow all namespaced resolution (non-absolute references).
        if (!absolute && scope.FindScopedVariable(ident.Name) is { } scoped)
        {
            _identReferences[ident.Id] = ident.Name;
            return scoped.Type;
        }

        foreach (var name in CandidateNames(ident.Name))
        {
            var decl = absolute ? scope.FindRootVariable(name) : scope.FindVariable(name);
            if (decl is not null)
            {
                _identReferences[ident.Id] = name;
                return decl.Type;
            }

            if (ProviderIdentType(name) is { } providerType)
            {
                _identReferences[ident.Id] = name;
                return providerType;
            }
        }

        Report(ident, $"undeclared reference to '{ident.Name}'"
            + (_container.Length > 0 ? $" (in container '{_container}')" : string.Empty));
        return CelType.Error;
    }

    /// <summary>Enum constants type as int; message type names type as type(struct).</summary>
    private CelType? ProviderIdentType(string name)
    {
        var ident = _provider.FindIdent(name);
        return ident is null ? null : ident.Type.Kind == CelTypeKind.Type && ident is Values.TypeValue tv
            ? new CelType(CelTypeKind.Type, "type", [tv.Value])
            : ident.Type;
    }

    private CelType CheckSelect(SelectExpr select, TypeEnv scope)
    {
        // A pure ident/select chain may itself be a declared (qualified) variable — longest wins,
        // unless the chain is rooted at a comprehension variable (which shadows).
        if (QualifiedName(select) is { } qualified)
        {
            var root = qualified.StartsWith('.') ? null : RootSegment(qualified);
            if (root is null || scope.FindScopedVariable(root) is null)
            {
                var absolute = qualified.StartsWith('.');
                foreach (var name in CandidateNames(qualified))
                {
                    var declType = absolute
                        ? scope.FindRootVariable(name)?.Type
                        : scope.FindVariable(name)?.Type;
                    declType ??= ProviderIdentType(name);
                    if (declType is not null)
                    {
                        _identReferences[select.Id] = name;
                        // Operand ids must still carry types for completeness.
                        StampChain(select.Operand, CelType.Dyn);
                        return select.TestOnly ? CelType.Bool : declType;
                    }
                }
            }
        }

        var operandType = _substitution.Resolve(CheckExpr(select.Operand, scope));
        CelType resultType;
        switch (operandType.Kind)
        {
            case CelTypeKind.Map:
                resultType = operandType.Parameters[1];
                break;
            case CelTypeKind.Dyn or CelTypeKind.Error or CelTypeKind.TypeParam:
                resultType = CelType.Dyn;
                break;
            case CelTypeKind.Struct:
                var fieldType = _provider.FindStructFieldType(operandType.Name, select.Field);
                if (fieldType is null)
                {
                    resultType = _provider.FindStructType(operandType.Name) is null
                        ? CelType.Dyn // unknown message type (no provider registered): stay dynamic
                        : ReportType(select, $"undefined field '{select.Field}'");
                }
                else
                {
                    resultType = fieldType;
                }

                break;
            case CelTypeKind.Opaque when TypeSubstitution.IsOptional(operandType):
                // Optional chaining: selecting through an optional yields an optional.
                resultType = CelType.Optional(CelType.Dyn);
                break;
            default:
                resultType = ReportType(select, $"type '{operandType}' does not support field selection");
                break;
        }

        return select.TestOnly ? CelType.Bool : resultType;
    }

    private void StampChain(Expr expr, CelType type)
    {
        _typeMap[expr.Id] = type;
        if (expr is SelectExpr s)
        {
            StampChain(s.Operand, type);
        }
    }

    private static string RootSegment(string qualifiedName)
    {
        var dot = qualifiedName.IndexOf('.');
        return dot < 0 ? qualifiedName : qualifiedName[..dot];
    }

    private static string? QualifiedName(Expr expr) => expr switch
    {
        IdentExpr ident => ident.Name,
        SelectExpr { TestOnly: false } select when QualifiedName(select.Operand) is { } prefix =>
            prefix + "." + select.Field,
        _ => null,
    };

    private CelType CheckCall(CallExpr call, TypeEnv scope)
    {
        // Resolve the function through the container; receiver-style calls may also be
        // namespace-qualified global functions (a.b.f(x) where a.b.f is declared).
        FunctionDecl? decl = null;
        var args = new List<Expr>(call.Args);
        var isInstance = false;

        if (call.Target is null)
        {
            foreach (var name in CandidateNames(call.Function))
            {
                if (scope.FindFunction(name) is { } found)
                {
                    decl = found;
                    break;
                }
            }
        }
        else
        {
            if (QualifiedName(call.Target) is { } targetName
                && scope.FindScopedVariable(RootSegment(targetName)) is null)
            {
                foreach (var name in CandidateNames(targetName + "." + call.Function))
                {
                    if (scope.FindFunction(name) is { } qualified)
                    {
                        decl = qualified;
                        StampChain(call.Target, CelType.Dyn);
                        break;
                    }
                }
            }

            if (decl is null)
            {
                decl = scope.FindFunction(call.Function);
                isInstance = true;
                args.Insert(0, call.Target);
            }
        }

        if (decl is null)
        {
            Report(call, $"undeclared reference to '{call.Function}'");
            foreach (var arg in args)
            {
                CheckExpr(arg, scope);
            }

            return CelType.Error;
        }

        var argTypes = args.Select(a => CheckExpr(a, scope)).ToArray();
        if (argTypes.Any(t => _substitution.Resolve(t).Kind == CelTypeKind.Error))
        {
            return CelType.Error; // avoid cascading diagnostics
        }

        var receiverStyle = call.Target is not null && isInstance;
        CelType? resultType = null;
        foreach (var overload in decl.Overloads)
        {
            if (overload.ArgTypes.Count != argTypes.Length || overload.IsInstance != receiverStyle)
            {
                continue;
            }

            // Fresh-rename the overload's type parameters for this call site.
            var rename = new Dictionary<string, CelType>(StringComparer.Ordinal);
            var parameters = overload.ArgTypes.Select(t => FreshRename(t, rename)).ToArray();
            var overloadResult = FreshRename(overload.ResultType, rename);

            // A failed attempt must not leak its speculative type-param bindings.
            var snapshot = _substitution.Snapshot();
            var matched = true;
            for (var i = 0; i < parameters.Length; i++)
            {
                if (!_substitution.IsAssignable(parameters[i], argTypes[i]))
                {
                    matched = false;
                    break;
                }
            }

            if (!matched)
            {
                _substitution.Restore(snapshot);
                continue;
            }

            resultType = resultType is null ? overloadResult : _substitution.Join(resultType, overloadResult);
        }

        if (resultType is null)
        {
            var signature = string.Join(", ", argTypes.Select(t => _substitution.Finalize(t).ToString()));
            Report(call, $"found no matching overload for '{call.Function}' applied to '({signature})'");
            return CelType.Error;
        }

        return resultType;
    }

    private CelType FreshRename(CelType type, Dictionary<string, CelType> rename)
    {
        if (type.Kind == CelTypeKind.TypeParam)
        {
            if (!rename.TryGetValue(type.Name, out var fresh))
            {
                fresh = FreshTypeParam();
                rename[type.Name] = fresh;
            }

            return fresh;
        }

        if (type.Parameters.Count == 0)
        {
            return type;
        }

        return new CelType(type.Kind, type.Name, type.Parameters.Select(p => FreshRename(p, rename)).ToArray());
    }

    private CelType CheckList(ListExpr list, TypeEnv scope)
    {
        CelType? element = null;
        for (var i = 0; i < list.Elements.Count; i++)
        {
            var itemType = CheckExpr(list.Elements[i], scope);
            if (list.OptionalIndices.Contains(i))
            {
                itemType = UnwrapOptional(itemType); // [?e] contributes the optional's inner type
            }

            element = element is null ? itemType : _substitution.Join(element, itemType);
        }

        return CelType.List(element ?? FreshTypeParam());
    }

    private CelType CheckMap(MapExpr map, TypeEnv scope)
    {
        CelType? key = null;
        CelType? value = null;
        foreach (var entry in map.Entries)
        {
            var keyType = CheckExpr(entry.Key, scope);
            var valueType = CheckExpr(entry.Value, scope);
            if (entry.Optional)
            {
                valueType = UnwrapOptional(valueType);
            }

            key = key is null ? keyType : _substitution.Join(key, keyType);
            value = value is null ? valueType : _substitution.Join(value, valueType);
        }

        return CelType.Map(key ?? FreshTypeParam(), value ?? FreshTypeParam());
    }

    private CelType UnwrapOptional(CelType type)
    {
        var resolved = _substitution.Resolve(type);
        return TypeSubstitution.IsOptional(resolved) && resolved.Parameters.Count == 1
            ? resolved.Parameters[0]
            : resolved;
    }

    private CelType CheckStruct(StructExpr st, TypeEnv scope)
    {
        string? resolved = null;
        CelType? structType = null;
        foreach (var name in CandidateNames(st.MessageName))
        {
            if (_provider.FindStructType(name) is { } found)
            {
                resolved = name;
                structType = found;
                break;
            }
        }

        if (resolved is null || structType is null)
        {
            Report(st, $"undeclared reference to '{st.MessageName}'");
            foreach (var field in st.Fields)
            {
                CheckExpr(field.Value, scope);
            }

            return CelType.Error;
        }

        _identReferences[st.Id] = resolved;
        foreach (var field in st.Fields)
        {
            var valueType = CheckExpr(field.Value, scope);
            var fieldType = _provider.FindStructFieldType(resolved, field.Name);
            if (fieldType is null)
            {
                Report(st, $"undefined field '{field.Name}'");
                continue;
            }

            // Optional-entry values ({?field: v}) carry optional-wrapped types.
            var expected = field.Optional ? CelType.Optional(fieldType) : fieldType;
            if (!_substitution.IsAssignable(expected, valueType))
            {
                Report(st, $"expected type of field '{field.Name}' is '{expected}' but provided type is '{_substitution.Finalize(valueType)}'");
            }
        }

        return structType;
    }

    private CelType CheckComprehension(ComprehensionExpr comp, TypeEnv scope)
    {
        var rangeType = _substitution.Resolve(CheckExpr(comp.IterRange, scope));
        CelType iterType;
        CelType iter2Type = CelType.Dyn;
        switch (rangeType.Kind)
        {
            case CelTypeKind.List:
                iterType = comp.IterVar2 is null ? rangeType.Parameters[0] : CelType.Int;
                iter2Type = rangeType.Parameters[0];
                break;
            case CelTypeKind.Map:
                iterType = rangeType.Parameters[0];
                iter2Type = rangeType.Parameters[1];
                break;
            case CelTypeKind.Dyn or CelTypeKind.Error or CelTypeKind.TypeParam:
                iterType = CelType.Dyn;
                break;
            default:
                Report(comp, $"expression of type '{rangeType}' cannot be range of a comprehension");
                iterType = CelType.Error;
                break;
        }

        var accuType = CheckExpr(comp.AccuInit, scope);
        var accuScope = scope.NewScope();
        accuScope.AddVariable(new VariableDecl(comp.AccuVar, accuType));

        var iterScope = accuScope.NewScope();
        iterScope.AddVariable(new VariableDecl(comp.IterVar, iterType));
        if (comp.IterVar2 is not null)
        {
            iterScope.AddVariable(new VariableDecl(comp.IterVar2, iter2Type));
        }

        var conditionType = _substitution.Resolve(CheckExpr(comp.LoopCondition, iterScope));
        if (conditionType.Kind is not (CelTypeKind.Bool or CelTypeKind.Dyn or CelTypeKind.Error or CelTypeKind.TypeParam))
        {
            Report(comp, $"found no matching overload for loop condition of type '{conditionType}'");
        }

        var stepType = CheckExpr(comp.LoopStep, iterScope);
        if (!_substitution.IsAssignable(accuType, stepType))
        {
            _substitution.Join(accuType, stepType);
        }

        return CheckExpr(comp.Result, accuScope);
    }

    private void Report(Expr expr, string message) =>
        _reporter.ReportError(_sourceInfo.LocationOf(expr.Id), message);

    private CelType ReportType(Expr expr, string message)
    {
        Report(expr, message);
        return CelType.Error;
    }
}
