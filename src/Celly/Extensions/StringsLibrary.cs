using System.Globalization;
using System.Text;
using Celly.Checking;
using Celly.Types;
using Celly.Values;

namespace Celly.Extensions;

/// <summary>
/// The strings extension (cel-spec doc/extensions/strings.md): charAt, indexOf, lastIndexOf,
/// join, replace, split, substring, trim, lowerAscii, upperAscii, reverse, format, quote.
/// All index/offset semantics are Unicode code-point based.
/// </summary>
public static class StringsLibrary
{
    private static readonly CelType S = CelType.String;
    private static readonly CelType I = CelType.Int;

    public static readonly CelLibrary Instance = new()
    {
        Name = "strings",
        Functions = Register,
        FunctionDecls =
        [
            new FunctionDecl("charAt", [new OverloadDecl("string_char_at_int", [S, I], S, isInstance: true)]),
            new FunctionDecl("indexOf",
            [
                new OverloadDecl("string_index_of_string", [S, S], I, isInstance: true),
                new OverloadDecl("string_index_of_string_int", [S, S, I], I, isInstance: true),
            ]),
            new FunctionDecl("lastIndexOf",
            [
                new OverloadDecl("string_last_index_of_string", [S, S], I, isInstance: true),
                new OverloadDecl("string_last_index_of_string_int", [S, S, I], I, isInstance: true),
            ]),
            new FunctionDecl("join",
            [
                new OverloadDecl("list_join", [CelType.List(S)], S, isInstance: true),
                new OverloadDecl("list_join_string", [CelType.List(S), S], S, isInstance: true),
            ]),
            new FunctionDecl("replace",
            [
                new OverloadDecl("string_replace_string_string", [S, S, S], S, isInstance: true),
                new OverloadDecl("string_replace_string_string_int", [S, S, S, I], S, isInstance: true),
            ]),
            new FunctionDecl("split",
            [
                new OverloadDecl("string_split_string", [S, S], CelType.List(S), isInstance: true),
                new OverloadDecl("string_split_string_int", [S, S, I], CelType.List(S), isInstance: true),
            ]),
            new FunctionDecl("substring",
            [
                new OverloadDecl("string_substring_int", [S, I], S, isInstance: true),
                new OverloadDecl("string_substring_int_int", [S, I, I], S, isInstance: true),
            ]),
            new FunctionDecl("trim", [new OverloadDecl("string_trim", [S], S, isInstance: true)]),
            new FunctionDecl("lowerAscii", [new OverloadDecl("string_lower_ascii", [S], S, isInstance: true)]),
            new FunctionDecl("upperAscii", [new OverloadDecl("string_upper_ascii", [S], S, isInstance: true)]),
            new FunctionDecl("reverse", [new OverloadDecl("string_reverse", [S], S, isInstance: true)]),
            new FunctionDecl("format", [new OverloadDecl("string_format", [S, CelType.List(CelType.Dyn)], S, isInstance: true)]),
            new FunctionDecl("strings.quote", [new OverloadDecl("strings_quote", [S], S)]),
        ],
    };

    private static int[] Runes(string s)
    {
        var runes = new List<int>(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                runes.Add(char.ConvertToUtf32(s[i], s[i + 1]));
                i++;
            }
            else
            {
                runes.Add(s[i]);
            }
        }

        return [.. runes];
    }

    private static string FromRunes(ReadOnlySpan<int> runes)
    {
        var sb = new StringBuilder(runes.Length);
        foreach (var rune in runes)
        {
            sb.Append(char.ConvertFromUtf32(rune));
        }

        return sb.ToString();
    }

    private static void Register(Stdlib.FunctionRegistry registry)
    {
        registry.Register("charAt", args =>
        {
            if (args is not [StringValue s, IntValue i])
            {
                return ErrorValue.NoSuchOverload();
            }

            var runes = Runes(s.Value);
            if (i.Value < 0 || i.Value > runes.Length)
            {
                return new ErrorValue($"index out of range: {i.Value}");
            }

            return StringValue.Of(i.Value == runes.Length ? string.Empty : char.ConvertFromUtf32(runes[(int)i.Value]));
        });

        registry.Register("indexOf", args =>
        {
            if (args is [StringValue s0, StringValue sub0])
            {
                return IndexOf(s0.Value, sub0.Value, 0, last: false);
            }

            if (args is [StringValue s1, StringValue sub1, IntValue from1])
            {
                return IndexOf(s1.Value, sub1.Value, from1.Value, last: false);
            }

            return ErrorValue.NoSuchOverload();
        });

        registry.Register("lastIndexOf", args =>
        {
            if (args is [StringValue s0, StringValue sub0])
            {
                return IndexOf(s0.Value, sub0.Value, long.MaxValue, last: true);
            }

            if (args is [StringValue s1, StringValue sub1, IntValue from1])
            {
                return IndexOf(s1.Value, sub1.Value, from1.Value, last: true);
            }

            return ErrorValue.NoSuchOverload();
        });

        registry.Register("join", args =>
        {
            var separator = string.Empty;
            ListValue? list = null;
            if (args is [ListValue l0])
            {
                list = l0;
            }
            else if (args is [ListValue l1, StringValue sep])
            {
                list = l1;
                separator = sep.Value;
            }

            if (list is null)
            {
                return ErrorValue.NoSuchOverload();
            }

            var parts = new List<string>(list.Elements.Count);
            foreach (var element in list.Elements)
            {
                if (element is not StringValue str)
                {
                    return ErrorValue.NoSuchOverload();
                }

                parts.Add(str.Value);
            }

            return StringValue.Of(string.Join(separator, parts));
        });

        registry.Register("replace", args =>
        {
            if (args is [StringValue s0, StringValue old0, StringValue new0])
            {
                return StringValue.Of(GoReplace(s0.Value, old0.Value, new0.Value, -1));
            }

            if (args is [StringValue s1, StringValue old1, StringValue new1, IntValue limit])
            {
                return StringValue.Of(GoReplace(s1.Value, old1.Value, new1.Value, limit.Value));
            }

            return ErrorValue.NoSuchOverload();
        });

        registry.Register("split", args =>
        {
            if (args is [StringValue s0, StringValue sep0])
            {
                return Split(s0.Value, sep0.Value, -1);
            }

            if (args is [StringValue s1, StringValue sep1, IntValue limit])
            {
                return Split(s1.Value, sep1.Value, limit.Value);
            }

            return ErrorValue.NoSuchOverload();
        });

        registry.Register("substring", args =>
        {
            if (args is [StringValue s0, IntValue start0])
            {
                return Substring(s0.Value, start0.Value, null);
            }

            if (args is [StringValue s1, IntValue start1, IntValue end1])
            {
                return Substring(s1.Value, start1.Value, end1.Value);
            }

            return ErrorValue.NoSuchOverload();
        });

        registry.Register("trim", args => args is [StringValue s]
            ? StringValue.Of(s.Value.Trim())
            : ErrorValue.NoSuchOverload());

        registry.Register("lowerAscii", args => args is [StringValue s]
            ? StringValue.Of(MapAscii(s.Value, upper: false))
            : ErrorValue.NoSuchOverload());

        registry.Register("upperAscii", args => args is [StringValue s]
            ? StringValue.Of(MapAscii(s.Value, upper: true))
            : ErrorValue.NoSuchOverload());

        registry.Register("reverse", args =>
        {
            if (args is not [StringValue s])
            {
                return ErrorValue.NoSuchOverload();
            }

            var runes = Runes(s.Value);
            Array.Reverse(runes);
            return StringValue.Of(FromRunes(runes));
        });

        registry.Register("format", args => args is [StringValue template, ListValue formatArgs]
            ? StringFormatter.Format(template.Value, formatArgs.Elements)
            : ErrorValue.NoSuchOverload());

        registry.Register("strings.quote", args => args is [StringValue s]
            ? StringValue.Of(Quote(s.Value))
            : ErrorValue.NoSuchOverload());
    }

    private static CelValue IndexOf(string s, string substring, long from, bool last)
    {
        var runes = Runes(s);
        var subRunes = Runes(substring);
        if (!last && subRunes.Length == 0)
        {
            return IntValue.Of(Math.Min(Math.Max(from, 0), runes.Length) is var clamped && from <= runes.Length && from >= 0 ? from : from < 0 ? -1 : runes.Length);
        }

        var defaultFrom = last && from == long.MaxValue;
        if (defaultFrom)
        {
            from = runes.Length - 1;
            if (subRunes.Length == 0)
            {
                return IntValue.Of(runes.Length);
            }

            if (subRunes.Length > runes.Length)
            {
                return IntValue.Of(-1); // needle longer than haystack: not found, not an error
            }
        }
        else if (last && subRunes.Length == 0)
        {
            if (from < 0 || from > runes.Length)
            {
                return new ErrorValue($"index out of range: {from}");
            }

            return IntValue.Of(from);
        }

        if (from < 0 || (from >= runes.Length && runes.Length > 0 && !last))
        {
            return new ErrorValue($"index out of range: {from}");
        }

        if (last)
        {
            if (!defaultFrom && from >= runes.Length)
            {
                return new ErrorValue($"index out of range: {from}");
            }

            for (var start = Math.Min((int)from, runes.Length - subRunes.Length); start >= 0; start--)
            {
                if (MatchesAt(runes, subRunes, start))
                {
                    return IntValue.Of(start);
                }
            }

            return IntValue.Of(-1);
        }

        for (var start = (int)from; start + subRunes.Length <= runes.Length; start++)
        {
            if (MatchesAt(runes, subRunes, start))
            {
                return IntValue.Of(start);
            }
        }

        return IntValue.Of(-1);
    }

    private static bool MatchesAt(int[] runes, int[] sub, int start)
    {
        for (var i = 0; i < sub.Length; i++)
        {
            if (runes[start + i] != sub[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Go strings.Replace semantics, including empty-old insertion between runes.</summary>
    private static string GoReplace(string s, string old, string replacement, long limit)
    {
        if (limit == 0 || (old == replacement))
        {
            return s;
        }

        var sb = new StringBuilder();
        var count = 0L;
        if (old.Length == 0)
        {
            var runes = Runes(s);
            sb.Append(replacement);
            count++;
            foreach (var rune in runes)
            {
                sb.Append(char.ConvertFromUtf32(rune));
                if (limit < 0 || count < limit)
                {
                    sb.Append(replacement);
                    count++;
                }
            }

            return sb.ToString();
        }

        var index = 0;
        while (index < s.Length && (limit < 0 || count < limit))
        {
            var found = s.IndexOf(old, index, StringComparison.Ordinal);
            if (found < 0)
            {
                break;
            }

            sb.Append(s, index, found - index).Append(replacement);
            index = found + old.Length;
            count++;
        }

        return sb.Append(s, index, s.Length - index).ToString();
    }

    /// <summary>Go strings.SplitN semantics; empty separator splits into individual code points.</summary>
    private static CelValue Split(string s, string separator, long limit)
    {
        if (limit == 0)
        {
            return ListValue.Empty;
        }

        var parts = new List<CelValue>();
        if (separator.Length == 0)
        {
            foreach (var rune in Runes(s))
            {
                if (limit > 0 && parts.Count == limit - 1)
                {
                    // remainder — Go semantics keep the rest joined
                    var index = FromRunesPrefixLength(s, parts.Count);
                    parts.Add(StringValue.Of(s[index..]));
                    return ListValue.Of(parts);
                }

                parts.Add(StringValue.Of(char.ConvertFromUtf32(rune)));
            }

            return ListValue.Of(parts);
        }

        var start = 0;
        while (true)
        {
            if (limit > 0 && parts.Count == limit - 1)
            {
                break;
            }

            var found = s.IndexOf(separator, start, StringComparison.Ordinal);
            if (found < 0)
            {
                break;
            }

            parts.Add(StringValue.Of(s[start..found]));
            start = found + separator.Length;
        }

        parts.Add(StringValue.Of(s[start..]));
        return ListValue.Of(parts);
    }

    private static int FromRunesPrefixLength(string s, int runeCount)
    {
        var index = 0;
        for (var i = 0; i < runeCount && index < s.Length; i++)
        {
            index += char.IsHighSurrogate(s[index]) && index + 1 < s.Length && char.IsLowSurrogate(s[index + 1]) ? 2 : 1;
        }

        return index;
    }

    private static CelValue Substring(string s, long start, long? end)
    {
        var runes = Runes(s);
        var to = end ?? runes.Length;
        if (start < 0 || start > runes.Length)
        {
            return new ErrorValue($"index out of range: {start}");
        }

        if (to < 0 || to > runes.Length)
        {
            return new ErrorValue($"index out of range: {to}");
        }

        if (to < start)
        {
            return new ErrorValue($"invalid substring range. start: {start}, end: {to}");
        }

        return StringValue.Of(FromRunes(runes.AsSpan((int)start, (int)(to - start))));
    }

    private static string MapAscii(string s, bool upper)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            sb.Append(upper
                ? c is >= 'a' and <= 'z' ? (char)(c - 32) : c
                : c is >= 'A' and <= 'Z' ? (char)(c + 32) : c);
        }

        return sb.ToString();
    }

    /// <summary>CEL string quoting: double-quoted with control-character escapes.</summary>
    private static string Quote(string s)
    {
        var sb = new StringBuilder(s.Length + 2).Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '\a': sb.Append("\\a"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\v': sb.Append("\\v"); break;
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                default: sb.Append(c); break;
            }
        }

        return sb.Append('"').ToString();
    }
}
