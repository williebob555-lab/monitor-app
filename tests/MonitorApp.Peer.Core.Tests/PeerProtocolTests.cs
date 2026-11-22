using MonitorApp.Peer.Core;
using Xunit;

namespace MonitorApp.Peer.Core.Tests;

public sealed class PeerProtocolTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void NegotiationSelectsHighestOverlappingMinor()
    {
        var result = PeerProtocolCompatibility.Negotiate(
            new PeerProtocolRange(1, 0, 4),
            new PeerProtocolRange(1, 2, 6));

        Assert.True(result.IsCompatible);
        Assert.Equal(new PeerProtocolVersion(1, 4), result.SelectedVersion);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void NegotiationReportsExplicitCompatibilityFailures()
    {
        Assert.Equal(
            PeerCompatibilityStatus.IncompatibleMajor,
            PeerProtocolCompatibility.Negotiate(
                new PeerProtocolRange(1, 0, 2),
                new PeerProtocolRange(2, 0, 2)).Status);
        Assert.Equal(
            PeerCompatibilityStatus.NoOverlappingMinor,
            PeerProtocolCompatibility.Negotiate(
                new PeerProtocolRange(1, 0, 1),
                new PeerProtocolRange(1, 2, 3)).Status);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void DuplicateSelectionIsSymmetricAndStable()
    {
        var fromA = new PeerSessionCandidate("session-z", "peer-a", "peer-b");
        var fromB = new PeerSessionCandidate("session-a", "peer-b", "peer-a");

        Assert.Same(fromA, DuplicateSessionSelector.Select(fromA, fromB));
        Assert.Same(fromA, DuplicateSessionSelector.Select(fromB, fromA));

        var alsoFromA = new PeerSessionCandidate("session-a", "peer-a", "peer-b");
        Assert.Same(alsoFromA, DuplicateSessionSelector.Select(fromA, alsoFromA));
    }
}
