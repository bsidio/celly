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

    [Theory]
    [MemberData(nameof(CaseIds))]
    public void Run(string caseId)
    {
        var test = TestData.Cases[caseId];
        var expectedFailure = KnownFailures.Contains(caseId);

        Exception? failure = null;
        try
        {
            ConformanceHarness.Run(test);
        }
        catch (Exception ex)
        {
            failure = ex;
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
