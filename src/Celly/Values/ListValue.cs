using Celly.Types;

namespace Celly.Values;

/// <summary>
/// A CEL list. Immutable. Concatenation is O(1): it builds a <see cref="ConcatListValue"/> rope
/// that flattens lazily on first element access — which turns the comprehension accumulation
/// pattern (<c>@result + [t]</c> per element) from O(n²) into O(n) overall.
/// </summary>
public class ListValue : CelValue, ISizedValue, IIndexerValue, IContainsTester, IAdder, IIterableValue
{
    private readonly IReadOnlyList<CelValue>? _elements;

    public ListValue(IReadOnlyList<CelValue> elements) => _elements = elements;

    /// <summary>Rope subclass ctor: elements are produced lazily by <see cref="MaterializeElements"/>.</summary>
    private protected ListValue() => _elements = null;

    public static readonly ListValue Empty = new([]);

    public IReadOnlyList<CelValue> Elements => _elements ?? MaterializeElements();

    private protected virtual IReadOnlyList<CelValue> MaterializeElements() => [];

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

        if (this == Empty || (_elements is not null && _elements.Count == 0))
        {
            return list;
        }

        if (list == Empty || (list._elements is not null && list._elements.Count == 0))
        {
            return this;
        }

        return new ConcatListValue(this, list);
    }

    public IEnumerable<CelValue> Iterate() => Elements;

    public override string ToString() => "[" + string.Join(", ", Elements) + "]";
}

/// <summary>
/// Lazy list concatenation (rope). Appending is O(1); the flat element list materializes once,
/// thread-safely, on first access. Flattening walks the rope iteratively, so arbitrarily long
/// append chains (deep left-leaning ropes from comprehensions) cannot overflow the stack.
/// </summary>
public sealed class ConcatListValue : ListValue
{
    private readonly ListValue _left;
    private readonly ListValue _right;
    private volatile IReadOnlyList<CelValue>? _flattened;

    internal ConcatListValue(ListValue left, ListValue right)
    {
        _left = left;
        _right = right;
    }

    private protected override IReadOnlyList<CelValue> MaterializeElements()
    {
        // Benign race: concurrent callers may both flatten; last write wins, results identical.
        if (_flattened is { } cached)
        {
            return cached;
        }

        var parts = new List<IReadOnlyList<CelValue>>();
        var stack = new Stack<ListValue>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node is ConcatListValue concat && concat._flattened is null)
            {
                stack.Push(concat._right);
                stack.Push(concat._left);
            }
            else
            {
                parts.Add(node.Elements);
            }
        }

        var total = parts.Sum(p => p.Count);
        var result = new List<CelValue>(total);
        foreach (var part in parts)
        {
            result.AddRange(part);
        }

        _flattened = result;
        return result;
    }
}
