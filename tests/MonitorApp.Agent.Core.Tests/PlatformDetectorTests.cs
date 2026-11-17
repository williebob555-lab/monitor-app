using MonitorApp.Agent.Core;
using MonitorApp.Contracts.V1;

namespace MonitorApp.Agent.Core.Tests;

public sealed class PlatformDetectorTests
{
    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("ID=ubuntu\nVERSION_ID=\"24.04\"\n", Platform.Ubuntu)]
    [InlineData("# comment\nNAME=Fedora\nID='fedora'\n", Platform.Fedora)]
    public void ParseLinuxDistributionMapsOnlyRequestedTargets(string content, Platform expected)
    {
        Assert.Equal(expected, PlatformDetector.ParseLinuxDistribution(content));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ParseLinuxDistributionRejectsUntestedDistribution()
    {
        var exception = Assert.Throws<PlatformNotSupportedException>(
            () => PlatformDetector.ParseLinuxDistribution("ID=debian\n"));

        Assert.Contains("has not passed", exception.Message, StringComparison.Ordinal);
    }
}
