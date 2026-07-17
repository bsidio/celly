namespace Celly.Parsing;

public enum TokenKind
{
    EndOfFile,

    Ident,
    Reserved, // reserved words: usable as selectors/receiver-call names, not as identifiers

    IntLit,
    UintLit,
    DoubleLit,
    StringLit,
    BytesLit,

    True,
    False,
    Null,
    In,

    LParen,
    RParen,
    LBracket,
    RBracket,
    LBrace,
    RBrace,
    Dot,
    Comma,
    Colon,
    Question,

    Plus,
    Minus,
    Star,
    Slash,
    Percent,

    Not,
    NotEquals,
    EqualsEquals,
    Less,
    LessEquals,
    Greater,
    GreaterEquals,
    AndAnd,
    OrOr,
}

/// <summary>A lexical token. Numeric literal tokens carry raw text; the parser applies signs.</summary>
public readonly struct Token(TokenKind kind, int offset, int length, object? value = null)
{
    public TokenKind Kind { get; } = kind;

    public int Offset { get; } = offset;

    public int Length { get; } = length;

    /// <summary>
    /// Ident/Reserved: the name. StringLit: decoded string. BytesLit: decoded byte[].
    /// IntLit/DoubleLit: raw source text (sign folded by the parser). UintLit: boxed ulong.
    /// </summary>
    public object? Value { get; } = value;

    public string Text => (string)Value!;
}
