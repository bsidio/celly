using Celly.Types;

namespace Celly.Values;

/// <summary>A CEL list. Immutable; concatenation copies (a concat-view optimization is an M7 task).</summary>
public class ListValue(IReadOnlyList<CelValue> elements)
    : CelValue, ISizedValue, IIndexerValue, IContainsTester, IAdder, IIterableValue
{
    public static readonly ListValue Empty = new([]);

    public IReadOnlyList<CelValue> Elements { get; } = elements;

    public static ListValue Of(IReadOnlyList<CelValue> elements) => new(elements);

    public override CelType Type => CelType.ListDyn;

    public override bool EqualTo(CelValue other)
    {
        if (other is not ListValue list || list.Elements.Count != Elements.Count)
        {
            return false;
        }

        for (var i = 0; i < Elements.Count; i++)
        {
            if (!Elements[i].EqualTo(list.Elements[i]))
            {
                return false;
            }
        }

        return true;
    }

    public override object ToNative() => Elements.Select(e => e.ToNative()).ToList();

    public CelValue Size() => IntValue.Of(Elements.Count);

    public CelValue Get(CelValue index)
    {
        long i;
        switch (index)
        {
            case IntValue iv:
                i = iv.Value;
                break;
            case UintValue uv when uv.Value <= long.MaxValue:
                i = (long)uv.Value;
                break;
            case UintValue uv:
                return ErrorValue.IndexOutOfRange(unchecked((long)uv.Value));
            case DoubleValue dv when dv.Value == Math.Floor(dv.Value) && !double.IsInfinity(dv.Value)
                && dv.Value is >= -9223372036854775808.0 and < 9223372036854775808.0:
                i = (long)dv.Value;
                break;
            case DoubleValue:
                return new ErrorValue("invalid_argument");
            default:
                return ErrorValue.NoSuchOverload();
        }

        if (i < 0 || i >= Elements.Count)
        {
            return ErrorValue.IndexOutOfRange(i);
        }

        return Elements[(int)i];
    }

    public CelValue Contains(CelValue element)
    {
        foreach (var e in Elements)
        {
            if (e.EqualTo(element))
            {
                return BoolValue.True;
            }
        }

        return BoolValue.False;
    }

    public CelValue Add(CelValue other)
    {
        if (other is not ListValue list)
        {
            return ErrorValue.NoSuchOverload();
        }

        if (Elements.Count == 0)
        {
            return list;
        }

        if (list.Elements.Count == 0)
        {
            return this;
        }

        var combined = new List<CelValue>(Elements.Count + list.Elements.Count);
        combined.AddRange(Elements);
        combined.AddRange(list.Elements);
        return new ListValue(combined);
    }

    public IEnumerable<CelValue> Iterate() => Elements;

    public override string ToString() => "[" + string.Join(", ", Elements) + "]";
}
