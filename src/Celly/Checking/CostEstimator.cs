using Celly.Ast;
using Celly.Types;

namespace Celly.Checking;

/// <summary>
/// Static, pre-evaluation worst-case cost analysis over a <em>checked</em> AST, modelled on
/// cel-go's cost estimator. It walks the expression once and returns a <see cref="CostEstimate"/>
/// whose <c>Max</c> bounds the number of elementary operations evaluation can perform.
///
/// <para>The dominant, security-relevant behaviour: a comprehension multiplies its per-iteration
/// body cost by the size of the range it iterates, so nested comprehensions compound
/// multiplicatively — and iterating an unconstrained input variable yields an <em>unbounded</em>
/// estimate. Supply an <see cref="ICostEstimator"/> to bound input sizes and get a finite number to
/// threshold against.</para>
///
/// <para>Requires a checked AST (<see cref="CelAbstractSyntax.IsChecked"/>): the type and reference
/// maps drive size reasoning and overload-specific costs.</para>
/// </summary>
public sealed class CostEstimator
{
    private static readonly IReadOnlyDictionary<long, ReferenceInfo> NoReferences =
        new Dictionary<long, ReferenceInfo>();

    private readonly IReadOnlyDictionary<long, CelType> _types;
    private readonly IReadOnlyDictionary<long, ReferenceInfo> _references;
    private readonly ICostEstimator? _hints;

    private CostEstimator(
        IReadOnlyDictionary<long, CelType> types,
        IReadOnlyDictionary<long, ReferenceInfo> references,
        ICostEstimator? hints)
    {
        _types = types;
        _references = references;
        _hints = hints;
    }

    /// <summary>
    /// Estimates the worst-case evaluation cost of a checked AST. <paramref name="hints"/> optionally
    /// bounds input sizes; without it, comprehensions over input variables are unbounded.
    /// </summary>
    /// <exception cref="InvalidOperationException">The AST has not been type-checked.</exception>
    public static CostEstimate Estimate(CelAbstractSyntax checkedAst, ICostEstimator? hints = null)
    {
        if (!checkedAst.IsChecked)
        {
            throw new InvalidOperationException(
                "Cost estimation requires a checked AST; call CelEnv.Check before EstimateCost.");
        }

        var estimator = new CostEstimator(checkedAst.TypeMap!, checkedAst.ReferenceMap ?? NoReferences, hints);
        return estimator.Cost(checkedAst.Expr);
    }

    private CostEstimate Cost(Expr expr) => expr switch
    {
        ConstExpr => CostEstimate.Zero,
        IdentExpr => CostEstimate.One,
        // A field select (or has() presence test) is one lookup on top of its operand.
        SelectExpr select => Cost(select.Operand).Add(CostEstimate.One),
        ListExpr list => Sum(list.Elements).Add(CostEstimate.One),
        MapExpr map => SumEntries(map).Add(CostEstimate.One),
        StructExpr strukt => SumFields(strukt).Add(CostEstimate.One),
        CallExpr call => CostOfCall(call),
        ComprehensionExpr comp => CostOfComprehension(comp),
        _ => CostEstimate.One,
    };

    private CostEstimate CostOfCall(CallExpr call)
    {
        var cost = call.Target is null ? CostEstimate.Zero : Cost(call.Target);
        cost = cost.Add(Sum(call.Args));
        return cost.Add(CallCost(call));
    }

    // Per-invocation cost of the function itself (excluding its already-counted operands).
    private CostEstimate CallCost(CallExpr call)
    {
        var operands = call.Target is null ? call.Args : Prepend(call.Target, call.Args);
        foreach (var overloadId in OverloadIds(call))
        {
            switch (overloadId)
            {
                // Regex match walks the input against the compiled program: input × pattern.
                case "matches" or "matches_string":
                    return FromSize(SizeOf(operands[0]).Multiply(SizeOf(operands[^1])));

                // Substring scans cost with the length of the receiver string.
                case "contains_string" or "starts_with_string" or "ends_with_string":
                    return FromSize(SizeOf(operands[0]));

                // Concatenation cost is proportional to the combined size of both operands.
                case "add_string" or "add_bytes" or "add_list":
                    return FromSize(SizeOf(operands[0]).Add(SizeOf(operands[^1])));

                // Deep equality on aggregates/strings compares element-by-element.
                case "equals" or "not_equals":
                    if (operands.Count == 2 && IsSized(TypeOf(operands[0])))
                    {
                        return FromSize(Max(SizeOf(operands[0]), SizeOf(operands[1])));
                    }

                    break;

                // Membership scans the collection operand.
                case "in_list" or "in_map":
                    return FromSize(SizeOf(operands[^1]));
            }
        }

        return CostEstimate.One;
    }

    private CostEstimate CostOfComprehension(ComprehensionExpr comp)
    {
        var rangeSize = SizeOf(comp.IterRange);

        // Set-up: evaluate the range and initialise the accumulator, once.
        var setup = Cost(comp.IterRange).Add(Cost(comp.AccuInit));

        // Body: the loop condition and step run once per element of the range.
        var body = Cost(comp.LoopCondition).Add(Cost(comp.LoopStep));

        // Finalise: project the accumulator to the result, once.
        return setup
            .Add(body.MultiplyBySize(rangeSize))
            .Add(Cost(comp.Result))
            .Add(CostEstimate.One);
    }

    // ---- size reasoning ----------------------------------------------------

    private SizeEstimate SizeOf(Expr expr)
    {
        switch (expr)
        {
            case ConstExpr { Value.Kind: ConstantKind.String } s:
                return SizeEstimate.Exact((ulong)s.Value.StringValue.Length);
            case ConstExpr { Value.Kind: ConstantKind.Bytes } b:
                return SizeEstimate.Exact((ulong)b.Value.BytesValue.Length);
            case ConstExpr:
                return SizeEstimate.Exact(1);
            case ListExpr list:
                return SizeEstimate.Exact((ulong)list.Elements.Count);
            case MapExpr map:
                return SizeEstimate.Exact((ulong)map.Entries.Count);
            case IdentExpr:
            case SelectExpr:
                return SizeOfReference(expr);
            case CallExpr call:
                return SizeOfCall(call);
            case ComprehensionExpr comp:
                // map/filter build a list no larger than the range; all/exists fold to a scalar.
                return IsSized(TypeOf(comp))
                    ? new SizeEstimate(0, SizeOf(comp.IterRange).Max)
                    : SizeEstimate.Exact(1);
            default:
                return SizeEstimate.Unbounded;
        }
    }

    private SizeEstimate SizeOfReference(Expr expr)
    {
        var type = TypeOf(expr);
        if (!IsSized(type))
        {
            return SizeEstimate.Exact(1);
        }

        return _hints?.EstimateSize(PathOf(expr), type) ?? SizeEstimate.Unbounded;
    }

    private SizeEstimate SizeOfCall(CallExpr call)
    {
        var operands = call.Target is null ? call.Args : Prepend(call.Target, call.Args);
        foreach (var overloadId in OverloadIds(call))
        {
            switch (overloadId)
            {
                // Concatenation produces the sum of its operands' sizes.
                case "add_string" or "add_bytes" or "add_list":
                    return SizeOf(operands[0]).Add(SizeOf(operands[^1]));
            }
        }

        // Unknown-producing calls: a sized result (list/map/string) is unbounded, a scalar is 1.
        return IsSized(TypeOf(call)) ? SizeEstimate.Unbounded : SizeEstimate.Exact(1);
    }

    // A dotted path (a.b.c) for hint lookup, or null when the operand isn't a simple ident/field chain.
    private static string? PathOf(Expr expr) => expr switch
    {
        IdentExpr ident => ident.Name,
        SelectExpr select => PathOf(select.Operand) is { } parent ? $"{parent}.{select.Field}" : null,
        _ => null,
    };

    // ---- helpers -----------------------------------------------------------

    private CostEstimate Sum(IReadOnlyList<Expr> exprs)
    {
        var total = CostEstimate.Zero;
        foreach (var expr in exprs)
        {
            total = total.Add(Cost(expr));
        }

        return total;
    }

    private CostEstimate SumEntries(MapExpr map)
    {
        var total = CostEstimate.Zero;
        foreach (var entry in map.Entries)
        {
            total = total.Add(Cost(entry.Key)).Add(Cost(entry.Value));
        }

        return total;
    }

    private CostEstimate SumFields(StructExpr strukt)
    {
        var total = CostEstimate.Zero;
        foreach (var field in strukt.Fields)
        {
            total = total.Add(Cost(field.Value));
        }

        return total;
    }

    private IReadOnlyList<string> OverloadIds(CallExpr call) =>
        _references.TryGetValue(call.Id, out var reference) ? reference.OverloadIds : [];

    private CelType TypeOf(Expr expr) => _types.GetValueOrDefault(expr.Id, CelType.Dyn);

    // Cost scales with the operand size; at least one unit even for an empty operand.
    private static CostEstimate FromSize(SizeEstimate size) =>
        new(1, System.Math.Max(1UL, size.Max));

    private static SizeEstimate Max(SizeEstimate a, SizeEstimate b) =>
        new(System.Math.Max(a.Min, b.Min), System.Math.Max(a.Max, b.Max));

    private static bool IsSized(CelType type) => type.Kind is
        CelTypeKind.String or CelTypeKind.Bytes or CelTypeKind.List or CelTypeKind.Map;

    private static IReadOnlyList<Expr> Prepend(Expr head, IReadOnlyList<Expr> tail)
    {
        var result = new Expr[tail.Count + 1];
        result[0] = head;
        for (var i = 0; i < tail.Count; i++)
        {
            result[i + 1] = tail[i];
        }

        return result;
    }
}
