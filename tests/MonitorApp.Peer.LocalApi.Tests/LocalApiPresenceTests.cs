using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using MonitorApp.Peer.Application;
using MonitorApp.Peer.Core;
using Xunit;

namespace MonitorApp.Peer.LocalApi.Tests;

/// <summary>
/// Desktop-local user-presence enforcement (T045) through the real loopback host. The signer here stands in for the
/// desktop client process: it holds a key, receives the node's challenge, and signs the exact payload.
/// </summary>
public sealed class LocalApiPresenceTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        "monitor-local-api-presence-tests",
        Guid.NewGuid().ToString("N"));

    private readonly MutableClock clock = new();

    [Fact]
    public async Task UnenrolledNodeKeepsTodaysBehaviorAndReportsNotSetUp()
    {
        var presence = LocalPresenceService.Create(null, LocalPresencePolicy.RequiredWhenEnrolled, clock);
        await using var fixture = await LocalApiHostTests.Fixture.CreateAsync(directory, presence: presence);

        var status = await fixture.Client.GetFromJsonAsync<LocalPresenceStatus>("api/v1/local-presence", Web);
        Assert.False(status!.Enrolled);
        Assert.False(status.Enforced);
        Assert.Null(status.FingerprintSha256);

        using var response = await PostAsync(fixture, $"api/pairings/{fixture.PendingPairingId}/approve");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task EnrolledNodeRefusesUnprovenApprovalAndAcceptsOneBoundProof()
    {
        var presence = LocalPresenceService.Create(null, LocalPresencePolicy.RequiredWhenEnrolled, clock);
        await using var fixture = await LocalApiHostTests.Fixture.CreateAsync(directory, presence: presence);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await EnrollAsync(fixture, key);

        var status = await fixture.Client.GetFromJsonAsync<LocalPresenceStatus>("api/v1/local-presence", Web);
        Assert.True(status!.Enrolled);
        Assert.True(status.Enforced);
        Assert.Equal(LocalPresenceService.Fingerprint(key.ExportSubjectPublicKeyInfo()), status.FingerprintSha256);

        using var bare = await PostAsync(fixture, $"api/pairings/{fixture.PendingPairingId}/approve");
        Assert.Equal(HttpStatusCode.Forbidden, bare.StatusCode);
        Assert.Equal("presence_required", await ErrorAsync(bare));
        await AssertPairingStateAsync(fixture, PairingApprovalState.PendingApproval);

        var proof = await ProveAsync(fixture, key, "pairing.approve", fixture.PendingPairingId);
        using var approved = await PostAsync(fixture, $"api/pairings/{fixture.PendingPairingId}/approve", proof);
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        await AssertPairingStateAsync(fixture, PairingApprovalState.Approved);

        // A proof is single-use: replaying it for the same request is refused.
        using var replay = await PostAsync(fixture, $"api/pairings/{fixture.PendingPairingId}/approve", proof);
        Assert.Equal(HttpStatusCode.Forbidden, replay.StatusCode);
        Assert.Equal("presence_invalid", await ErrorAsync(replay));
    }

    [Fact]
    public async Task ProofIsBoundToActionSubjectKeyAndLifetime()
    {
        var presence = LocalPresenceService.Create(null, LocalPresencePolicy.RequiredWhenEnrolled, clock);
        await using var fixture = await LocalApiHostTests.Fixture.CreateAsync(directory, presence: presence);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await EnrollAsync(fixture, key);
        var other = await fixture.CreatePendingPairingAsync("peer-other");

        // Valid proof for another pairing is useless for this one, and is consumed by the attempt.
        var forOther = await ProveAsync(fixture, key, "pairing.approve", other);
        using var wrongSubject = await PostAsync(fixture, $"api/pairings/{fixture.PendingPairingId}/approve", forOther);
        Assert.Equal("presence_invalid", await ErrorAsync(wrongSubject));

        // A different action's proof is refused.
        var wrongAction = await ProveAsync(fixture, key, "update.approve", fixture.PendingPairingId);
        using var crossAction = await PostAsync(fixture, $"api/pairings/{fixture.PendingPairingId}/approve", wrongAction);
        Assert.Equal("presence_invalid", await ErrorAsync(crossAction));

        // Signed by a key that is not the enrolled one.
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var forged = await ProveAsync(fixture, stranger, "pairing.approve", fixture.PendingPairingId);
        using var forgedResponse = await PostAsync(fixture, $"api/pairings/{fixture.PendingPairingId}/approve", forged);
        Assert.Equal("presence_invalid", await ErrorAsync(forgedResponse));

        // Expired before use.
        var stale = await ProveAsync(fixture, key, "pairing.approve", fixture.PendingPairingId);
        clock.Advance(presence.ChallengeLifetime + TimeSpan.FromSeconds(1));
        using var expired = await PostAsync(fixture, $"api/pairings/{fixture.PendingPairingId}/approve", stale);
        Assert.Equal("presence_invalid", await ErrorAsync(expired));

        await AssertPairingStateAsync(fixture, PairingApprovalState.PendingApproval);
    }

    [Fact]
    public async Task RejectingAPairingNeedsNoPresenceBecauseItOnlyReducesTrust()
    {
        var presence = LocalPresenceService.Create(null, LocalPresencePolicy.RequiredWhenEnrolled, clock);
        await using var fixture = await LocalApiHostTests.Fixture.CreateAsync(directory, presence: presence);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await EnrollAsync(fixture, key);

        using var response = await PostAsync(fixture, $"api/pairings/{fixture.PendingPairingId}/reject");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await AssertPairingStateAsync(fixture, PairingApprovalState.Rejected);
    }

    [Fact]
    public async Task RequiredPolicyFailsClosedUntilAKeyIsEnrolled()
    {
        var presence = LocalPresenceService.Create(null, LocalPresencePolicy.Required, clock);
        await using var fixture = await LocalApiHostTests.Fixture.CreateAsync(directory, presence: presence);

        using var response = await PostAsync(fixture, $"api/pairings/{fixture.PendingPairingId}/approve");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("presence_not_enrolled", await ErrorAsync(response));
        await AssertPairingStateAsync(fixture, PairingApprovalState.PendingApproval);

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await EnrollAsync(fixture, key);
        var proof = await ProveAsync(fixture, key, "pairing.approve", fixture.PendingPairingId);
        using var approved = await PostAsync(fixture, $"api/pairings/{fixture.PendingPairingId}/approve", proof);
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
    }

    [Fact]
    public async Task OffPolicyNeverDemandsAProofEvenWhenEnrolled()
    {
        var path = Path.Combine(directory, "presence.json");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await using (var enrolling = await LocalApiHostTests.Fixture.CreateAsync(
            Path.Combine(directory, "a"),
            presence: LocalPresenceService.Create(path, LocalPresencePolicy.RequiredWhenEnrolled, clock)))
        {
            await EnrollAsync(enrolling, key);
        }

        var off = LocalPresenceService.Create(path, LocalPresencePolicy.Off, clock);
        await using var fixture = await LocalApiHostTests.Fixture.CreateAsync(Path.Combine(directory, "b"), presence: off);
        using var response = await PostAsync(fixture, $"api/pairings/{fixture.PendingPairingId}/approve");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task OnlyTheFirstKeyEnrollsAndTheEnrollmentProvesPossession()
    {
        var presence = LocalPresenceService.Create(null, LocalPresencePolicy.RequiredWhenEnrolled, clock);
        await using var fixture = await LocalApiHostTests.Fixture.CreateAsync(directory, presence: presence);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var impostor = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        // Presenting someone else's public key without the private key cannot enroll it.
        var spki = key.ExportSubjectPublicKeyInfo();
        var challenge = await ChallengeAsync(fixture, "presence.enroll", LocalPresenceService.Fingerprint(spki));
        using var stolen = await PostJsonAsync(fixture, "api/v1/local-presence/enrollment", new
        {
            publicKeySpki = Convert.ToBase64String(spki),
            challengeId = challenge.GetProperty("challengeId").GetString(),
            signature = Convert.ToBase64String(Sign(impostor, challenge)),
        });
        Assert.Equal(HttpStatusCode.Conflict, stolen.StatusCode);
        Assert.False(presence.GetStatus().Enrolled);

        await EnrollAsync(fixture, key);
        using var second = await PostJsonAsync(fixture, "api/v1/local-presence/enrollment", new
        {
            publicKeySpki = Convert.ToBase64String(impostor.ExportSubjectPublicKeyInfo()),
            challengeId = "x",
            signature = "x",
        });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(LocalPresenceService.Fingerprint(spki), presence.GetStatus().FingerprintSha256);
    }

    [Fact]
    public async Task RemovalNeedsAProofFromTheEnrolledKeyAndRestoresTodaysBehavior()
    {
        var presence = LocalPresenceService.Create(null, LocalPresencePolicy.RequiredWhenEnrolled, clock);
        await using var fixture = await LocalApiHostTests.Fixture.CreateAsync(directory, presence: presence);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await EnrollAsync(fixture, key);
        var fingerprint = LocalPresenceService.Fingerprint(key.ExportSubjectPublicKeyInfo());

        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var bad = await ChallengeAsync(fixture, "presence.remove", fingerprint);
        using var refused = await PostJsonAsync(fixture, "api/v1/local-presence/removal", new
        {
            challengeId = bad.GetProperty("challengeId").GetString(),
            signature = Convert.ToBase64String(Sign(stranger, bad)),
        });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.True(presence.GetStatus().Enrolled);

        var good = await ChallengeAsync(fixture, "presence.remove", fingerprint);
        using var removed = await PostJsonAsync(fixture, "api/v1/local-presence/removal", new
        {
            challengeId = good.GetProperty("challengeId").GetString(),
            signature = Convert.ToBase64String(Sign(key, good)),
        });
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.False(presence.GetStatus().Enrolled);
        using var approve = await PostAsync(fixture, $"api/pairings/{fixture.PendingPairingId}/approve");
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
    }

    [Fact]
    public async Task UpdateApprovalIsGatedAndBoundToTheStagedVersion()
    {
        var controller = new StagedController();
        var presence = LocalPresenceService.Create(null, LocalPresencePolicy.RequiredWhenEnrolled, clock);
        await using var fixture = await LocalApiHostTests.Fixture.CreateAsync(directory, updates: controller, presence: presence);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await EnrollAsync(fixture, key);

        using var bare = await PostAsync(fixture, "api/v1/update/approve");
        Assert.Equal(HttpStatusCode.Forbidden, bare.StatusCode);
        Assert.Equal(0, controller.Approvals);

        var wrongVersion = await ProveAsync(fixture, key, "update.approve", "9.9.9");
        using var mismatched = await PostAsync(fixture, "api/v1/update/approve", wrongVersion);
        Assert.Equal(HttpStatusCode.Forbidden, mismatched.StatusCode);
        Assert.Equal(0, controller.Approvals);

        var proof = await ProveAsync(fixture, key, "update.approve", "0.1.2");
        using var accepted = await PostAsync(fixture, "api/v1/update/approve", proof);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(1, controller.Approvals);
    }

    [Fact]
    public async Task RsaWindowsHelloStyleKeysVerify()
    {
        var presence = LocalPresenceService.Create(null, LocalPresencePolicy.RequiredWhenEnrolled, clock);
        await using var fixture = await LocalApiHostTests.Fixture.CreateAsync(directory, presence: presence);
        using var rsa = RSA.Create(2048);
        var spki = rsa.ExportSubjectPublicKeyInfo();
        var challenge = await ChallengeAsync(fixture, "presence.enroll", LocalPresenceService.Fingerprint(spki));
        var payload = Convert.FromBase64String(challenge.GetProperty("payload").GetString()!);
        using var enrolled = await PostJsonAsync(fixture, "api/v1/local-presence/enrollment", new
        {
            publicKeySpki = Convert.ToBase64String(spki),
            challengeId = challenge.GetProperty("challengeId").GetString(),
            signature = Convert.ToBase64String(rsa.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)),
        });
        Assert.Equal(HttpStatusCode.OK, enrolled.StatusCode);

        var approval = await ChallengeAsync(fixture, "pairing.approve", fixture.PendingPairingId);
        var approvalPayload = Convert.FromBase64String(approval.GetProperty("payload").GetString()!);
        using var response = await PostAsync(
            fixture,
            $"api/pairings/{fixture.PendingPairingId}/approve",
            (approval.GetProperty("challengeId").GetString()!,
                Convert.ToBase64String(rsa.SignData(approvalPayload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task EnrollmentPersistsAcrossRestartAndACorruptStoreFailsClosed()
    {
        var path = Path.Combine(directory, "store", "local-presence.json");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await using (var first = await LocalApiHostTests.Fixture.CreateAsync(
            Path.Combine(directory, "one"),
            presence: LocalPresenceService.Create(path, LocalPresencePolicy.RequiredWhenEnrolled, clock)))
        {
            await EnrollAsync(first, key);
        }

        var reopened = LocalPresenceService.Create(path, LocalPresencePolicy.RequiredWhenEnrolled, clock);
        Assert.True(reopened.GetStatus().Enrolled);
        Assert.Equal(LocalPresenceService.Fingerprint(key.ExportSubjectPublicKeyInfo()), reopened.GetStatus().FingerprintSha256);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }

        File.WriteAllText(path, "{ not json");
        Assert.Throws<InvalidOperationException>(
            () => LocalPresenceService.Create(path, LocalPresencePolicy.RequiredWhenEnrolled, clock));
    }

    [Fact]
    public async Task ChallengeRequestsNeedTheAntiforgeryTokenAndAKnownAction()
    {
        var presence = LocalPresenceService.Create(null, LocalPresencePolicy.RequiredWhenEnrolled, clock);
        await using var fixture = await LocalApiHostTests.Fixture.CreateAsync(directory, presence: presence);

        using var noToken = await fixture.Client.PostAsJsonAsync(
            "api/v1/local-presence/challenge", new { action = "pairing.approve", subject = "x" });
        Assert.Equal(HttpStatusCode.Forbidden, noToken.StatusCode);

        using var unknown = await PostJsonAsync(
            fixture, "api/v1/local-presence/challenge", new { action = "format.disk", subject = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static async Task<JsonElement> ChallengeAsync(LocalApiHostTests.Fixture fixture, string action, string subject)
    {
        using var response = await PostJsonAsync(fixture, "api/v1/local-presence/challenge", new { action, subject });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Web)).Clone();
    }

    private static byte[] Sign(ECDsa key, JsonElement challenge) =>
        key.SignData(
            Convert.FromBase64String(challenge.GetProperty("payload").GetString()!),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    private static async Task<(string ChallengeId, string Signature)> ProveAsync(
        LocalApiHostTests.Fixture fixture, ECDsa key, string action, string subject)
    {
        var challenge = await ChallengeAsync(fixture, action, subject);
        return (challenge.GetProperty("challengeId").GetString()!, Convert.ToBase64String(Sign(key, challenge)));
    }

    private static async Task EnrollAsync(LocalApiHostTests.Fixture fixture, ECDsa key)
    {
        var spki = key.ExportSubjectPublicKeyInfo();
        var challenge = await ChallengeAsync(fixture, "presence.enroll", LocalPresenceService.Fingerprint(spki));
        using var response = await PostJsonAsync(fixture, "api/v1/local-presence/enrollment", new
        {
            publicKeySpki = Convert.ToBase64String(spki),
            challengeId = challenge.GetProperty("challengeId").GetString(),
            signature = Convert.ToBase64String(Sign(key, challenge)),
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<HttpResponseMessage> PostAsync(
        LocalApiHostTests.Fixture fixture, string url, (string ChallengeId, string Signature)? proof = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(new { }) };
        request.Headers.Add("X-CSRF-TOKEN", await fixture.GetCsrfTokenAsync());
        if (proof is { } attached)
        {
            request.Headers.Add(LocalPresenceService.ChallengeHeader, attached.ChallengeId);
            request.Headers.Add(LocalPresenceService.SignatureHeader, attached.Signature);
        }

        return await fixture.Client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> PostJsonAsync(LocalApiHostTests.Fixture fixture, string url, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-CSRF-TOKEN", await fixture.GetCsrfTokenAsync());
        return await fixture.Client.SendAsync(request);
    }

    private static async Task<string?> ErrorAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(Web)).GetProperty("error").GetString();

    private static async Task AssertPairingStateAsync(LocalApiHostTests.Fixture fixture, PairingApprovalState expected) =>
        Assert.Equal(expected, (await fixture.ApplicationStore.ReadPairingAsync(fixture.PendingPairingId))!.State);

    private sealed class MutableClock : IPeerClock
    {
        private DateTimeOffset now = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);

        public DateTimeOffset UtcNow => now;

        public void Advance(TimeSpan by) => now += by;
    }

    private sealed class StagedController : ILocalUpdateController
    {
        public int Approvals { get; private set; }

        public LocalUpdateStatus GetStatus() =>
            new("approve", "0.1.1", "Staged", "0.1.2", null, null, null, true);

        public Task<bool> ApproveAsync(CancellationToken cancellationToken)
        {
            Approvals++;
            return Task.FromResult(true);
        }
    }
}
