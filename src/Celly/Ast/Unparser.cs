using System.Globalization;
using System.Text;
using Celly.Common;
using Celly.Stdlib;

namespace Celly.Ast;

/// <summary>
/// Converts an AST back to CEL source text. Macro expansions render in their original call form
/// when the AST carries macro-call records (the normal case for parsed expressions); a bare
/// comprehension with no record renders as a diagnostic <c>__comprehension__(…)</c> pseudo-call.
/// </summary>
public static class Unparser
{
    public static string Unparse(CelAbstractSyntax ast) => Unparse(ast.Expr, ast.SourceInfo);

    public static string Unparse(Expr expr, SourceInfo? sourceInfo = null)
    {
        var sb = new StringBuilder();
        Write(sb, expr, sourceInfo, parentPrecedence: 0, rightOperand: false);
        return sb.ToString();
    }

    // Precedence: higher binds tighter. 0 = no context (never parenthesize).
    private const int PrecTernary = 1;
    private const int PrecOr = 2;
    private const int PrecAnd = 3;
    private const int PrecRelation = 4;
    private const int PrecAdditive = 5;
    private const int PrecMultiplicative = 6;
    private const int PrecUnary = 7;

    private static readonly Dictionary<string, (string Symbol, int Precedence)> BinaryOps = new()
    {
        [Operators.LogicalOr] = ("||", PrecOr),
        [Operators.LogicalAnd] = ("&&", PrecAnd),
        [Operators.Equals] = ("==", PrecRelation),
        [Operators.NotEquals] = ("!=", PrecRelation),
        [Operators.Less] = ("<", PrecRelation),
        [Operators.LessEquals] = ("<=", PrecRelation),
        [Operators.Greater] = (">", PrecRelation),
        [Operators.GreaterEquals] = (">=", PrecRelation),
        [Operators.In] = ("in", PrecRelation),
        [Operators.Add] = ("+", PrecAdditive),
        [Operators.Subtract] = ("-", PrecAdditive),
        [Operators.Multiply] = ("*", PrecMultiplicative),
        [Operators.Divide] = ("/", PrecMultiplicative),
        [Operators.Modulo] = ("%", PrecMultiplicative),
    };

    private static void Write(StringBuilder sb, Expr expr, SourceInfo? info, int parentPrecedence, bool rightOperand)
    {
        // Macro expansions render as their original source form.
        if (info is not null && info.MacroCalls.TryGetValue(expr.Id, out var macroCall))
        {
            Write(sb, macroCall, info, parentPrecedence, rightOperand);
            return;
        }

        switch (expr)
        {
            case ConstExpr c:
                sb.Append(FormatConstant(c.Value));
                break;

            case IdentExpr ident:
                sb.Append(ident.Name);
                break;

            case SelectExpr select:
                if (select.TestOnly)
                {
                    sb.Append("has(");
                    WriteSelect(sb, select.Operand, select.Field, info);
                    sb.Append(')');
                }
                else
                {
                    WriteSelect(sb, select.Operand, select.Field, info);
                }

                break;

            case CallExpr call:
                WriteCall(sb, call, info, parentPrecedence, rightOperand);
                break;

            case ListExpr list:
                sb.Append('[');
                for (var i = 0; i < list.Elements.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(", ");
                    }

                    if (list.OptionalIndices.Contains(i))
                    {
                        sb.Append('?');
                    }

                    Write(sb, list.Elements[i], info, 0, false);
                }

                sb.Append(']');
                break;

            case MapExpr map:
                sb.Append('{');
                for (var i = 0; i < map.Entries.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(", ");
                    }

                    var entry = map.Entries[i];
                    if (entry.Optional)
                    {
                        sb.Append('?');
                    }

                    Write(sb, entry.Key, info, 0, false);
                    sb.Append(": ");
                    Write(sb, entry.Value, info, 0, false);
                }

                sb.Append('}');
                break;

            case StructExpr st:
                sb.Append(st.MessageName).Append('{');
                for (var i = 0; i < st.Fields.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(", ");
                    }

                    var field = st.Fields[i];
                    if (field.Optional)
                    {
                        sb.Append('?');
                    }

                    sb.Append(FieldName(field.Name)).Append(": ");
                    Write(sb, field.Value, info, 0, false);
                }

                sb.Append('}');
                break;

            case ComprehensionExpr comp:
                // No macro record: render a diagnostic pseudo-call.
                sb.Append("__comprehension__(");
                sb.Append(comp.IterVar);
                if (comp.IterVar2 is not null)
                {
                    sb.Append(", ").Append(comp.IterVar2);
                }

                sb.Append(", ");
                Write(sb, comp.IterRange, info, 0, false);
                sb.Append(", ").Append(comp.AccuVar).Append(", ");
                Write(sb, comp.AccuInit, info, 0, false);
                sb.Append(", ");
                Write(sb, comp.LoopCondition, info, 0, false);
                sb.Append(", ");
                Write(sb, comp.LoopStep, info, 0, false);
                sb.Append(", ");
                Write(sb, comp.Result, info, 0, false);
                sb.Append(')');
                break;

            default:
                sb.Append("<unspecified>");
                break;
        }
    }

    private static void WriteSelect(StringBuilder sb, Expr operand, string field, SourceInfo? info)
    {
        Write(sb, operand, info, PrecUnary + 1, false);
        sb.Append('.').Append(FieldName(field));
    }

    private static void WriteCall(StringBuilder sb, CallExpr call, SourceInfo? info, int parentPrecedence, bool rightOperand)
    {
        // Ternary.
        if (call.Function == Operators.Conditional && call.Args.Count == 3)
        {
            var needsParens = parentPrecedence > PrecTernary || (parentPrecedence == PrecTernary && !rightOperand);
            if (needsParens)
            {
                sb.Append('(');
            }

            Write(sb, call.Args[0], info, PrecTernary + 1, false);
            sb.Append(" ? ");
            Write(sb, call.Args[1], info, PrecTernary + 1, false);
            sb.Append(" : ");
            Write(sb, call.Args[2], info, PrecTernary, rightOperand: true);
            if (needsParens)
            {
                sb.Append(')');
            }

            return;
        }

        // Binary operators.
        if (call.Args.Count == 2 && BinaryOps.TryGetValue(call.Function, out var op))
        {
            var needsParens = parentPrecedence > op.Precedence
                || (parentPrecedence == op.Precedence && rightOperand);
            if (needsParens)
            {
                sb.Append('(');
            }

            Write(sb, call.Args[0], info, op.Precedence, rightOperand: false);
            sb.Append(' ').Append(op.Symbol).Append(' ');
            Write(sb, call.Args[1], info, op.Precedence, rightOperand: true);
            if (needsParens)
            {
                sb.Append(')');
            }

            return;
        }

        // Unary operators.
        if (call.Args.Count == 1 && call.Function is Operators.LogicalNot or Operators.Negate)
        {
            sb.Append(call.Function == Operators.LogicalNot ? '!' : '-');
            Write(sb, call.Args[0], info, PrecUnary, rightOperand: false);
            return;
        }

        // Indexing and optional access.
        switch (call.Function)
        {
            case Operators.Index when call.Args.Count == 2:
                Write(sb, call.Args[0], info, PrecUnary + 1, false);
                sb.Append('[');
                Write(sb, call.Args[1], info, 0, false);
                sb.Append(']');
                return;
            case Operators.OptIndex when call.Args.Count == 2:
                Write(sb, call.Args[0], info, PrecUnary + 1, false);
                sb.Append("[?");
                Write(sb, call.Args[1], info, 0, false);
                sb.Append(']');
                return;
            case Operators.OptSelect when call.Args is [_, ConstExpr { Value.Kind: ConstantKind.String } fieldConst]:
                Write(sb, call.Args[0], info, PrecUnary + 1, false);
                sb.Append(".?").Append(FieldName(fieldConst.Value.StringValue));
                return;
        }

        // Ordinary calls.
        if (call.Target is not null)
        {
            Write(sb, call.Target, info, PrecUnary + 1, false);
            sb.Append('.');
        }

        sb.Append(call.Function).Append('(');
        for (var i = 0; i < call.Args.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }

            Write(sb, call.Args[i], info, 0, false);
        }

        sb.Append(')');
    }

    private static string FieldName(string name) =>
        IsSimpleIdentifier(name) ? name : "`" + name + "`";

    private static bool IsSimpleIdentifier(string name)
    {
        if (name.Length == 0 || (!char.IsAsciiLetter(name[0]) && name[0] != '_'))
        {
            return false;
        }

        return name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
    }

    private static string FormatConstant(CelConstant constant)
    {
        switch (constant.Kind)
        {
            case ConstantKind.Null:
                return "null";
            case ConstantKind.Bool:
                return constant.BoolValue ? "true" : "false";
            case ConstantKind.Int:
                return constant.IntValue.ToString(CultureInfo.InvariantCulture);
            case ConstantKind.Uint:
                return constant.UintValue.ToString(CultureInfo.InvariantCulture) + "u";
            case ConstantKind.Double:
            {
                var s = GoDoubleFormatter.Format(constant.DoubleValue);
                return s.Contains('.') || s.Contains('e') || s.Contains("Inf") || s.Contains("NaN") ? s : s + ".0";
            }

            case ConstantKind.String:
                return QuoteString(constant.StringValue);
            case ConstantKind.Bytes:
                return "b" + QuoteBytes(constant.BytesValue);
            default:
                return "<const>";
        }
    }

    private static string QuoteString(string value)
    {
        var sb = new StringBuilder(value.Length + 2).Append('"');
        foreach (var c in value)
        {
            sb.Append(c switch
            {
                '\a' => "\\a",
                '\b' => "\\b",
                '\f' => "\\f",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                '\v' => "\\v",
                '\\' => "\\\\",
                '"' => "\\\"",
                _ when char.IsControl(c) => $"\\u{(int)c:x4}",
                _ => c.ToString(),
            });
        }

        return sb.Append('"').ToString();
    }

    private static string QuoteBytes(byte[] value)
    {
        var sb = new StringBuilder(value.Length + 2).Append('"');
        foreach (var b in value)
        {
            if (b is >= 0x20 and < 0x7F && b != (byte)'"' && b != (byte)'\\')
            {
                sb.Append((char)b);
            }
            else
            {
                sb.Append($"\\x{b:x2}");
            }
        }

        return sb.Append('"').ToString();
    }
}
