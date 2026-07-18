using Celly.Ast;
using Celly.Types;

namespace Celly.Checking;

/// <summary>
/// A saturating <c>[min, max]</c> range of element counts (list length, string code points, map
/// entries) used by <see cref="CostEstimator"/> to scale size-dependent costs. <c>Max</c> equal to
/// <see cref="ulong.MaxValue"/> means "unbounded" — the size could not be bounded statically.
/// </summary>
public readonly struct SizeEstimate
{
    public SizeEstimate(ulong min, ulong max)
    {
        Min = min;
        Max = max;
    }

    public ulong Min { get; }

    public ulong Max { get; }

    /// <summary>An unknown, statically-unbounded size (0 up to unbounded).</summary>
    public static readonly SizeEstimate Unbounded = new(0, ulong.MaxValue);

    /// <summary>A size known exactly (e.g. a list literal's element count).</summary>
    public static SizeEstimate Exact(ulong n) => new(n, n);

    public bool IsUnbounded => Max == ulong.MaxValue;

    internal SizeEstimate Add(SizeEstimate other) =>
        new(Saturating.Add(Min, other.Min), Saturating.Add(Max, other.Max));

    internal SizeEstimate Multiply(SizeEstimate other) =>
        new(Saturating.Mul(Min, other.Min), Saturating.Mul(Max, other.Max));

    public override string ToString() => IsUnbounded ? $"[{Min}, unbounded]" : $"[{Min}, {Max}]";
}

/// <summary>
/// A saturating <c>[min, max]</c> estimate of an expression's worst-case evaluation cost, in
/// abstract cost units (roughly "one cost unit per elementary operation"). <c>Max</c> equal to
/// <see cref="ulong.MaxValue"/> means the cost is statically unbounded — typically a comprehension
/// iterating an input whose size isn't constrained. Use <see cref="Max"/> as an admission gate for
/// untrusted expressions (reject before evaluation), and pair it with the runtime
/// <see cref="Interpreter.EvalLimits"/> budget for defence in depth.
/// </summary>
public readonly struct CostEstimate
{
    public CostEstimate(ulong min, ulong max)
    {
        Min = min;
        Max = max;
    }

    public ulong Min { get; }

    public ulong Max { get; }

    public static readonly CostEstimate Zero = new(0, 0);

    public static readonly CostEstimate One = new(1, 1);

    /// <summary>True when the worst-case cost could not be bounded statically.</summary>
    public bool IsUnbounded => Max == ulong.MaxValue;

    public CostEstimate Add(CostEstimate other) =>
        new(Saturating.Add(Min, other.Min), Saturating.Add(Max, other.Max));

    /// <summary>Multiplies this per-iteration cost by an iteration-count range (comprehensions).</summary>
    internal CostEstimate MultiplyBySize(SizeEstimate iterations) =>
        new(Saturating.Mul(Min, iterations.Min), Saturating.Mul(Max, iterations.Max));

    public override string ToString() => IsUnbounded ? $"[{Min}, unbounded]" : $"[{Min}, {Max}]";
}

/// <summary>
/// Supplies static size hints for the inputs of a cost estimate. Without hints, list/map/string
/// <em>variables</em> are treated as unbounded, so any comprehension over them yields an unbounded
/// cost. Provide hints (e.g. "the <c>request.items</c> list has at most 100 elements") to obtain a
/// finite bound you can threshold.
/// </summary>
public interface ICostEstimator
{
    /// <summary>
    /// Estimate the size of the value at <paramref name="path"/> (a dotted ident/field path such as
    /// <c>request.items</c>, or <c>null</c> when the operand isn't a simple path), given its checked
    /// <paramref name="type"/>. Return <c>null</c> to accept the default (unbounded).
    /// </summary>
    SizeEstimate? EstimateSize(string? path, CelType type);
}

internal static class Saturating
{
    public static ulong Add(ulong a, ulong b)
    {
        var sum = unchecked(a + b);
        return sum < a ? ulong.MaxValue : sum;
    }

    public static ulong Mul(ulong a, ulong b)
    {
        if (a == 0 || b == 0)
        {
            return 0;
        }

        var product = unchecked(a * b);
        return product / a != b ? ulong.MaxValue : product;
    }
}
