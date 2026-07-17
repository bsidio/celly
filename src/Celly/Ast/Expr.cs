namespace Celly.Ast;

/// <summary>
/// A CEL expression AST node. The node set mirrors <c>cel.expr.Expr</c> so checked/parsed ASTs
/// convert losslessly to the canonical protos (via Celly.Protobuf's AstConverter).
/// Every node carries a parse-unique id used by source info, type maps, and reference maps.
/// </summary>
public abstract class Expr
{
    protected Expr(long id) => Id = id;

    public long Id { get; }

    /// <summary>Sentinel for absent sub-expressions (e.g. an empty parse result after errors).</summary>
    public static readonly Expr Unspecified = new UnspecifiedExpr();

    private sealed class UnspecifiedExpr() : Expr(0);
}

/// <summary>A literal.</summary>
public sealed class ConstExpr(long id, CelConstant value) : Expr(id)
{
    public CelConstant Value { get; } = value;
}

/// <summary>An (possibly namespaced) identifier reference, e.g. <c>a</c> or <c>.a</c>.</summary>
public sealed class IdentExpr(long id, string name) : Expr(id)
{
    public string Name { get; } = name;
}

/// <summary>Field selection <c>operand.field</c>; <c>TestOnly</c> marks a has() macro expansion.</summary>
public sealed class SelectExpr(long id, Expr operand, string field, bool testOnly = false) : Expr(id)
{
    public Expr Operand { get; } = operand;

    public string Field { get; } = field;

    public bool TestOnly { get; } = testOnly;
}

/// <summary>A function call; global when <see cref="Target"/> is null, receiver-style otherwise.</summary>
public sealed class CallExpr(long id, Expr? target, string function, IReadOnlyList<Expr> args) : Expr(id)
{
    public Expr? Target { get; } = target;

    public string Function { get; } = function;

    public IReadOnlyList<Expr> Args { get; } = args;
}

/// <summary>A list literal. <see cref="OptionalIndices"/> marks <c>[?e]</c> entries.</summary>
public sealed class ListExpr(long id, IReadOnlyList<Expr> elements, IReadOnlyList<int> optionalIndices) : Expr(id)
{
    public IReadOnlyList<Expr> Elements { get; } = elements;

    public IReadOnlyList<int> OptionalIndices { get; } = optionalIndices;
}

/// <summary>A map literal entry <c>key: value</c> (or <c>?key: value</c> when optional).</summary>
public sealed class MapEntry(long id, Expr key, Expr value, bool optional)
{
    public long Id { get; } = id;

    public Expr Key { get; } = key;

    public Expr Value { get; } = value;

    public bool Optional { get; } = optional;
}

/// <summary>A map literal.</summary>
public sealed class MapExpr(long id, IReadOnlyList<MapEntry> entries) : Expr(id)
{
    public IReadOnlyList<MapEntry> Entries { get; } = entries;
}

/// <summary>A message literal field initializer <c>field: value</c> (or <c>?field: value</c>).</summary>
public sealed class StructField(long id, string name, Expr value, bool optional)
{
    public long Id { get; } = id;

    public string Name { get; } = name;

    public Expr Value { get; } = value;

    public bool Optional { get; } = optional;
}

/// <summary>A message construction literal <c>pkg.Type{field: value}</c>.</summary>
public sealed class StructExpr(long id, string messageName, IReadOnlyList<StructField> fields) : Expr(id)
{
    public string MessageName { get; } = messageName;

    public IReadOnlyList<StructField> Fields { get; } = fields;
}

/// <summary>
/// A comprehension (the expansion target of all/exists/exists_one/map/filter macros and the
/// two-variable comprehension extensions). Evaluation semantics per the CEL spec:
/// evaluate <see cref="IterRange"/>; bind <see cref="AccuVar"/> to <see cref="AccuInit"/>; for each
/// element (and key/value when <see cref="IterVar2"/> is set) while <see cref="LoopCondition"/> is
/// true, rebind the accumulator to <see cref="LoopStep"/>; finally yield <see cref="Result"/>.
/// </summary>
public sealed class ComprehensionExpr(
    long id,
    string iterVar,
    string? iterVar2,
    Expr iterRange,
    string accuVar,
    Expr accuInit,
    Expr loopCondition,
    Expr loopStep,
    Expr result) : Expr(id)
{
    public string IterVar { get; } = iterVar;

    public string? IterVar2 { get; } = iterVar2;

    public Expr IterRange { get; } = iterRange;

    public string AccuVar { get; } = accuVar;

    public Expr AccuInit { get; } = accuInit;

    public Expr LoopCondition { get; } = loopCondition;

    public Expr LoopStep { get; } = loopStep;

    public Expr Result { get; } = result;
}
