namespace Celly.Ast;

/// <summary>Programmatic AST inspection: child enumeration, traversal, and common queries.</summary>
public static class AstTools
{
    /// <summary>The direct children of a node, in evaluation-relevant order.</summary>
    public static IEnumerable<Expr> Children(Expr expr)
    {
        switch (expr)
        {
            case SelectExpr select:
                yield return select.Operand;
                break;
            case CallExpr call:
                if (call.Target is not null)
                {
                    yield return call.Target;
                }

                foreach (var arg in call.Args)
                {
                    yield return arg;
                }

                break;
            case ListExpr list:
                foreach (var element in list.Elements)
                {
                    yield return element;
                }

                break;
            case MapExpr map:
                foreach (var entry in map.Entries)
                {
                    yield return entry.Key;
                    yield return entry.Value;
                }

                break;
            case StructExpr st:
                foreach (var field in st.Fields)
                {
                    yield return field.Value;
                }

                break;
            case ComprehensionExpr comp:
                yield return comp.IterRange;
                yield return comp.AccuInit;
                yield return comp.LoopCondition;
                yield return comp.LoopStep;
                yield return comp.Result;
                break;
        }
    }

    /// <summary>Pre-order traversal of the node and all descendants.</summary>
    public static IEnumerable<Expr> DescendantsAndSelf(Expr expr)
    {
        var stack = new Stack<Expr>();
        stack.Push(expr);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            yield return current;
            foreach (var child in Children(current).Reverse())
            {
                stack.Push(child);
            }
        }
    }

    /// <summary>
    /// The free variable roots an expression references (comprehension-internal variables
    /// excluded). For dotted references like <c>a.b.c</c> this reports the root, <c>a</c> —
    /// resolution against qualified names and containers happens later, at check/eval time.
    /// </summary>
    public static IReadOnlySet<string> ReferencedVariables(Expr expr)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        Walk(expr, []);
        return result;

        void Walk(Expr node, HashSet<string> bound)
        {
            switch (node)
            {
                case IdentExpr ident:
                    var name = ident.Name.StartsWith('.') ? ident.Name[1..] : ident.Name;
                    var root = name.Split('.')[0];
                    if (!bound.Contains(root))
                    {
                        result.Add(root);
                    }

                    break;
                case ComprehensionExpr comp:
                    Walk(comp.IterRange, bound);
                    Walk(comp.AccuInit, bound);
                    var inner = new HashSet<string>(bound, StringComparer.Ordinal)
                    {
                        comp.IterVar,
                        comp.AccuVar,
                    };
                    if (comp.IterVar2 is not null)
                    {
                        inner.Add(comp.IterVar2);
                    }

                    Walk(comp.LoopCondition, inner);
                    Walk(comp.LoopStep, inner);
                    var resultScope = new HashSet<string>(bound, StringComparer.Ordinal) { comp.AccuVar };
                    Walk(comp.Result, resultScope);
                    break;
                default:
                    foreach (var child in Children(node))
                    {
                        Walk(child, bound);
                    }

                    break;
            }
        }
    }

    /// <summary>All function names called anywhere in the expression (operators included).</summary>
    public static IReadOnlySet<string> CalledFunctions(Expr expr)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in DescendantsAndSelf(expr))
        {
            if (node is CallExpr call)
            {
                result.Add(call.Function);
            }
        }

        return result;
    }
}
