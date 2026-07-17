using Celly.Types;

namespace Celly.Values;

/// <summary>
/// A strong-mode enum value: a named enum type paired with its numeric value. In the default
/// (legacy) mode enums are plain ints and this type is unused.
/// </summary>
public sealed class EnumValue(CelType enumType, long number) : CelValue
{
    public CelType EnumType { get; } = enumType;

    public long Number { get; } = number;

    public override CelType Type => EnumType;

    public override bool EqualTo(CelValue other) =>
        other is EnumValue e
        && string.Equals(e.EnumType.Name, EnumType.Name, StringComparison.Ordinal)
        && e.Number == Number;

    public override object ToNative() => Number;

    public override string ToString() => $"{EnumType.Name}({Number})";
}
