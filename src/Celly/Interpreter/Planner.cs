using Celly.Ast;
using Celly.Common;
using Celly.Stdlib;
using Celly.Types;
using Celly.Values;

namespace Celly.Interpreter;

/// <summary>
/// Compiles an AST into an <see cref="IInterpretable"/> tree: constants materialize once,
/// logical operators become dedicated absorption nodes, and identifier/select chains carry their
/// container-qualified candidate names for parse-only (maybe-attribute) resolution.
/// </summary>
public sealed class Planner(FunctionRegistry functions, string container, Providers.ITypeProvider provider)
{
    internal static readonly Dictionary<string, CelValue> TypeIdents = new()
    {
        ["bool"] = new TypeValue(CelType.Bool),
        ["int"] = new TypeValue(CelType.Int),
        ["uint"] = new TypeValue(CelType.Uint),
        ["double"] = new TypeValue(CelType.Double),
        ["string"] = new TypeValue(CelType.String),
        ["bytes"] = new TypeValue(CelType.Bytes),
        ["list"] = new TypeValue(CelType.ListDyn),
        ["map"] = new TypeValue(CelType.MapDyn),
        ["null_type"] = new TypeValue(CelType.Null),
        ["type"] = new TypeValue(CelType.TypeType),
        ["google.protobuf.Timestamp"] = new TypeValue(CelType.Timestamp),
        ["google.protobuf.Duration"] = new TypeValue(CelType.Duration),
    };

    /// <summary>In-scope comprehension variable names (refcounted for same-name nesting).</summary>
    private readonly Dictionary<string, int> _scopeVars = new(StringComparer.Ordinal);

    public IInterpretable Plan(Expr expr)
    {
        switch (expr)
        {
            case ConstExpr c:
                return new ConstEval(Constants.ToValue(c.Value));

            case IdentExpr ident:
            {
                // Comprehension variables shadow all namespaced resolution.
                if (!ident.Name.StartsWith('.') && _scopeVars.ContainsKey(ident.Name))
                {
                    return new ScopeIdentEval(ident.Name);
                }

                var absolute = ident.Name.StartsWith('.');
                var candidates = ResolveCandidateNames(ident.Name);
                return new IdentEval(candidates, StaticFallback(candidates), absolute);
            }

            case SelectExpr select:
            {
                var operandEval = Plan(select.Operand);
                if (select.TestOnly)
                {
                    return new TestOnlySelectEval(operandEval, select.Field);
                }

                var qualified = QualifiedName(select);
                // A chain rooted at a comprehension variable is plain field selection.
                if (qualified is not null && _scopeVars.ContainsKey(RootSegment(qualified)))
                {
                    qualified = null;
                }

                var absolute = qualified is not null && qualified.StartsWith('.');
                IReadOnlyList<string> candidates = qualified is null ? [] : ResolveCandidateNames(qualified);
                return new SelectEval(operandEval, select.Field, candidates, StaticFallback(candidates), absolute);
            }

            case CallExpr call:
                return PlanCall(call);

            case ListExpr list:
                return new ListEval([.. list.Elements.Select(Plan)], list.OptionalIndices);

            case MapExpr map:
                return new MapEval([.. map.Entries.Select(e => new MapEntryEval(Plan(e.Key), Plan(e.Value), e.Optional))]);

            case StructExpr st:
            {
                // Resolve the message type name through the container at plan time.
                foreach (var name in ResolveCandidateNames(st.MessageName))
                {
                    if (provider.FindStructType(name) is not null)
                    {
                        var fields = st.Fields
                            .Select(f => new StructFieldEval(f.Name, Plan(f.Value), f.Optional))
                            .ToArray();
                        return new StructEval(provider, name, fields);
                    }
                }

                return new UnknownStructEval(st.MessageName);
            }

            case ComprehensionExpr comp:
            {
                var range = Plan(comp.IterRange);
                var accuInit = Plan(comp.AccuInit);
                PushScopeVar(comp.AccuVar);
                PushScopeVar(comp.IterVar);
                if (comp.IterVar2 is not null)
                {
                    PushScopeVar(comp.IterVar2);
                }

                var loopCondition = Plan(comp.LoopCondition);
                var loopStep = Plan(comp.LoopStep);
                PopScopeVar(comp.IterVar);
                if (comp.IterVar2 is not null)
                {
                    PopScopeVar(comp.IterVar2);
                }

                var result = Plan(comp.Result);
                PopScopeVar(comp.AccuVar);
                return new ComprehensionEval(
                    comp.IterVar, comp.IterVar2, range, comp.AccuVar, accuInit, loopCondition, loopStep, result);
            }

            default:
                return new ConstEval(new ErrorValue("unspecified expression"));
        }
    }

    private void PushScopeVar(string name) =>
        _scopeVars[name] = _scopeVars.GetValueOrDefault(name) + 1;

    private void PopScopeVar(string name)
    {
        if (_scopeVars.TryGetValue(name, out var count))
        {
            if (count <= 1)
            {
                _scopeVars.Remove(name);
            }
            else
            {
                _scopeVars[name] = count - 1;
            }
        }
    }

    private static string RootSegment(string qualifiedName)
    {
        var dot = qualifiedName.IndexOf('.');
        return dot < 0 ? qualifiedName : qualifiedName[..dot];
    }

    /// <summary>Plan-time constant resolution: standard type idents, then provider idents (enums, message types).</summary>
    private CelValue? StaticFallback(IReadOnlyList<string> candidates)
    {
        foreach (var name in candidates)
        {
            if (TypeIdents.TryGetValue(name, out var typeIdent))
            {
                return typeIdent;
            }

            if (provider.FindIdent(name) is { } providerIdent)
            {
                return providerIdent;
            }
        }

        return null;
    }

    private IInterpretable PlanCall(CallExpr call)
    {
        switch (call.Function)
        {
            case Operators.LogicalAnd:
                return new AndEval(Plan(call.Args[0]), Plan(call.Args[1]));
            case Operators.LogicalOr:
                return new OrEval(Plan(call.Args[0]), Plan(call.Args[1]));
            case Operators.Conditional:
                return new ConditionalEval(Plan(call.Args[0]), Plan(call.Args[1]), Plan(call.Args[2]));
            case Operators.NotStrictlyFalse:
                return new NotStrictlyFalseEval(Plan(call.Args[0]));
        }

        var argEvals = new List<IInterpretable>(call.Args.Count + 1);
        if (call.Target is not null)
        {
            argEvals.Add(Plan(call.Target));
        }

        argEvals.AddRange(call.Args.Select(Plan));

        // Global function names resolve through the container (f → a.b.f, a.f, f); the leading-dot
        // form is absolute. Receiver-style calls dispatch on the bare name.
        var name = call.Function;
        if (call.Target is null)
        {
            foreach (var candidate in ResolveCandidateNames(name))
            {
                if (functions.Find(candidate) is not null)
                {
                    name = candidate;
                    break;
                }
            }

            if (name.StartsWith('.'))
            {
                name = name[1..];
            }
        }

        return new CallEval(name, functions.Find(name), [.. argEvals]);
    }

    /// <summary>The dotted name for a pure ident/select chain, or null when the chain contains other nodes.</summary>
    private static string? QualifiedName(Expr expr) => expr switch
    {
        IdentExpr ident => ident.Name,
        SelectExpr { TestOnly: false } select when QualifiedName(select.Operand) is { } prefix => prefix + "." + select.Field,
        _ => null,
    };

    /// <summary>
    /// Container-based candidate resolution per the spec's C.name rules: container a.b resolves
    /// name R as a.b.R, a.R, R; a leading dot means absolute (single candidate).
    /// </summary>
    private IReadOnlyList<string> ResolveCandidateNames(string name)
    {
        if (name.StartsWith('.'))
        {
            return [name[1..]];
        }

        if (container.Length == 0)
        {
            return [name];
        }

        var candidates = new List<string>();
        var prefix = container;
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
}

/// <summary>AST constant → runtime value materialization.</summary>
public static class Constants
{
    public static CelValue ToValue(CelConstant constant) => constant.Kind switch
    {
        ConstantKind.Null => NullValue.Instance,
        ConstantKind.Bool => BoolValue.Of(constant.BoolValue),
        ConstantKind.Int => IntValue.Of(constant.IntValue),
        ConstantKind.Uint => UintValue.Of(constant.UintValue),
        ConstantKind.Double => DoubleValue.Of(constant.DoubleValue),
        ConstantKind.String => StringValue.Of(constant.StringValue),
        ConstantKind.Bytes => BytesValue.Of(constant.BytesValue),
        _ => new ErrorValue("invalid constant"),
    };
}
