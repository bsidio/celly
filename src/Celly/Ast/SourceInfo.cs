using Celly.Common;

namespace Celly.Ast;

/// <summary>
/// Position and macro metadata gathered during parsing (shape-compatible with
/// <c>cel.expr.SourceInfo</c>).
/// </summary>
public sealed class SourceInfo
{
    private readonly Dictionary<long, int> _positions = [];
    private readonly Dictionary<long, Expr> _macroCalls = [];

    public SourceInfo(Source source) => Source = source;

    public Source Source { get; }

    /// <summary>Character offset (UTF-16 code unit index) of each expression id.</summary>
    public IReadOnlyDictionary<long, int> Positions => _positions;

    /// <summary>
    /// For each macro-expansion result id, the original (unexpanded) call expression.
    /// Sub-expressions that appear in the expansion are elided from the stored call.
    /// </summary>
    public IReadOnlyDictionary<long, Expr> MacroCalls => _macroCalls;

    public void SetPosition(long exprId, int offset) => _positions[exprId] = offset;

    public void AddMacroCall(long exprId, Expr call) => _macroCalls[exprId] = call;

    public int PositionOf(long exprId) => _positions.GetValueOrDefault(exprId, -1);

    public Location LocationOf(long exprId) => Source.LocationOf(PositionOf(exprId));
}

/// <summary>A parsed (and optionally checked) CEL expression.</summary>
public sealed class CelAbstractSyntax
{
    public CelAbstractSyntax(Expr expr, SourceInfo sourceInfo)
    {
        Expr = expr;
        SourceInfo = sourceInfo;
    }

    public Expr Expr { get; }

    public SourceInfo SourceInfo { get; }

    /// <summary>Checker output: expression id → deduced type; null until checked.</summary>
    public IReadOnlyDictionary<long, Types.CelType>? TypeMap { get; internal set; }

    public bool IsChecked => TypeMap is not null;
}
