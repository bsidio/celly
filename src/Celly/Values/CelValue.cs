using Celly.Types;

namespace Celly.Values;

/// <summary>
/// A CEL runtime value. Errors and unknowns are values too — they flow through evaluation rather
/// than being thrown, which is what makes the commutative error-absorbing semantics of
/// <c>&amp;&amp;</c>/<c>||</c>/<c>?:</c> expressible.
/// </summary>
public abstract class CelValue
{
    /// <summary>The value's runtime type (also the payload of <c>type(x)</c>).</summary>
    public abstract CelType Type { get; }

    /// <summary>
    /// CEL equality: deep, numeric cross-type aware (<c>1 == 1.0u</c> is true), NaN-is-never-equal,
    /// and false (never an error) across mismatched types. Error/unknown operands are handled
    /// before dispatch by the evaluator.
    /// </summary>
    public abstract bool EqualTo(CelValue other);

    /// <summary>Converts to a plain .NET representation (used for debugging and adaption back out).</summary>
    public abstract object? ToNative();

    public bool IsError => this is ErrorValue;

    public bool IsUnknown => this is UnknownValue;
}

// ---- capability traits (drive operator dispatch) -------------------------------------------------

public interface ISizedValue
{
    CelValue Size();
}

public interface IIndexerValue
{
    CelValue Get(CelValue index);
}

public interface IContainsTester
{
    CelValue Contains(CelValue element);
}

/// <summary>Ordering: returns an IntValue in {-1, 0, 1} or an ErrorValue when unordered.</summary>
public interface IComparableValue
{
    CelValue CompareTo(CelValue other);
}

public interface IAdder
{
    CelValue Add(CelValue other);
}

public interface ISubtractor
{
    CelValue Subtract(CelValue other);
}

public interface IMultiplier
{
    CelValue Multiply(CelValue other);
}

public interface IDivider
{
    CelValue Divide(CelValue other);
}

public interface IModder
{
    CelValue Modulo(CelValue other);
}

public interface INegater
{
    CelValue Negate();
}

public interface IIterableValue
{
    IEnumerable<CelValue> Iterate();
}

/// <summary>Zero-value test used by optional.ofNonZeroValue (messages: all fields unset).</summary>
public interface IZeroTester
{
    bool IsZeroValue();
}
