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

            // And the phone syncs with the server directly: the server receives the phone's readings from the phone itself.
            await phone.CollectOnceAsync();
            var phoneStream = (await PhoneStore(roots[2]).ReadLocalIdentityAsync())!.OriginStreamId;
            await WaitAsync(async () => (await server.Store.ReadOriginRangeAsync(phoneId, phoneStream, 1, 1))
                .SingleOrDefault()?.ReceivedFromPeerId == phoneId);

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
