using System.Text;
using Celly.Types;

namespace Celly.Values;

/// <summary>
/// A CEL string. CEL string semantics (<c>size</c>, comparisons, extension functions) are Unicode
/// code-point based while .NET strings are UTF-16, so surrogate presence is computed lazily and
/// the common no-surrogate case takes fast paths.
/// </summary>
public sealed class StringValue(string value) : CelValue, IComparableValue, IAdder, ISizedValue
{
    private int _codePointLength = -1; // -1: unknown; otherwise cached

    public string Value { get; } = value;

    public static StringValue Of(string value) => new(value);

    public override CelType Type => CelType.String;

    /// <summary>Length in Unicode code points (== UTF-16 length when no surrogate pairs exist).</summary>
    public int CodePointLength
    {
        get
        {
            if (_codePointLength < 0)
            {
                var count = 0;
                for (var i = 0; i < Value.Length; i++)
                {
                    if (char.IsHighSurrogate(Value[i]) && i + 1 < Value.Length && char.IsLowSurrogate(Value[i + 1]))
                    {
                        i++;
                    }

                    count++;
                }

                _codePointLength = count;
            }

            return _codePointLength;
        }
    }

    public bool HasSupplementary => CodePointLength != Value.Length;

    public override bool EqualTo(CelValue other) => other is StringValue s && string.Equals(Value, s.Value, StringComparison.Ordinal);

    public override object ToNative() => Value;

    public CelValue CompareTo(CelValue other)
    {
        if (other is not StringValue s)
        {
            return ErrorValue.NoSuchOverload();
        }

        return IntValue.OfComparison(CompareOrdinalByCodePoint(Value, s.Value));
    }

    /// <summary>
    /// Code-point-order comparison. Plain ordinal (UTF-16 unit) comparison mis-orders supplementary
    /// characters: high surrogates (0xD800+) sort below U+E000..U+FFFF even though the code points
    /// they encode (≥ U+10000) sort above, so rune-wise comparison is required when surrogates
    /// may be present.
    /// </summary>
    public static int CompareOrdinalByCodePoint(string a, string b)
    {
        var min = Math.Min(a.Length, b.Length);
        for (var i = 0; i < min; i++)
        {
            var ca = a[i];
            var cb = b[i];
            if (ca == cb)
            {
                continue;
            }

            // A difference involving a surrogate needs code-point comparison from this position.
            if (char.IsSurrogate(ca) || char.IsSurrogate(cb))
            {
                return CompareByRunes(a, b, i);
            }

            return ca < cb ? -1 : 1;
        }

        return a.Length.CompareTo(b.Length);
    }

    private static int CompareByRunes(string a, string b, int start)
    {
        var ia = start;
        var ib = start;
        while (ia < a.Length && ib < b.Length)
        {
            var ra = CodePointAt(a, ref ia);
            var rb = CodePointAt(b, ref ib);
            if (ra != rb)
            {
                return ra < rb ? -1 : 1;
            }
        }

        return (a.Length - ia).CompareTo(b.Length - ib);
    }

    private static int CodePointAt(string s, ref int i)
    {
        var c = s[i];
        if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
        {
            var cp = char.ConvertToUtf32(c, s[i + 1]);
            i += 2;
            return cp;
        }

        i++;
        return c; // lone surrogates compare by their code unit value
    }

    public CelValue Add(CelValue other) =>
        other is StringValue s ? Of(Value + s.Value) : ErrorValue.NoSuchOverload();

    public CelValue Size() => IntValue.Of(CodePointLength);

    public override string ToString() => Value;
}

/// <summary>CEL bytes: an immutable octet sequence. Comparison is unsigned lexicographic.</summary>
public sealed class BytesValue(byte[] value) : CelValue, IComparableValue, IAdder, ISizedValue
{
    private readonly byte[] _value = value;

    public ReadOnlySpan<byte> Span => _value;

    public byte[] ToByteArray() => (byte[])_value.Clone();

    public static BytesValue Of(byte[] value) => new(value);

    public override CelType Type => CelType.Bytes;

    public override bool EqualTo(CelValue other) => other is BytesValue b && Span.SequenceEqual(b.Span);

    public override object ToNative() => ToByteArray();

    public CelValue CompareTo(CelValue other) =>
        other is BytesValue b ? IntValue.OfComparison(Span.SequenceCompareTo(b.Span)) : ErrorValue.NoSuchOverload();

    public CelValue Add(CelValue other)
    {
        if (other is not BytesValue b)
        {
            return ErrorValue.NoSuchOverload();
        }

        var combined = new byte[_value.Length + b._value.Length];
        _value.CopyTo(combined, 0);
        b._value.CopyTo(combined, _value.Length);
        return new BytesValue(combined);
    }

    public CelValue Size() => IntValue.Of(_value.Length);

    /// <summary>Decodes as UTF-8; error when the bytes are not valid UTF-8 (used by string(bytes)).</summary>
    public CelValue DecodeUtf8()
    {
        try
        {
            return StringValue.Of(new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(_value));
        }
        catch (DecoderFallbackException)
        {
            return new ErrorValue("invalid UTF-8 in bytes, cannot convert to string");
        }
    }

    public override string ToString() => "b\"" + Convert.ToHexString(_value).ToLowerInvariant() + "\"";
}
