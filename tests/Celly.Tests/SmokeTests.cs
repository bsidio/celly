using Xunit;

namespace Celly.Tests;

public class SmokeTests
{
    [Fact]
    public void SpecCommitIsPinned() => Assert.Equal(40, CellyInfo.SpecCommit.Length);
}
