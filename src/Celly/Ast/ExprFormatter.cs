using System.Globalization;
using System.Text;

namespace Celly.Ast;

/// <summary>
/// Renders an AST as a compact, deterministic single-line string. Used by parser golden tests
/// and debug output — not an unparser (macro expansions render in expanded form).
/// </summary>
public static class ExprFormatter
{
    public static string Format(Expr expr)
    {
        var sb = new StringBuilder();
        Write(sb, expr);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, Expr expr)
    {
        switch (expr)
        {
            case ConstExpr c:
                sb.Append(FormatConstant(c.Value));
                break;

            case IdentExpr ident:
                sb.Append(ident.Name);
                break;

            case SelectExpr select:
                Write(sb, select.Operand);
                sb.Append('.').Append(select.Field);
                if (select.TestOnly)
                {
                    sb.Append("~test-only~");
                }

                break;

            case CallExpr call:
                if (call.Target is not null)
                {
                    Write(sb, call.Target);
                    sb.Append('.');
                }

                sb.Append(call.Function).Append('(');
                WriteList(sb, call.Args);
                sb.Append(')');
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

                    Write(sb, list.Elements[i]);
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

                    Write(sb, entry.Key);
                    sb.Append(": ");
                    Write(sb, entry.Value);
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

                    sb.Append(field.Name).Append(": ");
                    Write(sb, field.Value);
                }

                sb.Append('}');
                break;

            case ComprehensionExpr comp:
                sb.Append("fold(");
                sb.Append(comp.IterVar);
                if (comp.IterVar2 is not null)
                {
                    sb.Append(", ").Append(comp.IterVar2);
                }

                sb.Append(", ");
                Write(sb, comp.IterRange);
                sb.Append(", ").Append(comp.AccuVar).Append(", ");
                Write(sb, comp.AccuInit);
                sb.Append(", ");
                Write(sb, comp.LoopCondition);
                sb.Append(", ");
                Write(sb, comp.LoopStep);
                sb.Append(", ");
                Write(sb, comp.Result);
                sb.Append(')');
                break;

            default:
                sb.Append("<unspecified>");
                break;
        }
    }

    private static void WriteList(StringBuilder sb, IReadOnlyList<Expr> exprs)
    {
        for (var i = 0; i < exprs.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }

            Write(sb, exprs[i]);
        }
    }

    private static string FormatConstant(CelConstant value) => value.Kind switch
    {
        ConstantKind.Null => "null",
        ConstantKind.Bool => value.BoolValue ? "true" : "false",
        ConstantKind.Int => value.IntValue.ToString(CultureInfo.InvariantCulture),
        ConstantKind.Uint => value.UintValue.ToString(CultureInfo.InvariantCulture) + "u",
        ConstantKind.Double => FormatDouble(value.DoubleValue),
        ConstantKind.String => Quote(value.StringValue),
        ConstantKind.Bytes => "b\"" + Convert.ToHexString(value.BytesValue).ToLowerInvariant() + "\"",
        _ => "?",
    };

    private static string FormatDouble(double d)
    {
        if (double.IsNaN(d))
        {
            return "NaN";
        }

        if (double.IsPositiveInfinity(d))
        {
            return "Inf";
        }

        if (double.IsNegativeInfinity(d))
        {
            return "-Inf";
        }

        var s = d.ToString("R", CultureInfo.InvariantCulture);
        // Distinguish doubles from ints in golden output.
        return s.Contains('.') || s.Contains('e') || s.Contains('E') ? s : s + ".0";
    }

    private static string Quote(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(c); break;
            }
        }

        return sb.Append('"').ToString();
    }
}
