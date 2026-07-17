using Celly.Types;

namespace Celly.Checking;

/// <summary>
/// Unification state: bindings for type parameters accumulated while matching overloads and
/// joining aggregate element types.
/// </summary>
public sealed class TypeSubstitution
{
    private readonly Dictionary<string, CelType> _bindings = new(StringComparer.Ordinal);

    /// <summary>Resolves a type through current bindings (shallow walk of parameter chains).</summary>
    public CelType Resolve(CelType type)
    {
        while (type.Kind == CelTypeKind.TypeParam && _bindings.TryGetValue(type.Name, out var bound))
        {
            type = bound;
        }

        return type;
    }

    /// <summary>Fully substitutes bindings; unbound type parameters collapse to dyn when requested.</summary>
    public CelType Finalize(CelType type, bool unboundToDyn = true)
    {
        type = Resolve(type);
        if (type.Kind == CelTypeKind.TypeParam)
        {
            return unboundToDyn ? CelType.Dyn : type;
        }

        if (type.Parameters.Count == 0)
        {
            return type;
        }

        var parameters = type.Parameters.Select(p => Finalize(p, unboundToDyn)).ToArray();
        return new CelType(type.Kind, type.Name, parameters);
    }

    /// <summary>
    /// Can a value of type <paramref name="from"/> be used where <paramref name="target"/> is
    /// expected? Binds type parameters as the most general solution; dyn absorbs everything.
    /// </summary>
    public bool IsAssignable(CelType target, CelType from)
    {
        target = Resolve(target);
        from = Resolve(from);

        if (target.Kind == CelTypeKind.TypeParam)
        {
            if (Occurs(target.Name, from))
            {
                return ReferenceEquals(Resolve(from), target); // A := A is fine; A := list(A) is not
            }

            _bindings[target.Name] = from;
            return true;
        }

        if (from.Kind == CelTypeKind.TypeParam)
        {
            if (Occurs(from.Name, target))
            {
                return false;
            }

            _bindings[from.Name] = target;
            return true;
        }

        if (target.Kind is CelTypeKind.Dyn or CelTypeKind.Error || from.Kind is CelTypeKind.Dyn or CelTypeKind.Error)
        {
            return true;
        }

        // Type values are mutually assignable regardless of their parameter: equality between
        // type(double) and type(int) must type-check (it compares the runtime type values).
        if (target.Kind == CelTypeKind.Type && from.Kind == CelTypeKind.Type)
        {
            return true;
        }

        // null is assignable to message, wrapper, abstract (incl. optional), and time types.
        if (from.Kind == CelTypeKind.Null
            && (target.Kind is CelTypeKind.Struct or CelTypeKind.Timestamp or CelTypeKind.Duration or CelTypeKind.Opaque
                || IsWrapper(target)))
        {
            return true;
        }

        // Wrapper types interoperate with their primitive: wrapper(int) ↔ int.
        if (IsWrapper(target) && !IsWrapper(from))
        {
            return IsAssignable(target.Parameters[0], from);
        }

        if (IsWrapper(from) && !IsWrapper(target))
        {
            return IsAssignable(target, from.Parameters[0]);
        }

        if (target.Kind != from.Kind || !string.Equals(target.Name, from.Name, StringComparison.Ordinal)
            || target.Parameters.Count != from.Parameters.Count)
        {
            return false;
        }

        for (var i = 0; i < target.Parameters.Count; i++)
        {
            if (!IsAssignable(target.Parameters[i], from.Parameters[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Least upper bound used for aggregate literals: equal stays, anything mixed is dyn.</summary>
    public CelType Join(CelType a, CelType b)
    {
        a = Resolve(a);
        b = Resolve(b);
        if (StructuralEquals(a, b))
        {
            return a;
        }

        // Try unification (binds fresh params from empty-aggregate literals). When both types are
        // compatible, the join is the MORE GENERAL of the two: joining tuple(int, uint) with
        // tuple(dyn, dyn) yields tuple(dyn, dyn).
        var speculative = Snapshot();
        if (IsAssignable(a, b))
        {
            return IsEqualOrLessSpecific(DeepResolve(b), DeepResolve(a)) ? DeepResolve(b) : DeepResolve(a);
        }

        Restore(speculative);
        if (IsAssignable(b, a))
        {
            return IsEqualOrLessSpecific(DeepResolve(a), DeepResolve(b)) ? DeepResolve(a) : DeepResolve(b);
        }

        Restore(speculative);
        return CelType.Dyn;
    }

    /// <summary>Is <paramref name="a"/> equal to or less specific (more general) than <paramref name="b"/>?</summary>
    private static bool IsEqualOrLessSpecific(CelType a, CelType b)
    {
        if (a.Kind is CelTypeKind.Dyn or CelTypeKind.TypeParam)
        {
            return true;
        }

        // wrapper(T) is less specific than T (it admits null as well).
        if (IsWrapper(a) && StructuralEquals(a.Parameters[0], b))
        {
            return true;
        }

        if (b.Kind is CelTypeKind.Dyn or CelTypeKind.TypeParam)
        {
            return false;
        }

        if (a.Kind != b.Kind || !string.Equals(a.Name, b.Name, StringComparison.Ordinal)
            || a.Parameters.Count != b.Parameters.Count)
        {
            return false;
        }

        for (var i = 0; i < a.Parameters.Count; i++)
        {
            if (!IsEqualOrLessSpecific(a.Parameters[i], b.Parameters[i]))
            {
                return false;
            }
        }

        return true;
    }

    public static bool StructuralEquals(CelType a, CelType b)
    {
        if (a.Kind != b.Kind || !string.Equals(a.Name, b.Name, StringComparison.Ordinal)
            || a.Parameters.Count != b.Parameters.Count)
        {
            return false;
        }

        for (var i = 0; i < a.Parameters.Count; i++)
        {
            if (!StructuralEquals(a.Parameters[i], b.Parameters[i]))
            {
                return false;
            }
        }

        return true;
    }

    public static bool IsWrapper(CelType type) =>
        type.Kind == CelTypeKind.Opaque && string.Equals(type.Name, "wrapper", StringComparison.Ordinal);

    public static bool IsOptional(CelType type) =>
        type.Kind == CelTypeKind.Opaque && string.Equals(type.Name, "optional_type", StringComparison.Ordinal);

    /// <summary>Deep substitution resolve (parameters included), without collapsing unbound params.</summary>
    public CelType DeepResolve(CelType type)
    {
        type = Resolve(type);
        if (type.Parameters.Count == 0)
        {
            return type;
        }

        return new CelType(type.Kind, type.Name, [.. type.Parameters.Select(DeepResolve)]);
    }

    private bool Occurs(string paramName, CelType type)
    {
        type = Resolve(type);
        if (type.Kind == CelTypeKind.TypeParam && string.Equals(type.Name, paramName, StringComparison.Ordinal))
        {
            return true;
        }

        return type.Parameters.Any(p => Occurs(paramName, p));
    }

    public Dictionary<string, CelType> Snapshot() => new(_bindings, StringComparer.Ordinal);

    public void Restore(Dictionary<string, CelType> snapshot)
    {
        _bindings.Clear();
        foreach (var (k, v) in snapshot)
        {
            _bindings[k] = v;
        }
    }
}
