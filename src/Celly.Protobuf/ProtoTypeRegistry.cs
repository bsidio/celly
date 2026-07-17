using Celly.Providers;
using Celly.Types;
using Celly.Values;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Wkt = Google.Protobuf.WellKnownTypes;

namespace Celly.Protobuf;

/// <summary>
/// Protobuf-backed <see cref="ITypeProvider"/>/<see cref="ITypeAdapter"/>: message construction,
/// field types, enum constants, well-known-type unwrapping, and Any packing/unpacking.
/// </summary>
public sealed class ProtoTypeRegistry : ITypeProvider, ITypeAdapter
{
    private readonly Dictionary<string, MessageDescriptor> _messages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CelValue> _idents = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Message, string Extension), FieldDescriptor> _extensions = [];
    private readonly List<FileDescriptor> _files = [];
    private TypeRegistry _anyRegistry = TypeRegistry.Empty;
    private ExtensionRegistry _extensionRegistry = [];

    public static ProtoTypeRegistry FromFiles(params FileDescriptor[] files)
    {
        var registry = new ProtoTypeRegistry();
        // Well-known types are always available.
        registry.RegisterFile(Wkt.Timestamp.Descriptor.File);
        registry.RegisterFile(Wkt.Duration.Descriptor.File);
        registry.RegisterFile(Wkt.Struct.Descriptor.File);
        registry.RegisterFile(Wkt.Int32Value.Descriptor.File);
        registry.RegisterFile(Wkt.Any.Descriptor.File);
        registry.RegisterFile(Wkt.FieldMask.Descriptor.File);
        registry.RegisterFile(Wkt.Empty.Descriptor.File);
        foreach (var file in files)
        {
            registry.RegisterFile(file);
        }

        registry._anyRegistry = TypeRegistry.FromFiles(registry._files);
        var extensions = new ExtensionRegistry();
        foreach (var field in registry._extensions.Values)
        {
            if (field.Extension is not null)
            {
                extensions.Add(field.Extension);
            }
        }

        registry._extensionRegistry = extensions;
        return registry;
    }

    private void RegisterFile(FileDescriptor file)
    {
        if (_files.Contains(file))
        {
            return;
        }

        _files.Add(file);
        foreach (var dependency in file.Dependencies)
        {
            RegisterFile(dependency);
        }

        foreach (var message in file.MessageTypes)
        {
            RegisterMessage(message);
        }

        foreach (var enumType in file.EnumTypes)
        {
            RegisterEnum(enumType);
        }

        foreach (var extension in file.Extensions.UnorderedExtensions)
        {
            _extensions[(extension.ExtendeeType.FullName, extension.FullName)] = extension;
        }
    }

    private void RegisterMessage(MessageDescriptor message)
    {
        _messages[message.FullName] = message;
        _idents[message.FullName] = new TypeValue(StructTypeFor(message.FullName) ?? CelType.Struct(message.FullName));
        foreach (var nested in message.NestedTypes)
        {
            RegisterMessage(nested);
        }

        foreach (var nestedEnum in message.EnumTypes)
        {
            RegisterEnum(nestedEnum);
        }

        foreach (var extension in message.Extensions.UnorderedExtensions)
        {
            _extensions[(extension.ExtendeeType.FullName, extension.FullName)] = extension;
        }
    }

    /// <summary>Finds a proto2 extension field of a message by its fully-qualified extension name.</summary>
    public FieldDescriptor? FindExtension(string messageName, string extensionName) =>
        _extensions.GetValueOrDefault((messageName, extensionName));

    private void RegisterEnum(EnumDescriptor enumType)
    {
        // The enum type name itself is a type ident (CEL enums are ints).
        _idents[enumType.FullName] = new TypeValue(CelType.Int);
        foreach (var value in enumType.Values)
        {
            _idents[$"{enumType.FullName}.{value.Name}"] = IntValue.Of(value.Number);
        }
    }

    // ---- ITypeProvider ---------------------------------------------------------------------------

    public CelType? FindStructType(string name) => StructTypeFor(name);

    private CelType? StructTypeFor(string name)
    {
        switch (name)
        {
            case "google.protobuf.Int32Value" or "google.protobuf.Int64Value":
                return CelType.Opaque("wrapper", CelType.Int);
            case "google.protobuf.UInt32Value" or "google.protobuf.UInt64Value":
                return CelType.Opaque("wrapper", CelType.Uint);
            case "google.protobuf.FloatValue" or "google.protobuf.DoubleValue":
                return CelType.Opaque("wrapper", CelType.Double);
            case "google.protobuf.BoolValue":
                return CelType.Opaque("wrapper", CelType.Bool);
            case "google.protobuf.StringValue":
                return CelType.Opaque("wrapper", CelType.String);
            case "google.protobuf.BytesValue":
                return CelType.Opaque("wrapper", CelType.Bytes);
            case "google.protobuf.Timestamp":
                return CelType.Timestamp;
            case "google.protobuf.Duration":
                return CelType.Duration;
            case "google.protobuf.Struct":
                return CelType.Map(CelType.String, CelType.Dyn);
            case "google.protobuf.Value":
            case "google.protobuf.Any":
                return CelType.Dyn;
            case "google.protobuf.ListValue":
                return CelType.List(CelType.Dyn);
            default:
                return _messages.ContainsKey(name) ? CelType.Struct(name) : null;
        }
    }

    public CelType? FindStructFieldType(string messageName, string fieldName)
    {
        if (!_messages.TryGetValue(messageName, out var descriptor))
        {
            return null;
        }

        var field = descriptor.FindFieldByName(fieldName)
            ?? FindExtension(messageName, fieldName);
        return field is null ? null : FieldCelType(field);
    }

    private CelType FieldCelType(FieldDescriptor field)
    {
        if (field.IsMap)
        {
            var kv = field.MessageType;
            return CelType.Map(SingularCelType(kv.Fields[1]), SingularCelType(kv.Fields[2]));
        }

        if (field.IsRepeated)
        {
            return CelType.List(SingularCelType(field));
        }

        return SingularCelType(field);
    }

    private CelType SingularCelType(FieldDescriptor field) => field.FieldType switch
    {
        FieldType.Int32 or FieldType.SInt32 or FieldType.SFixed32
            or FieldType.Int64 or FieldType.SInt64 or FieldType.SFixed64 => CelType.Int,
        FieldType.UInt32 or FieldType.Fixed32 or FieldType.UInt64 or FieldType.Fixed64 => CelType.Uint,
        FieldType.Float or FieldType.Double => CelType.Double,
        FieldType.Bool => CelType.Bool,
        FieldType.String => CelType.String,
        FieldType.Bytes => CelType.Bytes,
        FieldType.Enum => CelType.Int,
        FieldType.Message or FieldType.Group =>
            StructTypeFor(field.MessageType.FullName) ?? CelType.Struct(field.MessageType.FullName),
        _ => CelType.Dyn,
    };

    public CelValue? FindIdent(string name) => _idents.GetValueOrDefault(name);

    // ---- construction ----------------------------------------------------------------------------

    public CelValue NewValue(string messageName, IReadOnlyList<KeyValuePair<string, CelValue>> fields)
    {
        if (!_messages.TryGetValue(messageName, out var descriptor))
        {
            return new ErrorValue($"unknown type: '{messageName}'");
        }

        var message = descriptor.Parser.ParseFrom(ReadOnlySpan<byte>.Empty);
        foreach (var (name, value) in fields)
        {
            var field = descriptor.FindFieldByName(name);
            if (field is null)
            {
                return new ErrorValue($"no_such_field '{name}'");
            }

            var error = SetField(message, field, value);
            if (error is not null)
            {
                return error;
            }
        }

        // Constructing an Any without a type_url is an error (adaption of pre-existing empty
        // Any data falls back to bytewise semantics instead).
        if (message is Wkt.Any { TypeUrl.Length: 0 })
        {
            return new ErrorValue("conversion error: invalid empty Any");
        }

        // Well-known types collapse to their CEL representation on construction.
        return AdaptMessage(message);
    }

    private ErrorValue? SetField(IMessage message, FieldDescriptor field, CelValue value)
    {
        if (field.IsMap)
        {
            if (value is not MapValue map)
            {
                return ErrorValue.NoSuchOverload(); // includes null: not assignable to map fields
            }

            var target = (System.Collections.IDictionary)field.Accessor.GetValue(message);
            var kv = field.MessageType;
            foreach (var key in map.Keys)
            {
                map.TryGet(key, out var entryValue);
                if (entryValue is NullValue && PrunesNull(kv.Fields[2]))
                {
                    continue; // null entries for message-kind values are pruned
                }

                if (Convert(key, kv.Fields[1], out var protoKey) is { } keyError)
                {
                    return keyError;
                }

                if (Convert(entryValue, kv.Fields[2], out var protoValue) is { } valueError)
                {
                    return valueError;
                }

                target[protoKey!] = protoValue;
            }

            return null;
        }

        if (field.IsRepeated)
        {
            if (value is not ListValue list)
            {
                return ErrorValue.NoSuchOverload(); // includes null: not assignable to repeated fields
            }

            var target = (System.Collections.IList)field.Accessor.GetValue(message);
            foreach (var element in list.Elements)
            {
                if (element is NullValue && PrunesNull(field))
                {
                    continue; // null elements for message-kind types are pruned
                }

                if (Convert(element, field, out var converted) is { } error)
                {
                    return error;
                }

                target.Add(converted);
            }

            return null;
        }

        // Singular: null leaves message-kind fields unset (google.protobuf.Value stores null; the
        // map/list-mapped WKTs Struct and ListValue reject null like their CEL types do).
        if (value is NullValue && field.FieldType is FieldType.Message or FieldType.Group
            && field.MessageType.FullName is not ("google.protobuf.Value" or "google.protobuf.Struct" or "google.protobuf.ListValue"))
        {
            return null;
        }

        if (Convert(value, field, out var singular) is { } singularError)
        {
            return singularError;
        }

        field.Accessor.SetValue(message, singular);
        return null;
    }

    /// <summary>Converts a CEL value for a proto field slot; returns the error or null on success.</summary>
    private ErrorValue? Convert(CelValue value, FieldDescriptor field, out object? result)
    {
        result = null;
        switch (field.FieldType)
        {
            case FieldType.Int32 or FieldType.SInt32 or FieldType.SFixed32:
                if (value is not IntValue i32)
                {
                    return ErrorValue.NoSuchOverload();
                }

                if (i32.Value is < int.MinValue or > int.MaxValue)
                {
                    return new ErrorValue("range error");
                }

                result = (int)i32.Value;
                return null;
            case FieldType.Int64 or FieldType.SInt64 or FieldType.SFixed64:
                if (value is not IntValue i64)
                {
                    return ErrorValue.NoSuchOverload();
                }

                result = i64.Value;
                return null;
            case FieldType.UInt32 or FieldType.Fixed32:
                if (value is not UintValue u32)
                {
                    return ErrorValue.NoSuchOverload();
                }

                if (u32.Value > uint.MaxValue)
                {
                    return new ErrorValue("range error");
                }

                result = (uint)u32.Value;
                return null;
            case FieldType.UInt64 or FieldType.Fixed64:
                if (value is not UintValue u64)
                {
                    return ErrorValue.NoSuchOverload();
                }

                result = u64.Value;
                return null;
            case FieldType.Float:
                if (value is not DoubleValue f)
                {
                    return ErrorValue.NoSuchOverload();
                }

                result = (float)f.Value;
                return null;
            case FieldType.Double:
                if (value is not DoubleValue d)
                {
                    return ErrorValue.NoSuchOverload();
                }

                result = d.Value;
                return null;
            case FieldType.Bool:
                if (value is not BoolValue b)
                {
                    return ErrorValue.NoSuchOverload();
                }

                result = b.Value;
                return null;
            case FieldType.String:
                if (value is not StringValue s)
                {
                    return ErrorValue.NoSuchOverload();
                }

                result = s.Value;
                return null;
            case FieldType.Bytes:
                if (value is not BytesValue by)
                {
                    return ErrorValue.NoSuchOverload();
                }

                result = ByteString.CopyFrom(by.Span);
                return null;
            case FieldType.Enum:
            {
                if (value is not IntValue enumValue)
                {
                    return ErrorValue.NoSuchOverload();
                }

                if (enumValue.Value is < int.MinValue or > int.MaxValue)
                {
                    return new ErrorValue("range error");
                }

                var number = (int)enumValue.Value;
                // Closed (proto2) enums reject undefined values.
                var syntax = field.EnumType.File.ToProto().Syntax;
                if ((string.IsNullOrEmpty(syntax) || syntax == "proto2")
                    && field.EnumType.FindValueByNumber(number) is null)
                {
                    return new ErrorValue("range error");
                }

                result = Enum.ToObject(field.EnumType.ClrType, number);
                return null;
            }

            case FieldType.Message or FieldType.Group:
            {
                var converted = ConvertMessage(value, field.MessageType);
                if (converted is null)
                {
                    return ErrorValue.NoSuchOverload();
                }

                if (converted is ErrorValue error)
                {
                    return error;
                }

                result = converted.ToNative();
                return null;
            }

            default:
                return ErrorValue.NoSuchOverload();
        }
    }

    /// <summary>Wraps an IMessage so it can travel through CelValue-returning helpers.</summary>
    private sealed class RawMessage(IMessage message) : CelValue
    {
        public override CelType Type => CelType.Dyn;

        public override bool EqualTo(CelValue other) => false;

        public override object ToNative() => message;
    }

    /// <summary>
    /// Null assignments prune (rather than error) for message-kind element types — except Any and
    /// Value, which represent null natively and retain it.
    /// </summary>
    private static bool PrunesNull(FieldDescriptor field) =>
        field.FieldType is FieldType.Message or FieldType.Group
        && field.MessageType.FullName is not (
            "google.protobuf.Struct" or "google.protobuf.ListValue"
            or "google.protobuf.Any" or "google.protobuf.Value");

    /// <summary>Boxes a plain CLR value (wrapper fields map to nullable primitives in C#).</summary>
    private sealed class RawScalar(object value) : CelValue
    {
        public override CelType Type => CelType.Dyn;

        public override bool EqualTo(CelValue other) => false;

        public override object ToNative() => value;
    }

    private CelValue? ConvertMessage(CelValue value, MessageDescriptor target)
    {
        switch (target.FullName)
        {
            // C# codegen maps wrapper-typed fields to nullable primitives, so the accessor
            // expects the boxed primitive, not a wrapper message.
            case "google.protobuf.Int32Value":
                return value is IntValue wi32
                    ? wi32.Value is >= int.MinValue and <= int.MaxValue
                        ? new RawScalar((int)wi32.Value)
                        : new ErrorValue("range error")
                    : null;
            case "google.protobuf.Int64Value":
                return value is IntValue wi64 ? new RawScalar(wi64.Value) : null;
            case "google.protobuf.UInt32Value":
                return value is UintValue wu32
                    ? wu32.Value <= uint.MaxValue
                        ? new RawScalar((uint)wu32.Value)
                        : new ErrorValue("range error")
                    : null;
            case "google.protobuf.UInt64Value":
                return value is UintValue wu64 ? new RawScalar(wu64.Value) : null;
            case "google.protobuf.FloatValue":
                return value is DoubleValue wf ? new RawScalar((float)wf.Value) : null;
            case "google.protobuf.DoubleValue":
                return value is DoubleValue wd ? new RawScalar(wd.Value) : null;
            case "google.protobuf.BoolValue":
                return value is BoolValue wb ? new RawScalar(wb.Value) : null;
            case "google.protobuf.StringValue":
                return value is StringValue ws ? new RawScalar(ws.Value) : null;
            case "google.protobuf.BytesValue":
                return value is BytesValue wby ? new RawScalar(ByteString.CopyFrom(wby.Span)) : null;
            case "google.protobuf.Timestamp":
                return value is TimestampValue ts
                    ? new RawMessage(new Wkt.Timestamp { Seconds = ts.Data.Seconds, Nanos = ts.Data.Nanos })
                    : null;
            case "google.protobuf.Duration":
                return value is DurationValue dur
                    ? new RawMessage(new Wkt.Duration { Seconds = dur.Data.Seconds, Nanos = dur.Data.Nanos })
                    : null;
            case "google.protobuf.Struct":
                return ToProtoStruct(value);
            case "google.protobuf.Value":
                return ToProtoValue(value);
            case "google.protobuf.ListValue":
                return ToProtoListValue(value);
            case "google.protobuf.Any":
                return ToProtoAny(value);
            default:
                // No clone needed: CEL values are immutable after construction.
                return value is ProtoMessageValue msg && msg.Descriptor.FullName == target.FullName
                    ? new RawMessage(msg.Message)
                    : null;
        }
    }

    private CelValue? ToProtoStruct(CelValue value)
    {
        if (value is not MapValue map)
        {
            return null;
        }

        var result = new Wkt.Struct();
        foreach (var key in map.Keys)
        {
            if (key is not StringValue keyString)
            {
                return new ErrorValue("bad key type");
            }

            map.TryGet(key, out var entryValue);
            var converted = ToProtoValue(entryValue);
            if (converted is not RawMessage raw)
            {
                return converted ?? ErrorValue.NoSuchOverload();
            }

            result.Fields[keyString.Value] = (Wkt.Value)raw.ToNative();
        }

        return new RawMessage(result);
    }

    private CelValue? ToProtoValue(CelValue value)
    {
        const long maxJsonInt = 9007199254740991; // 2^53 - 1: JSON-safe integer range
        switch (value)
        {
            case NullValue:
                return new RawMessage(Wkt.Value.ForNull());
            case BoolValue b:
                return new RawMessage(Wkt.Value.ForBool(b.Value));
            // Proto3 JSON: int64/uint64 beyond the double-safe range render as strings.
            case IntValue i:
                return new RawMessage(Math.Abs(i.Value) <= maxJsonInt
                    ? Wkt.Value.ForNumber(i.Value)
                    : Wkt.Value.ForString(i.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            case UintValue u:
                return new RawMessage(u.Value <= maxJsonInt
                    ? Wkt.Value.ForNumber(u.Value)
                    : Wkt.Value.ForString(u.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            case DoubleValue d:
                return new RawMessage(Wkt.Value.ForNumber(d.Value));
            case StringValue s:
                return new RawMessage(Wkt.Value.ForString(s.Value));
            case BytesValue by:
                // JSON mapping: bytes encode as base64 strings.
                return new RawMessage(Wkt.Value.ForString(System.Convert.ToBase64String(by.Span)));
            case TimestampValue ts:
                // JSON mapping: RFC 3339 string.
                return new RawMessage(Wkt.Value.ForString(Stdlib.Rfc3339.Format(ts.Data)));
            case DurationValue dur:
                // JSON mapping: decimal seconds + "s".
                return new RawMessage(Wkt.Value.ForString(Stdlib.TimeFunctions.FormatDuration(dur.Data)));
            case ProtoMessageValue { Message: Wkt.FieldMask mask }:
                // JSON mapping: comma-joined paths.
                return new RawMessage(Wkt.Value.ForString(string.Join(",", mask.Paths)));
            case ProtoMessageValue { Message: Wkt.Empty }:
                // JSON mapping: empty object.
                return new RawMessage(new Wkt.Value { StructValue = new Wkt.Struct() });
            case ListValue list:
            {
                var converted = ToProtoListValue(list);
                return converted is RawMessage raw
                    ? new RawMessage(new Wkt.Value { ListValue = (Wkt.ListValue)raw.ToNative() })
                    : converted;
            }

            case MapValue map:
            {
                var converted = ToProtoStruct(map);
                return converted is RawMessage raw
                    ? new RawMessage(new Wkt.Value { StructValue = (Wkt.Struct)raw.ToNative() })
                    : converted;
            }

            default:
                return null;
        }
    }

    private CelValue? ToProtoListValue(CelValue value)
    {
        if (value is not ListValue list)
        {
            return null;
        }

        var result = new Wkt.ListValue();
        foreach (var element in list.Elements)
        {
            var converted = ToProtoValue(element);
            if (converted is not RawMessage raw)
            {
                return converted ?? ErrorValue.NoSuchOverload();
            }

            result.Values.Add((Wkt.Value)raw.ToNative());
        }

        return new RawMessage(result);
    }

    private CelValue? ToProtoAny(CelValue value)
    {
        IMessage? packed = value switch
        {
            ProtoMessageValue { Message: Wkt.Any any } => any,
            ProtoMessageValue msg => Wkt.Any.Pack(msg.Message),
            IntValue i => Wkt.Any.Pack(new Wkt.Int64Value { Value = i.Value }),
            UintValue u => Wkt.Any.Pack(new Wkt.UInt64Value { Value = u.Value }),
            DoubleValue d => Wkt.Any.Pack(new Wkt.DoubleValue { Value = d.Value }),
            BoolValue b => Wkt.Any.Pack(new Wkt.BoolValue { Value = b.Value }),
            StringValue s => Wkt.Any.Pack(new Wkt.StringValue { Value = s.Value }),
            BytesValue by => Wkt.Any.Pack(new Wkt.BytesValue { Value = ByteString.CopyFrom(by.Span) }),
            TimestampValue ts => Wkt.Any.Pack(new Wkt.Timestamp { Seconds = ts.Data.Seconds, Nanos = ts.Data.Nanos }),
            DurationValue dur => Wkt.Any.Pack(new Wkt.Duration { Seconds = dur.Data.Seconds, Nanos = dur.Data.Nanos }),
            ListValue list when ToProtoListValue(list) is RawMessage raw => Wkt.Any.Pack((IMessage)raw.ToNative()),
            MapValue map when ToProtoStruct(map) is RawMessage raw => Wkt.Any.Pack((IMessage)raw.ToNative()),
            NullValue => Wkt.Any.Pack(Wkt.Value.ForNull()),
            _ => null,
        };
        return packed is null ? null : new RawMessage(packed);
    }

    // ---- adaption (proto → CEL) --------------------------------------------------------------------

    public CelValue NativeToValue(object? value) => value switch
    {
        IMessage message => AdaptMessage(message),
        _ => NativeTypeAdapter.Instance.NativeToValue(value),
    };

    public CelValue AdaptMessage(IMessage message)
    {
        switch (message)
        {
            case Wkt.Int32Value v:
                return IntValue.Of(v.Value);
            case Wkt.Int64Value v:
                return IntValue.Of(v.Value);
            case Wkt.UInt32Value v:
                return UintValue.Of(v.Value);
            case Wkt.UInt64Value v:
                return UintValue.Of(v.Value);
            case Wkt.FloatValue v:
                return DoubleValue.Of(v.Value);
            case Wkt.DoubleValue v:
                return DoubleValue.Of(v.Value);
            case Wkt.BoolValue v:
                return BoolValue.Of(v.Value);
            case Wkt.StringValue v:
                return StringValue.Of(v.Value);
            case Wkt.BytesValue v:
                return BytesValue.Of(v.Value.ToByteArray());
            case Wkt.Timestamp ts:
                return TimestampValue.Of(ts.Seconds, ts.Nanos);
            case Wkt.Duration dur:
                return DurationValue.Of(dur.Seconds, dur.Nanos);
            case Wkt.Struct st:
            {
                var pairs = st.Fields.Select(kv => new KeyValuePair<CelValue, CelValue>(
                    StringValue.Of(kv.Key), AdaptMessage(kv.Value)));
                return MapValue.Build(pairs);
            }

            case Wkt.Value v:
                return v.KindCase switch
                {
                    Wkt.Value.KindOneofCase.NullValue or Wkt.Value.KindOneofCase.None => NullValue.Instance,
                    Wkt.Value.KindOneofCase.NumberValue => DoubleValue.Of(v.NumberValue),
                    Wkt.Value.KindOneofCase.StringValue => StringValue.Of(v.StringValue),
                    Wkt.Value.KindOneofCase.BoolValue => BoolValue.Of(v.BoolValue),
                    Wkt.Value.KindOneofCase.StructValue => AdaptMessage(v.StructValue),
                    Wkt.Value.KindOneofCase.ListValue => AdaptMessage(v.ListValue),
                    _ => new ErrorValue("unsupported google.protobuf.Value"),
                };
            case Wkt.ListValue list:
                return ListValue.Of([.. list.Values.Select(v => AdaptMessage(v))]);
            case Wkt.Any any:
                return UnpackAny(any);
            default:
                return new ProtoMessageValue(message, this);
        }
    }

    public CelValue UnpackAny(Wkt.Any any)
    {
        // Unresolvable or malformed Any values stay wrapped: equality then compares the raw
        // type_url + value bytes (the conformance "bytewise fallback").
        if (any.TypeUrl.Length == 0)
        {
            return new ProtoMessageValue(any, this);
        }

        var typeName = Wkt.Any.GetTypeName(any.TypeUrl);
        if (!_messages.TryGetValue(typeName, out var descriptor))
        {
            return new ProtoMessageValue(any, this);
        }

        try
        {
            // Parse with the extension registry so proto2 extension fields survive unpacking.
            var parser = descriptor.Parser.WithExtensionRegistry(_extensionRegistry);
            return AdaptMessage(parser.ParseFrom(any.Value));
        }
        catch (InvalidProtocolBufferException)
        {
            return new ProtoMessageValue(any, this);
        }
    }

    /// <summary>Adapts one field of a message, with well-known-type and presence semantics.</summary>
    public CelValue AdaptField(IMessage message, FieldDescriptor field)
    {
        if (field.IsMap)
        {
            var dict = (System.Collections.IDictionary)field.Accessor.GetValue(message);
            var kv = field.MessageType;
            var pairs = new List<KeyValuePair<CelValue, CelValue>>();
            foreach (System.Collections.DictionaryEntry entry in dict)
            {
                pairs.Add(new(AdaptScalar(entry.Key, kv.Fields[1]), AdaptScalar(entry.Value, kv.Fields[2])));
            }

            return MapValue.Build(pairs);
        }

        if (field.IsRepeated)
        {
            var list = (System.Collections.IList)field.Accessor.GetValue(message);
            var elements = new List<CelValue>(list.Count);
            foreach (var item in list)
            {
                elements.Add(AdaptScalar(item, field));
            }

            return ListValue.Of(elements);
        }

        if (field.FieldType is FieldType.Message or FieldType.Group)
        {
            var raw = field.Accessor.GetValue(message);
            if (raw is null)
            {
                // Unset message-typed fields: wrappers, Value, and Any read as null; other
                // messages read as their default (empty) instance.
                var typeName = field.MessageType.FullName;
                if (IsNullWhenUnset(typeName))
                {
                    return NullValue.Instance;
                }

                return AdaptMessage(field.MessageType.Parser.ParseFrom(ReadOnlySpan<byte>.Empty));
            }

            // Wrapper fields surface as boxed CLR primitives via reflection, not messages.
            if (raw is not IMessage rawMessage)
            {
                return NativeToValue(raw);
            }

            return AdaptMessage(rawMessage);
        }

        return AdaptScalar(field.Accessor.GetValue(message), field);
    }

    private static bool IsNullWhenUnset(string typeName) => typeName is
        "google.protobuf.Int32Value" or "google.protobuf.Int64Value"
        or "google.protobuf.UInt32Value" or "google.protobuf.UInt64Value"
        or "google.protobuf.FloatValue" or "google.protobuf.DoubleValue"
        or "google.protobuf.BoolValue" or "google.protobuf.StringValue"
        or "google.protobuf.BytesValue" or "google.protobuf.Value"
        or "google.protobuf.Any";

    private CelValue AdaptScalar(object? raw, FieldDescriptor field) => field.FieldType switch
    {
        FieldType.Int32 or FieldType.SInt32 or FieldType.SFixed32 => IntValue.Of((int)raw!),
        FieldType.Int64 or FieldType.SInt64 or FieldType.SFixed64 => IntValue.Of((long)raw!),
        FieldType.UInt32 or FieldType.Fixed32 => UintValue.Of((uint)raw!),
        FieldType.UInt64 or FieldType.Fixed64 => UintValue.Of((ulong)raw!),
        FieldType.Float => DoubleValue.Of((float)raw!),
        FieldType.Double => DoubleValue.Of((double)raw!),
        FieldType.Bool => BoolValue.Of((bool)raw!),
        FieldType.String => StringValue.Of((string)raw!),
        FieldType.Bytes => BytesValue.Of(((ByteString)raw!).ToByteArray()),
        FieldType.Enum => IntValue.Of(System.Convert.ToInt32(raw, System.Globalization.CultureInfo.InvariantCulture)),
        // Wrapper-typed elements surface as boxed primitives (or null) through reflection.
        FieldType.Message or FieldType.Group => raw switch
        {
            null => NullValue.Instance,
            IMessage m => AdaptMessage(m),
            _ => NativeToValue(raw),
        },
        _ => new ErrorValue($"unsupported field type {field.FieldType}"),
    };
}
