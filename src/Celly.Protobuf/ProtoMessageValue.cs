using Celly.Providers;
using Celly.Types;
using Celly.Values;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Celly.Protobuf;

/// <summary>
/// A protobuf message as a CEL value. Field access adapts lazily through the registry; equality
/// is field-wise CEL equality (presence-aware, IEEE NaN semantics — deliberately NOT proto.Equal,
/// which treats NaN bitwise).
/// </summary>
public sealed class ProtoMessageValue : CelValue, IStructValue, IZeroTester
{
    /// <summary>All-fields-unset test (proto3 defaults serialize to nothing).</summary>
    public bool IsZeroValue() => Message.CalculateSize() == 0;

    private readonly ProtoTypeRegistry _registry;

    public ProtoMessageValue(IMessage message, ProtoTypeRegistry registry)
    {
        Message = message;
        _registry = registry;
    }

    public IMessage Message { get; }

    public MessageDescriptor Descriptor => Message.Descriptor;

    public override CelType Type => CelType.Struct(Descriptor.FullName);

    public override object ToNative() => Message;

    public override bool EqualTo(CelValue other)
    {
        if (other is not ProtoMessageValue msg || msg.Descriptor.FullName != Descriptor.FullName)
        {
            return false;
        }

        foreach (var field in Descriptor.Fields.InDeclarationOrder())
        {
            if (field.HasPresence)
            {
                var hasA = field.Accessor.HasValue(Message);
                var hasB = field.Accessor.HasValue(msg.Message);
                if (hasA != hasB)
                {
                    return false;
                }

                if (!hasA)
                {
                    continue;
                }
            }

            var a = _registry.AdaptField(Message, field);
            var b = _registry.AdaptField(msg.Message, field);
            if (a is ErrorValue || b is ErrorValue || !a.EqualTo(b))
            {
                return false;
            }
        }

        return true;
    }

    private FieldDescriptor? FindField(string name) =>
        Descriptor.FindFieldByName(name) ?? _registry.FindExtension(Descriptor.FullName, name);

    public CelValue GetField(string name)
    {
        var field = FindField(name);
        if (field is null)
        {
            return new ErrorValue($"no_such_field '{name}'");
        }

        return _registry.AdaptField(Message, field);
    }

    public CelValue HasField(string name)
    {
        var field = FindField(name);
        if (field is null)
        {
            return new ErrorValue($"no_such_field '{name}'");
        }

        if (field.IsMap)
        {
            return BoolValue.Of(((System.Collections.IDictionary)field.Accessor.GetValue(Message)).Count > 0);
        }

        if (field.IsRepeated)
        {
            return BoolValue.Of(((System.Collections.IList)field.Accessor.GetValue(Message)).Count > 0);
        }

        if (field.HasPresence)
        {
            return BoolValue.Of(field.Accessor.HasValue(Message));
        }

        // proto3 implicit-presence scalars: set means non-default.
        var value = _registry.AdaptField(Message, field);
        return BoolValue.Of(value switch
        {
            IntValue i => i.Value != 0,
            UintValue u => u.Value != 0,
            DoubleValue d => d.Value != 0,
            BoolValue b => b.Value,
            StringValue s => s.Value.Length > 0,
            BytesValue by => by.Span.Length > 0,
            NullValue => false,
            _ => true,
        });
    }

    public override string ToString() => $"{Descriptor.FullName}{{{Message}}}";
}
