using Celly.Ast;
using Celly.Checking;
using Celly.Common;
using Celly.Parsing;
using Celly.Providers;
using Celly.Types;
using Celly.Values;

namespace Celly.Extensions;

/// <summary>
/// The optionals extension: optional.of/ofNonZeroValue/none, hasValue/value/or/orValue,
/// optional select (a.?b), optional index (a[?b]), and the optMap/optFlatMap macros.
/// </summary>
public static class OptionalsLibrary
{
    private static readonly CelType A = CelType.TypeParam("A");
    private static readonly CelType B = CelType.TypeParam("B");

    public static readonly CelLibrary Instance = new()
    {
        Name = "optionals",
        Macros =
        [
            new Macro("optMap", 2, receiverStyle: true, (ctx, target, args) => ExpandOptMap(ctx, target!, args, flat: false)),
            new Macro("optFlatMap", 2, receiverStyle: true, (ctx, target, args) => ExpandOptMap(ctx, target!, args, flat: true)),
        ],
        Functions = Register,
        FunctionDecls =
        [
            new FunctionDecl("optional.of", [new OverloadDecl("optional_of", [A], CelType.Optional(A))]),
            new FunctionDecl("optional.ofNonZeroValue", [new OverloadDecl("optional_ofNonZeroValue", [A], CelType.Optional(A))]),
            new FunctionDecl("optional.none", [new OverloadDecl("optional_none", [], CelType.Optional(A))]),
            new FunctionDecl("hasValue", [new OverloadDecl("optional_hasValue", [CelType.Optional(A)], CelType.Bool, isInstance: true)]),
            new FunctionDecl("value", [new OverloadDecl("optional_value", [CelType.Optional(A)], A, isInstance: true)]),
            new FunctionDecl("or", [new OverloadDecl("optional_or", [CelType.Optional(A), CelType.Optional(A)], CelType.Optional(A), isInstance: true)]),
            new FunctionDecl("orValue", [new OverloadDecl("optional_orValue", [CelType.Optional(A), A], A, isInstance: true)]),
            new FunctionDecl(Operators.OptSelect,
            [
                new OverloadDecl("select_optional_field_map", [CelType.Map(A, B), CelType.String], CelType.Optional(B)),
                new OverloadDecl("select_optional_field", [CelType.Dyn, CelType.String], CelType.Optional(CelType.Dyn)),
            ]),
            new FunctionDecl(Operators.OptIndex,
            [
                new OverloadDecl("optional_map_index_value", [CelType.Map(A, B), A], CelType.Optional(B)),
                new OverloadDecl("optional_list_index_int", [CelType.List(A), CelType.Int], CelType.Optional(A)),
                new OverloadDecl("optional_index_dyn", [CelType.Dyn, CelType.Dyn], CelType.Optional(CelType.Dyn)),
            ]),
            // Plain indexing through an optional operand also chains optionally.
            new FunctionDecl(Operators.Index,
            [
                new OverloadDecl("optional_map_index", [CelType.Optional(CelType.Map(A, B)), A], CelType.Optional(B)),
                new OverloadDecl("optional_list_index", [CelType.Optional(CelType.List(A)), CelType.Int], CelType.Optional(A)),
                new OverloadDecl("optional_dyn_index", [CelType.Optional(CelType.Dyn), CelType.Dyn], CelType.Optional(CelType.Dyn)),
            ]),
        ],
        VariableDecls =
        [
            new VariableDecl("optional_type", new CelType(CelTypeKind.Type, "type", [CelType.OptionalDyn])),
        ],
    };

    private static void Register(Stdlib.FunctionRegistry registry)
    {
        registry.Register("optional.of", args => args is [var v] ? OptionalValue.OfValue(v) : ErrorValue.NoSuchOverload());
        registry.Register("optional.ofNonZeroValue", args =>
            args is [var v] ? (IsZeroValue(v) ? OptionalValue.None : OptionalValue.OfValue(v)) : ErrorValue.NoSuchOverload());
        registry.Register("optional.none", args => args.Length == 0 ? OptionalValue.None : ErrorValue.NoSuchOverload());

        registry.Register("hasValue", args =>
            args is [OptionalValue opt] ? BoolValue.Of(opt.HasValue) : ErrorValue.NoSuchOverload());
        registry.Register("value", args =>
            args is [OptionalValue opt]
                ? opt.HasValue ? opt.Value : new ErrorValue("optional.none() dereference")
                : ErrorValue.NoSuchOverload());
        registry.Register("or", args =>
            args is [OptionalValue left, OptionalValue right]
                ? left.HasValue ? left : right
                : ErrorValue.NoSuchOverload());
        registry.Register("orValue", args =>
            args is [OptionalValue left, var fallback]
                ? left.HasValue ? left.Value : fallback
                : ErrorValue.NoSuchOverload());

        registry.Register(Operators.OptSelect, args =>
        {
            if (args is not [var operand, StringValue field])
            {
                return ErrorValue.NoSuchOverload();
            }

            return OptSelect(operand, field);
        });

        registry.Register(Operators.OptIndex, args =>
        {
            if (args is not [var operand, var index])
            {
                return ErrorValue.NoSuchOverload();
            }

            return OptIndex(operand, index);
        });
    }

    private static CelValue OptSelect(CelValue operand, StringValue field)
    {
        switch (operand)
        {
            case OptionalValue opt:
                return opt.HasValue ? OptSelect(opt.Value, field) : OptionalValue.None;
            case MapValue map:
                return map.TryGet(field, out var value) ? OptionalValue.OfValue(value) : OptionalValue.None;
            case IStructValue st:
            {
                var has = st.HasField(field.Value);
                if (has is ErrorValue error)
                {
                    return error;
                }

                return has is BoolValue { Value: true }
                    ? OptionalValue.OfValue(st.GetField(field.Value))
                    : OptionalValue.None;
            }

            default:
                return ErrorValue.NoSuchOverload();
        }
    }

    private static CelValue OptIndex(CelValue operand, CelValue index)
    {
        switch (operand)
        {
            case OptionalValue opt:
                return opt.HasValue ? OptIndex(opt.Value, index) : OptionalValue.None;
            case MapValue map:
                return map.TryGet(index, out var value) ? OptionalValue.OfValue(value) : OptionalValue.None;
            case ListValue list:
            {
                if (index is OptionalValue optIndex)
                {
                    return optIndex.HasValue ? OptIndex(list, optIndex.Value) : OptionalValue.None;
                }

                var result = list.Get(index);
                return result is ErrorValue ? OptionalValue.None : OptionalValue.OfValue(result);
            }

            default:
                return ErrorValue.NoSuchOverload();
        }
    }

    internal static bool IsZeroValue(CelValue value) => value switch
    {
        NullValue => true,
        BoolValue b => !b.Value,
        IntValue i => i.Value == 0,
        UintValue u => u.Value == 0,
        DoubleValue d => d.Value == 0,
        StringValue s => s.Value.Length == 0,
        BytesValue by => by.Span.Length == 0,
        ListValue list => list.Elements.Count == 0,
        MapValue map => map.Count == 0,
        DurationValue dur => dur.Data is { Seconds: 0, Nanos: 0 },
        TimestampValue ts => ts.Data is { Seconds: 0, Nanos: 0 },
        OptionalValue opt => !opt.HasValue,
        IZeroTester zero => zero.IsZeroValue(),
        _ => false,
    };

    /// <summary>
    /// target.optMap(v, f): hasValue() ? optional.of(bind(v, target.value(), f)) : optional.none().
    /// The flat variant returns f directly (f itself yields an optional).
    /// </summary>
    private static Expr? ExpandOptMap(IMacroContext ctx, Expr target, IReadOnlyList<Expr> args, bool flat)
    {
        if (args[0] is not IdentExpr ident || ident.Name.StartsWith('.'))
        {
            return ctx.ReportError("optMap() variable name must be a simple identifier");
        }

        var varName = ident.Name;
        var mapExpr = args[1];
        var hasValue = new CallExpr(ctx.NextId(), target, "hasValue", []);
        var value = new CallExpr(ctx.NextId(), target, "value", []);
        // bind(v, target.value(), f) as a comprehension: empty range, accu = the bound variable.
        Expr bound = new ComprehensionExpr(
            ctx.NextId(),
            "#unused",
            null,
            new ListExpr(ctx.NextId(), [], []),
            varName,
            value,
            new ConstExpr(ctx.NextId(), CelConstant.False),
            new IdentExpr(ctx.NextId(), varName),
            mapExpr);
        if (!flat)
        {
            bound = new CallExpr(ctx.NextId(), null, "optional.of", [bound]);
        }

        var none = new CallExpr(ctx.NextId(), null, "optional.none", []);
        return new CallExpr(ctx.NextId(), null, Operators.Conditional, [hasValue, bound, none]);
    }
}
