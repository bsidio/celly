using Celly.Parsing;
using Xunit;

namespace Celly.Tests.Parsing;

public class LexerTests
{
    private static Token Single(string text)
    {
        var tokens = Lexer.Tokenize(text);
        Assert.Equal(2, tokens.Count); // token + EOF
        return tokens[0];
    }

    // ---- strings -------------------------------------------------------------------------------

    [Theory]
    [InlineData("\"hello\"", "hello")]
    [InlineData("'hello'", "hello")]
    [InlineData("\"\"", "")]
    [InlineData("'wo\"rld'", "wo\"rld")]
    [InlineData("\"wo'rld\"", "wo'rld")]
    [InlineData("\"\\n\"", "\n")]
    [InlineData("\"\\a\\b\\f\\n\\r\\t\\v\"", "\a\b\f\n\r\t\v")]
    [InlineData("\"\\\\\"", "\\")]
    [InlineData("\"\\?\"", "?")]
    [InlineData("\"\\`\"", "`")]
    [InlineData("\"\\'\"", "'")]
    [InlineData("\"\\\"\"", "\"")]
    [InlineData("\"\\x41\"", "A")]
    [InlineData("\"\\X41\"", "A")]
    [InlineData("\"\\101\"", "A")]
    [InlineData("\"\\u0041\"", "A")]
    [InlineData("\"\\U00000041\"", "A")]
    [InlineData("\"\\u00e9\"", "é")]
    [InlineData("\"\\U0001F600\"", "😀")]
    [InlineData("r\"\\n\"", "\\n")]
    [InlineData("R\"\\n\"", "\\n")]
    [InlineData("r'\\x'", "\\x")]
    [InlineData("\"\"\"triple \" quote\"\"\"", "triple \" quote")]
    [InlineData("'''triple ' quote'''", "triple ' quote")]
    [InlineData("\"\"\"line1\nline2\"\"\"", "line1\nline2")]
    [InlineData("\"\"\"\"\"\"", "")]
    public void StringLiterals(string source, string expected)
    {
        var token = Single(source);
        Assert.Equal(TokenKind.StringLit, token.Kind);
        Assert.Equal(expected, (string)token.Value!);
    }

    [Theory]
    [InlineData("b\"abc\"", new byte[] { 0x61, 0x62, 0x63 })]
    [InlineData("B\"abc\"", new byte[] { 0x61, 0x62, 0x63 })]
    [InlineData("b\"\\xff\"", new byte[] { 0xFF })]
    [InlineData("b\"\\377\"", new byte[] { 0xFF })]
    [InlineData("b\"ÿ\"", new byte[] { 0xC3, 0xBF })] // non-escaped char encodes as UTF-8
    [InlineData("b\"\\n\"", new byte[] { 0x0A })]
    [InlineData("rb\"\\n\"", new byte[] { 0x5C, 0x6E })] // raw bytes: backslash-n literally
    [InlineData("br\"\\n\"", new byte[] { 0x5C, 0x6E })]
    [InlineData("b'😀'", new byte[] { 0xF0, 0x9F, 0x98, 0x80 })]
    public void BytesLiterals(string source, byte[] expected)
    {
        var token = Single(source);
        Assert.Equal(TokenKind.BytesLit, token.Kind);
        Assert.Equal(expected, (byte[])token.Value!);
    }

    [Theory]
    [InlineData("\"\\u2FE0")] // unterminated
    [InlineData("\"abc")]
    [InlineData("'abc\ndef'")] // newline in single-quoted string
    [InlineData("\"\\ud800\"")] // surrogate escape
    [InlineData("\"\\udfff\"")]
    [InlineData("\"\\U00110000\"")] // beyond U+10FFFF
    [InlineData("\"\\s\"")] // invalid escape
    [InlineData("\"\\x4\"")] // short hex
    [InlineData("\"\\u041\"")] // short unicode
    [InlineData("\"\\400\"")] // octal first digit out of range -> invalid escape '\4'
    [InlineData("\"\\088\"")] // invalid octal digits
    [InlineData("b\"\\u0041\"")] // \u not allowed in bytes
    [InlineData("b\"\\U00000041\"")]
    public void InvalidStrings(string source) => Assert.Throws<LexException>(() => Lexer.Tokenize(source));

    // ---- numbers -------------------------------------------------------------------------------

    [Theory]
    [InlineData("0", TokenKind.IntLit, "0")]
    [InlineData("123", TokenKind.IntLit, "123")]
    [InlineData("0x1F", TokenKind.IntLit, "0x1F")]
    [InlineData("0XA", TokenKind.IntLit, "0XA")]
    [InlineData("9223372036854775808", TokenKind.IntLit, "9223372036854775808")] // overflow deferred to parser
    [InlineData("1.5", TokenKind.DoubleLit, "1.5")]
    [InlineData(".5", TokenKind.DoubleLit, ".5")]
    [InlineData("1e3", TokenKind.DoubleLit, "1e3")]
    [InlineData("1.5e-3", TokenKind.DoubleLit, "1.5e-3")]
    [InlineData("2E+2", TokenKind.DoubleLit, "2E+2")]
    public void NumericLiteralsKeepRawText(string source, TokenKind kind, string raw)
    {
        var token = Single(source);
        Assert.Equal(kind, token.Kind);
        Assert.Equal(raw, (string)token.Value!);
    }

    [Theory]
    [InlineData("0u", 0UL)]
    [InlineData("42u", 42UL)]
    [InlineData("42U", 42UL)]
    [InlineData("0xFFu", 255UL)]
    [InlineData("18446744073709551615u", ulong.MaxValue)]
    public void UintLiterals(string source, ulong expected)
    {
        var token = Single(source);
        Assert.Equal(TokenKind.UintLit, token.Kind);
        Assert.Equal(expected, (ulong)token.Value!);
    }

    [Fact]
    public void UintOverflowIsLexError() =>
        Assert.Throws<LexException>(() => Lexer.Tokenize("18446744073709551616u"));

    // ---- identifiers and keywords ---------------------------------------------------------------

    [Theory]
    [InlineData("abc", TokenKind.Ident)]
    [InlineData("_ab1", TokenKind.Ident)]
    [InlineData("r", TokenKind.Ident)] // bare prefix letters are identifiers
    [InlineData("b", TokenKind.Ident)]
    [InlineData("rb", TokenKind.Ident)]
    [InlineData("true", TokenKind.True)]
    [InlineData("false", TokenKind.False)]
    [InlineData("null", TokenKind.Null)]
    [InlineData("in", TokenKind.In)]
    [InlineData("while", TokenKind.Reserved)]
    [InlineData("import", TokenKind.Reserved)]
    [InlineData("void", TokenKind.Reserved)]
    public void IdentifiersAndKeywords(string source, TokenKind kind) => Assert.Equal(kind, Single(source).Kind);

    // ---- operators, comments, errors -------------------------------------------------------------

    [Fact]
    public void OperatorsAndComments()
    {
        var kinds = Lexer.Tokenize("a && b || !c == d // trailing comment\n < <= > >= != ? : .").Select(t => t.Kind).ToArray();
        Assert.Equal(
            [
                TokenKind.Ident, TokenKind.AndAnd, TokenKind.Ident, TokenKind.OrOr, TokenKind.Not,
                TokenKind.Ident, TokenKind.EqualsEquals, TokenKind.Ident,
                TokenKind.Less, TokenKind.LessEquals, TokenKind.Greater, TokenKind.GreaterEquals,
                TokenKind.NotEquals, TokenKind.Question, TokenKind.Colon, TokenKind.Dot,
                TokenKind.EndOfFile,
            ],
            kinds);
    }

    [Theory]
    [InlineData("=")]
    [InlineData("&")]
    [InlineData("|")]
    [InlineData("@")]
    [InlineData("#")]
    [InlineData("^")]
    public void UnexpectedCharacters(string source) => Assert.Throws<LexException>(() => Lexer.Tokenize(source));
}
