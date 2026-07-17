# Lexer & Parser

Celly's front end is hand-written — no ANTLR, no parser generator. The CEL grammar is small
(~15 productions), and a hand-rolled recursive-descent parser gives precise error messages,
zero build-time dependencies, and full control over the spec's edge cases.

## Lexer (`src/Celly/Parsing/Lexer.cs`)

A single forward scan producing a `List<Token>`. Each token carries its kind, source
offset, and (for literals) a decoded value. The interesting parts:

**String literals** support every CEL form — `"…"`, `'…'`, triple-quoted `"""…"""`
(may span lines), raw `r"…"` (no escape processing), and bytes `b"…"` — with the full
escape set: `\n`-style controls, `\ooo` octal, `\xHH` hex, `\uHHHH`/`\UHHHHHHHH` unicode.
The lexer *rejects* surrogate code points and values above `U+10FFFF` at lex time, as the
spec requires. In bytes literals, `\xHH`/octal denote single **octets**, and `\u` escapes
are illegal.

**Numbers keep their raw text.** The lexer does *not* parse `123` into a long — it stores
the string. Why: `-9223372036854775808` (int64 min) only exists as a *negated* literal.
Its magnitude overflows on its own, so the sign must be folded in before parsing. The
parser does that (see below). Only `u`-suffixed uints are parsed in the lexer (their range
is unambiguous).

**Identifier subtleties**: `true/false/null/in` are keywords; `while`, `import`, etc. are
*reserved* — banned as bare identifiers but legal as **selectors** (`a.while` is valid CEL,
per the spec's `SELECTOR = identifier − KEYWORD` rule). Backtick-escaped identifiers
(`` `content-type` ``) lex as a distinct kind, valid only in selector/field positions.

## Parser (`src/Celly/Parsing/Parser.cs`)

### Precedence climbing, not a production cascade

The naive translation of the grammar (`ParseOr → ParseAnd → ParseRelation → ParseAdd →
ParseMul → ParseUnary → …`) costs ~10 stack frames *per nesting level*, and the spec
mandates surviving deeply nested input. Celly instead parses all binary operators in one
function:

```csharp
private Expr ParseBinary(int minPrecedence)
{
    var left = ParseUnary();
    while (true)
    {
        var prec = BinaryPrecedence(Current.Kind);   // || < && < relations < +- < */%
        if (prec < minPrecedence) return left;
        var op = Advance();
        var right = ParseBinary(prec + 1);           // recurse only for HIGHER precedence
        left = new CallExpr(NextId(op.Offset), null, BinaryFunction(op.Kind), [left, right]);
    }
}
```

Flat chains (`a + b + c + …`) loop with **zero** extra stack; recursion depth is bounded by
the number of distinct precedence levels (5). Deep *paren* nesting costs ~6 frames/level.

Two guards make hostile input a clean parse error rather than a crash:

- a depth counter (default limit 250), and
- `RuntimeHelpers.EnsureSufficientExecutionStack()` — if the host thread's stack is nearly
  exhausted, parsing aborts with "expression nesting exceeds available stack".

### Operators become calls

There are no operator nodes. `a + b` parses as `CallExpr("_+_", [a, b])`, `!x` as
`CallExpr("!_", [x])`, `a[i]` as `CallExpr("_[_]", [a, i])`. The internal names
(`src/Celly/Common/Operators.cs`) are the spec's canonical operator function names — the
checker declares overloads under them and the evaluator dispatches on them. One uniform
mechanism instead of special cases.

### Literal sign folding

In `ParseUnary`, a run of `-` tokens followed directly by a numeric literal folds one sign
into the literal (`-9223372036854775808` → parse `"-" + raw` → `long.MinValue`; also gives
a true `-0.0`). Even-length runs of `-` or `!` cancel entirely (`--a` ≡ `a`, `!!b` ≡ `b`),
matching cel-go.

### Message literals need lookahead

`a.b.c` is field selection, but `a.b.C{…}` is *message construction* — and only the token
after the name chain tells them apart. `IsStructLiteralAhead()` scans
`SELECTOR (. SELECTOR)* {` before committing. Notably the grammar allows *reserved words*
as message-name segments (`while.for{f: 1}` parses!).

### Optional syntax

`a.?b`, `a[?b]`, `[?e]`, `{?k: v}`, `Msg{?f: v}` parse into `_?._`/`_[?_]` calls and
optional-entry flags on the aggregate nodes. Purely syntactic here — semantics live in the
[optionals library](../guide/extensions.md#optionals).

### What comes out

A `ParseResult` holding a `CelAbstractSyntax`: the root `Expr` plus `SourceInfo` (expr id →
source offset, macro-call records). Every node id is unique — the checker's type map and
the source positions key off those ids.

Next: [Macros](macros.md) — the parser's most interesting job.
