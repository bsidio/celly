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
internal sealed class IdentEval(IReadOnlyList<string> candidates, CelValue? typeIdent) : IInterpretable
{
    public CelValue Eval(IActivation activation)
    {
        foreach (var name in candidates)
        {
            if (activation.TryFind(name, out var value))
            {
                return value;
            }
        }

        return typeIdent ?? ErrorValue.NoSuchAttribute(candidates[^1]);
    }
}

internal static class CandidateResolution
{
    /// <summary>Per-candidate resolution: activation bindings first, then standard type idents.</summary>
    public static CelValue? Resolve(IActivation activation, IReadOnlyList<string> candidates)
    {
        foreach (var name in candidates)
        {
            if (activation.TryFind(name, out var value))
            {
                return value;
            }

            if (Planner.TypeIdents.TryGetValue(name, out var typeIdent))
            {
                return typeIdent;
            }
        }

        return null;
    }
}

/// <summary>
/// Field selection with maybe-attribute semantics: in parse-only mode <c>a.b.c</c> may be a
/// variable named "a.b.c" (per container resolution) rather than field accesses, so qualified
/// candidates are tried against the activation before falling back to evaluating the operand.
/// </summary>
internal sealed class SelectEval(IInterpretable operand, string field, IReadOnlyList<string> qualifiedCandidates) : IInterpretable
{
    public CelValue Eval(IActivation activation)
    {
        if (CandidateResolution.Resolve(activation, qualifiedCandidates) is { } bound)
        {
            return bound;
        }

        var value = operand.Eval(activation);
        return value switch
        {
            ErrorValue or UnknownValue => value,
            MapValue map => map.Get(StringValue.Of(field)),
            _ => ErrorValue.NoSuchOverload(),
        };
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
            _ => ErrorValue.NoSuchOverload(),
        };
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
