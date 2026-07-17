using Cel.Expr.Conformance.Test;

namespace Celly.Conformance;

/// <summary>
/// Executes a single conformance case: build an environment from <c>type_env</c> and
/// <c>container</c>, parse the expression (check unless <c>disable_check</c>), evaluate with
/// <c>bindings</c>, and match the expected result (value / typed_result / eval_error / unknown).
/// </summary>
public static class ConformanceHarness
{
    public static void Run(SimpleTest test)
    {
        // Wired up in M2 when CelEnv/CelProgram exist (see docs/PLAN.md). Until then every case
        // fails here and is carried by testdata/known-failures.txt.
        throw new NotImplementedException("Celly evaluator not yet implemented (M2).");
    }
}
