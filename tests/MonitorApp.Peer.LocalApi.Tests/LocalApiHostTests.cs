using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using MonitorApp.Peer.Alerts;
using MonitorApp.Peer.Application;
using MonitorApp.Peer.Core;
using MonitorApp.Peer.LocalApi;
using MonitorApp.Peer.Persistence;
using Xunit;

namespace MonitorApp.Peer.LocalApi.Tests;

public sealed class LocalApiHostTests : IDisposable
{
    internal static readonly DateTimeOffset Now =
        new(2026, 11, 21, 16, 0, 0, TimeSpan.Zero);

    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        "monitor-local-api-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void NonLoopbackBindingFailsClosed()
    {
        using var localKey = EcdsaPeerIdentityKey.Create();
        var identity = new IdentityProvider("local", localKey);
        var databasePath = Path.Combine(directory, "peer.db");
        var store = new PeerStore(PeerStoreOptions.Create(databasePath));
        var appStore = new PairingApplicationStore(PairingApplicationStoreOptions.Create(databasePath));
        var coordinator = Coordinator(store, appStore, identity);
        var options = new LocalApiOptions(databasePath, IPAddress.Any, 0);

        var exception = Assert.Throws<InvalidOperationException>(
            () => LocalApiHost.Create(options, store, appStore, coordinator, TestClock.Instance));

        Assert.Contains("loopback", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("No authenticated remote-access mode", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RuntimePeerPairingAndObservationQueriesUseRealSqliteState()
    {
        await using var fixture = await Fixture.CreateAsync(directory);

        var runtime = await fixture.Client.GetFromJsonAsync<LocalRuntimeView>("api/runtime", JsonOptions);
        var peers = await fixture.Client.GetFromJsonAsync<TrustedPeerView[]>("api/peers", JsonOptions);
        var pairings = await fixture.Client.GetFromJsonAsync<PendingPairingView[]>("api/pairings", JsonOptions);
        var observations = await fixture.Client.GetFromJsonAsync<OriginObservationView[]>(
            "api/observations?originPeerId=peer-local&originStreamId=origin-local&firstSequence=1&limit=10",
            JsonOptions);

        Assert.Equal("running", runtime!.Status);
        Assert.Equal("peer-local", runtime.LocalPeer!.PeerId);
        Assert.Equal(Convert.ToHexString(fixture.LocalIdentity.PublicKeyFingerprint), runtime.LocalPeer.FingerprintSha256);
        Assert.Contains("Implemented", runtime.Scope, StringComparison.Ordinal);
        Assert.Contains("Planned", runtime.Scope, StringComparison.Ordinal);
        Assert.Contains(peers!, peer => peer.PeerId == "peer-trusted" && peer.TrustState == "Active");
        Assert.Contains(pairings!, pairing => pairing.PairingId == fixture.PendingPairingId && pairing.ApprovalState == "PendingApproval");
        var observation = Assert.Single(observations!);
        Assert.Equal("peer-local", observation.OriginPeerId);
        Assert.Equal("adapter-one", observation.Source);
        Assert.Equal(Now.AddMinutes(-1), observation.SourceTime);
        Assert.Equal(Now, observation.ReceivedTime);
        Assert.Equal("Good", observation.Quality);
        Assert.Equal("LocalOrigin", observation.Provenance);
    }

    [Fact]
    public async Task AlertsEndpointReturnsActiveAndRecentlyResolvedAlertsBounded()
    {
        await using var fixture = await Fixture.CreateAsync(directory);
        Alert Make(string subject, DateTimeOffset at) => new(
            Alert.CreateId("peer-local", "rule.x", subject, at), 1, "rule.x", subject, "peer-local",
            MonitorApp.Contracts.V1.AlertSeverity.Warning, MonitorApp.Contracts.V1.AlertState.Raised, "T " + subject, "D", at, null);
        await fixture.Alerts.RaiseAsync(Make("a", Now.AddMinutes(-30)), [], TimeSpan.FromDays(1), Now);
        await fixture.Alerts.RaiseAsync(Make("b", Now.AddMinutes(-20)), [], TimeSpan.FromDays(1), Now);
        await fixture.Alerts.RaiseAsync(Make("old", Now.AddDays(-20)), [], TimeSpan.FromDays(1), Now);
        await fixture.Alerts.ResolveAsync("peer-local", "rule.x", "old", Now.AddDays(-19), [], TimeSpan.FromDays(1), Now);
        await fixture.Alerts.ResolveAsync("peer-local", "rule.x", "b", Now.AddMinutes(-5), [], TimeSpan.FromDays(1), Now);

        using var all = JsonDocument.Parse(await fixture.Client.GetStringAsync("api/v1/alerts"));
        var items = all.RootElement.GetProperty("alerts").EnumerateArray().ToArray();
        Assert.Equal("a,b", string.Join(",", items.Select(item => item.GetProperty("subjectId").GetString())));
        Assert.Equal("Raised", items[0].GetProperty("state").GetString());
        Assert.Equal("Resolved", items[1].GetProperty("state").GetString());

        using var bounded = JsonDocument.Parse(await fixture.Client.GetStringAsync("api/v1/alerts?limit=1"));
        Assert.Single(bounded.RootElement.GetProperty("alerts").EnumerateArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await fixture.Client.GetAsync("api/v1/alerts?limit=501")).StatusCode);
    }

    [Fact]
    public async Task MutationWithoutCsrfTokenIsForbidden()
    {
        await using var fixture = await Fixture.CreateAsync(directory);

        var response = await fixture.Client.PostAsync(
            $"api/pairings/{fixture.PendingPairingId}/approve",
            JsonContent.Create(new { }));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var pairing = await fixture.ApplicationStore.ReadPairingAsync(fixture.PendingPairingId);
        Assert.Equal(PairingApprovalState.PendingApproval, pairing!.State);
    }

    [Fact]
    public async Task ValidCsrfTokenAllowsApproveRejectAndRevokeMutations()
    {
        await using var fixture = await Fixture.CreateAsync(directory);
        var token = await fixture.GetCsrfTokenAsync();

        using var approve = Request(
            HttpMethod.Post,
            $"api/pairings/{fixture.PendingPairingId}/approve",
            token);
        var approved = await fixture.Client.SendAsync(approve);
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);

        var secondPairing = await fixture.CreatePendingPairingAsync("peer-reject");
        using var reject = Request(HttpMethod.Post, $"api/pairings/{secondPairing}/reject", token);
        var rejected = await fixture.Client.SendAsync(reject);
        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);

        using var revoke = Request(HttpMethod.Post, "api/peers/peer-trusted/revoke", token);
        var revoked = await fixture.Client.SendAsync(revoke);
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);

        Assert.Equal(
            PairingApprovalState.Approved,
            (await fixture.ApplicationStore.ReadPairingAsync(fixture.PendingPairingId))!.State);
        Assert.Equal(
            PairingApprovalState.Rejected,
            (await fixture.ApplicationStore.ReadPairingAsync(secondPairing))!.State);
        Assert.Equal(
            PeerTrustState.Revoked,
            (await fixture.PeerStore.ReadTrustedPeerAsync("peer-trusted"))!.TrustState);
    }

    [Fact]
    public async Task HtmlIsKeyboardUsableTruthfulAndRedactsLocalSecrets()
    {
        await using var fixture = await Fixture.CreateAsync(directory);

        var page = await fixture.Client.GetStringAsync("/");
        var runtime = await fixture.Client.GetStringAsync("api/runtime");
        var peers = await fixture.Client.GetStringAsync("api/peers");
        var pairings = await fixture.Client.GetStringAsync("api/pairings");
        var combined = string.Concat(page, runtime, peers, pairings);

        Assert.Contains("<h1>Monitor App local administration</h1>", page, StringComparison.Ordinal);
        Assert.Contains("<button type=\"submit\">", page, StringComparison.Ordinal);
        Assert.Contains("Implemented now", page, StringComparison.Ordinal);
        Assert.Contains("Planned", page, StringComparison.Ordinal);
        Assert.DoesNotContain(Fixture.KeyProviderSecret, combined, StringComparison.Ordinal);
        Assert.DoesNotContain(Fixture.AdapterCredentialSecret, combined, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(fixture.LocalIdentity.PublicKeySpki), combined, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvitationFormIsExplicitAndCsrfProtected()
    {
        await using var fixture = await Fixture.CreateAsync(directory);

        var page = await fixture.Client.GetStringAsync("/");
        var response = await fixture.Client.PostAsJsonAsync("api/invitations", ValidInvitation());

        Assert.Contains("<h2 id=\"invite\">Invite a fresh peer</h2>", page, StringComparison.Ordinal);
        Assert.Contains("name=\"expectedPeerId\"", page, StringComparison.Ordinal);
        Assert.Contains("name=\"expectedFingerprintSha256\"", page, StringComparison.Ordinal);
        Assert.Contains("name=\"securityPolicy\"", page, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("8.8.8.8")]
    public async Task InvitationRejectsWildcardAndPublicAddressesBeforeDispatch(string address)
    {
        var invitationService = new RecordingInvitationService();
        await using var fixture = await Fixture.CreateAsync(directory, invitationService);
        var csrf = await fixture.GetCsrfTokenAsync();
        using var request = Request(
            HttpMethod.Post,
            "api/invitations",
            csrf,
            ValidInvitation() with { Address = address });

        var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(invitationService.Commands);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(Fixture.KeyProviderSecret, body, StringComparison.Ordinal);
        Assert.DoesNotContain(Fixture.AdapterCredentialSecret, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidInvitationDispatchesNormalizedIdentityAndPolicy()
    {
        var invitationService = new RecordingInvitationService();
        await using var fixture = await Fixture.CreateAsync(directory, invitationService);
        var csrf = await fixture.GetCsrfTokenAsync();
        using var request = Request(HttpMethod.Post, "api/invitations", csrf, ValidInvitation());

        var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var command = Assert.Single(invitationService.Commands);
        Assert.Equal("peer-new", command.ExpectedPeerId);
        Assert.Equal(IPAddress.Loopback, command.Address);
        Assert.Equal(51831, command.Port);
        Assert.Equal(new string('A', 64), command.ExpectedFingerprintSha256);
        Assert.Equal(PeerInvitationSecurityPolicy.LoopbackPrototype, command.SecurityPolicy);
    }

    [Fact]
    public async Task DefaultInvitationServiceFailsClosedAsUnavailable()
    {
        await using var fixture = await Fixture.CreateAsync(directory);
        var csrf = await fixture.GetCsrfTokenAsync();
        using var request = Request(HttpMethod.Post, "api/invitations", csrf, ValidInvitation());

        var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<PeerInvitationResult>(JsonOptions);
        Assert.Equal(PeerInvitationStatus.Unavailable, result!.Status);
    }

    [Fact]
    public async Task FormDispatchDisplaysServiceStatusWithHtmlEncoding()
    {
        var invitationService = new RecordingInvitationService(
            new PeerInvitationResult(PeerInvitationStatus.Dispatched, "Queued <script>alert('x')</script>"));
        await using var fixture = await Fixture.CreateAsync(directory, invitationService);
        var page = await fixture.Client.GetStringAsync("/");
        var token = ExtractHiddenToken(page);
        var form = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["expectedPeerId"] = "peer-new",
            ["address"] = "127.0.0.1",
            ["port"] = "51831",
            ["expectedFingerprintSha256"] = new string('a', 64),
            ["securityPolicy"] = "LoopbackPrototype",
        };

        var response = await fixture.Client.PostAsync("api/invitations", new FormUrlEncodedContent(form));
        var updatedPage = await fixture.Client.GetStringAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Queued &lt;script&gt;alert(&#x27;x&#x27;)&lt;/script&gt;", updatedPage, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>alert('x')</script>", updatedPage, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }

    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static PeerInvitationInput ValidInvitation() => new(
        " peer-new ",
        "127.0.0.1",
        51831,
        string.Join(':', Enumerable.Repeat("aa", 32)),
        "LoopbackPrototype");

    private static HttpRequestMessage Request(
        HttpMethod method,
        string path,
        string csrf,
        object? content = null)
    {
        var request = new HttpRequestMessage(method, path)
        {
            Content = JsonContent.Create(content ?? new { }),
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        return request;
    }

    private static string ExtractHiddenToken(string page)
    {
        const string marker = "name=\"__RequestVerificationToken\" value=\"";
        var start = page.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0);
        start += marker.Length;
        var end = page.IndexOf('"', start);
        Assert.True(end > start);
        return WebUtility.HtmlDecode(page[start..end]);
    }

    private static PeerPairingCoordinator Coordinator(
        PeerStore store,
        PairingApplicationStore appStore,
        ILocalPeerIdentityProvider identity) => new(
            store,
            appStore,
            identity,
            TestClock.Instance,
            new TestRandom(),
            new PeerPairingCoordinatorOptions(
                new PeerProtocolRange(1, 0, 0),
                TimeSpan.FromMinutes(5),
                TimeSpan.FromMinutes(2)));

    internal sealed class Fixture : IAsyncDisposable
    {
        public const string KeyProviderSecret = "key-provider-super-secret";
        public const string AdapterCredentialSecret = "adapter-credential-super-secret";

        private readonly EcdsaPeerIdentityKey localKey;
        private readonly List<EcdsaPeerIdentityKey> remoteKeys = [];
        private readonly IdentityProvider identityProvider;
        private readonly PeerPairingCoordinator coordinator;

        private Fixture(
            EcdsaPeerIdentityKey localKey,
            IdentityProvider identityProvider,
            PeerStore peerStore,
            PairingApplicationStore applicationStore,
            PeerPairingCoordinator coordinator,
            LocalApiHost host,
            HttpClient client,
            string pendingPairingId,
            AlertStore alerts)
        {
            Alerts = alerts;
            this.localKey = localKey;
            this.identityProvider = identityProvider;
            this.coordinator = coordinator;
            PeerStore = peerStore;
            ApplicationStore = applicationStore;
            Host = host;
            Client = client;
            PendingPairingId = pendingPairingId;
        }

        public AlertStore Alerts { get; }
        public PeerStore PeerStore { get; }
        public PairingApplicationStore ApplicationStore { get; }
        public LocalApiHost Host { get; }
        public HttpClient Client { get; }
        public string PendingPairingId { get; }
        public PeerPublicIdentity LocalIdentity => identityProvider.PublicIdentity;

        public static async Task<Fixture> CreateAsync(
            string directory,
            IPeerInvitationService? invitationService = null,
            PhonePairingOptions? phonePairing = null)
        {
            Directory.CreateDirectory(directory);
            var databasePath = Path.Combine(directory, "peer.db");
            var localKey = EcdsaPeerIdentityKey.Create();
            var identityProvider = new IdentityProvider("peer-local", localKey);
            var store = new PeerStore(PeerStoreOptions.Create(databasePath));
            var appStore = new PairingApplicationStore(PairingApplicationStoreOptions.Create(databasePath));
            var coordinator = Coordinator(store, appStore, identityProvider);
            await coordinator.InitializeAsync();
            await store.SetLocalIdentityAsync(new LocalPeerIdentity(
                "peer-local",
                identityProvider.PublicIdentity.PublicKeySpki,
                identityProvider.PublicIdentity.PublicKeyFingerprint,
                KeyProviderSecret,
                "origin-local",
                1,
                Now.AddHours(-1)));

            using var trustedKey = EcdsaPeerIdentityKey.Create();
            var trustedIdentity = PeerPublicIdentity.Create("peer-trusted", trustedKey);
            await store.UpsertTrustedPeerAsync(new TrustedPeer(
                trustedIdentity.PeerId,
                trustedIdentity.PublicKeySpki,
                trustedIdentity.PublicKeyFingerprint,
                "Trusted workstation",
                PeerTrustState.Active,
                Now.AddDays(-1),
                null));
            await store.UpsertAdapterInstanceAsync(new AdapterInstance(
                "adapter-one",
                "test.adapter",
                "1.0.0",
                "peer-local",
                null,
                AdapterCredentialSecret,
                true));
            await store.AppendOriginRecordAsync(new OriginRecordWrite(
                "peer-local",
                "origin-local",
                1,
                "entity-one",
                "adapter-one",
                "temperature",
                [1, 2, 3, 4],
                Enumerable.Repeat((byte)0x2a, 64).ToArray(),
                Now.AddMinutes(-1),
                Now,
                null,
                RecordProvenance.LocalOrigin,
                "Good"));

            var pendingKey = EcdsaPeerIdentityKey.Create();
            var pending = await coordinator.BeginPairingAsync(new PairingCandidate(
                PeerPublicIdentity.Create("peer-pending", pendingKey),
                "Pending laptop",
                PeerProofRole.Initiator,
                new PeerProtocolRange(1, 0, 0),
                []));

            var alerts = new AlertStore(AlertStoreOptions.Create(databasePath));
            await alerts.InitializeAsync();
            var host = LocalApiHost.Create(
                LocalApiOptions.Loopback(databasePath),
                store,
                appStore,
                coordinator,
                TestClock.Instance,
                invitationService,
                phonePairing,
                alerts);
            await host.StartAsync();
            var handler = new HttpClientHandler
            {
                CookieContainer = new CookieContainer(),
                UseCookies = true,
            };
            var client = new HttpClient(handler) { BaseAddress = host.Address };
            var fixture = new Fixture(
                localKey, identityProvider, store, appStore, coordinator, host, client, pending.PairingId!, alerts);
            fixture.remoteKeys.Add(pendingKey);
            return fixture;
        }

        public async Task<string> GetCsrfTokenAsync()
        {
            var response = await Client.GetFromJsonAsync<CsrfResponse>("api/csrf", JsonOptions);
            return response!.RequestToken;
        }

        public async Task<string> CreatePendingPairingAsync(string peerId)
        {
            var key = EcdsaPeerIdentityKey.Create();
            remoteKeys.Add(key);
            var result = await coordinator.BeginPairingAsync(new PairingCandidate(
                PeerPublicIdentity.Create(peerId, key),
                peerId,
                PeerProofRole.Initiator,
                new PeerProtocolRange(1, 0, 0),
                []));
            return result.PairingId!;
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Host.DisposeAsync();
            foreach (var key in remoteKeys)
            {
                key.Dispose();
            }

            localKey.Dispose();
        }
    }

    private sealed record CsrfResponse(string RequestToken);

    private sealed class IdentityProvider : ILocalPeerIdentityProvider
    {
        public IdentityProvider(string peerId, IPeerIdentityKey key)
        {
            IdentityKey = key;
            PublicIdentity = PeerPublicIdentity.Create(peerId, key);
        }

        public PeerPublicIdentity PublicIdentity { get; }
        public IPeerIdentityKey IdentityKey { get; }
    }

    private sealed class TestClock : IPeerClock
    {
        public static TestClock Instance { get; } = new();
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class TestRandom : IPeerRandomSource
    {
        private int id;
        public string CreateId() => $"id-{Interlocked.Increment(ref id)}";
        public byte[] GetBytes(int count) => RandomNumberGenerator.GetBytes(count);
    }

    private sealed class RecordingInvitationService(
        PeerInvitationResult? result = null) : IPeerInvitationService
    {
        public List<PeerInvitationCommand> Commands { get; } = [];

        public ValueTask<PeerInvitationResult> InviteAsync(
            PeerInvitationCommand invitation,
            CancellationToken cancellationToken)
        {
            Commands.Add(invitation);
            return ValueTask.FromResult(result ?? new PeerInvitationResult(
                PeerInvitationStatus.Dispatched,
                "Invitation dispatched."));
        }
    }
}
