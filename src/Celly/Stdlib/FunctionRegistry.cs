using Celly.Common;
using Celly.Types;
using Celly.Values;

namespace Celly.Stdlib;

/// <summary>Runtime function dispatch: name → implementation over already-evaluated values.</summary>
public sealed class FunctionRegistry
{
    private readonly Dictionary<string, Func<CelValue[], CelValue>> _functions = [];

    public void Register(string name, Func<CelValue[], CelValue> impl) => _functions[name] = impl;

    public Func<CelValue[], CelValue>? Find(string name) => _functions.GetValueOrDefault(name);

    public FunctionRegistry Clone()
    {
        var copy = new FunctionRegistry();
        foreach (var (name, impl) in _functions)
        {
            copy._functions[name] = impl;
        }

        return copy;
    }
}

/// <summary>The standard CEL operators and functions (M2 scope; conversions and temporal ops in M3).</summary>
public static class StandardFunctions
{
    public static FunctionRegistry CreateRegistry()
    {
        var registry = new FunctionRegistry();

        // Equality (never errors; cross-type numeric aware).
        registry.Register(Operators.Equals, args => BoolValue.Of(args[0].EqualTo(args[1])));
        registry.Register(Operators.NotEquals, args => BoolValue.Of(!args[0].EqualTo(args[1])));

        // Ordering.
        registry.Register(Operators.Less, args => Compare(args[0], args[1], cmp => cmp < 0));
        registry.Register(Operators.LessEquals, args => Compare(args[0], args[1], cmp => cmp <= 0));
        registry.Register(Operators.Greater, args => Compare(args[0], args[1], cmp => cmp > 0));
        registry.Register(Operators.GreaterEquals, args => Compare(args[0], args[1], cmp => cmp >= 0));

        // Arithmetic via capability traits.
        registry.Register(Operators.Add, args =>
            args[0] is IAdder a ? a.Add(args[1]) : ErrorValue.NoSuchOverload());
        registry.Register(Operators.Subtract, args =>
            args[0] is ISubtractor s ? s.Subtract(args[1]) : ErrorValue.NoSuchOverload());
        registry.Register(Operators.Multiply, args =>
            args[0] is IMultiplier m ? m.Multiply(args[1]) : ErrorValue.NoSuchOverload());
        registry.Register(Operators.Divide, args =>
            args[0] is IDivider d ? d.Divide(args[1]) : ErrorValue.NoSuchOverload());
        registry.Register(Operators.Modulo, args =>
            args[0] is IModder mod ? mod.Modulo(args[1]) : ErrorValue.NoSuchOverload());
        registry.Register(Operators.Negate, args =>
            args[0] is INegater n ? n.Negate() : ErrorValue.NoSuchOverload());
        registry.Register(Operators.LogicalNot, args =>
            args[0] is BoolValue b ? BoolValue.Of(!b.Value) : ErrorValue.NoSuchOverload());

        // Index and membership. Indexing through an optional chains optionally.
        registry.Register(Operators.Index, args => args[0] switch
        {
            IIndexerValue idx => idx.Get(args[1]),
            OptionalValue { HasValue: false } => OptionalValue.None,
            OptionalValue opt when opt.Value is MapValue innerMap =>
                innerMap.TryGet(args[1], out var found) ? OptionalValue.OfValue(found) : OptionalValue.None,
            OptionalValue opt when opt.Value is ListValue innerList =>
                innerList.Get(args[1]) is { } item && item is not ErrorValue
                    ? OptionalValue.OfValue(item)
                    : OptionalValue.None,
            _ => ErrorValue.NoSuchOverload(),
        });
        registry.Register(Operators.In, args =>
            args[1] is IContainsTester c ? c.Contains(args[0]) : ErrorValue.NoSuchOverload());

        // size — both global size(x) and receiver x.size() dispatch here (receiver is arg 0).
        registry.Register("size", args =>
            args is [ISizedValue sized] ? sized.Size() : ErrorValue.NoSuchOverload());

        // Type reflection and dyn.
        registry.Register("type", args => args is [var v] ? new TypeValue(v.Type) : ErrorValue.NoSuchOverload());
        registry.Register("dyn", args => args is [var v] ? v : ErrorValue.NoSuchOverload());

        // Conversions.
        registry.Register("bool", args => args is [var v] ? Conversions.ToBool(v) : ErrorValue.NoSuchOverload());
        registry.Register("bytes", args => args is [var v] ? Conversions.ToBytes(v) : ErrorValue.NoSuchOverload());
        registry.Register("double", args => args is [var v] ? Conversions.ToDouble(v) : ErrorValue.NoSuchOverload());
        registry.Register("int", args => args is [var v] ? Conversions.ToInt(v) : ErrorValue.NoSuchOverload());
        registry.Register("uint", args => args is [var v] ? Conversions.ToUint(v) : ErrorValue.NoSuchOverload());
        registry.Register("string", args => args is [var v] ? Conversions.ToString(v) : ErrorValue.NoSuchOverload());
        registry.Register("timestamp", args => args switch
        {
            [TimestampValue ts] => ts,
            [StringValue s] => TimeFunctions.ParseTimestamp(s.Value),
            [IntValue i] => TimestampValue.Of(i.Value, 0), // epoch seconds
            _ => ErrorValue.NoSuchOverload(),
        });
        registry.Register("duration", args => args switch
        {
            [DurationValue d] => d,
            [StringValue s] => TimeFunctions.ParseDuration(s.Value),
            _ => ErrorValue.NoSuchOverload(),
        });

        // String tests (receiver style; matches also has a global form).
        registry.Register("contains", args => args is [StringValue s, StringValue sub]
            ? BoolValue.Of(s.Value.Contains(sub.Value, StringComparison.Ordinal))
            : ErrorValue.NoSuchOverload());
        registry.Register("startsWith", args => args is [StringValue s, StringValue prefix]
            ? BoolValue.Of(s.Value.StartsWith(prefix.Value, StringComparison.Ordinal))
            : ErrorValue.NoSuchOverload());
        registry.Register("endsWith", args => args is [StringValue s, StringValue suffix]
            ? BoolValue.Of(s.Value.EndsWith(suffix.Value, StringComparison.Ordinal))
            : ErrorValue.NoSuchOverload());
        registry.Register("matches", args => args is [StringValue s, StringValue pattern]
            ? NonBacktrackingRegexEngine.Instance.IsMatch(pattern.Value, s.Value)
            : ErrorValue.NoSuchOverload());

        // Temporal accessors: timestamp forms take an optional IANA/fixed-offset timezone.
        foreach (var accessor in (string[])
        [
            "getFullYear", "getMonth", "getDate", "getDayOfMonth", "getDayOfWeek",
            "getDayOfYear", "getHours", "getMinutes", "getSeconds", "getMilliseconds",
        ])
        {
            var name = accessor;
            registry.Register(name, args => args switch
            {
                [TimestampValue ts] => TimeFunctions.TimestampAccessor(ts, name, null),
                [TimestampValue ts, StringValue tz] => TimeFunctions.TimestampAccessor(ts, name, tz.Value),
                [DurationValue d] when name is "getHours" or "getMinutes" or "getSeconds" or "getMilliseconds"
                    => TimeFunctions.DurationAccessor(d, name),
                _ => ErrorValue.NoSuchOverload(),
            });
        }

        return registry;
    }

    private static CelValue Compare(CelValue left, CelValue right, Func<int, bool> predicate)
    {
        if (left is not IComparableValue comparable)
        {
            return ErrorValue.NoSuchOverload();
        }

        var cmp = comparable.CompareTo(right);
        return cmp is IntValue i ? BoolValue.Of(predicate((int)i.Value)) : cmp;
    }
}
