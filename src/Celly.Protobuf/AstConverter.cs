using Celly.Ast;
using Celly.Checking;
using Celly.Common;
using Celly.Types;
using Google.Protobuf;
using ProtoExpr = Cel.Expr.Expr;
using ProtoType = Cel.Expr.Type;

namespace Celly.Protobuf;

/// <summary>
/// Lossless conversion between Celly's native AST and the canonical <c>cel.expr</c> protos
/// (<c>ParsedExpr</c>/<c>CheckedExpr</c>). Node ids, source positions, macro-call records,
/// deduced types, and resolved references all round-trip — so ASTs can be cached, transported,
/// or exchanged with other CEL implementations (cel-go services, policy stores, …).
/// </summary>
public static class AstConverter
{
    // ---- native → proto --------------------------------------------------------------------------

    public static Cel.Expr.ParsedExpr ToParsedExpr(CelAbstractSyntax ast) => new()
    {
        Expr = ToProto(ast.Expr),
        SourceInfo = ToProtoSourceInfo(ast.SourceInfo),
    };

    /// <summary>Requires a checked AST (env.Check must have succeeded).</summary>
    public static Cel.Expr.CheckedExpr ToCheckedExpr(CelAbstractSyntax ast)
    {
        if (!ast.IsChecked)
        {
            throw new InvalidOperationException("AST is not checked; call CelEnv.Check first");
        }

        var checkedExpr = new Cel.Expr.CheckedExpr
        {
            Expr = ToProto(ast.Expr),
            SourceInfo = ToProtoSourceInfo(ast.SourceInfo),
        };
        foreach (var (id, type) in ast.TypeMap!)
        {
            checkedExpr.TypeMap[id] = ToProtoType(type);
        }

        foreach (var (id, reference) in ast.ReferenceMap ?? new Dictionary<long, ReferenceInfo>())
        {
            var protoRef = new Cel.Expr.Reference();
            if (reference.Name is not null)
            {
                protoRef.Name = reference.Name;
            }

            protoRef.OverloadId.AddRange(reference.OverloadIds);
            checkedExpr.ReferenceMap[id] = protoRef;
        }

        return checkedExpr;
    }

    public static ProtoExpr ToProto(Expr expr)
    {
        var result = new ProtoExpr { Id = expr.Id };
        switch (expr)
        {
            case ConstExpr c:
                result.ConstExpr = ToProtoConstant(c.Value);
                break;
            case IdentExpr ident:
                result.IdentExpr = new ProtoExpr.Types.Ident { Name = ident.Name };
                break;
            case SelectExpr select:
                result.SelectExpr = new ProtoExpr.Types.Select
                {
                    Operand = ToProto(select.Operand),
                    Field = select.Field,
                    TestOnly = select.TestOnly,
                };
                break;
            case CallExpr call:
            {
                var protoCall = new ProtoExpr.Types.Call { Function = call.Function };
                if (call.Target is not null)
                {
                    protoCall.Target = ToProto(call.Target);
                }

                protoCall.Args.AddRange(call.Args.Select(ToProto));
                result.CallExpr = protoCall;
                break;
            }

            case ListExpr list:
            {
                var protoList = new ProtoExpr.Types.CreateList();
                protoList.Elements.AddRange(list.Elements.Select(ToProto));
                protoList.OptionalIndices.AddRange(list.OptionalIndices.Select(i => (int)i));
                result.ListExpr = protoList;
                break;
            }

            case MapExpr map:
            {
                var protoStruct = new ProtoExpr.Types.CreateStruct();
                protoStruct.Entries.AddRange(map.Entries.Select(e => new ProtoExpr.Types.CreateStruct.Types.Entry
                {
                    Id = e.Id,
                    MapKey = ToProto(e.Key),
                    Value = ToProto(e.Value),
                    OptionalEntry = e.Optional,
                }));
                result.StructExpr = protoStruct;
                break;
            }

            case StructExpr st:
            {
                var protoStruct = new ProtoExpr.Types.CreateStruct { MessageName = st.MessageName };
                protoStruct.Entries.AddRange(st.Fields.Select(f => new ProtoExpr.Types.CreateStruct.Types.Entry
                {
                    Id = f.Id,
                    FieldKey = f.Name,
                    Value = ToProto(f.Value),
                    OptionalEntry = f.Optional,
                }));
                result.StructExpr = protoStruct;
                break;
            }

            case ComprehensionExpr comp:
            {
                var protoComp = new ProtoExpr.Types.Comprehension
                {
                    IterVar = comp.IterVar,
                    IterRange = ToProto(comp.IterRange),
                    AccuVar = comp.AccuVar,
                    AccuInit = ToProto(comp.AccuInit),
                    LoopCondition = ToProto(comp.LoopCondition),
                    LoopStep = ToProto(comp.LoopStep),
                    Result = ToProto(comp.Result),
                };
                if (comp.IterVar2 is not null)
                {
                    protoComp.IterVar2 = comp.IterVar2;
                }

                result.ComprehensionExpr = protoComp;
                break;
            }
        }

        return result;
    }

    private static Cel.Expr.Constant ToProtoConstant(CelConstant constant) => constant.Kind switch
    {
        ConstantKind.Null => new Cel.Expr.Constant { NullValue = Google.Protobuf.WellKnownTypes.NullValue.NullValue },
        ConstantKind.Bool => new Cel.Expr.Constant { BoolValue = constant.BoolValue },
        ConstantKind.Int => new Cel.Expr.Constant { Int64Value = constant.IntValue },
        ConstantKind.Uint => new Cel.Expr.Constant { Uint64Value = constant.UintValue },
        ConstantKind.Double => new Cel.Expr.Constant { DoubleValue = constant.DoubleValue },
        ConstantKind.String => new Cel.Expr.Constant { StringValue = constant.StringValue },
        ConstantKind.Bytes => new Cel.Expr.Constant { BytesValue = ByteString.CopyFrom(constant.BytesValue) },
        _ => throw new InvalidOperationException($"unsupported constant kind {constant.Kind}"),
    };

    private static Cel.Expr.SourceInfo ToProtoSourceInfo(SourceInfo sourceInfo)
    {
        var proto = new Cel.Expr.SourceInfo
        {
            Location = sourceInfo.Source.Description,
            SyntaxVersion = "cel1",
        };
        foreach (var (id, offset) in sourceInfo.Positions)
        {
            proto.Positions[id] = offset;
        }

        foreach (var (id, call) in sourceInfo.MacroCalls)
        {
            proto.MacroCalls[id] = ToProto(call);
        }

        return proto;
    }

    /// <summary>Celly type → canonical cel.expr.Type (inverse of <see cref="TypeConverter"/>).</summary>
    public static ProtoType ToProtoType(CelType type)
    {
        switch (type.Kind)
        {
            case CelTypeKind.Dyn:
                return new ProtoType { Dyn = new Google.Protobuf.WellKnownTypes.Empty() };
            case CelTypeKind.Null:
                return new ProtoType { Null = Google.Protobuf.WellKnownTypes.NullValue.NullValue };
            case CelTypeKind.Bool:
                return new ProtoType { Primitive = ProtoType.Types.PrimitiveType.Bool };
            case CelTypeKind.Int:
                return new ProtoType { Primitive = ProtoType.Types.PrimitiveType.Int64 };
            case CelTypeKind.Uint:
                return new ProtoType { Primitive = ProtoType.Types.PrimitiveType.Uint64 };
            case CelTypeKind.Double:
                return new ProtoType { Primitive = ProtoType.Types.PrimitiveType.Double };
            case CelTypeKind.String:
                return new ProtoType { Primitive = ProtoType.Types.PrimitiveType.String };
            case CelTypeKind.Bytes:
                return new ProtoType { Primitive = ProtoType.Types.PrimitiveType.Bytes };
            case CelTypeKind.Timestamp:
                return new ProtoType { WellKnown = ProtoType.Types.WellKnownType.Timestamp };
            case CelTypeKind.Duration:
                return new ProtoType { WellKnown = ProtoType.Types.WellKnownType.Duration };
            case CelTypeKind.List:
                return new ProtoType { ListType = new ProtoType.Types.ListType { ElemType = ToProtoType(type.Parameters[0]) } };
            case CelTypeKind.Map:
                return new ProtoType
                {
                    MapType = new ProtoType.Types.MapType
                    {
                        KeyType = ToProtoType(type.Parameters[0]),
                        ValueType = ToProtoType(type.Parameters[1]),
                    },
                };
            case CelTypeKind.Struct:
                return new ProtoType { MessageType = type.Name };
            case CelTypeKind.TypeParam:
                return new ProtoType { TypeParam = type.Name };
            case CelTypeKind.Type:
                return new ProtoType
                {
                    Type_ = type.Parameters.Count == 1 ? ToProtoType(type.Parameters[0]) : new ProtoType(),
                };
            case CelTypeKind.Error:
                return new ProtoType { Error = new Google.Protobuf.WellKnownTypes.Empty() };
            case CelTypeKind.Opaque when TypeSubstitution.IsWrapper(type):
                return new ProtoType { Wrapper = ToProtoType(type.Parameters[0]).Primitive };
            case CelTypeKind.Opaque or CelTypeKind.Enum:
            {
                var abstractType = new ProtoType.Types.AbstractType { Name = type.Name };
                abstractType.ParameterTypes.AddRange(type.Parameters.Select(ToProtoType));
                return new ProtoType { AbstractType = abstractType };
            }

            default:
                return new ProtoType { Dyn = new Google.Protobuf.WellKnownTypes.Empty() };
        }
    }

    // ---- proto → native --------------------------------------------------------------------------

    public static CelAbstractSyntax FromParsedExpr(Cel.Expr.ParsedExpr parsed)
    {
        var source = Source.FromText(string.Empty, parsed.SourceInfo?.Location is { Length: > 0 } loc ? loc : "<proto>");
        var sourceInfo = new SourceInfo(source);
        if (parsed.SourceInfo is not null)
        {
            foreach (var (id, offset) in parsed.SourceInfo.Positions)
            {
                sourceInfo.SetPosition(id, offset);
            }

            foreach (var (id, call) in parsed.SourceInfo.MacroCalls)
            {
                sourceInfo.AddMacroCall(id, FromProto(call));
            }
        }

        return new CelAbstractSyntax(FromProto(parsed.Expr), sourceInfo);
    }

    /// <summary>Restores a checked AST — including the deduced-type and reference maps.</summary>
    public static CelAbstractSyntax FromCheckedExpr(Cel.Expr.CheckedExpr checkedExpr)
    {
        var ast = FromParsedExpr(new Cel.Expr.ParsedExpr
        {
            Expr = checkedExpr.Expr,
            SourceInfo = checkedExpr.SourceInfo,
        });
        var typeMap = new Dictionary<long, CelType>();
        foreach (var (id, type) in checkedExpr.TypeMap)
        {
            typeMap[id] = TypeConverter.ToCelType(type);
        }

        var referenceMap = new Dictionary<long, ReferenceInfo>();
        foreach (var (id, reference) in checkedExpr.ReferenceMap)
        {
            referenceMap[id] = new ReferenceInfo(
                reference.Name.Length == 0 ? null : reference.Name,
                [.. reference.OverloadId]);
        }

        ast.AnnotateChecked(typeMap, referenceMap);
        return ast;
    }

    public static Expr FromProto(ProtoExpr expr)
    {
        switch (expr.ExprKindCase)
        {
            case ProtoExpr.ExprKindOneofCase.ConstExpr:
                return new ConstExpr(expr.Id, FromProtoConstant(expr.ConstExpr));
            case ProtoExpr.ExprKindOneofCase.IdentExpr:
                return new IdentExpr(expr.Id, expr.IdentExpr.Name);
            case ProtoExpr.ExprKindOneofCase.SelectExpr:
                return new SelectExpr(
                    expr.Id,
                    FromProto(expr.SelectExpr.Operand),
                    expr.SelectExpr.Field,
                    expr.SelectExpr.TestOnly);
            case ProtoExpr.ExprKindOneofCase.CallExpr:
                return new CallExpr(
                    expr.Id,
                    expr.CallExpr.Target is null ? null : FromProto(expr.CallExpr.Target),
                    expr.CallExpr.Function,
                    [.. expr.CallExpr.Args.Select(FromProto)]);
            case ProtoExpr.ExprKindOneofCase.ListExpr:
                return new ListExpr(
                    expr.Id,
                    [.. expr.ListExpr.Elements.Select(FromProto)],
                    [.. expr.ListExpr.OptionalIndices]);
            case ProtoExpr.ExprKindOneofCase.StructExpr when expr.StructExpr.MessageName.Length == 0:
                return new MapExpr(
                    expr.Id,
                    [
                        .. expr.StructExpr.Entries.Select(e =>
                            new MapEntry(e.Id, FromProto(e.MapKey), FromProto(e.Value), e.OptionalEntry)),
                    ]);
            case ProtoExpr.ExprKindOneofCase.StructExpr:
                return new StructExpr(
                    expr.Id,
                    expr.StructExpr.MessageName,
                    [
                        .. expr.StructExpr.Entries.Select(e =>
                            new StructField(e.Id, e.FieldKey, FromProto(e.Value), e.OptionalEntry)),
                    ]);
            case ProtoExpr.ExprKindOneofCase.ComprehensionExpr:
            {
                var comp = expr.ComprehensionExpr;
                return new ComprehensionExpr(
                    expr.Id,
                    comp.IterVar,
                    comp.IterVar2.Length == 0 ? null : comp.IterVar2,
                    FromProto(comp.IterRange),
                    comp.AccuVar,
                    FromProto(comp.AccuInit),
                    FromProto(comp.LoopCondition),
                    FromProto(comp.LoopStep),
                    FromProto(comp.Result));
            }

            default:
                return Expr.Unspecified;
        }
    }

    private static CelConstant FromProtoConstant(Cel.Expr.Constant constant) => constant.ConstantKindCase switch
    {
        Cel.Expr.Constant.ConstantKindOneofCase.NullValue => CelConstant.Null,
        Cel.Expr.Constant.ConstantKindOneofCase.BoolValue => CelConstant.Of(constant.BoolValue),
        Cel.Expr.Constant.ConstantKindOneofCase.Int64Value => CelConstant.Of(constant.Int64Value),
        Cel.Expr.Constant.ConstantKindOneofCase.Uint64Value => CelConstant.OfUint(constant.Uint64Value),
        Cel.Expr.Constant.ConstantKindOneofCase.DoubleValue => CelConstant.Of(constant.DoubleValue),
        Cel.Expr.Constant.ConstantKindOneofCase.StringValue => CelConstant.Of(constant.StringValue),
        Cel.Expr.Constant.ConstantKindOneofCase.BytesValue => CelConstant.Of(constant.BytesValue.ToByteArray()),
        _ => throw new InvalidOperationException($"unsupported constant kind {constant.ConstantKindCase}"),
    };
}
