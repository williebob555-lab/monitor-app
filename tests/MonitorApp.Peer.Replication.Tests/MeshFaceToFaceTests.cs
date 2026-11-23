using Google.Protobuf;
using MonitorApp.Contracts.V1;
using MonitorApp.Peer.Core;
using MonitorApp.Peer.Replication;
using Xunit;

namespace MonitorApp.Peer.Replication.Tests;

/// <summary>
/// Face-to-face join codes (T085): a join code is bound to its sender's key and expires; a welcome is bound to the
/// member's key and to one join request, so neither can be altered, replayed to another device, or reused.
/// </summary>
public sealed class MeshFaceToFaceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
    private readonly EcdsaPeerIdentityKey joinerKey = EcdsaPeerIdentityKey.Create();
    private readonly EcdsaPeerIdentityKey memberKey = EcdsaPeerIdentityKey.Create();
    private readonly EcdsaPeerIdentityKey otherKey = EcdsaPeerIdentityKey.Create();
    private readonly PeerPublicIdentity joiner;
    private readonly PeerPublicIdentity member;
    private readonly PeerPublicIdentity other;

    public MeshFaceToFaceTests()
    {
        joiner = PeerPublicIdentity.Create("tablet-1", joinerKey);
        member = PeerPublicIdentity.Create("phone-1", memberKey);
        other = PeerPublicIdentity.Create("laptop-1", otherKey);
    }

    [Fact]
    public void AJoinCodeRoundTripsAndAWelcomeAnswersExactlyThatRequest()
    {
        var request = MeshFaceToFace.CreateJoinRequest(joinerKey, joiner, "Tablet", [new MeshLocator { Host = "100.64.0.7", Port = 7443 }], Now);
        var joinCode = MeshFaceToFace.Encode(request);
        Assert.True(MeshFaceToFace.IsMeshCode(joinCode));
        Assert.False(MeshFaceToFace.IsMeshCode("monitor-pair1.abc"));

        var decoded = MeshFaceToFace.DecodeJoin(joinCode, Now.AddMinutes(1));
        Assert.Equal(joiner.PeerId, decoded.PeerId);
        Assert.Equal(joiner.PublicKeyFingerprint, MeshFaceToFace.JoinerIdentity(decoded).PublicKeyFingerprint);
        Assert.Equal("Tablet", decoded.DisplayName);

        var admit = MeshStatementCryptography.CreateSigned(memberKey, member, MeshStatementKind.Admit, other, "Laptop",
            [new MeshLocator { Host = "100.64.0.2", Port = 7443 }], Now);
        var welcomeCode = MeshFaceToFace.Encode(MeshFaceToFace.CreateWelcome(memberKey, member, "Phone", [], decoded, [admit]));
        var welcome = MeshFaceToFace.DecodeWelcome(welcomeCode, joiner.PeerId, request.Nonce.Span);
        Assert.Equal(member.PeerId, MeshFaceToFace.MemberIdentity(welcome).PeerId);
        Assert.Equal(admit, Assert.Single(welcome.Statements));

        // Both screens show the same short code for each device.
        Assert.Matches("^[0-9A-F]{4}-[0-9A-F]{4}$", MeshFaceToFace.VerificationCode(member.PublicKeyFingerprint));
    }

    [Fact]
    public void AlteredForgedOrExpiredJoinCodesAreRefused()
    {
        var request = MeshFaceToFace.CreateJoinRequest(joinerKey, joiner, "Tablet", [], Now);

        // Renamed after signing.
        var renamed = request.Clone();
        renamed.DisplayName = "Laptop";
        Assert.Throws<FormatException>(() => MeshFaceToFace.DecodeJoin(MeshFaceToFace.Encode(renamed), Now));

        // Another device's key swapped in (the signature no longer matches the key it carries).
        var swapped = request.Clone();
        swapped.PublicKeySpki = ByteString.CopyFrom(other.PublicKeySpki);
        Assert.Throws<FormatException>(() => MeshFaceToFace.DecodeJoin(MeshFaceToFace.Encode(swapped), Now));

        // An address added after signing.
        var redirected = request.Clone();
        redirected.SelfLocators.Add(new MeshLocator { Host = "203.0.113.5", Port = 7443 });
        Assert.Throws<FormatException>(() => MeshFaceToFace.DecodeJoin(MeshFaceToFace.Encode(redirected), Now));

        // Expired, from the future, garbage, wrong prefix, oversized.
        var code = MeshFaceToFace.Encode(request);
        Assert.Throws<FormatException>(() => MeshFaceToFace.DecodeJoin(code, Now + MeshFaceToFace.JoinLifetime + TimeSpan.FromSeconds(1)));
        Assert.Throws<FormatException>(() => MeshFaceToFace.DecodeJoin(code, Now - TimeSpan.FromHours(1)));
        Assert.Throws<FormatException>(() => MeshFaceToFace.DecodeJoin(MeshFaceToFace.JoinPrefix + "!!!", Now));
        Assert.Throws<FormatException>(() => MeshFaceToFace.DecodeJoin(code.Replace(MeshFaceToFace.JoinPrefix, MeshFaceToFace.WelcomePrefix, StringComparison.Ordinal), Now));
        Assert.Throws<FormatException>(() => MeshFaceToFace.DecodeJoin(MeshFaceToFace.JoinPrefix + new string('A', 20000), Now));
    }

    [Fact]
    public void AWelcomeCannotBeReplayedToAnotherRequestOrAlteredOrForged()
    {
        var request = MeshFaceToFace.CreateJoinRequest(joinerKey, joiner, "Tablet", [], Now);
        var admit = MeshStatementCryptography.CreateSigned(memberKey, member, MeshStatementKind.Admit, other, "Laptop", [], Now);
        var welcome = MeshFaceToFace.CreateWelcome(memberKey, member, "Phone", [], request, [admit]);
        var code = MeshFaceToFace.Encode(welcome);

        // A later join request from the same device has a new nonce: an old welcome does not answer it.
        var later = MeshFaceToFace.CreateJoinRequest(joinerKey, joiner, "Tablet", [], Now.AddMinutes(1));
        Assert.Throws<FormatException>(() => MeshFaceToFace.DecodeWelcome(code, joiner.PeerId, later.Nonce.Span));
        // Nor does it answer another device.
        Assert.Throws<FormatException>(() => MeshFaceToFace.DecodeWelcome(code, other.PeerId, request.Nonce.Span));

        // A statement swapped in after signing.
        var smuggled = welcome.Clone();
        smuggled.Statements.Clear();
        smuggled.Statements.Add(MeshStatementCryptography.CreateSigned(otherKey, other, MeshStatementKind.Admit, member, "x", [], Now));
        Assert.Throws<FormatException>(() => MeshFaceToFace.DecodeWelcome(MeshFaceToFace.Encode(smuggled), joiner.PeerId, request.Nonce.Span));

        // An address changed after signing.
        var redirected = welcome.Clone();
        redirected.MemberLocators.Add(new MeshLocator { Host = "203.0.113.5", Port = 7443 });
        Assert.Throws<FormatException>(() => MeshFaceToFace.DecodeWelcome(MeshFaceToFace.Encode(redirected), joiner.PeerId, request.Nonce.Span));

        // Signed by a different key than the member it names.
        var forged = MeshFaceToFace.CreateWelcome(otherKey, member, "Phone", [], request, []);
        Assert.Throws<FormatException>(() => MeshFaceToFace.DecodeWelcome(MeshFaceToFace.Encode(forged), joiner.PeerId, request.Nonce.Span));

        // A device cannot welcome itself.
        var self = MeshFaceToFace.CreateWelcome(joinerKey, joiner, "Tablet", [], request, []);
        Assert.Throws<FormatException>(() => MeshFaceToFace.DecodeWelcome(MeshFaceToFace.Encode(self), joiner.PeerId, request.Nonce.Span));
    }

    public void Dispose()
    {
        joinerKey.Dispose();
        memberKey.Dispose();
        otherKey.Dispose();
    }
}
