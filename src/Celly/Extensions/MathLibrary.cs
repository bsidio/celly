using Celly.Ast;
using Celly.Checking;
using Celly.Parsing;
using Celly.Types;
using Celly.Values;

namespace Celly.Extensions;

/// <summary>The math extension: greatest/least, rounding, abs/sign, bitwise ops, NaN/Inf tests, sqrt.</summary>
public static class MathLibrary
{
    private static readonly CelType A = CelType.TypeParam("A");

    public static readonly CelLibrary Instance = new()
    {
        Name = "math",
        Macros =
        [
            new Macro("greatest", Macro.VarArg, receiverStyle: true, (ctx, t, a) => ExpandMinMax(ctx, t, a, "math.@max")),
            new Macro("least", Macro.VarArg, receiverStyle: true, (ctx, t, a) => ExpandMinMax(ctx, t, a, "math.@min")),
        ],
        Functions = Register,
        FunctionDecls =
        [
            new FunctionDecl("math.@max", [new OverloadDecl("math_max_list", [CelType.List(A)], A)]),
            new FunctionDecl("math.@min", [new OverloadDecl("math_min_list", [CelType.List(A)], A)]),
            Unary("math.ceil", CelType.Double, CelType.Double),
            Unary("math.floor", CelType.Double, CelType.Double),
            Unary("math.round", CelType.Double, CelType.Double),
            Unary("math.trunc", CelType.Double, CelType.Double),
            new FunctionDecl("math.abs",
            [
                new OverloadDecl("math_abs_int", [CelType.Int], CelType.Int),
                new OverloadDecl("math_abs_uint", [CelType.Uint], CelType.Uint),
                new OverloadDecl("math_abs_double", [CelType.Double], CelType.Double),
            ]),
            new FunctionDecl("math.sign",
            [
                new OverloadDecl("math_sign_int", [CelType.Int], CelType.Int),
                new OverloadDecl("math_sign_uint", [CelType.Uint], CelType.Uint),
                new OverloadDecl("math_sign_double", [CelType.Double], CelType.Double),
            ]),
            Unary("math.isNaN", CelType.Double, CelType.Bool),
            Unary("math.isInf", CelType.Double, CelType.Bool),
            Unary("math.isFinite", CelType.Double, CelType.Bool),
            new FunctionDecl("math.sqrt",
            [
                new OverloadDecl("math_sqrt_double", [CelType.Double], CelType.Double),
                new OverloadDecl("math_sqrt_int", [CelType.Int], CelType.Double),
                new OverloadDecl("math_sqrt_uint", [CelType.Uint], CelType.Double),
            ]),
            Binary("math.bitAnd"),
            Binary("math.bitOr"),
            Binary("math.bitXor"),
            new FunctionDecl("math.bitNot",
            [
                new OverloadDecl("math_bitNot_int", [CelType.Int], CelType.Int),
                new OverloadDecl("math_bitNot_uint", [CelType.Uint], CelType.Uint),
            ]),
            Shift("math.bitShiftLeft"),
            Shift("math.bitShiftRight"),
        ],
    };

    private static FunctionDecl Unary(string name, CelType arg, CelType result) =>
        new(name, [new OverloadDecl(name.Replace('.', '_').Replace("@", string.Empty), [arg], result)]);

    private static FunctionDecl Binary(string name) =>
        new(name,
        [
            new OverloadDecl(name.Replace('.', '_') + "_int", [CelType.Int, CelType.Int], CelType.Int),
            new OverloadDecl(name.Replace('.', '_') + "_uint", [CelType.Uint, CelType.Uint], CelType.Uint),
        ]);

    private static FunctionDecl Shift(string name) =>
        new(name,
        [
            new OverloadDecl(name.Replace('.', '_') + "_int", [CelType.Int, CelType.Int], CelType.Int),
            new OverloadDecl(name.Replace('.', '_') + "_uint", [CelType.Uint, CelType.Int], CelType.Uint),
        ]);

    private static Expr? ExpandMinMax(IMacroContext ctx, Expr? target, IReadOnlyList<Expr> args, string function)
    {
        if (target is not IdentExpr { Name: "math" })
        {
            return null;
        }

        if (args.Count == 0)
        {
            return ctx.ReportError("math.greatest/least requires at least one argument");
        }

        // Normalize to a single-list call: math.@max([a, b, …]).
        var list = args.Count == 1 && args[0] is ListExpr existing
            ? (Expr)existing
            : ctx.NewList([.. args]);
        return ctx.NewGlobalCall(function, list);
    }

    private static void Register(Stdlib.FunctionRegistry registry)
    {
        registry.Register("math.@max", args => MinMax(args, max: true));
        registry.Register("math.@min", args => MinMax(args, max: false));

        registry.Register("math.ceil", args => args is [DoubleValue d] ? DoubleValue.Of(Math.Ceiling(d.Value)) : ErrorValue.NoSuchOverload());
        registry.Register("math.floor", args => args is [DoubleValue d] ? DoubleValue.Of(Math.Floor(d.Value)) : ErrorValue.NoSuchOverload());
        registry.Register("math.round", args => args is [DoubleValue d]
            ? DoubleValue.Of(double.IsNaN(d.Value) || double.IsInfinity(d.Value) ? d.Value : Math.Round(d.Value, MidpointRounding.AwayFromZero))
            : ErrorValue.NoSuchOverload());
        registry.Register("math.trunc", args => args is [DoubleValue d] ? DoubleValue.Of(Math.Truncate(d.Value)) : ErrorValue.NoSuchOverload());

        registry.Register("math.abs", args => args switch
        {
            [IntValue i] => i.Value == long.MinValue ? ErrorValue.Overflow() : IntValue.Of(Math.Abs(i.Value)),
            [UintValue u] => u,
            [DoubleValue d] => DoubleValue.Of(Math.Abs(d.Value)),
            _ => ErrorValue.NoSuchOverload(),
        });
        registry.Register("math.sign", args => args switch
        {
            [IntValue i] => IntValue.Of(Math.Sign(i.Value)),
            [UintValue u] => UintValue.Of(u.Value == 0 ? 0UL : 1UL),
            [DoubleValue d] => DoubleValue.Of(double.IsNaN(d.Value) ? double.NaN : d.Value > 0 ? 1.0 : d.Value < 0 ? -1.0 : d.Value),
            _ => ErrorValue.NoSuchOverload(),
        });

        registry.Register("math.isNaN", args => args is [DoubleValue d] ? BoolValue.Of(double.IsNaN(d.Value)) : ErrorValue.NoSuchOverload());
        registry.Register("math.isInf", args => args is [DoubleValue d] ? BoolValue.Of(double.IsInfinity(d.Value)) : ErrorValue.NoSuchOverload());
        registry.Register("math.isFinite", args => args is [DoubleValue d] ? BoolValue.Of(double.IsFinite(d.Value)) : ErrorValue.NoSuchOverload());
        registry.Register("math.sqrt", args => args switch
        {
            [DoubleValue d] => DoubleValue.Of(Math.Sqrt(d.Value)),
            [IntValue i] => DoubleValue.Of(Math.Sqrt(i.Value)),
            [UintValue u] => DoubleValue.Of(Math.Sqrt(u.Value)),
            _ => ErrorValue.NoSuchOverload(),
        });

        registry.Register("math.bitAnd", args => args switch
        {
            [IntValue a, IntValue b] => IntValue.Of(a.Value & b.Value),
            [UintValue a, UintValue b] => UintValue.Of(a.Value & b.Value),
            _ => ErrorValue.NoSuchOverload(),
        });
        registry.Register("math.bitOr", args => args switch
        {
            [IntValue a, IntValue b] => IntValue.Of(a.Value | b.Value),
            [UintValue a, UintValue b] => UintValue.Of(a.Value | b.Value),
            _ => ErrorValue.NoSuchOverload(),
        });
        registry.Register("math.bitXor", args => args switch
        {
            [IntValue a, IntValue b] => IntValue.Of(a.Value ^ b.Value),
            [UintValue a, UintValue b] => UintValue.Of(a.Value ^ b.Value),
            _ => ErrorValue.NoSuchOverload(),
        });
        registry.Register("math.bitNot", args => args switch
        {
            [IntValue i] => IntValue.Of(~i.Value),
            [UintValue u] => UintValue.Of(~u.Value),
            _ => ErrorValue.NoSuchOverload(),
        });
        registry.Register("math.bitShiftLeft", args => args switch
        {
            [IntValue v, IntValue s] when s.Value < 0 => new ErrorValue("math.bitShiftLeft() negative offset"),
            [IntValue v, IntValue s] => IntValue.Of(s.Value >= 64 ? 0 : v.Value << (int)s.Value),
            [UintValue v, IntValue s] when s.Value < 0 => new ErrorValue("math.bitShiftLeft() negative offset"),
            [UintValue v, IntValue s] => UintValue.Of(s.Value >= 64 ? 0 : v.Value << (int)s.Value),
            _ => ErrorValue.NoSuchOverload(),
        });
        registry.Register("math.bitShiftRight", args => args switch
        {
            [IntValue v, IntValue s] when s.Value < 0 => new ErrorValue("math.bitShiftRight() negative offset"),
            // Logical (unsigned) right shift, matching cel-go.
            [IntValue v, IntValue s] => IntValue.Of(s.Value >= 64 ? 0 : unchecked((long)((ulong)v.Value >> (int)s.Value))),
            [UintValue v, IntValue s] when s.Value < 0 => new ErrorValue("math.bitShiftRight() negative offset"),
            [UintValue v, IntValue s] => UintValue.Of(s.Value >= 64 ? 0 : v.Value >> (int)s.Value),
            _ => ErrorValue.NoSuchOverload(),
        });
    }

    private static CelValue MinMax(CelValue[] args, bool max)
    {
        if (args is not [ListValue list])
        {
            return ErrorValue.NoSuchOverload();
        }

        if (list.Elements.Count == 0)
        {
            return new ErrorValue("math.@max|min requires a non-empty list");
        }

        var best = list.Elements[0];
        if (best is not (IntValue or UintValue or DoubleValue))
        {
            return ErrorValue.NoSuchOverload();
        }

        for (var i = 1; i < list.Elements.Count; i++)
        {
            var candidate = list.Elements[i];
            if (candidate is not (IntValue or UintValue or DoubleValue))
            {
                return ErrorValue.NoSuchOverload();
            }

            var cmp = ((IComparableValue)candidate).CompareTo(best);
            if (cmp is ErrorValue err)
            {
                return err;
            }

            if (cmp is IntValue c && (max ? c.Value > 0 : c.Value < 0))
            {
                best = candidate;
            }
        }

        return best;
    }
}
