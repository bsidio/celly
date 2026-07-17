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

        // Index and membership.
        registry.Register(Operators.Index, args =>
            args[0] is IIndexerValue idx ? idx.Get(args[1]) : ErrorValue.NoSuchOverload());
        registry.Register(Operators.In, args =>
            args[1] is IContainsTester c ? c.Contains(args[0]) : ErrorValue.NoSuchOverload());

        // size — both global size(x) and receiver x.size() dispatch here (receiver is arg 0).
        registry.Register("size", args =>
            args is [ISizedValue sized] ? sized.Size() : ErrorValue.NoSuchOverload());

        // Type reflection and dyn.
        registry.Register("type", args => args is [var v] ? new TypeValue(v.Type) : ErrorValue.NoSuchOverload());
        registry.Register("dyn", args => args is [var v] ? v : ErrorValue.NoSuchOverload());

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
