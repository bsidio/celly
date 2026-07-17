using System.Globalization;
using System.Text;

namespace Celly.Parsing;

/// <summary>Thrown for lexical errors; converted to a <see cref="Common.CelIssue"/> by the parser.</summary>
public sealed class LexException(int offset, string message) : Exception(message)
{
    public int Offset { get; } = offset;
}

/// <summary>
/// Hand-written lexer for the CEL grammar (cel-spec doc/langdef.md, Syntax section).
/// Numeric int/double literals keep their raw text so the parser can fold a leading unary minus
/// (required for <c>-9223372036854775808</c> and <c>-0.0</c>).
/// </summary>
public sealed class Lexer
{
    private static readonly HashSet<string> ReservedWords =
    [
        "as", "break", "const", "continue", "else", "for", "function", "if", "import",
        "let", "loop", "package", "namespace", "return", "var", "void", "while",
    ];

    private readonly string _text;
    private int _pos;

    private Lexer(string text) => _text = text;

    public static List<Token> Tokenize(string text) => new Lexer(text).Run();

    private List<Token> Run()
    {
        var tokens = new List<Token>();
        while (true)
        {
            SkipWhitespaceAndComments();
            if (_pos >= _text.Length)
            {
                tokens.Add(new Token(TokenKind.EndOfFile, _pos, 0));
                return tokens;
            }

            tokens.Add(NextToken());
        }
    }

    private void SkipWhitespaceAndComments()
    {
        while (_pos < _text.Length)
        {
            var c = _text[_pos];
            if (c is ' ' or '\t' or '\r' or '\n' or '\f')
            {
                _pos++;
            }
            else if (c == '/' && _pos + 1 < _text.Length && _text[_pos + 1] == '/')
            {
                while (_pos < _text.Length && _text[_pos] != '\n')
                {
                    _pos++;
                }
            }
            else
            {
                break;
            }
        }
    }

    private Token NextToken()
    {
        var start = _pos;
        var c = _text[_pos];

        if (char.IsAsciiDigit(c))
        {
            return LexNumber();
        }

        if (c == '.' && _pos + 1 < _text.Length && char.IsAsciiDigit(_text[_pos + 1]))
        {
            return LexNumber();
        }

        if (c == '_' || char.IsAsciiLetter(c))
        {
            return LexIdentOrString();
        }

        if (c is '"' or '\'')
        {
            return LexString(start, isRaw: false, isBytes: false);
        }

        if (c == '`')
        {
            return LexEscapedIdent();
        }

        _pos++;
        switch (c)
        {
            case '(': return new Token(TokenKind.LParen, start, 1);
            case ')': return new Token(TokenKind.RParen, start, 1);
            case '[': return new Token(TokenKind.LBracket, start, 1);
            case ']': return new Token(TokenKind.RBracket, start, 1);
            case '{': return new Token(TokenKind.LBrace, start, 1);
            case '}': return new Token(TokenKind.RBrace, start, 1);
            case '.': return new Token(TokenKind.Dot, start, 1);
            case ',': return new Token(TokenKind.Comma, start, 1);
            case ':': return new Token(TokenKind.Colon, start, 1);
            case '?': return new Token(TokenKind.Question, start, 1);
            case '+': return new Token(TokenKind.Plus, start, 1);
            case '-': return new Token(TokenKind.Minus, start, 1);
            case '*': return new Token(TokenKind.Star, start, 1);
            case '/': return new Token(TokenKind.Slash, start, 1);
            case '%': return new Token(TokenKind.Percent, start, 1);
            case '<':
                return Take('=') ? new Token(TokenKind.LessEquals, start, 2) : new Token(TokenKind.Less, start, 1);
            case '>':
                return Take('=') ? new Token(TokenKind.GreaterEquals, start, 2) : new Token(TokenKind.Greater, start, 1);
            case '!':
                return Take('=') ? new Token(TokenKind.NotEquals, start, 2) : new Token(TokenKind.Not, start, 1);
            case '=':
                if (Take('='))
                {
                    return new Token(TokenKind.EqualsEquals, start, 2);
                }

                throw new LexException(start, "unexpected character '=' (did you mean '==' ?)");
            case '&':
                if (Take('&'))
                {
                    return new Token(TokenKind.AndAnd, start, 2);
                }

                throw new LexException(start, "unexpected character '&' (did you mean '&&' ?)");
            case '|':
                if (Take('|'))
                {
                    return new Token(TokenKind.OrOr, start, 2);
                }

                throw new LexException(start, "unexpected character '|' (did you mean '||' ?)");
            default:
                throw new LexException(start, $"unexpected character '{c}'");
        }
    }

    private bool Take(char expected)
    {
        if (_pos < _text.Length && _text[_pos] == expected)
        {
            _pos++;
            return true;
        }

        return false;
    }

    /// <summary>Backtick-escaped identifier: letters, digits, and [_.-/ ], selector positions only.</summary>
    private Token LexEscapedIdent()
    {
        var start = _pos;
        _pos++; // opening backtick
        var contentStart = _pos;
        while (_pos < _text.Length && _text[_pos] != '`')
        {
            var c = _text[_pos];
            if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-' or '/' or ' '))
            {
                throw new LexException(_pos, $"invalid character '{c}' in escaped identifier");
            }

            _pos++;
        }

        if (_pos >= _text.Length)
        {
            throw new LexException(start, "unterminated escaped identifier");
        }

        if (_pos == contentStart)
        {
            throw new LexException(start, "empty escaped identifier");
        }

        var name = _text[contentStart.._pos];
        _pos++; // closing backtick
        return new Token(TokenKind.EscapedIdent, start, _pos - start, name);
    }

    // ---- identifiers, keywords, and string prefixes -------------------------------------------

    private Token LexIdentOrString()
    {
        var start = _pos;

        // r/R/b/B prefixes directly attached to a quote start a string/bytes literal.
        var (isRaw, isBytes, prefixLen) = ScanStringPrefix();
        if (prefixLen > 0)
        {
            _pos += prefixLen;
            return LexString(start, isRaw, isBytes);
        }

        while (_pos < _text.Length && (_text[_pos] == '_' || char.IsAsciiLetterOrDigit(_text[_pos])))
        {
            _pos++;
        }

        var name = _text[start.._pos];
        return name switch
        {
            "true" => new Token(TokenKind.True, start, _pos - start),
            "false" => new Token(TokenKind.False, start, _pos - start),
            "null" => new Token(TokenKind.Null, start, _pos - start),
            "in" => new Token(TokenKind.In, start, _pos - start),
            _ when ReservedWords.Contains(name) => new Token(TokenKind.Reserved, start, _pos - start, name),
            _ => new Token(TokenKind.Ident, start, _pos - start, name),
        };
    }

    private (bool IsRaw, bool IsBytes, int PrefixLen) ScanStringPrefix()
    {
        var isRaw = false;
        var isBytes = false;
        var i = _pos;
        for (var n = 0; n < 2 && i < _text.Length; n++, i++)
        {
            var c = _text[i];
            if ((c is 'r' or 'R') && !isRaw)
            {
                isRaw = true;
            }
            else if ((c is 'b' or 'B') && !isBytes)
            {
                isBytes = true;
            }
            else
            {
                break;
            }
        }

        if (i < _text.Length && _text[i] is '"' or '\'')
        {
            return (isRaw, isBytes, i - _pos);
        }

        return (false, false, 0);
    }

    // ---- numbers -------------------------------------------------------------------------------

    private Token LexNumber()
    {
        var start = _pos;

        if (_text[_pos] == '0' && _pos + 1 < _text.Length && (_text[_pos + 1] is 'x' or 'X'))
        {
            _pos += 2;
            var digitsStart = _pos;
            while (_pos < _text.Length && char.IsAsciiHexDigit(_text[_pos]))
            {
                _pos++;
            }

            if (_pos == digitsStart)
            {
                throw new LexException(start, "invalid hex literal");
            }

            if (_pos < _text.Length && (_text[_pos] is 'u' or 'U'))
            {
                var hex = _text[digitsStart.._pos];
                _pos++;
                return new Token(TokenKind.UintLit, start, _pos - start, ParseUint(hex, isHex: true, start));
            }

            return new Token(TokenKind.IntLit, start, _pos - start, _text[start.._pos]);
        }

        var isDouble = false;
        if (_text[_pos] == '.')
        {
            isDouble = true;
            _pos++; // leading-dot float: .5
        }

        while (_pos < _text.Length && char.IsAsciiDigit(_text[_pos]))
        {
            _pos++;
        }

        if (!isDouble && _pos + 1 < _text.Length && _text[_pos] == '.' && char.IsAsciiDigit(_text[_pos + 1]))
        {
            isDouble = true;
            _pos++;
            while (_pos < _text.Length && char.IsAsciiDigit(_text[_pos]))
            {
                _pos++;
            }
        }

        if (_pos < _text.Length && (_text[_pos] is 'e' or 'E'))
        {
            var save = _pos;
            _pos++;
            if (_pos < _text.Length && (_text[_pos] is '+' or '-'))
            {
                _pos++;
            }

            if (_pos < _text.Length && char.IsAsciiDigit(_text[_pos]))
            {
                isDouble = true;
                while (_pos < _text.Length && char.IsAsciiDigit(_text[_pos]))
                {
                    _pos++;
                }
            }
            else
            {
                _pos = save; // not an exponent — e.g. `3e` starts identifier-ish garbage; let parser complain
            }
        }

        if (isDouble)
        {
            return new Token(TokenKind.DoubleLit, start, _pos - start, _text[start.._pos]);
        }

        if (_pos < _text.Length && (_text[_pos] is 'u' or 'U'))
        {
            var digits = _text[start.._pos];
            _pos++;
            return new Token(TokenKind.UintLit, start, _pos - start, ParseUint(digits, isHex: false, start));
        }

        return new Token(TokenKind.IntLit, start, _pos - start, _text[start.._pos]);
    }

    private static ulong ParseUint(string digits, bool isHex, int offset)
    {
        try
        {
            return isHex
                ? ulong.Parse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                : ulong.Parse(digits, CultureInfo.InvariantCulture);
        }
        catch (OverflowException)
        {
            throw new LexException(offset, "invalid unsigned integer literal: value out of range");
        }
    }

    // ---- strings and bytes ---------------------------------------------------------------------

    private Token LexString(int start, bool isRaw, bool isBytes)
    {
        var quote = _text[_pos];
        var tripled = _pos + 2 < _text.Length && _text[_pos + 1] == quote && _text[_pos + 2] == quote;
        _pos += tripled ? 3 : 1;

        var contentStart = _pos;
        while (true)
        {
            if (_pos >= _text.Length)
            {
                throw new LexException(start, "unterminated string literal");
            }

            var c = _text[_pos];
            if (c == quote)
            {
                if (!tripled)
                {
                    break;
                }

                if (_pos + 2 < _text.Length && _text[_pos + 1] == quote && _text[_pos + 2] == quote)
                {
                    break;
                }

                _pos++;
                continue;
            }

            if (!tripled && (c == '\n' || c == '\r'))
            {
                throw new LexException(start, "unterminated string literal (newline in single-quoted string)");
            }

            if (c == '\\' && !isRaw)
            {
                _pos++; // skip escape introducer; validity checked during decoding
                if (_pos >= _text.Length)
                {
                    throw new LexException(start, "unterminated string literal");
                }
            }

            _pos++;
        }

        var contentEnd = _pos;
        _pos += tripled ? 3 : 1;

        var content = _text[contentStart..contentEnd];
        if (isBytes)
        {
            var bytes = DecodeBytes(content, isRaw, contentStart);
            return new Token(TokenKind.BytesLit, start, _pos - start, bytes);
        }

        var str = DecodeString(content, isRaw, contentStart);
        return new Token(TokenKind.StringLit, start, _pos - start, str);
    }

    private static string DecodeString(string content, bool isRaw, int offset)
    {
        if (isRaw)
        {
            return content;
        }

        var sb = new StringBuilder(content.Length);
        var i = 0;
        while (i < content.Length)
        {
            var c = content[i];
            if (c != '\\')
            {
                sb.Append(c);
                i++;
                continue;
            }

            i++; // past backslash
            var (codePoint, consumed) = DecodeEscape(content, i, offset, allowUnicode: true);
            i += consumed;
            sb.Append(char.ConvertFromUtf32(codePoint));
        }

        return sb.ToString();
    }

    private static byte[] DecodeBytes(string content, bool isRaw, int offset)
    {
        var bytes = new List<byte>(content.Length);
        var i = 0;
        while (i < content.Length)
        {
            var c = content[i];
            if (c != '\\' || isRaw)
            {
                // Encode the source character(s) as UTF-8.
                if (char.IsHighSurrogate(c) && i + 1 < content.Length && char.IsLowSurrogate(content[i + 1]))
                {
                    bytes.AddRange(Encoding.UTF8.GetBytes(content.AsSpan(i, 2).ToString()));
                    i += 2;
                }
                else
                {
                    bytes.AddRange(Encoding.UTF8.GetBytes(content.AsSpan(i, 1).ToString()));
                    i++;
                }

                continue;
            }

            i++; // past backslash
            var next = i < content.Length ? content[i] : '\0';
            if (next is 'x' or 'X' || (next >= '0' && next <= '3'))
            {
                // \xHH and octal escapes denote single OCTETS in bytes literals.
                var (value, consumed) = DecodeEscape(content, i, offset, allowUnicode: false);
                bytes.Add((byte)value);
                i += consumed;
            }
            else
            {
                var (codePoint, consumed) = DecodeEscape(content, i, offset, allowUnicode: false);
                i += consumed;
                bytes.AddRange(Encoding.UTF8.GetBytes(char.ConvertFromUtf32(codePoint)));
            }
        }

        return [.. bytes];
    }

    /// <summary>Decodes one escape sequence after the backslash; returns (code point/octet, chars consumed).</summary>
    private static (int Value, int Consumed) DecodeEscape(string content, int i, int offset, bool allowUnicode)
    {
        if (i >= content.Length)
        {
            throw new LexException(offset + i, "invalid escape: trailing backslash");
        }

        var c = content[i];
        switch (c)
        {
            case 'a': return (0x07, 1);
            case 'b': return (0x08, 1);
            case 'f': return (0x0C, 1);
            case 'n': return (0x0A, 1);
            case 'r': return (0x0D, 1);
            case 't': return (0x09, 1);
            case 'v': return (0x0B, 1);
            case '\\': return ('\\', 1);
            case '?': return ('?', 1);
            case '"': return ('"', 1);
            case '\'': return ('\'', 1);
            case '`': return ('`', 1);
            case 'x' or 'X':
                return ((int)ParseHex(content, i + 1, 2, offset), 3);
            case 'u' when allowUnicode:
                return (ValidateCodePoint(ParseHex(content, i + 1, 4, offset), offset + i), 5);
            case 'U' when allowUnicode:
                return (ValidateCodePoint(ParseHex(content, i + 1, 8, offset), offset + i), 9);
            case >= '0' and <= '3':
                if (i + 2 >= content.Length || content[i + 1] is < '0' or > '7' || content[i + 2] is < '0' or > '7')
                {
                    throw new LexException(offset + i, "invalid octal escape (expect 3 digits, [0-3][0-7][0-7])");
                }

                return (((c - '0') << 6) | ((content[i + 1] - '0') << 3) | (content[i + 2] - '0'), 3);
            default:
                throw new LexException(offset + i, $"invalid escape sequence '\\{c}'");
        }
    }

    // Accumulates into a long: an 8-digit \U escape can reach 0xFFFFFFFF, which overflows int.
    private static long ParseHex(string content, int i, int count, int offset)
    {
        if (i + count > content.Length)
        {
            throw new LexException(offset + i, "invalid hex escape: too few digits");
        }

        var value = 0L;
        for (var k = 0; k < count; k++)
        {
            var c = content[i + k];
            long digit = c switch
            {
                >= '0' and <= '9' => c - '0',
                >= 'a' and <= 'f' => c - 'a' + 10,
                >= 'A' and <= 'F' => c - 'A' + 10,
                _ => throw new LexException(offset + i + k, "invalid hex escape digit"),
            };
            value = (value << 4) | digit;
        }

        return value;
    }

    private static int ValidateCodePoint(long value, int offset)
    {
        if (value is >= 0xD800 and <= 0xDFFF)
        {
            throw new LexException(offset, "invalid unicode escape: surrogate code points are not allowed");
        }

        if (value > 0x10FFFF)
        {
            throw new LexException(offset, "invalid unicode escape: code point out of range");
        }

        return (int)value;
    }
}
