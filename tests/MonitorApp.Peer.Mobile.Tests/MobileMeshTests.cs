using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MonitorApp.Peer.Node;
using MonitorApp.Peer.Persistence;
using MonitorApp.Peer.Transport;
using Xunit;

namespace MonitorApp.Peer.Mobile.Tests;

/// <summary>
/// The mesh from the phone's side (T080), over real loopback gRPC: a phone paired with one computer trusts and talks to
/// every computer that computer trusts, and a device the phone removes is removed on the computers.
/// </summary>
public sealed class MobileMeshTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task APhonePairedWithOneComputerReachesTheOtherComputersAndItsRemovalsSpread()
    {
        var roots = new[] { TestDirectories.Create(), TestDirectories.Create(), TestDirectories.Create() };
        try
        {
            var nodeOptions = new NodeLiveOptions(CollectionInterval: TimeSpan.FromHours(1), CollectSelfLan: false,
                PeerStateInterval: TimeSpan.FromSeconds(1));
            var deskPort = FreePort();
            var serverPort = FreePort();
            await using var desk = await PeerNodeRuntime.StartAsync(new NodeOptions(roots[0], IPAddress.Loopback, deskPort, 0), nodeOptions);
            await using var server = await PeerNodeRuntime.StartAsync(new NodeOptions(roots[1], IPAddress.Loopback, serverPort, 0), nodeOptions);
            await PairNodesAsync(desk, server, serverPort);

            using var phoneIdentity = new SoftwareIdentity();
            await using var phone = await MobilePeerRuntime.CreateAsync(
                new MobilePeerOptions(roots[2], "phone", CollectionInterval: TimeSpan.FromHours(1),
                    SyncInterval: TimeSpan.FromMilliseconds(200), MaximumSyncBackoff: TimeSpan.FromSeconds(1),
                    HeartbeatInterval: TimeSpan.FromSeconds(5)),
                phoneIdentity,
                new ScriptedDeviceReader());
            // The phone pairs with the desk only.
            await PairPhoneAsync(phone, desk, deskPort);
            phone.Start();

            // The phone learns the server from the desk, and the server learns the phone, without pairing.
            var phoneId = phone.Identity.PeerId;
            await WaitAsync(async () =>
                (await PhoneStore(roots[2]).ReadTrustedPeerAsync(server.Identity.PeerId))?.TrustState == PeerTrustState.Active &&
                (await server.Store.ReadTrustedPeerAsync(phoneId))?.TrustState == PeerTrustState.Active);

            // And the phone syncs with the server directly: the server accepts the phone's readings from the phone itself
            // (they may also arrive first through the desk, which forwards them, T087).
            await phone.CollectOnceAsync();
            var phoneStream = (await PhoneStore(roots[2]).ReadLocalIdentityAsync())!.OriginStreamId;
            await WaitAsync(async () => (await server.Store.ReadReplicationCursorAsync(phoneId, phoneId, phoneStream))
                .Cursor?.AcceptedThrough >= 1);

            // Removing the server on the phone removes it on the desk, which paired it by hand.
            await phone.RevokeAsync(server.Identity.PeerId);
            await WaitAsync(async () => (await desk.Store.ReadTrustedPeerAsync(server.Identity.PeerId))?.TrustState == PeerTrustState.Revoked);
            Assert.Equal(PeerTrustState.Active, (await desk.Store.ReadTrustedPeerAsync(phoneId))?.TrustState);
        }
        finally
        {
            foreach (var root in roots)
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    [Fact]
    public async Task APhoneStillReachesItsComputerWhenANewerAddressIsUnreachable()
    {
        var roots = new[] { TestDirectories.Create(), TestDirectories.Create() };
        try
        {
            var deskPort = FreePort();
            await using var desk = await PeerNodeRuntime.StartAsync(new NodeOptions(roots[0], IPAddress.Loopback, deskPort, 0),
                new NodeLiveOptions(CollectionInterval: TimeSpan.FromHours(1), CollectSelfLan: false, PeerStateInterval: TimeSpan.FromSeconds(1)));
            using var phoneIdentity = new SoftwareIdentity();
            await using var phone = await MobilePeerRuntime.CreateAsync(
                new MobilePeerOptions(roots[1], "phone", CollectionInterval: TimeSpan.FromHours(1),
                    SyncInterval: TimeSpan.FromMilliseconds(200), MaximumSyncBackoff: TimeSpan.FromSeconds(1),
                    HeartbeatInterval: TimeSpan.FromSeconds(5)),
                phoneIdentity,
                new ScriptedDeviceReader());
            await PairPhoneAsync(phone, desk, deskPort);

            // What 0.1.18 did: the computer's advertised address (unreachable from this phone) stored as the newest.
            await PhoneStore(roots[1]).UpsertPeerLocatorAsync(
                new PeerLocator(desk.Identity.PeerId, "127.0.0.1", FreePort(), DateTimeOffset.UtcNow.AddMinutes(1), null));
            Assert.NotEqual(deskPort, (await PhoneStore(roots[1]).ReadPeerLocatorsAsync(desk.Identity.PeerId))[0].Port);

            phone.Start();
            await phone.CollectOnceAsync();
            var phoneId = phone.Identity.PeerId;
            var phoneStream = (await PhoneStore(roots[1]).ReadLocalIdentityAsync())!.OriginStreamId;
            await WaitAsync(async () => (await desk.Store.ReadOriginRangeAsync(phoneId, phoneStream, 1, 1)).Count == 1);

            // The address that worked is remembered and dialed first from now on.
            await WaitAsync(async () => (await PhoneStore(roots[1]).ReadPeerLocatorsAsync(desk.Identity.PeerId))[0].Port == deskPort);
        }
        finally
        {
            foreach (var root in roots)
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    [Fact]
    public async Task ANewlyPairedPhoneStartsSyncingAtOnceInsteadOfAtTheNextPass()
    {
        var roots = new[] { TestDirectories.Create(), TestDirectories.Create() };
        try
        {
            var deskPort = FreePort();
            await using var desk = await PeerNodeRuntime.StartAsync(new NodeOptions(roots[0], IPAddress.Loopback, deskPort, 0),
                new NodeLiveOptions(CollectionInterval: TimeSpan.FromHours(1), CollectSelfLan: false, PeerStateInterval: TimeSpan.FromSeconds(1)));
            using var phoneIdentity = new SoftwareIdentity();
            // A pass interval far longer than the test: only the wake-up after pairing can start the session in time.
            await using var phone = await MobilePeerRuntime.CreateAsync(
                new MobilePeerOptions(roots[1], "phone", CollectionInterval: TimeSpan.FromHours(1),
                    SyncInterval: TimeSpan.FromHours(1), MaximumSyncBackoff: TimeSpan.FromHours(1), HeartbeatInterval: TimeSpan.FromSeconds(5)),
                phoneIdentity,
                new ScriptedDeviceReader());
            phone.Start();
            await phone.CollectOnceAsync();
            await PairPhoneAsync(phone, desk, deskPort);

            var phoneId = phone.Identity.PeerId;
            var phoneStream = (await PhoneStore(roots[1]).ReadLocalIdentityAsync())!.OriginStreamId;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while ((await desk.Store.ReadOriginRangeAsync(phoneId, phoneStream, 1, 1)).Count == 0)
            {
                await Task.Delay(100, timeout.Token);
            }
        }
        finally
        {
            foreach (var root in roots)
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    [Fact]
    public async Task ATabletAddedFaceToFaceByAPhoneJoinsTheMeshAndReachesTheComputers()
    {
        var roots = new[] { TestDirectories.Create(), TestDirectories.Create(), TestDirectories.Create() };
        try
        {
            var deskPort = FreePort();
            await using var desk = await PeerNodeRuntime.StartAsync(new NodeOptions(roots[0], IPAddress.Loopback, deskPort, 0),
                new NodeLiveOptions(CollectionInterval: TimeSpan.FromHours(1), CollectSelfLan: false, PeerStateInterval: TimeSpan.FromSeconds(1)));

            using var phoneIdentity = new SoftwareIdentity();
            using var phonePresence = new SoftwarePresenceSigner();
            await using var phone = await MobilePeerRuntime.CreateAsync(MobileOptions(roots[1], "phone"), phoneIdentity,
                new ScriptedDeviceReader(), phonePresence);
            await PairPhoneAsync(phone, desk, deskPort);
            phone.Start();

            using var tabletIdentity = new SoftwareIdentity();
            using var tabletPresence = new SoftwarePresenceSigner();
            await using var tablet = await MobilePeerRuntime.CreateAsync(MobileOptions(roots[2], "tablet"), tabletIdentity,
                new ScriptedDeviceReader(), tabletPresence);

            // The tablet shows its join code; the phone scans it and sees the code the tablet shows for itself.
            var joinCode = tablet.CreateMeshJoinCode();
            var joinPreview = phone.PreviewMeshCode(joinCode);
            Assert.True(joinPreview.IsJoinRequest);
            Assert.Equal(tablet.VerificationCode, joinPreview.VerificationCode);

            // Without the owner's fingerprint nothing is added.
            phonePresence.Deny = true;
            await Assert.ThrowsAsync<MonitorApp.Peer.Control.UserPresenceDeniedException>(() => phone.AdmitFromJoinCodeAsync(joinCode));
            Assert.Null(await PhoneStore(roots[1]).ReadTrustedPeerAsync(tablet.Identity.PeerId));
            phonePresence.Deny = false;

            var welcomeCode = await phone.AdmitFromJoinCodeAsync(joinCode);
            var welcomePreview = tablet.PreviewMeshCode(welcomeCode);
            Assert.False(welcomePreview.IsJoinRequest);
            Assert.Equal(phone.VerificationCode, welcomePreview.VerificationCode);
            Assert.Equal(phone.Identity.PeerId, await tablet.JoinFromWelcomeAsync(welcomeCode));
            Assert.Equal(1, tabletPresence.Prompts);

            // The welcome is used up: the same reply cannot be applied again.
            await Assert.ThrowsAsync<FormatException>(() => tablet.JoinFromWelcomeAsync(welcomeCode));

            // The desk learns the tablet from the phone, the tablet learns the desk (and its address) from the welcome,
            // and the tablet syncs with the desk directly.
            tablet.Start();
            var tabletId = tablet.Identity.PeerId;
            await WaitAsync(async () =>
                (await desk.Store.ReadTrustedPeerAsync(tabletId))?.TrustState == PeerTrustState.Active &&
                (await PhoneStore(roots[2]).ReadTrustedPeerAsync(desk.Identity.PeerId))?.TrustState == PeerTrustState.Active);
            await tablet.CollectOnceAsync();
            var tabletStream = (await PhoneStore(roots[2]).ReadLocalIdentityAsync())!.OriginStreamId;
            await WaitAsync(async () => (await desk.Store.ReadOriginRangeAsync(tabletId, tabletStream, 1, 1))
                .SingleOrDefault()?.ReceivedFromPeerId == tabletId);
        }
        finally
        {
            foreach (var root in roots)
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    [Fact]
    public async Task APhoneAndATabletSeeEachOthersReadingsThroughTheComputerTheyBothReach()
    {
        var roots = new[] { TestDirectories.Create(), TestDirectories.Create(), TestDirectories.Create() };
        try
        {
            var deskPort = FreePort();
            await using var desk = await PeerNodeRuntime.StartAsync(new NodeOptions(roots[0], IPAddress.Loopback, deskPort, 0),
                new NodeLiveOptions(CollectionInterval: TimeSpan.FromHours(1), CollectSelfLan: false, PeerStateInterval: TimeSpan.FromSeconds(1)));
            var relayFast = (string root, string name) => MobileOptions(root, name) with { RelayPullInterval = TimeSpan.FromMilliseconds(500) };

            // Neither phone nor tablet listens, so they can never connect to each other: only through the desk.
            using var phoneIdentity = new SoftwareIdentity();
            await using var phone = await MobilePeerRuntime.CreateAsync(relayFast(roots[1], "phone"), phoneIdentity, new ScriptedDeviceReader());
            await PairPhoneAsync(phone, desk, deskPort);
            using var tabletIdentity = new SoftwareIdentity();
            await using var tablet = await MobilePeerRuntime.CreateAsync(relayFast(roots[2], "tablet"), tabletIdentity, new ScriptedDeviceReader());
            await PairPhoneAsync(tablet, desk, deskPort);
            var (phoneId, tabletId) = (phone.Identity.PeerId, tablet.Identity.PeerId);

            // A reading taken before the tablet connects arrives with its first sync.
            phone.Start();
            await phone.CollectOnceAsync();
            var phoneStream = (await PhoneStore(roots[1]).ReadLocalIdentityAsync())!.OriginStreamId;
            await WaitAsync(async () => (await desk.Store.ReadOriginRangeAsync(phoneId, phoneStream, 1, 1)).Count == 1);
            tablet.Start();
            await WaitAsync(async () =>
                (await PhoneStore(roots[2]).ReadTrustedPeerAsync(phoneId))?.TrustState == PeerTrustState.Active &&
                (await PhoneStore(roots[2]).ReadOriginRangeAsync(phoneId, phoneStream, 1, 1))
                    .SingleOrDefault()?.ReceivedFromPeerId == desk.Identity.PeerId);

            // Readings taken while both stay connected keep arriving, in both directions.
            await phone.CollectOnceAsync();
            await tablet.CollectOnceAsync();
            var tabletStream = (await PhoneStore(roots[2]).ReadLocalIdentityAsync())!.OriginStreamId;
            await WaitAsync(async () =>
                await PhoneStore(roots[2]).ReadHeldThroughAsync(phoneId, phoneStream) >= 2 &&
                (await PhoneStore(roots[1]).ReadOriginRangeAsync(tabletId, tabletStream, 1, 1)).Count == 1);
        }
        finally
        {
            foreach (var root in roots)
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    [Fact]
    public async Task AddingADeviceFailsClosedWithoutAFingerprintReader()
    {
        var roots = new[] { TestDirectories.Create(), TestDirectories.Create() };
        try
        {
            using var phoneIdentity = new SoftwareIdentity();
            await using var phone = await MobilePeerRuntime.CreateAsync(MobileOptions(roots[0], "phone"), phoneIdentity, new ScriptedDeviceReader());
            using var tabletIdentity = new SoftwareIdentity();
            await using var tablet = await MobilePeerRuntime.CreateAsync(MobileOptions(roots[1], "tablet"), tabletIdentity, new ScriptedDeviceReader());

            var joinCode = tablet.CreateMeshJoinCode();
            await Assert.ThrowsAsync<MonitorApp.Peer.Control.UserPresenceDeniedException>(() => phone.AdmitFromJoinCodeAsync(joinCode));
            Assert.Null(await PhoneStore(roots[0]).ReadTrustedPeerAsync(tablet.Identity.PeerId));

            // A device's own join code is refused, and a reply cannot be scanned before showing a join code.
            Assert.Throws<FormatException>(() => tablet.PreviewMeshCode(joinCode));
            Assert.Throws<FormatException>(() => phone.PreviewMeshCode(MonitorApp.Peer.Replication.MeshFaceToFace.WelcomePrefix + "AAAA"));
        }
        finally
        {
            foreach (var root in roots)
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    [Fact]
    public async Task APhoneHearsOnceThatADeviceJoinedThroughAnotherComputer()
    {
        var roots = new[] { TestDirectories.Create(), TestDirectories.Create(), TestDirectories.Create(), TestDirectories.Create() };
        try
        {
            var nodeOptions = new NodeLiveOptions(CollectionInterval: TimeSpan.FromHours(1), CollectSelfLan: false,
                PeerStateInterval: TimeSpan.FromSeconds(1));
            var deskPort = FreePort();
            var serverPort = FreePort();
            await using var desk = await PeerNodeRuntime.StartAsync(new NodeOptions(roots[0], IPAddress.Loopback, deskPort, 0), nodeOptions);
            await using var server = await PeerNodeRuntime.StartAsync(new NodeOptions(roots[1], IPAddress.Loopback, serverPort, 0), nodeOptions);
            await PairNodesAsync(desk, server, serverPort);

            using var phoneIdentity = new SoftwareIdentity();
            await using var phone = await MobilePeerRuntime.CreateAsync(MobileOptions(roots[2], "phone"), phoneIdentity, new ScriptedDeviceReader());
            await PairPhoneAsync(phone, desk, deskPort);
            phone.Start();
            await WaitAsync(() => Task.FromResult(phone.GetStatus().Peers.Any(peer =>
                peer.PeerId == server.Identity.PeerId && peer.State == MobileSyncState.Synced)));

            // A tablet paired on the desk: the server learns it from the desk and tells the phone, which never paired it.
            using var tabletIdentity = new SoftwareIdentity();
            await using var tablet = await MobilePeerRuntime.CreateAsync(MobileOptions(roots[3], "tablet"), tabletIdentity, new ScriptedDeviceReader());
            await PairPhoneAsync(tablet, desk, deskPort);
            var tabletId = tablet.Identity.PeerId;
            await WaitAsync(async () => (await phone.ReadAlertsAsync()).Any(item =>
                item.Alert.RuleId == MonitorApp.Peer.Alerts.MeshJoinAlerts.RuleId && item.Alert.SubjectId == tabletId));
            var joined = (await phone.ReadAlertsAsync()).Where(item => item.Alert.RuleId == MonitorApp.Peer.Alerts.MeshJoinAlerts.RuleId).ToArray();
            Assert.Equal(tabletId, Assert.Single(joined).Alert.SubjectId);
            Assert.Equal(server.Identity.PeerId, joined[0].Alert.OriginPeerId);

            // The device list says how each device came to be trusted.
            var provenance = await MeshProvenance.DescribeAsync(PhoneStore(roots[2]));
            Assert.Equal("Paired on this device", provenance[desk.Identity.PeerId]);
            Assert.StartsWith("Added by ", provenance[server.Identity.PeerId], StringComparison.Ordinal);
            Assert.StartsWith("Added by ", provenance[tabletId], StringComparison.Ordinal);
        }
        finally
        {
            foreach (var root in roots)
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    [Fact]
    public async Task RemovingADeviceOnAPhoneAsksForTheFingerprint()
    {
        var root = TestDirectories.Create();
        try
        {
            using var phoneIdentity = new SoftwareIdentity();
            using var presence = new SoftwarePresenceSigner();
            await using var phone = await MobilePeerRuntime.CreateAsync(MobileOptions(root, "phone"), phoneIdentity,
                new ScriptedDeviceReader(), presence);
            using var tabletKey = MonitorApp.Peer.Core.EcdsaPeerIdentityKey.Create();
            var tablet = MonitorApp.Peer.Core.PeerPublicIdentity.Create("tablet-peer", tabletKey);
            await PhoneStore(root).UpsertTrustedPeerAsync(new TrustedPeer(tablet.PeerId, tablet.PublicKeySpki, tablet.PublicKeyFingerprint,
                "Tablet", PeerTrustState.Active, DateTimeOffset.UtcNow, null));

            presence.Deny = true;
            await Assert.ThrowsAsync<MonitorApp.Peer.Control.UserPresenceDeniedException>(() => phone.RevokeAsync(tablet.PeerId));
            Assert.Equal(PeerTrustState.Active, (await PhoneStore(root).ReadTrustedPeerAsync(tablet.PeerId))?.TrustState);

            presence.Deny = false;
            await phone.RevokeAsync(tablet.PeerId);
            Assert.Equal(PeerTrustState.Revoked, (await PhoneStore(root).ReadTrustedPeerAsync(tablet.PeerId))?.TrustState);
            Assert.Equal(2, presence.Prompts);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static MobilePeerOptions MobileOptions(string root, string name) =>
        new(root, name, CollectionInterval: TimeSpan.FromHours(1), SyncInterval: TimeSpan.FromMilliseconds(200),
            MaximumSyncBackoff: TimeSpan.FromSeconds(1), HeartbeatInterval: TimeSpan.FromSeconds(5));

    private static PeerStore PhoneStore(string root) => new(PeerStoreOptions.Create(Path.Combine(root, "peer.db")));

    private static async Task PairPhoneAsync(MobilePeerRuntime phone, PeerNodeRuntime node, int port)
    {
        var invitation = new PeerPairingInvitation(IPAddress.Loopback, port, node.Identity.PeerId, node.Identity.PublicKeyFingerprint, null);
        var pairTask = phone.PairAsync(invitation, "mesh-phone-" + Guid.NewGuid().ToString("N"));
        await ApprovePendingAsync(node);
        await pairTask.WaitAsync(Deadline);
    }

    private static async Task PairNodesAsync(PeerNodeRuntime first, PeerNodeRuntime second, int secondPort)
    {
        using var client = Client();
        var csrf = await CsrfAsync(client, first.AdminAddress!);
        var body = JsonSerializer.Serialize(new
        {
            expectedPeerId = second.Identity.PeerId,
            address = "127.0.0.1",
            port = secondPort,
            expectedFingerprintSha256 = Convert.ToHexString(second.Identity.PublicKeyFingerprint),
            securityPolicy = "LoopbackPrototype",
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(first.AdminAddress!, "/api/invitations"))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        var invitationTask = client.SendAsync(request);
        await ApprovePendingAsync(second);
        using var response = await invitationTask.WaitAsync(Deadline);
        response.EnsureSuccessStatusCode();
    }

    private static async Task ApprovePendingAsync(PeerNodeRuntime node)
    {
        using var client = Client();
        using var timeout = new CancellationTokenSource(Deadline);
        string? pairingId = null;
        while (pairingId is null)
        {
            using var document = JsonDocument.Parse(await client.GetStringAsync(new Uri(node.AdminAddress!, "/api/pairings"), timeout.Token));
            if (document.RootElement.GetArrayLength() > 0)
            {
                pairingId = document.RootElement[0].GetProperty("pairingId").GetString();
            }
            else
            {
                await Task.Delay(50, timeout.Token);
            }
        }

        var csrf = await CsrfAsync(client, node.AdminAddress!);
        using var approval = new HttpRequestMessage(HttpMethod.Post,
            new Uri(node.AdminAddress!, $"/api/pairings/{Uri.EscapeDataString(pairingId)}/approve"))
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        approval.Headers.Add("X-CSRF-TOKEN", csrf);
        using var response = await client.SendAsync(approval, timeout.Token);
        response.EnsureSuccessStatusCode();
    }

    private static HttpClient Client() => new(new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true });

    private static async Task<string> CsrfAsync(HttpClient client, Uri admin)
    {
        using var document = JsonDocument.Parse(await client.GetStringAsync(new Uri(admin, "/api/csrf")));
        return document.RootElement.GetProperty("requestToken").GetString()!;
    }

    private static async Task WaitAsync(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(Deadline);
        while (!await condition())
        {
            await Task.Delay(100, timeout.Token);
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
