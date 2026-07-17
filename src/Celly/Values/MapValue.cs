using Celly.Types;

namespace Celly.Values;

/// <summary>
/// Equality/hashing for CEL map keys. Valid key types are int, uint, string, and bool; numerically
/// equal int/uint keys are the SAME key, so both hash into the int64 number line when possible.
/// </summary>
public sealed class MapKeyComparer : IEqualityComparer<CelValue>
{
    public static readonly MapKeyComparer Instance = new();

    public bool Equals(CelValue? x, CelValue? y) => x!.EqualTo(y!);

    public int GetHashCode(CelValue obj) => obj switch
    {
        IntValue i => i.Value.GetHashCode(),
        UintValue u => u.Value <= long.MaxValue ? ((long)u.Value).GetHashCode() : u.Value.GetHashCode(),
        StringValue s => StringComparer.Ordinal.GetHashCode(s.Value),
        BoolValue b => b.Value.GetHashCode(),
        DoubleValue d => throw new InvalidOperationException("double map keys must be normalized before storage"),
        _ => 0,
    };
}

/// <summary>A CEL map. Keys are int/uint/string/bool with cross-numeric identity.</summary>
public class MapValue : CelValue, ISizedValue, IIndexerValue, IContainsTester, IIterableValue
{
    private readonly Dictionary<CelValue, CelValue> _entries;
    private readonly List<CelValue> _keyOrder; // deterministic iteration

    public MapValue(Dictionary<CelValue, CelValue> entries, List<CelValue> keyOrder)
    {
        _entries = entries;
        _keyOrder = keyOrder;
    }

    public static readonly MapValue Empty = new([], []);

    /// <summary>
    /// Builds a map from key/value pairs; returns an error on invalid or duplicate keys
    /// (duplicates judged by CEL key identity, so <c>{1: v, 1u: w}</c> is a duplicate).
    /// </summary>
    public static CelValue Build(IEnumerable<KeyValuePair<CelValue, CelValue>> pairs)
    {
        var entries = new Dictionary<CelValue, CelValue>(MapKeyComparer.Instance);
        var order = new List<CelValue>();
        foreach (var (rawKey, value) in pairs)
        {
            var key = NormalizeKey(rawKey);
            if (key is ErrorValue err)
            {
                return err;
            }

            if (!entries.TryAdd(key, value))
            {
                return new ErrorValue("Failed with repeated key");
            }

            order.Add(key);
        }

        return new MapValue(entries, order);
    }

    /// <summary>
    /// Canonicalizes a lookup/storage key: integral doubles land on the int/uint number line;
    /// invalid key types become errors.
    /// </summary>
    public static CelValue NormalizeKey(CelValue key)
    {
        switch (key)
        {
            case IntValue or UintValue or StringValue or BoolValue:
                return key;
            case DoubleValue d:
                var v = d.Value;
                if (double.IsNaN(v) || double.IsInfinity(v) || v != Math.Floor(v))
                {
                    return new ErrorValue($"unsupported key value: {v}");
                }

                if (v is >= -9223372036854775808.0 and < 9223372036854775808.0)
                {
                    return IntValue.Of((long)v);
                }

                if (v is >= 0.0 and < 18446744073709551616.0)
                {
                    return UintValue.Of((ulong)v);
                }

                return new ErrorValue($"unsupported key value: {v}");
            default:
                return new ErrorValue($"unsupported key type: {key.Type.Name}");
        }
    }

    public int Count => _entries.Count;

    public IReadOnlyList<CelValue> Keys => _keyOrder;

    public bool TryGet(CelValue rawKey, out CelValue value)
    {
        value = NullValue.Instance;
        var key = NormalizeKey(rawKey);
        if (key is ErrorValue || !_entries.TryGetValue(key, out var found))
        {
            return false;
        }

        value = found;
        return true;
    }

    public override CelType Type => CelType.MapDyn;

    public override bool EqualTo(CelValue other)
    {
        if (other is not MapValue map || map.Count != Count)
        {
            return false;
        }

        foreach (var (key, value) in _entries)
        {
            if (!map._entries.TryGetValue(key, out var otherValue) || !value.EqualTo(otherValue))
            {
                return false;
            }
        }

        return true;
    }

    public override object ToNative()
    {
        var result = new Dictionary<object, object?>();
        foreach (var key in _keyOrder)
        {
            result[key.ToNative()!] = _entries[key].ToNative();
        }

        return result;
    }

    public CelValue Size() => IntValue.Of(Count);

    public CelValue Get(CelValue rawKey)
    {
        var key = NormalizeKey(rawKey);
        if (key is ErrorValue err)
        {
            return err;
        }

        return _entries.TryGetValue(key, out var value) ? value : ErrorValue.NoSuchKey(key);
    }

    public CelValue Contains(CelValue rawKey)
    {
        var key = NormalizeKey(rawKey);
        if (key is ErrorValue)
        {
            // Membership tests with invalid key types are false, not errors.
            return BoolValue.False;
        }

        return BoolValue.Of(_entries.ContainsKey(key));
    }

    /// <summary>Iteration yields keys (per the comprehension spec for maps).</summary>
    public IEnumerable<CelValue> Iterate() => _keyOrder;

    public override string ToString() =>
        "{" + string.Join(", ", _keyOrder.Select(k => $"{k}: {_entries[k]}")) + "}";
}
