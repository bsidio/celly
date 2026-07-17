using Celly.Types;
using Celly.Values;
using ProtoValue = Cel.Expr.Value;

namespace Celly.Protobuf;

/// <summary>
/// Converts between the canonical <c>cel.expr.Value</c> proto and Celly runtime values.
/// Message-typed values (object_value / enum_value) arrive with M5's ProtoTypeRegistry.
/// </summary>
public static class ValueConverter
{
    public static CelValue ToCelValue(ProtoValue value, ProtoTypeRegistry? registry = null)
    {
        switch (value.KindCase)
        {
            case ProtoValue.KindOneofCase.NullValue:
                return Celly.Values.NullValue.Instance;
            case ProtoValue.KindOneofCase.BoolValue:
                return Celly.Values.BoolValue.Of(value.BoolValue);
            case ProtoValue.KindOneofCase.Int64Value:
                return IntValue.Of(value.Int64Value);
            case ProtoValue.KindOneofCase.Uint64Value:
                return UintValue.Of(value.Uint64Value);
            case ProtoValue.KindOneofCase.DoubleValue:
                return DoubleValue.Of(value.DoubleValue);
            case ProtoValue.KindOneofCase.StringValue:
                return StringValue.Of(value.StringValue);
            case ProtoValue.KindOneofCase.BytesValue:
                return BytesValue.Of(value.BytesValue.ToByteArray());
            case ProtoValue.KindOneofCase.ListValue:
            {
                var elements = value.ListValue.Values.Select(v => ToCelValue(v, registry)).ToList();
                return Celly.Values.ListValue.Of(elements);
            }

            case ProtoValue.KindOneofCase.MapValue:
            {
                var pairs = value.MapValue.Entries.Select(e => new KeyValuePair<CelValue, CelValue>(
                    ToCelValue(e.Key, registry), ToCelValue(e.Value, registry)));
                return Celly.Values.MapValue.Build(pairs);
            }

            case ProtoValue.KindOneofCase.TypeValue:
                return new TypeValue(TypeByName(value.TypeValue));
            case ProtoValue.KindOneofCase.ObjectValue when registry is not null:
                return registry.UnpackAny(value.ObjectValue);
            case ProtoValue.KindOneofCase.ObjectValue when value.ObjectValue.TryUnpack<Google.Protobuf.WellKnownTypes.Timestamp>(out var ts):
                return TimestampValue.Of(ts.Seconds, ts.Nanos);
            case ProtoValue.KindOneofCase.ObjectValue when value.ObjectValue.TryUnpack<Google.Protobuf.WellKnownTypes.Duration>(out var dur):
                return DurationValue.Of(dur.Seconds, dur.Nanos);
            case ProtoValue.KindOneofCase.EnumValue:
                return IntValue.Of(value.EnumValue.Value);
            case ProtoValue.KindOneofCase.ObjectValue:
                throw new NotSupportedException("proto-typed conformance value requires a type registry");
            default:
                throw new NotSupportedException($"unsupported cel.expr.Value kind: {value.KindCase}");
        }
    }

    public static CelType TypeByName(string name) => name switch
    {
        "bool" => CelType.Bool,
        "int" => CelType.Int,
        "uint" => CelType.Uint,
        "double" => CelType.Double,
        "string" => CelType.String,
        "bytes" => CelType.Bytes,
        "list" => CelType.ListDyn,
        "map" => CelType.MapDyn,
        "null_type" => CelType.Null,
        "type" => CelType.TypeType,
        "google.protobuf.Timestamp" => CelType.Timestamp,
        "google.protobuf.Duration" => CelType.Duration,
        "optional_type" => CelType.OptionalDyn,
        _ => CelType.Struct(name),
    };
}
