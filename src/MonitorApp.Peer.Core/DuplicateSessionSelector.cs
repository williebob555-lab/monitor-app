namespace MonitorApp.Peer.Core;

public sealed record PeerSessionCandidate(
    string SessionId,
    string InitiatorPeerId,
    string ResponderPeerId)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(SessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(InitiatorPeerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ResponderPeerId);
        if (string.Equals(InitiatorPeerId, ResponderPeerId, StringComparison.Ordinal))
        {
            throw new ArgumentException("A peer session requires distinct peers.");
        }
    }
}

public static class DuplicateSessionSelector
{
    public static PeerSessionCandidate Select(
        PeerSessionCandidate first,
        PeerSessionCandidate second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        first.Validate();
        second.Validate();

        var firstPeers = CanonicalPeerPair(first);
        var secondPeers = CanonicalPeerPair(second);
        if (!string.Equals(firstPeers.Low, secondPeers.Low, StringComparison.Ordinal) ||
            !string.Equals(firstPeers.High, secondPeers.High, StringComparison.Ordinal))
        {
            throw new ArgumentException("Candidates must connect the same two peers.", nameof(second));
        }

        return Compare(first, second) <= 0 ? first : second;
    }

    private static int Compare(PeerSessionCandidate left, PeerSessionCandidate right)
    {
        var initiator = string.CompareOrdinal(left.InitiatorPeerId, right.InitiatorPeerId);
        return initiator != 0 ? initiator : string.CompareOrdinal(left.SessionId, right.SessionId);
    }

    private static (string Low, string High) CanonicalPeerPair(PeerSessionCandidate candidate) =>
        string.CompareOrdinal(candidate.InitiatorPeerId, candidate.ResponderPeerId) < 0
            ? (candidate.InitiatorPeerId, candidate.ResponderPeerId)
            : (candidate.ResponderPeerId, candidate.InitiatorPeerId);
}
