using Celly.Providers;
using Celly.Values;

namespace Celly.Interpreter;

/// <summary>Provides variable bindings during evaluation.</summary>
public interface IActivation
{
    bool TryFind(string name, out CelValue value);
}

public sealed class EmptyActivation : IActivation
{
    public static readonly EmptyActivation Instance = new();

    public bool TryFind(string name, out CelValue value)
    {
        value = NullValue.Instance;
        return false;
    }
}

/// <summary>Dictionary-backed activation; values are adapted to CelValues lazily and memoized.</summary>
public sealed class MapActivation(IReadOnlyDictionary<string, object?> bindings, ITypeAdapter adapter) : IActivation
{
    private readonly Dictionary<string, CelValue> _cache = [];

    public bool TryFind(string name, out CelValue value)
    {
        if (_cache.TryGetValue(name, out value!))
        {
            return true;
        }

        if (bindings.TryGetValue(name, out var native))
        {
            value = adapter.NativeToValue(native);
            _cache[name] = value;
            return true;
        }

        value = NullValue.Instance;
        return false;
    }
}

/// <summary>An activation of pre-adapted CEL values (used by the conformance harness and tests).</summary>
public sealed class ValueActivation(IReadOnlyDictionary<string, CelValue> bindings) : IActivation
{
    public bool TryFind(string name, out CelValue value) => bindings.TryGetValue(name, out value!);
}

/// <summary>A single mutable binding over a parent scope (comprehension accumulator / iteration variables).</summary>
public sealed class ScopedActivation(IActivation parent, string name, CelValue initial) : IActivation
{
    public CelValue Value { get; set; } = initial;

    public IActivation Parent => parent;

    public bool TryFind(string name1, out CelValue value)
    {
        if (string.Equals(name1, name, StringComparison.Ordinal))
        {
            value = Value;
            return true;
        }

        return parent.TryFind(name1, out value);
    }

    /// <summary>Peels comprehension scopes for absolute (leading-dot) name resolution.</summary>
    public static IActivation Unwrap(IActivation activation)
    {
        while (activation is ScopedActivation scoped)
        {
            activation = scoped.Parent;
        }

        return activation;
    }
}
