using Celly.Ast;
using Celly.Checking;
using Celly.Parsing;
using Celly.Types;
using Celly.Values;

namespace Celly.Extensions;

/// <summary>base64.encode(bytes) → string, base64.decode(string) → bytes.</summary>
public static class EncodersLibrary
{
    public static readonly CelLibrary Instance = new()
    {
        Name = "encoders",
        Functions = registry =>
        {
            registry.Register("base64.encode", args => args is [BytesValue b]
                ? StringValue.Of(Convert.ToBase64String(b.Span))
                : ErrorValue.NoSuchOverload());
            registry.Register("base64.decode", args =>
            {
                if (args is not [StringValue s])
                {
                    return ErrorValue.NoSuchOverload();
                }

                // Tolerate missing padding, like cel-go's encoder extension.
                var text = s.Value;
                if (text.Length % 4 != 0)
                {
                    text += new string('=', 4 - text.Length % 4);
                }

                try
                {
                    return BytesValue.Of(Convert.FromBase64String(text));
                }
                catch (FormatException)
                {
                    return new ErrorValue("invalid base64 string");
                }
            });
        },
        FunctionDecls =
        [
            new FunctionDecl("base64.encode", [new OverloadDecl("base64_encode_bytes", [CelType.Bytes], CelType.String)]),
            new FunctionDecl("base64.decode", [new OverloadDecl("base64_decode_string", [CelType.String], CelType.Bytes)]),
        ],
    };
}

/// <summary>proto.getExt(msg, pkg.ext_name) and proto.hasExt(msg, pkg.ext_name) macros.</summary>
public static class ProtosLibrary
{
    public static readonly CelLibrary Instance = new()
    {
        Name = "protos",
        Macros =
        [
            new Macro("getExt", 2, receiverStyle: true, (ctx, target, args) => ExpandExt(ctx, target, args, testOnly: false)),
            new Macro("hasExt", 2, receiverStyle: true, (ctx, target, args) => ExpandExt(ctx, target, args, testOnly: true)),
        ],
    };

    private static Expr? ExpandExt(IMacroContext ctx, Expr? target, IReadOnlyList<Expr> args, bool testOnly)
    {
        if (target is not IdentExpr { Name: "proto" })
        {
            return null;
        }

        var extensionName = QualifiedName(args[1]);
        if (extensionName is null)
        {
            return ctx.ReportError("invalid extension field name");
        }

        // The extension full name becomes the (dotted) field selector on the message.
        return new SelectExpr(ctx.NextId(), args[0], extensionName, testOnly);
    }

    private static string? QualifiedName(Expr expr) => expr switch
    {
        IdentExpr ident => ident.Name.StartsWith('.') ? ident.Name[1..] : ident.Name,
        SelectExpr { TestOnly: false } select when QualifiedName(select.Operand) is { } prefix =>
            prefix + "." + select.Field,
        _ => null,
    };
}

/// <summary>
/// Two-variable comprehensions: all/exists/existsOne(k, v, p), transformList/transformMap/
/// transformMapEntry over (index, value) for lists and (key, value) for maps.
/// </summary>
public static class TwoVarComprehensionsLibrary
{
    private const string Accu = Parsing.StandardMacros.AccumulatorName;
    private static readonly CelType A = CelType.TypeParam("A");
    private static readonly CelType B = CelType.TypeParam("B");

    public static readonly CelLibrary Instance = new()
    {
        Name = "two-var-comprehensions",
        Macros =
        [
            new Macro("all", 3, receiverStyle: true, (ctx, t, a) => Quantifier(ctx, t!, a, Kind.All)),
            new Macro("exists", 3, receiverStyle: true, (ctx, t, a) => Quantifier(ctx, t!, a, Kind.Exists)),
            new Macro("existsOne", 3, receiverStyle: true, (ctx, t, a) => Quantifier(ctx, t!, a, Kind.ExistsOne)),
            new Macro("exists_one", 3, receiverStyle: true, (ctx, t, a) => Quantifier(ctx, t!, a, Kind.ExistsOne)),
            new Macro("transformList", 3, receiverStyle: true, (ctx, t, a) => TransformList(ctx, t!, a[0], a[1], null, a[2])),
            new Macro("transformList", 4, receiverStyle: true, (ctx, t, a) => TransformList(ctx, t!, a[0], a[1], a[2], a[3])),
            new Macro("transformMap", 3, receiverStyle: true, (ctx, t, a) => TransformMap(ctx, t!, a[0], a[1], null, a[2], entry: false)),
            new Macro("transformMap", 4, receiverStyle: true, (ctx, t, a) => TransformMap(ctx, t!, a[0], a[1], a[2], a[3], entry: false)),
            new Macro("transformMapEntry", 3, receiverStyle: true, (ctx, t, a) => TransformMap(ctx, t!, a[0], a[1], null, a[2], entry: true)),
            new Macro("transformMapEntry", 4, receiverStyle: true, (ctx, t, a) => TransformMap(ctx, t!, a[0], a[1], a[2], a[3], entry: true)),
        ],
        Functions = registry =>
        {
            registry.Register("cel.@mapInsert", args => args switch
            {
                [MapValue map, var key, var value] => Insert(map, [new(key, value)]),
                [MapValue map, MapValue entries] => Insert(map, entries.Keys.Select(k =>
                {
                    entries.TryGet(k, out var v);
                    return new KeyValuePair<CelValue, CelValue>(k, v);
                })),
                _ => ErrorValue.NoSuchOverload(),
            });
        },
        FunctionDecls =
        [
            new FunctionDecl("cel.@mapInsert",
            [
                new OverloadDecl("map_insert_key_value", [CelType.Map(A, B), A, B], CelType.Map(A, B)),
                new OverloadDecl("map_insert_map", [CelType.Map(A, B), CelType.Map(A, B)], CelType.Map(A, B)),
            ]),
        ],
    };

    private static CelValue Insert(MapValue map, IEnumerable<KeyValuePair<CelValue, CelValue>> entries)
    {
        var pairs = new List<KeyValuePair<CelValue, CelValue>>();
        foreach (var key in map.Keys)
        {
            map.TryGet(key, out var value);
            pairs.Add(new(key, value));
        }

        foreach (var (key, value) in entries)
        {
            if (map.TryGet(key, out _))
            {
                return new ErrorValue($"insert failed: key {key.ToNative()} already exists");
            }

            pairs.Add(new(key, value));
        }

        return MapValue.Build(pairs);
    }

    private enum Kind
    {
        All,
        Exists,
        ExistsOne,
    }

    private static (string, string)? TwoVars(IMacroContext ctx, IReadOnlyList<Expr> args)
    {
        if (args[0] is not IdentExpr v1 || v1.Name.StartsWith('.')
            || args[1] is not IdentExpr v2 || v2.Name.StartsWith('.'))
        {
            return null;
        }

        if (v1.Name == v2.Name)
        {
            return null;
        }

        return (v1.Name, v2.Name);
    }

    private static Expr Quantifier(IMacroContext ctx, Expr target, IReadOnlyList<Expr> args, Kind kind)
    {
        if (TwoVars(ctx, args) is not { } vars)
        {
            return ctx.ReportError("iteration variables must be distinct simple identifiers");
        }

        var predicate = args[2];
        Expr init;
        Expr condition;
        Expr step;
        Expr result;
        switch (kind)
        {
            case Kind.All:
                init = ctx.NewConst(CelConstant.True);
                condition = ctx.NewGlobalCall(Common.Operators.NotStrictlyFalse, ctx.NewIdent(Accu));
                step = ctx.NewGlobalCall(Common.Operators.LogicalAnd, ctx.NewIdent(Accu), predicate);
                result = ctx.NewIdent(Accu);
                break;
            case Kind.Exists:
                init = ctx.NewConst(CelConstant.False);
                condition = ctx.NewGlobalCall(
                    Common.Operators.NotStrictlyFalse,
                    ctx.NewGlobalCall(Common.Operators.LogicalNot, ctx.NewIdent(Accu)));
                step = ctx.NewGlobalCall(Common.Operators.LogicalOr, ctx.NewIdent(Accu), predicate);
                result = ctx.NewIdent(Accu);
                break;
            default:
                init = ctx.NewConst(CelConstant.Of(0L));
                condition = ctx.NewConst(CelConstant.True);
                step = ctx.NewGlobalCall(
                    Common.Operators.Conditional,
                    predicate,
                    ctx.NewGlobalCall(Common.Operators.Add, ctx.NewIdent(Accu), ctx.NewConst(CelConstant.Of(1L))),
                    ctx.NewIdent(Accu));
                result = ctx.NewGlobalCall(Common.Operators.Equals, ctx.NewIdent(Accu), ctx.NewConst(CelConstant.Of(1L)));
                break;
        }

        return new ComprehensionExpr(ctx.NextId(), vars.Item1, vars.Item2, target, Accu, init, condition, step, result);
    }

    private static Expr TransformList(IMacroContext ctx, Expr target, Expr var1, Expr var2, Expr? filter, Expr transform)
    {
        if (TwoVars(ctx, [var1, var2]) is not { } vars)
        {
            return ctx.ReportError("iteration variables must be distinct simple identifiers");
        }

        Expr step = ctx.NewGlobalCall(Common.Operators.Add, ctx.NewIdent(Accu), ctx.NewList(transform));
        if (filter is not null)
        {
            step = ctx.NewGlobalCall(Common.Operators.Conditional, filter, step, ctx.NewIdent(Accu));
        }

        return new ComprehensionExpr(
            ctx.NextId(), vars.Item1, vars.Item2, target, Accu,
            ctx.NewList(),
            ctx.NewConst(CelConstant.True),
            step,
            ctx.NewIdent(Accu));
    }

    private static Expr TransformMap(IMacroContext ctx, Expr target, Expr var1, Expr var2, Expr? filter, Expr transform, bool entry)
    {
        if (TwoVars(ctx, [var1, var2]) is not { } vars)
        {
            return ctx.ReportError("iteration variables must be distinct simple identifiers");
        }

        // transformMap inserts (k, t); transformMapEntry merges the map produced by t.
        Expr step = entry
            ? ctx.NewGlobalCall("cel.@mapInsert", ctx.NewIdent(Accu), transform)
            : ctx.NewGlobalCall("cel.@mapInsert", ctx.NewIdent(Accu), ctx.NewIdent(vars.Item1), transform);
        if (filter is not null)
        {
            step = ctx.NewGlobalCall(Common.Operators.Conditional, filter, step, ctx.NewIdent(Accu));
        }

        return new ComprehensionExpr(
            ctx.NextId(), vars.Item1, vars.Item2, target, Accu,
            new MapExpr(ctx.NextId(), []),
            ctx.NewConst(CelConstant.True),
            step,
            ctx.NewIdent(Accu));
    }
}
