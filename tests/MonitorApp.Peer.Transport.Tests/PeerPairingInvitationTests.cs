using System.Net;
using System.Text;
using MonitorApp.Peer.Transport;
using Xunit;

namespace MonitorApp.Peer.Transport.Tests;

public sealed class PeerPairingInvitationTests
{
    private static readonly byte[] Fingerprint = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

    [Fact]
    public void EncodeDecodeRoundTripsAllFieldsAndMapsToTheProductionEndpointPolicy()
    {
        var expiry = new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var original = new PeerPairingInvitation(IPAddress.Parse("100.64.1.2"), 5443, "peer-abc", Fingerprint, expiry);
        var decoded = PeerPairingInvitation.Decode("  " + original.Encode() + "\n");
        Assert.Equal(original.Address, decoded.Address);
        Assert.Equal(5443, decoded.Port);
        Assert.Equal("peer-abc", decoded.PeerId);
        Assert.Equal(Fingerprint, decoded.FingerprintSha256);
        Assert.Equal(expiry, decoded.ExpiresAt);
        Assert.Equal(PeerTransportSecurity.TailscaleEncrypted, decoded.ToEndpoint().Security);
        Assert.StartsWith(PeerPairingInvitation.Prefix, original.Encode(), StringComparison.Ordinal);
        Assert.True(original.Encode().Length < 300);
        var loopback = new PeerPairingInvitation(IPAddress.Loopback, 6000, "peer-x", Fingerprint, null);
        Assert.Equal(PeerTransportSecurity.LoopbackDevelopment, loopback.ToEndpoint().Security);
        Assert.Null(PeerPairingInvitation.Decode(loopback.Encode()).ExpiresAt);
    }

    [Fact]
    public void ExpiryIsEnforcedByTheCaller()
    {
        var expiry = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var invitation = new PeerPairingInvitation(IPAddress.Loopback, 6000, "peer-x", Fingerprint, expiry);
        Assert.False(invitation.IsExpired(expiry.AddSeconds(-1)));
        Assert.True(invitation.IsExpired(expiry));
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("monitor-pair2.e30")]
    [InlineData("monitor-pair1.!!!notbase64")]
    [InlineData("monitor-pair1.e30")]
    public void MalformedOrUnsupportedPayloadsAreRejected(string text) =>
        Assert.Throws<FormatException>(() => PeerPairingInvitation.Decode(text));

    [Theory]
    [InlineData("{\"v\":2,\"a\":\"127.0.0.1\",\"p\":5000,\"id\":\"p\",\"fp\":\"00\"}")]
    [InlineData("{\"v\":1,\"a\":\"8.8.8.8\",\"p\":5000,\"id\":\"p\",\"fp\":\"0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20\"}")]
    [InlineData("{\"v\":1,\"a\":\"127.0.0.1\",\"p\":0,\"id\":\"p\",\"fp\":\"0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20\"}")]
    [InlineData("{\"v\":1,\"a\":\"127.0.0.1\",\"p\":5000,\"id\":\"p\",\"fp\":\"0102\"}")]
    [InlineData("{\"v\":1,\"a\":\"host.example\",\"p\":5000,\"id\":\"p\",\"fp\":\"0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20\"}")]
    [InlineData("{\"v\":1,\"a\":\"127.0.0.1\",\"p\":5000,\"fp\":\"0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20\"}")]
    public void PolicyViolationsAreRejectedAfterDecoding(string json)
    {
        var text = PeerPairingInvitation.Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Throws<FormatException>(() => PeerPairingInvitation.Decode(text));
    }

    [Fact]
    public void OversizedPayloadIsRejected() =>
        Assert.Throws<FormatException>(() => PeerPairingInvitation.Decode(PeerPairingInvitation.Prefix + new string('A', 2000)));
}
