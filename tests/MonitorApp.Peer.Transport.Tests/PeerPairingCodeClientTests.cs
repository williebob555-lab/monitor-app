using System.Net;
using System.Net.Sockets;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using MonitorApp.Contracts.V1;
using MonitorApp.Peer.Core;
using MonitorApp.Peer.Transport;
using Xunit;

namespace MonitorApp.Peer.Transport.Tests;

public sealed class PeerPairingCodeClientTests
{
    private static readonly byte[] Fingerprint = Enumerable.Repeat((byte)7, 32).ToArray();

    [Fact]
    public async Task ListenerThatDoesNotKnowTheCodeCannotImpersonateTheDesktop()
    {
        await using var host = await RogueHost.StartAsync(new RedeemPairingCodeResponse
        {
            PeerId = "impostor",
            PublicKeySha256 = ByteString.CopyFrom(Fingerprint),
            // Best an impostor can do without the code: a proof keyed from some other code.
            Proof = ByteString.CopyFrom(PairingShortCode.ComputeProof(
                PairingShortCode.Derive("0000000000").ProofKey, new byte[16], "impostor", Fingerprint)),
        });

        var exception = await Assert.ThrowsAsync<PeerAuthenticationException>(() =>
            PeerPairingCodeClient.RedeemAsync(IPAddress.Loopback, host.Port, "ABCDE-FGHJK"));

        Assert.Contains("could not prove", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReplyWithAProofForADifferentIdentityIsRefused()
    {
        var keys = PairingShortCode.Derive("ABCDEFGHJK");
        await using var host = await RogueHost.StartAsync(new RedeemPairingCodeResponse
        {
            PeerId = "swapped-peer",
            PublicKeySha256 = ByteString.CopyFrom(Fingerprint),
            // Valid proof for the real identity, replayed with a swapped peer id.
            Proof = ByteString.CopyFrom(PairingShortCode.ComputeProof(keys.ProofKey, keys.LookupId, "real-peer", Fingerprint)),
        });

        await Assert.ThrowsAsync<PeerAuthenticationException>(() =>
            PeerPairingCodeClient.RedeemAsync(IPAddress.Loopback, host.Port, "abcde fghjk"));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("0.0.0.0")]
    public async Task PublicAndWildcardAddressesAreRefusedBeforeAnyConnection(string address)
    {
        await Assert.ThrowsAnyAsync<Exception>(() =>
            PeerPairingCodeClient.RedeemAsync(IPAddress.Parse(address), 47000, "ABCDE-FGHJK"));
    }

    [Fact]
    public async Task MalformedCodeIsRejectedLocally()
    {
        await Assert.ThrowsAsync<FormatException>(() =>
            PeerPairingCodeClient.RedeemAsync(IPAddress.Loopback, 47000, "short"));
    }

    private sealed class RogueService(RedeemPairingCodeResponse response) : PeerPairingCodeService.PeerPairingCodeServiceBase
    {
        public override Task<RedeemPairingCodeResponse> Redeem(RedeemPairingCodeRequest request, ServerCallContext context) =>
            Task.FromResult(response);
    }

    private sealed class RogueHost(WebApplication app, int port) : IAsyncDisposable
    {
        public int Port { get; } = port;

        public static async Task<RogueHost> StartAsync(RedeemPairingCodeResponse response)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.ConfigureKestrel(kestrel =>
                kestrel.Listen(IPAddress.Loopback, port, options => options.Protocols = HttpProtocols.Http2));
            builder.Services.AddGrpc();
            builder.Services.AddSingleton(new RogueService(response));
            var app = builder.Build();
            app.MapGrpcService<RogueService>();
            await app.StartAsync();
            return new RogueHost(app, port);
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
