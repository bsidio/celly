namespace Celly.Common;

/// <summary>
/// Internal function names for CEL operators, mirroring cel-go's <c>operators</c> package.
/// These names appear in the AST for operator calls and key the standard declarations.
/// </summary>
public static class Operators
{
    // Symbolic operators.
    public const string Conditional = "_?_:_";
    public const string LogicalAnd = "_&&_";
    public const string LogicalOr = "_||_";
    public const string LogicalNot = "!_";
    public new const string Equals = "_==_";
    public const string NotEquals = "_!=_";
    public const string Less = "_<_";
    public const string LessEquals = "_<=_";
    public const string Greater = "_>_";
    public const string GreaterEquals = "_>=_";
    public const string Add = "_+_";
    public const string Subtract = "_-_";
    public const string Multiply = "_*_";
    public const string Divide = "_/_";
    public const string Modulo = "_%_";
    public const string Negate = "-_";
    public const string Index = "_[_]";

    // Optional-syntax operators (parsed when optional syntax is enabled).
    public const string OptSelect = "_?._";
    public const string OptIndex = "_[?_]";

    // Macro-internal operators.
    public const string Has = "has";
    public const string All = "all";
    public const string Exists = "exists";
    public const string ExistsOne = "exists_one";
    public const string Map = "map";
    public const string Filter = "filter";
    public const string NotStrictlyFalse = "@not_strictly_false";
    public const string In = "@in";

    private static readonly Dictionary<string, string> BinaryOperatorNames = new()
    {
        ["&&"] = LogicalAnd,
        ["||"] = LogicalOr,
        ["=="] = Equals,
        ["!="] = NotEquals,
        ["<"] = Less,
        ["<="] = LessEquals,
        [">"] = Greater,
        [">="] = GreaterEquals,
        ["+"] = Add,
        ["-"] = Subtract,
        ["*"] = Multiply,
        ["/"] = Divide,
        ["%"] = Modulo,
        ["in"] = In,
    };

    public static string FindBinary(string symbol) => BinaryOperatorNames[symbol];
}
