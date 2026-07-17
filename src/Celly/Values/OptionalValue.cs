using Celly.Types;

namespace Celly.Values;

/// <summary>
/// An optional value (the CEL optionals extension): either holds a value or is none. Produced by
/// <c>optional.of</c>, <c>?.</c> selection, <c>[?]</c> indexing; consumed by optional-entry
/// literal syntax and <c>orValue</c>/<c>optMap</c>/… functions (registered in M6's OptionalsLibrary).
/// </summary>
public sealed class OptionalValue : CelValue
{
    public static readonly OptionalValue None = new(null);

    private readonly CelValue? _value;

    private OptionalValue(CelValue? value) => _value = value;

    public static OptionalValue OfValue(CelValue value) => new(value);

    public bool HasValue => _value is not null;

    public CelValue Value => _value ?? throw new InvalidOperationException("optional.none() has no value");

    public override CelType Type => CelType.OptionalDyn;

    public override bool EqualTo(CelValue other)
    {
        if (other is not OptionalValue opt)
        {
            return false;
        }

        if (!HasValue || !opt.HasValue)
        {
            return HasValue == opt.HasValue;
        }

        return Value.EqualTo(opt.Value);
    }

    public override object? ToNative() => HasValue ? Value.ToNative() : null;

    public override string ToString() => HasValue ? $"optional.of({Value})" : "optional.none()";
}
