using Cel.Expr.Conformance.Test;
using Xunit;
using Xunit.Sdk;

namespace Celly.Conformance;

public class ConformanceTests
{
    public static TheoryData<string> CaseIds()
    {
        var data = new TheoryData<string>();
        foreach (var id in TestData.Cases.Keys)
        {
            data.Add(id);
        }

        return data;
    }

    private static readonly string? ReportPath = Environment.GetEnvironmentVariable("CELLY_CONFORMANCE_REPORT");
    private static readonly object ReportLock = new();

    [Theory]
    [MemberData(nameof(CaseIds))]
    public void Run(string caseId)
    {
        var test = TestData.Cases[caseId];
        var expectedFailure = KnownFailures.Contains(caseId);

        Exception? failure = null;
        try
        {
            // The strong_* sections document the strong-enum mode; run them with it enabled.
            ConformanceHarness.Run(test, strongEnums: caseId.Contains("/strong_", StringComparison.Ordinal));
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        // Report mode (CELLY_CONFORMANCE_REPORT=<file>): record pass/fail per case and never
        // assert — used to regenerate testdata/known-failures.txt after milestone work.
        if (ReportPath is not null)
        {
            lock (ReportLock)
            {
                var detail = failure is null ? string.Empty : "\t" + failure.Message.ReplaceLineEndings(" ");
                File.AppendAllText(ReportPath, $"{(failure is null ? "PASS" : "FAIL")}\t{caseId}{detail}\n");
            }

            return;
        }

        if (failure is null && expectedFailure)
        {
            Assert.Fail(
                $"'{caseId}' is listed in testdata/known-failures.txt but now PASSES. " +
                "Remove it from the list to ratchet conformance forward.");
        }

        if (failure is not null && !expectedFailure)
        {
            throw new XunitException($"Conformance case '{caseId}' failed.", failure);
        }
    }
}
