using Celly.Ast;
using Celly.Common;

namespace Celly.Parsing;

/// <summary>
/// Services a macro expander needs from the parser: fresh expression ids (with position tracking)
/// and error reporting anchored to the macro call.
/// </summary>
public interface IMacroContext
{
    long NextId();

    Expr NewConst(CelConstant value);

    Expr NewIdent(string name);

    Expr NewGlobalCall(string function, params Expr[] args);

    Expr NewList(params Expr[] elements);

    /// <summary>Reports an error at the macro call site and returns an error sentinel expression.</summary>
    Expr ReportError(string message);
}

/// <summary>A parse-time macro: rewrites a matching call into another expression form.</summary>
public sealed class Macro(string function, int argCount, bool receiverStyle, Func<IMacroContext, Expr?, IReadOnlyList<Expr>, Expr> expander)
{
    public string Function { get; } = function;

    public int ArgCount { get; } = argCount;

    public bool ReceiverStyle { get; } = receiverStyle;

    public Expr Expand(IMacroContext ctx, Expr? target, IReadOnlyList<Expr> args) => expander(ctx, target, args);

    public (string, int, bool) Key => (Function, ArgCount, ReceiverStyle);
}

/// <summary>The standard CEL macros (langdef.md "Macros"), expanding to comprehension ASTs.</summary>
public static class StandardMacros
{
    /// <summary>
    /// The hidden accumulator variable name. The '@' prefix makes collisions with user
    /// identifiers impossible ('@' cannot appear in source identifiers).
    /// </summary>
    public const string AccumulatorName = "@result";

    public static readonly IReadOnlyList<Macro> All =
    [
        new Macro("has", 1, receiverStyle: false, ExpandHas),
        new Macro("all", 2, receiverStyle: true, (ctx, t, a) => ExpandQuantifier(ctx, QuantifierKind.All, t!, a)),
        new Macro("exists", 2, receiverStyle: true, (ctx, t, a) => ExpandQuantifier(ctx, QuantifierKind.Exists, t!, a)),
        new Macro("exists_one", 2, receiverStyle: true, (ctx, t, a) => ExpandQuantifier(ctx, QuantifierKind.ExistsOne, t!, a)),
        new Macro("map", 2, receiverStyle: true, (ctx, t, a) => ExpandMap(ctx, t!, a[0], null, a[1])),
        new Macro("map", 3, receiverStyle: true, (ctx, t, a) => ExpandMap(ctx, t!, a[0], a[1], a[2])),
        new Macro("filter", 2, receiverStyle: true, ExpandFilter),
    ];

    private enum QuantifierKind
    {
        All,
        Exists,
        ExistsOne,
    }

    private static Expr ExpandHas(IMacroContext ctx, Expr? target, IReadOnlyList<Expr> args)
    {
        if (args[0] is SelectExpr { TestOnly: false } sel)
        {
            return new SelectExpr(ctx.NextId(), sel.Operand, sel.Field, testOnly: true);
        }

        return ctx.ReportError("invalid argument to has() macro");
    }

    private static bool TryGetIterVar(IReadOnlyList<Expr> args, out string name)
    {
        if (args[0] is IdentExpr ident && !ident.Name.StartsWith('.'))
        {
            name = ident.Name;
            return true;
        }

        name = string.Empty;
        return false;
    }

    private static Expr ExpandQuantifier(IMacroContext ctx, QuantifierKind kind, Expr target, IReadOnlyList<Expr> args)
    {
        if (!TryGetIterVar(args, out var v))
        {
            return ctx.ReportError("argument must be a simple name");
        }

        var predicate = args[1];
        Expr init;
        Expr condition;
        Expr step;
        Expr result;
        switch (kind)
        {
            case QuantifierKind.All:
                init = ctx.NewConst(CelConstant.True);
                condition = ctx.NewGlobalCall(Operators.NotStrictlyFalse, ctx.NewIdent(AccumulatorName));
                step = ctx.NewGlobalCall(Operators.LogicalAnd, ctx.NewIdent(AccumulatorName), predicate);
                result = ctx.NewIdent(AccumulatorName);
                break;
            case QuantifierKind.Exists:
                init = ctx.NewConst(CelConstant.False);
                condition = ctx.NewGlobalCall(
                    Operators.NotStrictlyFalse,
                    ctx.NewGlobalCall(Operators.LogicalNot, ctx.NewIdent(AccumulatorName)));
                step = ctx.NewGlobalCall(Operators.LogicalOr, ctx.NewIdent(AccumulatorName), predicate);
                result = ctx.NewIdent(AccumulatorName);
                break;
            default:
                init = ctx.NewConst(CelConstant.Of(0L));
                condition = ctx.NewConst(CelConstant.True);
                step = ctx.NewGlobalCall(
                    Operators.Conditional,
                    predicate,
                    ctx.NewGlobalCall(Operators.Add, ctx.NewIdent(AccumulatorName), ctx.NewConst(CelConstant.Of(1L))),
                    ctx.NewIdent(AccumulatorName));
                result = ctx.NewGlobalCall(Operators.Equals, ctx.NewIdent(AccumulatorName), ctx.NewConst(CelConstant.Of(1L)));
                break;
        }

        return new ComprehensionExpr(ctx.NextId(), v, null, target, AccumulatorName, init, condition, step, result);
    }

    private static Expr ExpandMap(IMacroContext ctx, Expr target, Expr varArg, Expr? predicate, Expr transform)
    {
        if (varArg is not IdentExpr ident || ident.Name.StartsWith('.'))
        {
            return ctx.ReportError("argument is not an identifier");
        }

        var v = ident.Name;
        var init = ctx.NewList();
        var condition = ctx.NewConst(CelConstant.True);
        Expr step = ctx.NewGlobalCall(Operators.Add, ctx.NewIdent(AccumulatorName), ctx.NewList(transform));
        if (predicate is not null)
        {
            step = ctx.NewGlobalCall(Operators.Conditional, predicate, step, ctx.NewIdent(AccumulatorName));
        }

        var result = ctx.NewIdent(AccumulatorName);
        return new ComprehensionExpr(ctx.NextId(), v, null, target, AccumulatorName, init, condition, step, result);
    }

    private static Expr ExpandFilter(IMacroContext ctx, Expr? target, IReadOnlyList<Expr> args)
    {
        if (!TryGetIterVar(args, out var v))
        {
            return ctx.ReportError("argument is not an identifier");
        }

        var predicate = args[1];
        var init = ctx.NewList();
        var condition = ctx.NewConst(CelConstant.True);
        var step = ctx.NewGlobalCall(
            Operators.Conditional,
            predicate,
            ctx.NewGlobalCall(Operators.Add, ctx.NewIdent(AccumulatorName), ctx.NewList(ctx.NewIdent(v))),
            ctx.NewIdent(AccumulatorName));
        var result = ctx.NewIdent(AccumulatorName);
        return new ComprehensionExpr(ctx.NextId(), v, null, target!, AccumulatorName, init, condition, step, result);
    }
}
