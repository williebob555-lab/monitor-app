using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using Grpc.Core;
using Grpc.Net.Client;
using MonitorApp.Contracts;
using MonitorApp.Contracts.V1;
using MonitorApp.Peer.Core;

namespace MonitorApp.Peer.Transport;

/// <summary>
/// Phone side of the typed-code fallback: redeems a one-time code at an explicitly typed private address and returns
/// the desktop identity to pin, but only if the reply carries the proof only the holder of the code can produce.
/// The code itself never leaves the phone.
/// </summary>
public static class PeerPairingCodeClient
{
    public static async Task<PeerPairingInvitation> RedeemAsync(
        IPAddress address, int port, string typedCode, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        var code = PairingShortCode.Normalize(typedCode)
            ?? throw new FormatException("The pairing code must be 10 characters (letters and digits).");
        // Placeholder peer id only satisfies the shared endpoint policy; the real identity comes from the verified reply.
        var endpoint = new PeerPairingInvitation(address, port, "pending-lookup", new byte[32], null).ToEndpoint();
        var options = PeerTransportOptions.Create(endpoint);
        options.Validate();
        var keys = await Task.Run(() => PairingShortCode.Derive(code), cancellationToken).ConfigureAwait(false);
        using var channel = GrpcChannel.ForAddress(endpoint.Address, new GrpcChannelOptions
        {
            HttpHandler = new SocketsHttpHandler { ConnectTimeout = options.ConnectTimeout },
            MaxReceiveMessageSize = 64 * 1024,
        });
        RedeemPairingCodeResponse response;
        try
        {
            response = await new PeerPairingCodeService.PeerPairingCodeServiceClient(channel).RedeemAsync(
                new RedeemPairingCodeRequest
                {
                    ProtocolVersion = new ProtocolVersion
                    {
                        Major = PeerContractRules.ProtocolMajor,
                        Minor = PeerContractRules.BilateralSessionProofMinor,
                    },
                    LookupId = Google.Protobuf.ByteString.CopyFrom(keys.LookupId),
                },
                deadline: DateTime.UtcNow + options.OperationTimeout,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (RpcException exception) when (exception.StatusCode == StatusCode.NotFound)
        {
            throw new PeerAuthenticationException("The pairing code was not accepted. It may be wrong, expired, or already used.");
        }

        var fingerprint = response.PublicKeySha256.ToByteArray();
        if (fingerprint.Length != 32 || string.IsNullOrWhiteSpace(response.PeerId) ||
            !CryptographicOperations.FixedTimeEquals(
                response.Proof.Span,
                PairingShortCode.ComputeProof(keys.ProofKey, keys.LookupId, response.PeerId, fingerprint)))
        {
            throw new PeerAuthenticationException("The listener at that address could not prove it holds this pairing code.");
        }

        return new PeerPairingInvitation(address, port, response.PeerId, fingerprint, null);
    }
}
