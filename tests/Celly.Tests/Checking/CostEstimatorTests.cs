using Celly.Checking;
using Celly.Types;
using Xunit;

namespace Celly.Tests.Checking;

public class CostEstimatorTests
{
    private static CostEstimate Estimate(
        string expression, CelEnvSettings settings, ICostEstimator? hints = null)
    {
        var env = CelEnv.Create(settings);
        var parsed = env.Parse(expression);
        Assert.NotNull(parsed.Ast);
        var result = env.Check(parsed.Ast!);
        Assert.False(result.HasErrors, string.Join("; ", result.Issues));
        return env.EstimateCost(parsed.Ast!, hints);
    }

    private static CelEnvSettings With(params VariableDecl[] decls) => new() { Declarations = decls };

    // A hint provider that gives every list/map/string a fixed maximum size.
    private sealed class FixedSize(ulong max) : ICostEstimator
    {
        public SizeEstimate? EstimateSize(string? path, CelType type) => new(0, max);
    }

    [Fact]
    public void Literal_costs_nothing_to_produce()
    {
        var cost = Estimate("1 + 2 * 3", new CelEnvSettings());
        // Two arithmetic calls (each 1 unit), constant operands cost nothing to fetch.
        Assert.False(cost.IsUnbounded);
        Assert.Equal(2UL, cost.Min);
        Assert.Equal(2UL, cost.Max);
    }

    [Fact]
    public void Simple_expression_is_cheap_and_bounded()
    {
        var cost = Estimate(
            "x + 1 > 3 && name.startsWith('h')",
            With(new VariableDecl("x", CelType.Int), new VariableDecl("name", CelType.String)),
            new FixedSize(32));

        Assert.False(cost.IsUnbounded);
        Assert.True(cost.Max < 100, $"expected a small bound, got {cost}");
    }

    [Fact]
    public void Comprehension_over_unhinted_variable_is_unbounded()
    {
        var cost = Estimate(
            "items.all(i, i > 0)",
            With(new VariableDecl("items", CelType.List(CelType.Int))));

        Assert.True(cost.IsUnbounded, $"expected unbounded, got {cost}");
    }

    [Fact]
    public void Size_hint_bounds_a_comprehension()
    {
        var cost = Estimate(
            "items.all(i, i > 0)",
            With(new VariableDecl("items", CelType.List(CelType.Int))),
            new FixedSize(100));

        Assert.False(cost.IsUnbounded, $"expected a finite bound, got {cost}");
        Assert.True(cost.Max >= 100, $"cost should scale with the 100-element range, got {cost}");
    }

    [Fact]
    public void Nested_comprehensions_compound_multiplicatively()
    {
        var settings = With(new VariableDecl("items", CelType.List(CelType.Int)));
        var flat = Estimate("items.all(i, i > 0)", settings, new FixedSize(100));
        var nested = Estimate("items.all(i, items.all(j, i > j))", settings, new FixedSize(100));

        Assert.False(nested.IsUnbounded);
        // The inner loop runs for every element of the outer loop: ~100x the flat cost.
        Assert.True(nested.Max > flat.Max * 50, $"nested {nested} should dwarf flat {flat}");
    }

    [Fact]
    public void Matches_scales_with_string_size()
    {
        var small = Estimate(
            "s.matches('a+')", With(new VariableDecl("s", CelType.String)), new FixedSize(10));
        var large = Estimate(
            "s.matches('a+')", With(new VariableDecl("s", CelType.String)), new FixedSize(10_000));

        Assert.True(large.Max > small.Max, $"larger input {large} should cost more than {small}");
    }

    [Fact]
    public void Requires_a_checked_ast()
    {
        var env = CelEnv.Create(new CelEnvSettings());
        var parsed = env.Parse("1 + 1");
        Assert.NotNull(parsed.Ast);
        // Not checked → EstimateCost must refuse rather than silently mis-estimate.
        Assert.Throws<InvalidOperationException>(() => env.EstimateCost(parsed.Ast!));
    }

    [Fact]
    public void Saturating_arithmetic_never_overflows_to_a_small_number()
    {
        // Deeply nested comprehensions over large hinted inputs would overflow ulong without
        // saturation; the estimate must stay unbounded (large), not wrap around to a tiny value.
        var settings = With(new VariableDecl("items", CelType.List(CelType.Int)));
        var cost = Estimate(
            "items.all(a, items.all(b, items.all(c, items.all(d, a + b + c + d > 0))))",
            settings,
            new FixedSize(1_000_000));

        Assert.True(cost.Max >= 1_000_000, $"expected a huge bound, got {cost}");
    }
}
