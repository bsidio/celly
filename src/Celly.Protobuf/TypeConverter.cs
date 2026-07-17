using Celly.Types;
using ProtoType = Cel.Expr.Type;

namespace Celly.Protobuf;

/// <summary>Converts the canonical <c>cel.expr.Type</c> proto to Celly's type model.</summary>
public static class TypeConverter
{
    public static CelType ToCelType(ProtoType type)
    {
        switch (type.TypeKindCase)
        {
            case ProtoType.TypeKindOneofCase.Dyn:
                return CelType.Dyn;
            case ProtoType.TypeKindOneofCase.Null:
                return CelType.Null;
            case ProtoType.TypeKindOneofCase.Primitive:
                return Primitive(type.Primitive);
            case ProtoType.TypeKindOneofCase.Wrapper:
                return CelType.Opaque("wrapper", Primitive(type.Wrapper));
            case ProtoType.TypeKindOneofCase.WellKnown:
                return type.WellKnown switch
                {
                    ProtoType.Types.WellKnownType.Timestamp => CelType.Timestamp,
                    ProtoType.Types.WellKnownType.Duration => CelType.Duration,
                    ProtoType.Types.WellKnownType.Any => CelType.Struct("google.protobuf.Any"),
                    _ => CelType.Dyn,
                };
            case ProtoType.TypeKindOneofCase.ListType:
                return CelType.List(ToCelType(type.ListType.ElemType));
            case ProtoType.TypeKindOneofCase.MapType:
                return CelType.Map(ToCelType(type.MapType.KeyType), ToCelType(type.MapType.ValueType));
            case ProtoType.TypeKindOneofCase.MessageType:
                // Well-known names (google.protobuf.Timestamp/Duration) map to their CEL types.
                return ValueConverter.TypeByName(type.MessageType);
            case ProtoType.TypeKindOneofCase.TypeParam:
                return CelType.TypeParam(type.TypeParam);
            case ProtoType.TypeKindOneofCase.Type_:
                return type.Type_.TypeKindCase == ProtoType.TypeKindOneofCase.None
                    ? CelType.TypeType
                    : new CelType(CelTypeKind.Type, "type", [ToCelType(type.Type_)]);
            case ProtoType.TypeKindOneofCase.Error:
                return CelType.Error;
            case ProtoType.TypeKindOneofCase.AbstractType:
                return CelType.Opaque(
                    type.AbstractType.Name,
                    [.. type.AbstractType.ParameterTypes.Select(ToCelType)]);
            default:
                return CelType.Dyn;
        }
    }

    private static CelType Primitive(ProtoType.Types.PrimitiveType primitive) => primitive switch
    {
        ProtoType.Types.PrimitiveType.Bool => CelType.Bool,
        ProtoType.Types.PrimitiveType.Int64 => CelType.Int,
        ProtoType.Types.PrimitiveType.Uint64 => CelType.Uint,
        ProtoType.Types.PrimitiveType.Double => CelType.Double,
        ProtoType.Types.PrimitiveType.String => CelType.String,
        ProtoType.Types.PrimitiveType.Bytes => CelType.Bytes,
        _ => CelType.Dyn,
    };
}
