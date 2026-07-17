using System.Globalization;
using Celly.Ast;
using Celly.Common;

namespace Celly.Parsing;

public sealed class ParserOptions
{
    public static readonly ParserOptions Default = new();

    /// <summary>Maximum parser recursion depth (spec minimum capacity is 12; cel-go default is 250).</summary>
    public int MaxRecursionDepth { get; init; } = 250;

    /// <summary>
    /// Maximum AST depth (spine length). Bounds the recursion of the later checker/planner/
    /// evaluator over the parsed tree. Operator chains parse iteratively, so a very long flat
    /// chain (<c>1+1+…</c>) yields a shallow parser recursion but a deep AST; this cap keeps such
    /// input from overflowing the stack downstream. Far above the spec minimums and any real
    /// expression.
    /// </summary>
    public int MaxExpressionDepth { get; init; } = 1000;

    /// <summary>Enables optional-syntax parsing: <c>a.?b</c>, <c>a[?b]</c>, <c>[?e]</c>, <c>{?k: v}</c>.</summary>
    public bool EnableOptionalSyntax { get; init; } = true;

    /// <summary>Parse-time macros. Defaults to the standard CEL macros.</summary>
    public IReadOnlyList<Macro> Macros { get; init; } = StandardMacros.All;
}

public sealed class ParseResult
{
    internal ParseResult(CelAbstractSyntax? ast, IReadOnlyList<CelIssue> issues)
    {
        Ast = ast;
        Issues = issues;
    }

    /// <summary>The parsed AST, or null when parsing failed.</summary>
    public CelAbstractSyntax? Ast { get; }

    public IReadOnlyList<CelIssue> Issues { get; }

    public bool HasErrors => Ast is null;
}

/// <summary>
/// Hand-written recursive-descent parser for CEL (cel-spec doc/langdef.md, Syntax section).
/// Binary operator chains are parsed iteratively so flat chains never consume stack depth;
/// only true nesting (parentheses, ternaries, calls, literals) counts against the depth limit.
/// </summary>
public sealed class CelParser
{
    private readonly Source _source;
    private readonly ParserOptions _options;
    private readonly Dictionary<(string, int, bool), Macro> _macros;
    private readonly SourceInfo _sourceInfo;
    private List<Token> _tokens = [];
    private int _pos;
    private long _lastId;
    private int _depth;

    private CelParser(Source source, ParserOptions options)
    {
        _source = source;
        _options = options;
        _sourceInfo = new SourceInfo(source);
        _macros = new Dictionary<(string, int, bool), Macro>();
        foreach (var m in options.Macros)
        {
            _macros[m.Key] = m;
        }
    }

    public static ParseResult Parse(string expression, ParserOptions? options = null) =>
        Parse(Source.FromText(expression), options);

    public static ParseResult Parse(Source source, ParserOptions? options = null)
    {
        var parser = new CelParser(source, options ?? ParserOptions.Default);
        var reporter = new ErrorReporter();
        try
        {
            var expr = parser.Run();
            return new ParseResult(new CelAbstractSyntax(expr, parser._sourceInfo), reporter.Issues);
        }
        catch (LexException ex)
        {
            reporter.ReportError(source.LocationOf(ex.Offset), ex.Message);
            return new ParseResult(null, reporter.Issues);
        }
        catch (ParseAbortException ex)
        {
            reporter.ReportError(source.LocationOf(ex.Offset), ex.Message);
            return new ParseResult(null, reporter.Issues);
        }
    }

    private sealed class ParseAbortException(int offset, string message) : Exception(message)
    {
        public int Offset { get; } = offset;
    }

    private Expr Run()
    {
        _tokens = Lexer.Tokenize(_source.Text);
        var expr = ParseExpr();
        if (Current.Kind != TokenKind.EndOfFile)
        {
            throw Abort($"unexpected token {Describe(Current)} after expression");
        }

        // Reject over-deep ASTs before they reach the (recursive) checker/planner/evaluator.
        // Computed iteratively so the check itself cannot overflow.
        var depth = MaxDepth(expr);
        if (depth > _options.MaxExpressionDepth)
        {
            throw new ParseAbortException(0, $"expression depth limit exceeded: {depth} > {_options.MaxExpressionDepth}");
        }

        return expr;
    }

    private static int MaxDepth(Expr root)
    {
        var max = 0;
        var stack = new Stack<(Expr Node, int Depth)>();
        stack.Push((root, 1));
        while (stack.Count > 0)
        {
            var (node, depth) = stack.Pop();
            if (depth > max)
            {
                max = depth;
            }

            foreach (var child in Ast.AstTools.Children(node))
            {
                stack.Push((child, depth + 1));
            }
        }

        return max;
    }

    // ---- token helpers -------------------------------------------------------------------------

    private Token Current => _tokens[_pos];

    private Token Peek(int n) => _pos + n < _tokens.Count ? _tokens[_pos + n] : _tokens[^1];

    private Token Advance() => _tokens[_pos++];

    private bool Take(TokenKind kind)
    {
        if (Current.Kind == kind)
        {
            _pos++;
            return true;
        }

        return false;
    }

    private Token Expect(TokenKind kind, string what)
    {
        if (Current.Kind != kind)
        {
            throw Abort($"expected {what}, found {Describe(Current)}");
        }

        return Advance();
    }

    private ParseAbortException Abort(string message) => new(Current.Offset, message);

    private static string Describe(Token token) => token.Kind switch
    {
        TokenKind.EndOfFile => "end of expression",
        TokenKind.Ident or TokenKind.Reserved => $"'{token.Text}'",
        TokenKind.StringLit => "string literal",
        TokenKind.BytesLit => "bytes literal",
        TokenKind.IntLit or TokenKind.UintLit or TokenKind.DoubleLit => "numeric literal",
        _ => $"'{TokenText(token.Kind)}'",
    };

    private static string TokenText(TokenKind kind) => kind switch
    {
        TokenKind.LParen => "(",
        TokenKind.RParen => ")",
        TokenKind.LBracket => "[",
        TokenKind.RBracket => "]",
        TokenKind.LBrace => "{",
        TokenKind.RBrace => "}",
        TokenKind.Dot => ".",
        TokenKind.Comma => ",",
        TokenKind.Colon => ":",
        TokenKind.Question => "?",
        TokenKind.Plus => "+",
        TokenKind.Minus => "-",
        TokenKind.Star => "*",
        TokenKind.Slash => "/",
        TokenKind.Percent => "%",
        TokenKind.Not => "!",
        TokenKind.NotEquals => "!=",
        TokenKind.EqualsEquals => "==",
        TokenKind.Less => "<",
        TokenKind.LessEquals => "<=",
        TokenKind.Greater => ">",
        TokenKind.GreaterEquals => ">=",
        TokenKind.AndAnd => "&&",
        TokenKind.OrOr => "||",
        TokenKind.True => "true",
        TokenKind.False => "false",
        TokenKind.Null => "null",
        TokenKind.In => "in",
        _ => kind.ToString(),
    };

    private long NextId(int offset)
    {
        var id = ++_lastId;
        _sourceInfo.SetPosition(id, offset);
        return id;
    }

    // ---- grammar -------------------------------------------------------------------------------

    // Expr = ConditionalOr ["?" ConditionalOr ":" Expr]
    private Expr ParseExpr()
    {
        if (++_depth > _options.MaxRecursionDepth)
        {
            throw Abort($"expression recursion limit exceeded: {_options.MaxRecursionDepth}");
        }

        // Convert impending stack exhaustion into a clean parse error regardless of host stack size.
        try
        {
            System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
        }
        catch (InsufficientExecutionStackException)
        {
            throw Abort("expression nesting exceeds available stack");
        }

        try
        {
            var condition = ParseBinary(1);
            if (Current.Kind != TokenKind.Question)
            {
                return condition;
            }

            var op = Advance();
            var truthy = ParseBinary(1);
            Expect(TokenKind.Colon, "':'");
            var falsy = ParseExpr();
            return new CallExpr(NextId(op.Offset), null, Operators.Conditional, [condition, truthy, falsy]);
        }
        finally
        {
            _depth--;
        }
    }

    // Binary operators via precedence climbing: one stack frame per active precedence level
    // (max 5) instead of one per grammar production, keeping deep paren nesting cheap.
    private Expr ParseBinary(int minPrecedence)
    {
        var left = ParseUnary();
        while (true)
        {
            var precedence = BinaryPrecedence(Current.Kind);
            if (precedence < minPrecedence)
            {
                return left;
            }

            var op = Advance();
            var right = ParseBinary(precedence + 1);
            left = new CallExpr(NextId(op.Offset), null, BinaryFunction(op.Kind), [left, right]);
        }
    }

    private static int BinaryPrecedence(TokenKind kind) => kind switch
    {
        TokenKind.OrOr => 1,
        TokenKind.AndAnd => 2,
        TokenKind.Less or TokenKind.LessEquals or TokenKind.Greater or TokenKind.GreaterEquals
            or TokenKind.EqualsEquals or TokenKind.NotEquals or TokenKind.In => 3,
        TokenKind.Plus or TokenKind.Minus => 4,
        TokenKind.Star or TokenKind.Slash or TokenKind.Percent => 5,
        _ => 0,
    };

    private static string BinaryFunction(TokenKind kind) => kind switch
    {
        TokenKind.OrOr => Operators.LogicalOr,
        TokenKind.AndAnd => Operators.LogicalAnd,
        TokenKind.Less => Operators.Less,
        TokenKind.LessEquals => Operators.LessEquals,
        TokenKind.Greater => Operators.Greater,
        TokenKind.GreaterEquals => Operators.GreaterEquals,
        TokenKind.EqualsEquals => Operators.Equals,
        TokenKind.NotEquals => Operators.NotEquals,
        TokenKind.In => Operators.In,
        TokenKind.Plus => Operators.Add,
        TokenKind.Minus => Operators.Subtract,
        TokenKind.Star => Operators.Multiply,
        TokenKind.Slash => Operators.Divide,
        _ => Operators.Modulo,
    };

    // Unary = Member | "!" {"!"} Member | "-" {"-"} Member
    // Even runs of an operator cancel out (matching cel-go); a single '-' directly before a
    // numeric literal folds into the literal, which is what makes -9223372036854775808 and
    // -0.0 representable.
    private Expr ParseUnary()
    {
        if (Current.Kind == TokenKind.Not)
        {
            var op = Current;
            var count = 0;
            while (Take(TokenKind.Not))
            {
                count++;
            }

            var member = ParseMember();
            return count % 2 == 0 ? member : new CallExpr(NextId(op.Offset), null, Operators.LogicalNot, [member]);
        }

        if (Current.Kind == TokenKind.Minus)
        {
            var op = Current;
            var count = 0;
            while (Take(TokenKind.Minus))
            {
                count++;
            }

            if (count % 2 == 1 && Current.Kind is TokenKind.IntLit or TokenKind.DoubleLit)
            {
                // Fold the sign into the literal, then allow member suffixes on the result.
                var lit = Advance();
                var constant = lit.Kind == TokenKind.IntLit
                    ? CelConstant.Of(ParseIntLiteral(lit, negative: true))
                    : CelConstant.Of(ParseDoubleLiteral(lit, negative: true));
                var expr = new ConstExpr(NextId(op.Offset), constant);
                return ParseMemberSuffixes(expr);
            }

            var member = ParseMember();
            return count % 2 == 0 ? member : new CallExpr(NextId(op.Offset), null, Operators.Negate, [member]);
        }

        return ParseMember();
    }

    private Expr ParseMember() => ParseMemberSuffixes(ParsePrimary());

    private Expr ParseMemberSuffixes(Expr expr)
    {
        while (true)
        {
            if (Current.Kind == TokenKind.Dot)
            {
                var dot = Advance();
                if (Current.Kind == TokenKind.Question)
                {
                    if (!_options.EnableOptionalSyntax)
                    {
                        throw Abort("unsupported syntax '.?'");
                    }

                    Advance();
                    var optField = ExpectSelector("field name");
                    var fieldConst = new ConstExpr(NextId(optField.Offset), CelConstant.Of(optField.Text));
                    expr = new CallExpr(NextId(dot.Offset), null, Operators.OptSelect, [expr, fieldConst]);
                    continue;
                }

                var selector = ExpectSelector("field or method name");
                if (Current.Kind == TokenKind.LParen)
                {
                    var args = ParseCallArgs();
                    expr = MaybeExpandMacro(dot.Offset, expr, selector.Text, args)
                        ?? new CallExpr(NextId(dot.Offset), expr, selector.Text, args);
                }
                else
                {
                    expr = new SelectExpr(NextId(dot.Offset), expr, selector.Text);
                }
            }
            else if (Current.Kind == TokenKind.LBracket)
            {
                var bracket = Advance();
                if (Current.Kind == TokenKind.Question)
                {
                    if (!_options.EnableOptionalSyntax)
                    {
                        throw Abort("unsupported syntax '[?'");
                    }

                    Advance();
                    var optIndex = ParseExpr();
                    Expect(TokenKind.RBracket, "']'");
                    expr = new CallExpr(NextId(bracket.Offset), null, Operators.OptIndex, [expr, optIndex]);
                    continue;
                }

                var index = ParseExpr();
                Expect(TokenKind.RBracket, "']'");
                expr = new CallExpr(NextId(bracket.Offset), null, Operators.Index, [expr, index]);
            }
            else
            {
                return expr;
            }
        }
    }

    /// <summary>
    /// Selectors admit reserved words (SELECTOR = identifier-shaped minus KEYWORD only) and
    /// backtick-escaped identifiers.
    /// </summary>
    private Token ExpectSelector(string what)
    {
        if (Current.Kind is TokenKind.Ident or TokenKind.Reserved or TokenKind.EscapedIdent)
        {
            return Advance();
        }

        throw Abort($"expected {what}, found {Describe(Current)}");
    }

    private Expr ParsePrimary()
    {
        var token = Current;
        switch (token.Kind)
        {
            case TokenKind.LParen:
            {
                Advance();
                var expr = ParseExpr();
                Expect(TokenKind.RParen, "')'");
                return expr;
            }

            case TokenKind.LBracket:
                return ParseListLiteral();

            case TokenKind.LBrace:
                return ParseMapLiteral();

            case TokenKind.Dot:
            case TokenKind.Ident:
                return ParseIdentOrCallOrStruct();

            case TokenKind.Reserved:
                // Reserved words are legal SELECTORs, so they may appear as message-name segments.
                if (IsStructLiteralAhead())
                {
                    return ParseStructLiteral(token.Offset, leadingDot: false);
                }

                throw Abort($"reserved identifier: {token.Text}");

            case TokenKind.IntLit:
                Advance();
                return new ConstExpr(NextId(token.Offset), CelConstant.Of(ParseIntLiteral(token, negative: false)));

            case TokenKind.UintLit:
                Advance();
                return new ConstExpr(NextId(token.Offset), CelConstant.OfUint((ulong)token.Value!));

            case TokenKind.DoubleLit:
                Advance();
                return new ConstExpr(NextId(token.Offset), CelConstant.Of(ParseDoubleLiteral(token, negative: false)));

            case TokenKind.StringLit:
                Advance();
                return new ConstExpr(NextId(token.Offset), CelConstant.Of((string)token.Value!));

            case TokenKind.BytesLit:
                Advance();
                return new ConstExpr(NextId(token.Offset), CelConstant.Of((byte[])token.Value!));

            case TokenKind.True:
                Advance();
                return new ConstExpr(NextId(token.Offset), CelConstant.True);

            case TokenKind.False:
                Advance();
                return new ConstExpr(NextId(token.Offset), CelConstant.False);

            case TokenKind.Null:
                Advance();
                return new ConstExpr(NextId(token.Offset), CelConstant.Null);

            default:
                throw Abort($"unexpected token {Describe(token)}");
        }
    }

    private Expr ParseIdentOrCallOrStruct()
    {
        var start = Current;
        var leadingDot = Take(TokenKind.Dot);

        if (IsStructLiteralAhead())
        {
            return ParseStructLiteral(start.Offset, leadingDot);
        }

        // Outside struct literals, ["."] IDENT requires a plain identifier (reserved words are
        // only valid in SELECTOR positions).
        var ident = Expect(TokenKind.Ident, "identifier");
        var name = leadingDot ? "." + ident.Text : ident.Text;

        if (Current.Kind == TokenKind.LParen)
        {
            var args = ParseCallArgs();
            if (!leadingDot)
            {
                var expanded = MaybeExpandMacro(start.Offset, null, name, args);
                if (expanded is not null)
                {
                    return expanded;
                }
            }

            return new CallExpr(NextId(start.Offset), null, name, args);
        }

        return new IdentExpr(NextId(start.Offset), name);
    }

    /// <summary>
    /// Lookahead for a message-construction literal: SELECTOR ("." SELECTOR)* "{" starting at the
    /// current token. Message literals are a Primary production, so the name chain must be plain
    /// selectors — any call, index, or other suffix disqualifies it.
    /// </summary>
    private bool IsStructLiteralAhead()
    {
        var i = 0;
        if (Peek(i).Kind is not (TokenKind.Ident or TokenKind.Reserved))
        {
            return false;
        }

        i++;
        while (Peek(i).Kind == TokenKind.Dot)
        {
            i++;
            if (Peek(i).Kind is not (TokenKind.Ident or TokenKind.Reserved))
            {
                return false;
            }

            i++;
        }

        return Peek(i).Kind == TokenKind.LBrace;
    }

    private Expr ParseStructLiteral(int startOffset, bool leadingDot)
    {
        var name = leadingDot ? "." : string.Empty;
        name += ExpectSelector("type name").Text;
        while (Take(TokenKind.Dot))
        {
            name += "." + ExpectSelector("type name").Text;
        }

        var brace = Expect(TokenKind.LBrace, "'{'");
        var fields = new List<StructField>();
        while (Current.Kind != TokenKind.RBrace)
        {
            if (fields.Count == 0 && Current.Kind == TokenKind.Comma)
            {
                Advance(); // grammar allows a lone trailing comma even with no fields
                break;
            }

            var optional = false;
            if (Current.Kind == TokenKind.Question)
            {
                if (!_options.EnableOptionalSyntax)
                {
                    throw Abort("unsupported syntax '?'");
                }

                Advance();
                optional = true;
            }

            var fieldName = ExpectSelector("field name");
            Expect(TokenKind.Colon, "':'");
            var value = ParseExpr();
            fields.Add(new StructField(NextId(fieldName.Offset), fieldName.Text, value, optional));

            if (!Take(TokenKind.Comma))
            {
                break;
            }
        }

        Expect(TokenKind.RBrace, "'}'");
        return new StructExpr(NextId(brace.Offset), name, fields);
    }

    private Expr ParseListLiteral()
    {
        var bracket = Expect(TokenKind.LBracket, "'['");
        var elements = new List<Expr>();
        var optionalIndices = new List<int>();
        while (Current.Kind != TokenKind.RBracket)
        {
            if (elements.Count == 0 && Current.Kind == TokenKind.Comma)
            {
                Advance(); // lone trailing comma: "[,]"
                break;
            }

            if (Current.Kind == TokenKind.Question)
            {
                if (!_options.EnableOptionalSyntax)
                {
                    throw Abort("unsupported syntax '?'");
                }

                Advance();
                optionalIndices.Add(elements.Count);
            }

            elements.Add(ParseExpr());
            if (!Take(TokenKind.Comma))
            {
                break;
            }
        }

        Expect(TokenKind.RBracket, "']'");
        return new ListExpr(NextId(bracket.Offset), elements, optionalIndices);
    }

    private Expr ParseMapLiteral()
    {
        var brace = Expect(TokenKind.LBrace, "'{'");
        var entries = new List<MapEntry>();
        while (Current.Kind != TokenKind.RBrace)
        {
            if (entries.Count == 0 && Current.Kind == TokenKind.Comma)
            {
                Advance(); // lone trailing comma: "{,}"
                break;
            }

            var optional = false;
            if (Current.Kind == TokenKind.Question)
            {
                if (!_options.EnableOptionalSyntax)
                {
                    throw Abort("unsupported syntax '?'");
                }

                Advance();
                optional = true;
            }

            var keyToken = Current;
            var key = ParseExpr();
            Expect(TokenKind.Colon, "':'");
            var value = ParseExpr();
            entries.Add(new MapEntry(NextId(keyToken.Offset), key, value, optional));

            if (!Take(TokenKind.Comma))
            {
                break;
            }
        }

        Expect(TokenKind.RBrace, "'}'");
        return new MapExpr(NextId(brace.Offset), entries);
    }

    private List<Expr> ParseCallArgs()
    {
        Expect(TokenKind.LParen, "'('");
        var args = new List<Expr>();
        if (Current.Kind != TokenKind.RParen)
        {
            args.Add(ParseExpr());
            while (Take(TokenKind.Comma))
            {
                args.Add(ParseExpr());
            }
        }

        Expect(TokenKind.RParen, "')'");
        return args;
    }

    // ---- literals ------------------------------------------------------------------------------

    private long ParseIntLiteral(Token token, bool negative)
    {
        var raw = (string)token.Value!;
        try
        {
            if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                var magnitude = ulong.Parse(raw.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                if (negative)
                {
                    return magnitude switch
                    {
                        0x8000000000000000UL => long.MinValue,
                        > 0x8000000000000000UL => throw new OverflowException(),
                        _ => -(long)magnitude,
                    };
                }

                return magnitude > long.MaxValue ? throw new OverflowException() : (long)magnitude;
            }

            return long.Parse(negative ? "-" + raw : raw, CultureInfo.InvariantCulture);
        }
        catch (OverflowException)
        {
            throw new ParseAbortException(token.Offset, "invalid int literal: value out of range");
        }
    }

    private double ParseDoubleLiteral(Token token, bool negative)
    {
        var raw = (string)token.Value!;
        var value = double.Parse(raw, CultureInfo.InvariantCulture);
        return negative ? -value : value;
    }

    // ---- macros --------------------------------------------------------------------------------

    private Expr? MaybeExpandMacro(int callOffset, Expr? target, string function, List<Expr> args)
    {
        if (!_macros.TryGetValue((function, args.Count, target is not null), out var macro)
            && !_macros.TryGetValue((function, Macro.VarArg, target is not null), out macro))
        {
            return null;
        }

        var ctx = new MacroContext(this, callOffset);
        var expanded = macro.Expand(ctx, target, args);
        if (expanded is null)
        {
            return null; // the macro declined; parse as an ordinary call
        }

        var originalCall = new CallExpr(0, target, function, args);
        _sourceInfo.AddMacroCall(expanded.Id, originalCall);
        return expanded;
    }

    private sealed class MacroContext(CelParser parser, int offset) : IMacroContext
    {
        public long NextId() => parser.NextId(offset);

        public Expr NewConst(CelConstant value) => new ConstExpr(NextId(), value);

        public Expr NewIdent(string name) => new IdentExpr(NextId(), name);

        public Expr NewGlobalCall(string function, params Expr[] args) => new CallExpr(NextId(), null, function, args);

        public Expr NewList(params Expr[] elements) => new ListExpr(NextId(), elements, []);

        public Expr ReportError(string message) => throw new ParseAbortException(offset, message);
    }
}
