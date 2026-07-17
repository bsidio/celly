using Celly.Ast;
using Celly.Parsing;

namespace Celly.Extensions;

/// <summary>cel.bind(var, init, expr): binds a value once for reuse within the sub-expression.</summary>
public static class BindingsLibrary
{
    public static readonly CelLibrary Instance = new()
    {
        Name = "bindings",
        Macros = [new Macro("bind", 3, receiverStyle: true, ExpandBind)],
    };

    private static Expr? ExpandBind(IMacroContext ctx, Expr? target, IReadOnlyList<Expr> args)
    {
        if (target is not IdentExpr { Name: "cel" })
        {
            return null; // only cel.bind(...) is the macro
        }

        if (args[0] is not IdentExpr ident || ident.Name.StartsWith('.'))
        {
            return ctx.ReportError("cel.bind() variable name must be a simple identifier");
        }

        // Comprehension with an empty range: the accumulator IS the bound variable.
        return new ComprehensionExpr(
            ctx.NextId(),
            "#unused",
            null,
            new ListExpr(ctx.NextId(), [], []),
            ident.Name,
            args[1],
            new ConstExpr(ctx.NextId(), CelConstant.False),
            new IdentExpr(ctx.NextId(), ident.Name),
            args[2]);
    }
}

/// <summary>
/// cel.block([e0, e1, …], result): an optimizer-internal form where cel.index(i) references the
/// i-th bound sub-expression (later entries and the result may reference earlier ones).
/// Expands to nested cel.bind-style comprehensions over rewritten @indexN variables.
/// </summary>
public static class BlockLibrary
{
    public static readonly CelLibrary Instance = new()
    {
        Name = "block",
        Macros =
        [
            new Macro("block", 2, receiverStyle: true, ExpandBlock),
            // Test-only variable spellings: cel.iterVar(d, u) / cel.accuVar(d, u) denote
            // comprehension variables; any consistent naming works since both the macro binding
            // position and the references expand identically.
            new Macro("iterVar", 2, receiverStyle: true, (ctx, t, a) => ExpandVarRef(ctx, t, a, "it")),
            new Macro("accuVar", 2, receiverStyle: true, (ctx, t, a) => ExpandVarRef(ctx, t, a, "ac")),
        ],
    };

    private static Expr? ExpandVarRef(IMacroContext ctx, Expr? target, IReadOnlyList<Expr> args, string kind)
    {
        if (target is not IdentExpr { Name: "cel" })
        {
            return null;
        }

        if (args[0] is not ConstExpr { Value.Kind: ConstantKind.Int } depth
            || args[1] is not ConstExpr { Value.Kind: ConstantKind.Int } unique)
        {
            return ctx.ReportError($"cel.{(kind == "it" ? "iterVar" : "accuVar")} requires int literal arguments");
        }

        return new IdentExpr(ctx.NextId(), $"@{kind}:{depth.Value.IntValue}:{unique.Value.IntValue}");
    }

    private static Expr? ExpandBlock(IMacroContext ctx, Expr? target, IReadOnlyList<Expr> args)
    {
        if (target is not IdentExpr { Name: "cel" })
        {
            return null;
        }

        if (args[0] is not ListExpr bindings)
        {
            return ctx.ReportError("cel.block() requires a list literal of binding expressions");
        }

        // Innermost-out: result first, then wrap binds N-1 .. 0.
        var body = RewriteIndexRefs(ctx, args[1]);
        for (var i = bindings.Elements.Count - 1; i >= 0; i--)
        {
            var init = RewriteIndexRefs(ctx, bindings.Elements[i]);
            body = new ComprehensionExpr(
                ctx.NextId(),
                "#unused",
                null,
                new ListExpr(ctx.NextId(), [], []),
                $"@index{i}",
                init,
                new ConstExpr(ctx.NextId(), CelConstant.False),
                new IdentExpr(ctx.NextId(), $"@index{i}"),
                body);
        }

        return body;
    }

    /// <summary>Clones the tree replacing cel.index(N) calls with @indexN identifiers.</summary>
    private static Expr RewriteIndexRefs(IMacroContext ctx, Expr expr)
    {
        switch (expr)
        {
            case CallExpr { Function: "index", Target: IdentExpr { Name: "cel" }, Args: [ConstExpr { Value.Kind: ConstantKind.Int } index] }:
                return new IdentExpr(ctx.NextId(), $"@index{index.Value.IntValue}");
            case CallExpr call:
                return new CallExpr(
                    call.Id,
                    call.Target is null ? null : RewriteIndexRefs(ctx, call.Target),
                    call.Function,
                    [.. call.Args.Select(a => RewriteIndexRefs(ctx, a))]);
            case SelectExpr select:
                return new SelectExpr(select.Id, RewriteIndexRefs(ctx, select.Operand), select.Field, select.TestOnly);
            case ListExpr list:
                return new ListExpr(list.Id, [.. list.Elements.Select(e => RewriteIndexRefs(ctx, e))], list.OptionalIndices);
            case MapExpr map:
                return new MapExpr(
                    map.Id,
                    [.. map.Entries.Select(e => new MapEntry(e.Id, RewriteIndexRefs(ctx, e.Key), RewriteIndexRefs(ctx, e.Value), e.Optional))]);
            case StructExpr st:
                return new StructExpr(
                    st.Id,
                    st.MessageName,
                    [.. st.Fields.Select(f => new StructField(f.Id, f.Name, RewriteIndexRefs(ctx, f.Value), f.Optional))]);
            case ComprehensionExpr comp:
                return new ComprehensionExpr(
                    comp.Id,
                    comp.IterVar,
                    comp.IterVar2,
                    RewriteIndexRefs(ctx, comp.IterRange),
                    comp.AccuVar,
                    RewriteIndexRefs(ctx, comp.AccuInit),
                    RewriteIndexRefs(ctx, comp.LoopCondition),
                    RewriteIndexRefs(ctx, comp.LoopStep),
                    RewriteIndexRefs(ctx, comp.Result));
            default:
                return expr;
        }
    }
}
