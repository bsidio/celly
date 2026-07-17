using Celly.Values;

namespace Celly.Providers;

/// <summary>Adapts native .NET values into CEL runtime values.</summary>
public interface ITypeAdapter
{
    CelValue NativeToValue(object? value);
}

/// <summary>
/// The core adapter for plain .NET types: primitives, strings, byte arrays, dictionaries, and
/// enumerables. Protobuf message adaption lives in Celly.Protobuf behind the same interface.
/// </summary>
public sealed class NativeTypeAdapter : ITypeAdapter
{
    public static readonly NativeTypeAdapter Instance = new();

    public CelValue NativeToValue(object? value)
    {
        switch (value)
        {
            case null:
                return NullValue.Instance;
            case CelValue cel:
                return cel;
            case bool b:
                return BoolValue.Of(b);
            case int i:
                return IntValue.Of(i);
            case long l:
                return IntValue.Of(l);
            case short s:
                return IntValue.Of(s);
            case sbyte sb:
                return IntValue.Of(sb);
            case uint ui:
                return UintValue.Of(ui);
            case ulong ul:
                return UintValue.Of(ul);
            case ushort us:
                return UintValue.Of(us);
            case byte by:
                return UintValue.Of(by);
            case double d:
                return DoubleValue.Of(d);
            case float f:
                return DoubleValue.Of(f);
            case string str:
                return StringValue.Of(str);
            case byte[] bytes:
                return BytesValue.Of(bytes);
            case System.Collections.IDictionary dict:
            {
                var pairs = new List<KeyValuePair<CelValue, CelValue>>();
                foreach (System.Collections.DictionaryEntry entry in dict)
                {
                    pairs.Add(new(NativeToValue(entry.Key), NativeToValue(entry.Value)));
                }

                return MapValue.Build(pairs);
            }

            case System.Collections.IEnumerable seq:
            {
                var elements = new List<CelValue>();
                foreach (var item in seq)
                {
                    elements.Add(NativeToValue(item));
                }

                return ListValue.Of(elements);
            }

            default:
                return new ErrorValue($"unsupported conversion to CEL value: {value.GetType()}");
        }
    }
}
