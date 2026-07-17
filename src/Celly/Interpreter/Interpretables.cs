using Celly.Values;

namespace Celly.Interpreter;

/// <summary>A planned, evaluatable expression node.</summary>
public interface IInterpretable
{
    CelValue Eval(IActivation activation);
}

internal sealed class ConstEval(CelValue value) : IInterpretable
{
    public CelValue Eval(IActivation activation) => value;
}

/// <summary>
/// Identifier resolution over container-qualified candidate names (longest first), with a fallback
/// to standard type identifiers (<c>int</c>, <c>list</c>, …).
/// </summary>
internal sealed class IdentEval(IReadOnlyList<string> candidates, CelValue? typeIdent, bool absolute = false) : IInterpretable
{
    public CelValue Eval(IActivation activation)
    {
        // Absolute references (.name) resolve outside comprehension scopes.
        var scope = absolute ? ScopedActivation.Unwrap(activation) : activation;
        foreach (var name in candidates)
        {
            if (scope.TryFind(name, out var value))
            {
                return value;
            }
        }

        return typeIdent ?? ErrorValue.NoSuchAttribute(candidates[^1]);
    }
}

/// <summary>A direct reference to a comprehension variable (shadows all qualified resolution).</summary>
internal sealed class ScopeIdentEval(string name) : IInterpretable
{
    public CelValue Eval(IActivation activation) =>
        activation.TryFind(name, out var value) ? value : ErrorValue.NoSuchAttribute(name);
}


/// <summary>
/// Field selection with maybe-attribute semantics: in parse-only mode <c>a.b.c</c> may be a
/// variable named "a.b.c" (per container resolution) rather than field accesses, so qualified
/// candidates are tried against the activation before falling back to evaluating the operand.
/// </summary>
internal sealed class SelectEval(
    IInterpretable operand,
    string field,
    IReadOnlyList<string> qualifiedCandidates,
    CelValue? staticFallback = null,
    bool absolute = false) : IInterpretable
{
    public CelValue Eval(IActivation activation)
    {
        // Absolute references (.name) resolve outside comprehension scopes.
        var scope = absolute ? ScopedActivation.Unwrap(activation) : activation;
        foreach (var name in qualifiedCandidates)
        {
            if (scope.TryFind(name, out var bound))
            {
                return bound;
            }
        }

        if (staticFallback is not null)
        {
            return staticFallback;
        }

        var value = operand.Eval(activation);
        return Select(value, field);
    }

    /// <summary>Field selection incl. optional chaining: select on an optional yields an optional.</summary>
    internal static CelValue Select(CelValue value, string field)
    {
        switch (value)
        {
            case ErrorValue or UnknownValue:
                return value;
            case MapValue map:
                return map.Get(StringValue.Of(field));
            case Providers.IStructValue st:
                return st.GetField(field);
            case OptionalValue opt:
            {
                if (!opt.HasValue)
                {
                    return OptionalValue.None;
                }

                switch (opt.Value)
                {
                    case MapValue inner:
                        return inner.TryGet(StringValue.Of(field), out var found)
                            ? OptionalValue.OfValue(found)
                            : OptionalValue.None;
                    case Providers.IStructValue innerStruct:
                    {
                        var has = innerStruct.HasField(field);
                        if (has is ErrorValue error)
                        {
                            return error;
                        }

                        return has is BoolValue { Value: true }
                            ? OptionalValue.OfValue(innerStruct.GetField(field))
                            : OptionalValue.None;
                    }

                    default:
                        return ErrorValue.NoSuchOverload();
                }
            }

            default:
                return ErrorValue.NoSuchOverload();
        }
    }
}

/// <summary>The has() macro expansion: field presence test.</summary>
internal sealed class TestOnlySelectEval(IInterpretable operand, string field) : IInterpretable
{
    public CelValue Eval(IActivation activation)
    {
        var value = operand.Eval(activation);
        return value switch
        {
            ErrorValue or UnknownValue => value,
            MapValue map => map.Contains(StringValue.Of(field)),
            Providers.IStructValue st => st.HasField(field),
            OptionalValue { HasValue: false } => BoolValue.False,
            OptionalValue opt => opt.Value switch
            {
                MapValue inner => inner.Contains(StringValue.Of(field)),
                Providers.IStructValue innerStruct => innerStruct.HasField(field),
                _ => ErrorValue.NoSuchOverload(),
            },
            _ => ErrorValue.NoSuchOverload(),
        };
    }
}

internal sealed class StructFieldEval(string name, IInterpretable value, bool optional)
{
    public string Name => name;

    public IInterpretable Value => value;

    public bool Optional => optional;
}

/// <summary>Message construction through the type provider.</summary>
internal sealed class StructEval(Providers.ITypeProvider provider, string messageName, StructFieldEval[] fields) : IInterpretable
{
    public CelValue Eval(IActivation activation)
    {
        var values = new List<KeyValuePair<string, CelValue>>(fields.Length);
        UnknownValue? unknown = null;
        foreach (var field in fields)
        {
            var v = field.Value.Eval(activation);
            if (v is ErrorValue)
            {
                return v;
            }

            if (v is UnknownValue u)
            {
                unknown = unknown is null ? u : UnknownValue.Merge(unknown, u);
                continue;
            }

            if (field.Optional)
            {
                if (v is OptionalValue opt)
                {
                    if (opt.HasValue)
                    {
                        values.Add(new(field.Name, opt.Value));
                    }

                    continue;
                }

                return ErrorValue.NoSuchOverload();
            }

            values.Add(new(field.Name, v));
        }

        return unknown is not null ? unknown : provider.NewValue(messageName, values);
    }
}

/// <summary>A strict function call: arguments evaluate first; unknowns merge, then errors propagate.</summary>
internal sealed class CallEval(string function, Func<CelValue[], CelValue>? impl, IInterpretable[] args) : IInterpretable
{
    public CelValue Eval(IActivation activation)
    {
        var values = new CelValue[args.Length];
        UnknownValue? unknown = null;
        ErrorValue? error = null;
        for (var i = 0; i < args.Length; i++)
        {
            var v = args[i].Eval(activation);
            values[i] = v;
            if (v is UnknownValue u)
            {
                unknown = unknown is null ? u : UnknownValue.Merge(unknown, u);
            }
            else if (v is ErrorValue e)
            {
                error ??= e;
            }
        }

        if (unknown is not null)
        {
            return unknown;
        }

        if (error is not null)
        {
            return error;
        }

        if (impl is null)
        {
            return new ErrorValue($"unknown function '{function}'");
        }

        try
        {
            return impl(values);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return new ErrorValue(ex.Message);
        }
    }
}

/// <summary>Commutative, error-absorbing logical AND.</summary>
internal sealed class AndEval(IInterpretable left, IInterpretable right) : IInterpretable
{
    public CelValue Eval(IActivation activation)
    {
        var l = left.Eval(activation);
        if (l is BoolValue { Value: false })
        {
            return BoolValue.False;
        }

        var r = right.Eval(activation);
        if (r is BoolValue { Value: false })
        {
            return BoolValue.False;
        }

        if (l is BoolValue && r is BoolValue)
        {
            return BoolValue.True;
        }

        return MergeNonBool(l, r);
    }

    internal static CelValue MergeNonBool(CelValue l, CelValue r)
    {
        if (l is UnknownValue lu)
        {
            return r is UnknownValue ru ? UnknownValue.Merge(lu, ru) : lu;
        }

        if (r is UnknownValue u)
        {
            return u;
        }

        if (l is ErrorValue le)
        {
            return le;
        }

        if (r is ErrorValue re)
        {
            return re;
        }

        return ErrorValue.NoSuchOverload();
    }
}

/// <summary>Commutative, error-absorbing logical OR.</summary>
internal sealed class OrEval(IInterpretable left, IInterpretable right) : IInterpretable
{
    public CelValue Eval(IActivation activation)
    {
        var l = left.Eval(activation);
        if (l is BoolValue { Value: true })
        {
            return BoolValue.True;
        }

        var r = right.Eval(activation);
        if (r is BoolValue { Value: true })
        {
            return BoolValue.True;
        }

        if (l is BoolValue && r is BoolValue)
        {
            return BoolValue.False;
        }

        return AndEval.MergeNonBool(l, r);
    }
}

/// <summary>The ternary: strict only on the condition.</summary>
internal sealed class ConditionalEval(IInterpretable condition, IInterpretable truthy, IInterpretable falsy) : IInterpretable
{
    public CelValue Eval(IActivation activation)
    {
        var c = condition.Eval(activation);
        return c switch
        {
            BoolValue b => b.Value ? truthy.Eval(activation) : falsy.Eval(activation),
            ErrorValue or UnknownValue => c,
            _ => ErrorValue.NoSuchOverload(),
        };
    }
}

/// <summary>@not_strictly_false: false only for false; true for true, error, and unknown.</summary>
internal sealed class NotStrictlyFalseEval(IInterpretable arg) : IInterpretable
{
    public CelValue Eval(IActivation activation) =>
        arg.Eval(activation) is BoolValue b ? b : BoolValue.True;
}

internal sealed class ListEval(IInterpretable[] elements, IReadOnlyList<int> optionalIndices) : IInterpretable
{
    public CelValue Eval(IActivation activation)
    {
        var values = new List<CelValue>(elements.Length);
        UnknownValue? unknown = null;
        for (var i = 0; i < elements.Length; i++)
        {
            var v = elements[i].Eval(activation);
            if (v is UnknownValue u)
            {
                unknown = unknown is null ? u : UnknownValue.Merge(unknown, u);
                continue;
            }

            if (v is ErrorValue)
            {
                return v;
            }

            if (optionalIndices.Contains(i))
            {
                if (v is OptionalValue opt)
                {
                    if (opt.HasValue)
                    {
                        values.Add(opt.Value);
                    }

                    continue;
                }

                return ErrorValue.NoSuchOverload();
            }

            values.Add(v);
        }

        return unknown is not null ? unknown : ListValue.Of(values);
    }
}

internal sealed class MapEntryEval(IInterpretable key, IInterpretable value, bool optional)
{
    public IInterpretable Key => key;

    public IInterpretable Value => value;

    public bool Optional => optional;
}

internal sealed class MapEval(MapEntryEval[] entries) : IInterpretable
{
    public CelValue Eval(IActivation activation)
    {
        var pairs = new List<KeyValuePair<CelValue, CelValue>>(entries.Length);
        UnknownValue? unknown = null;
        foreach (var entry in entries)
        {
            var k = entry.Key.Eval(activation);
            if (k is ErrorValue)
            {
                return k;
            }

            var v = entry.Value.Eval(activation);
            if (v is ErrorValue)
            {
                return v;
            }

            if (k is UnknownValue ku)
            {
                unknown = unknown is null ? ku : UnknownValue.Merge(unknown, ku);
                continue;
            }

            if (v is UnknownValue vu)
            {
                unknown = unknown is null ? vu : UnknownValue.Merge(unknown, vu);
                continue;
            }

            if (entry.Optional)
            {
                if (v is OptionalValue opt)
                {
                    if (opt.HasValue)
                    {
                        pairs.Add(new(k, opt.Value));
                    }

                    continue;
                }

                return ErrorValue.NoSuchOverload();
            }

            pairs.Add(new(k, v));
        }

        return unknown is not null ? unknown : MapValue.Build(pairs);
    }
}

/// <summary>Message construction; requires a type provider (Celly.Protobuf) for non-map types.</summary>
internal sealed class UnknownStructEval(string messageName) : IInterpretable
{
    public CelValue Eval(IActivation activation) =>
        new ErrorValue($"unknown type: '{messageName}'");
}

/// <summary>
/// Comprehension evaluation per the spec: iterate the range (list elements / map keys), rebinding
/// the accumulator through the loop step while the loop condition holds, then yield the result.
/// </summary>
internal sealed class ComprehensionEval(
    string iterVar,
    string? iterVar2,
    IInterpretable iterRange,
    string accuVar,
    IInterpretable accuInit,
    IInterpretable loopCondition,
    IInterpretable loopStep,
    IInterpretable result) : IInterpretable
{
    public CelValue Eval(IActivation activation)
    {
        var range = iterRange.Eval(activation);
        if (range is ErrorValue or UnknownValue)
        {
            return range;
        }

        if (range is not IIterableValue iterable)
        {
            return ErrorValue.NoSuchOverload();
        }

        var context = RootEvalActivation.Resolve(activation);
        var accu = new ScopedActivation(activation, accuVar, accuInit.Eval(activation));
        var iterScope = new ScopedActivation(accu, iterVar, NullValue.Instance);
        IActivation loopScope = iterScope;
        ScopedActivation? iterScope2 = null;
        if (iterVar2 is not null)
        {
            iterScope2 = new ScopedActivation(iterScope, iterVar2, NullValue.Instance);
            loopScope = iterScope2;
        }

        var index = 0L;
        foreach (var element in iterable.Iterate())
        {
            // Charge this iteration against the (possibly unlimited) evaluation budget.
            if (context.Charge(1) is { } budgetError)
            {
                return budgetError;
            }

            if (iterVar2 is null)
            {
                iterScope.Value = element;
            }
            else if (range is MapValue map)
            {
                // Two-variable form over a map: key, value.
                iterScope.Value = element;
                iterScope2!.Value = map.TryGet(element, out var v) ? v : NullValue.Instance;
            }
            else
            {
                // Two-variable form over a list: index, value.
                iterScope.Value = IntValue.Of(index);
                iterScope2!.Value = element;
            }

            var condition = loopCondition.Eval(loopScope);
            if (condition is BoolValue b)
            {
                if (!b.Value)
                {
                    break;
                }
            }
            else
            {
                return condition is ErrorValue or UnknownValue ? condition : ErrorValue.NoSuchOverload();
            }

            accu.Value = loopStep.Eval(loopScope);
            index++;
        }

        return result.Eval(accu);
    }
}
