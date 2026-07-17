using Cel.Expr;
using Cel.Expr.Conformance.Test;
using Celly.Interpreter;
using Celly.Protobuf;
using Celly.Values;

namespace Celly.Conformance;

/// <summary>
/// Executes a single conformance case: build an environment from <c>container</c> (and, from M4,
/// <c>type_env</c>), parse the expression (+check unless <c>disable_check</c>), evaluate with
/// <c>bindings</c>, and match the expected result. Throws on mismatch; the ratchet in
/// <see cref="ConformanceTests"/> interprets throws against known-failures.txt.
/// </summary>
public static class ConformanceHarness
{
    public static void Run(SimpleTest test)
    {
        var env = CelEnv.Create(new CelEnvSettings
        {
            Container = test.Container,
            DisableMacros = test.DisableMacros,
            Declarations =
            [
                .. test.TypeEnv
                    .Where(d => d.DeclKindCase == Cel.Expr.Decl.DeclKindOneofCase.Ident)
                    .Select(ToVariableDecl),
            ],
            FunctionDeclarations =
            [
                .. test.TypeEnv
                    .Where(d => d.DeclKindCase == Cel.Expr.Decl.DeclKindOneofCase.Function)
                    .Select(ToFunctionDecl),
            ],
        });

        var parsed = env.Parse(test.Expr);
        if (parsed.Ast is null)
        {
            if (ExpectsError(test))
            {
                return; // an expected-error case may legitimately fail at parse time
            }

            throw new InvalidOperationException(
                $"parse failed: {string.Join("; ", parsed.Issues)} (expr: {test.Expr})");
        }

        if (!test.DisableCheck)
        {
            var checkResult = env.Check(parsed.Ast);
            if (checkResult.HasErrors)
            {
                if (ExpectsError(test))
                {
                    return; // type errors satisfy error expectations
                }

                throw new InvalidOperationException(
                    $"check failed: {string.Join("; ", checkResult.Issues)} (expr: {test.Expr})");
            }

            if (test.ResultMatcherCase == SimpleTest.ResultMatcherOneofCase.TypedResult
                && test.TypedResult.DeducedType is { } deducedType)
            {
                var expectedType = TypeConverter.ToCelType(deducedType);
                var actualType = parsed.Ast.TypeMap![parsed.Ast.Expr.Id];
                if (!Celly.Checking.TypeSubstitution.StructuralEquals(expectedType, actualType))
                {
                    throw new InvalidOperationException(
                        $"deduced type mismatch: expected {expectedType}, got {actualType} (expr: {test.Expr})");
                }
            }
        }
        else if (test.CheckOnly)
        {
            throw new InvalidOperationException("check_only test with disable_check is contradictory");
        }

        if (test.CheckOnly)
        {
            return; // deduced-type comparison (above) is the whole assertion
        }

        var program = env.Program(parsed.Ast);
        var result = program.Eval(BuildActivation(test));

        switch (test.ResultMatcherCase)
        {
            case SimpleTest.ResultMatcherOneofCase.Value:
                AssertValue(ValueConverter.ToCelValue(test.Value), result, test);
                break;

            case SimpleTest.ResultMatcherOneofCase.TypedResult:
                // Deduced-type assertions arrive with the M4 checker; the result value must match now.
                if (test.TypedResult.Result is null)
                {
                    throw new NotSupportedException("typed_result without result requires the M4 checker");
                }

                AssertValue(ValueConverter.ToCelValue(test.TypedResult.Result), result, test);
                break;

            case SimpleTest.ResultMatcherOneofCase.EvalError:
            case SimpleTest.ResultMatcherOneofCase.AnyEvalErrors:
                if (result is not ErrorValue)
                {
                    throw new InvalidOperationException($"expected an eval error, got: {result} (expr: {test.Expr})");
                }

                break;

            case SimpleTest.ResultMatcherOneofCase.Unknown:
            case SimpleTest.ResultMatcherOneofCase.AnyUnknowns:
                if (result is not UnknownValue)
                {
                    throw new InvalidOperationException($"expected an unknown result, got: {result} (expr: {test.Expr})");
                }

                break;

            case SimpleTest.ResultMatcherOneofCase.None:
            default:
                // Default matcher: bool true.
                AssertValue(BoolValue.True, result, test);
                break;
        }
    }

    private static bool ExpectsError(SimpleTest test) =>
        test.ResultMatcherCase is SimpleTest.ResultMatcherOneofCase.EvalError
            or SimpleTest.ResultMatcherOneofCase.AnyEvalErrors;

    private static Celly.Checking.VariableDecl ToVariableDecl(Cel.Expr.Decl decl) =>
        new(decl.Name, TypeConverter.ToCelType(decl.Ident.Type));

    private static Celly.Checking.FunctionDecl ToFunctionDecl(Cel.Expr.Decl decl) =>
        new(
            decl.Name,
            [
                .. decl.Function.Overloads.Select(o => new Celly.Checking.OverloadDecl(
                    o.OverloadId,
                    [.. o.Params.Select(TypeConverter.ToCelType)],
                    TypeConverter.ToCelType(o.ResultType),
                    o.IsInstanceFunction)),
            ]);

    private static IActivation BuildActivation(SimpleTest test)
    {
        if (test.Bindings.Count == 0)
        {
            return EmptyActivation.Instance;
        }

        var bindings = new Dictionary<string, CelValue>(StringComparer.Ordinal);
        foreach (var (name, exprValue) in test.Bindings)
        {
            bindings[name] = exprValue.KindCase switch
            {
                ExprValue.KindOneofCase.Value => ValueConverter.ToCelValue(exprValue.Value),
                _ => throw new NotSupportedException($"unsupported binding kind: {exprValue.KindCase}"),
            };
        }

        return new ValueActivation(bindings);
    }

    private static void AssertValue(CelValue expected, CelValue actual, SimpleTest test)
    {
        if (!ResultMatcher.Matches(expected, actual))
        {
            throw new InvalidOperationException($"expected {expected}, got {actual} (expr: {test.Expr})");
        }
    }
}

/// <summary>
/// Strict conformance result comparison: types must match exactly (no int/uint coercion — a uint
/// expression must produce a uint), doubles compare bitwise except that any NaN matches any NaN,
/// and maps compare order-agnostically.
/// </summary>
public static class ResultMatcher
{
    public static bool Matches(CelValue expected, CelValue actual)
    {
        switch (expected, actual)
        {
            case (Celly.Values.NullValue, Celly.Values.NullValue):
                return true;
            case (Celly.Values.BoolValue e, Celly.Values.BoolValue a):
                return e.Value == a.Value;
            case (IntValue e, IntValue a):
                return e.Value == a.Value;
            case (UintValue e, UintValue a):
                return e.Value == a.Value;
            case (DoubleValue e, DoubleValue a):
                if (double.IsNaN(e.Value) && double.IsNaN(a.Value))
                {
                    return true; // any NaN matches any NaN
                }

                // Bitwise: preserves the -0.0 vs 0.0 distinction proto equality makes.
                return BitConverter.DoubleToInt64Bits(e.Value) == BitConverter.DoubleToInt64Bits(a.Value);
            case (Celly.Values.StringValue e, Celly.Values.StringValue a):
                return string.Equals(e.Value, a.Value, StringComparison.Ordinal);
            case (BytesValue e, BytesValue a):
                return e.Span.SequenceEqual(a.Span);
            case (TypeValue e, TypeValue a):
                return string.Equals(e.Value.Name, a.Value.Name, StringComparison.Ordinal);
            case (TimestampValue e, TimestampValue a):
                return e.Data == a.Data;
            case (DurationValue e, DurationValue a):
                return e.Data == a.Data;
            case (Celly.Values.ListValue e, Celly.Values.ListValue a):
            {
                if (e.Elements.Count != a.Elements.Count)
                {
                    return false;
                }

                for (var i = 0; i < e.Elements.Count; i++)
                {
                    if (!Matches(e.Elements[i], a.Elements[i]))
                    {
                        return false;
                    }
                }

                return true;
            }

            case (Celly.Values.MapValue e, Celly.Values.MapValue a):
            {
                if (e.Count != a.Count)
                {
                    return false;
                }

                foreach (var key in e.Keys)
                {
                    var matched = false;
                    foreach (var actualKey in a.Keys)
                    {
                        if (Matches(key, actualKey))
                        {
                            a.TryGet(actualKey, out var actualValue);
                            e.TryGet(key, out var expectedValue);
                            matched = Matches(expectedValue, actualValue);
                            break;
                        }
                    }

                    if (!matched)
                    {
                        return false;
                    }
                }

                return true;
            }

            default:
                return false;
        }
    }
}
