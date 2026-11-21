using System.Security.Cryptography;
using MonitorApp.Peer.Core;

namespace MonitorApp.Peer.Application;

public interface IPeerClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemPeerClock : IPeerClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public interface IPeerRandomSource
{
    string CreateId();

    byte[] GetBytes(int count);
}

public sealed class CryptographicPeerRandomSource : IPeerRandomSource
{
    public string CreateId() => Guid.NewGuid().ToString("N");

    public byte[] GetBytes(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        return RandomNumberGenerator.GetBytes(count);
    }
}

public interface ILocalPeerIdentityProvider
{
    PeerPublicIdentity PublicIdentity { get; }

    IPeerIdentityKey IdentityKey { get; }
}

public sealed record PeerPairingCoordinatorOptions(
    PeerProtocolRange SupportedProtocol,
    TimeSpan PairingApprovalLifetime,
    TimeSpan ChallengeLifetime)
{
    public static PeerPairingCoordinatorOptions Default { get; } = new(
        new PeerProtocolRange(1, 0, 0),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(2));

    public void Validate()
    {
        SupportedProtocol.Validate();
        if (PairingApprovalLifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(PairingApprovalLifetime));
        }

        if (ChallengeLifetime <= TimeSpan.Zero ||
            ChallengeLifetime > PeerProofRules.MaximumChallengeLifetime)
        {
            throw new ArgumentOutOfRangeException(nameof(ChallengeLifetime));
        }
    }
}
