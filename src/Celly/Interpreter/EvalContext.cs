using Celly.Values;

namespace Celly.Interpreter;

/// <summary>
/// Per-evaluation limits that bound the work a single <c>Eval</c> may do — the safety valve for
/// evaluating untrusted expressions. Comprehension iterations are counted against a shared budget
/// so a nested-comprehension "bomb" terminates with an error instead of exhausting CPU/memory.
/// </summary>
public sealed class EvalLimits
{
    /// <summary>No limits (the default for trusted, first-party expressions).</summary>
    public static readonly EvalLimits None = new();

    /// <summary>
    /// Maximum total comprehension iterations across the whole evaluation (not per loop), or 0 for
    /// unlimited. A quadratic bomb like <c>range.map(x, range.map(y, …))</c> trips this.
    /// </summary>
    public long MaxIterations { get; init; }

    /// <summary>An external cancellation signal checked periodically during long evaluations.</summary>
    public CancellationToken CancellationToken { get; init; }

    public bool IsUnlimited => MaxIterations == 0 && CancellationToken == default;
}

/// <summary>Mutable state threaded through one evaluation: the live iteration count and limits.</summary>
public sealed class EvalContext
{
    public static readonly EvalContext Unlimited = new(EvalLimits.None);

    private long _iterations;
    private int _cancellationCheck;

    public EvalContext(EvalLimits limits) => Limits = limits;

    public EvalLimits Limits { get; }

    /// <summary>
    /// Charges <paramref name="count"/> comprehension iterations; returns an ErrorValue when the
    /// budget is exhausted or cancellation was requested, else null.
    /// </summary>
    public ErrorValue? Charge(long count)
    {
        if (Limits.MaxIterations > 0)
        {
            _iterations += count;
            if (_iterations > Limits.MaxIterations)
            {
                return new ErrorValue("operation cancelled: evaluation iteration budget exceeded");
            }
        }

        // Check cancellation every ~1024 charges to keep the hot path cheap.
        if (Limits.CancellationToken != default && (++_cancellationCheck & 0x3FF) == 0
            && Limits.CancellationToken.IsCancellationRequested)
        {
            return new ErrorValue("operation cancelled");
        }

        return null;
    }
}
