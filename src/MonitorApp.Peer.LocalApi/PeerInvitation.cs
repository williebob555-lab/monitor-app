using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace MonitorApp.Peer.LocalApi;

public enum PeerInvitationSecurityPolicy
{
    LoopbackPrototype = 1,
    AuthenticatedPrivateTransport = 2,
}

public enum PeerInvitationStatus
{
    Dispatched = 1,
    Unavailable = 2,
    Rejected = 3,
}

public sealed record PeerInvitationInput(
    string ExpectedPeerId,
    string Address,
    int Port,
    string ExpectedFingerprintSha256,
    string SecurityPolicy);

public sealed record PeerInvitationCommand(
    string ExpectedPeerId,
    IPAddress Address,
    int Port,
    string ExpectedFingerprintSha256,
    PeerInvitationSecurityPolicy SecurityPolicy);

public sealed record PeerInvitationResult(PeerInvitationStatus Status, string Message);

public interface IPeerInvitationService
{
    ValueTask<PeerInvitationResult> InviteAsync(
        PeerInvitationCommand invitation,
        CancellationToken cancellationToken);
}

public sealed class UnavailablePeerInvitationService : IPeerInvitationService
{
    public ValueTask<PeerInvitationResult> InviteAsync(
        PeerInvitationCommand invitation,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(new PeerInvitationResult(
            PeerInvitationStatus.Unavailable,
            "Peer invitation transport is not configured on this prototype."));
}

internal sealed class PeerInvitationStatusState
{
    private PeerInvitationResult? result;

    public PeerInvitationResult? LastResult => Volatile.Read(ref result);

    public void Set(PeerInvitationResult value) => Volatile.Write(ref result, value);
}

internal static partial class PeerInvitationValidator
{
    private const int MaximumPeerIdLength = 256;

    public static bool TryValidate(
        PeerInvitationInput? input,
        out PeerInvitationCommand? command,
        out string error)
    {
        command = null;
        if (input is null)
        {
            error = "Invitation details are required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(input.ExpectedPeerId) ||
            input.ExpectedPeerId.Length > MaximumPeerIdLength)
        {
            error = "Expected peer id is required and must be at most 256 characters.";
            return false;
        }

        if (!IPAddress.TryParse(input.Address, out var address) ||
            address.Equals(IPAddress.Any) ||
            address.Equals(IPAddress.IPv6Any) ||
            address.Equals(IPAddress.None) ||
            address.Equals(IPAddress.IPv6None) ||
            (!IPAddress.IsLoopback(address) && !IsPrivate(address)))
        {
            error = "Address must be an explicit loopback or private IP address; wildcard and public addresses are prohibited.";
            return false;
        }

        if (input.Port is < 1 or > 65535)
        {
            error = "Port must be between 1 and 65535.";
            return false;
        }

        var fingerprint = input.ExpectedFingerprintSha256.Replace(":", string.Empty, StringComparison.Ordinal);
        if (!FingerprintPattern().IsMatch(fingerprint))
        {
            error = "Expected fingerprint must be a 32-byte SHA-256 value in hexadecimal.";
            return false;
        }

        if (!Enum.TryParse<PeerInvitationSecurityPolicy>(input.SecurityPolicy, ignoreCase: true, out var policy) ||
            !Enum.IsDefined(policy))
        {
            error = "A supported invitation security policy must be declared.";
            return false;
        }

        if (IPAddress.IsLoopback(address) && policy != PeerInvitationSecurityPolicy.LoopbackPrototype)
        {
            error = "Loopback invitations must declare the loopback prototype policy.";
            return false;
        }

        if (!IPAddress.IsLoopback(address) && policy != PeerInvitationSecurityPolicy.AuthenticatedPrivateTransport)
        {
            error = "Private-network invitations must declare authenticated private transport.";
            return false;
        }

        command = new PeerInvitationCommand(
            input.ExpectedPeerId.Trim(),
            address,
            input.Port,
            fingerprint.ToUpperInvariant(),
            policy);
        error = string.Empty;
        return true;
    }

    private static bool IsPrivate(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 10 ||
                (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                (bytes[0] == 192 && bytes[1] == 168) ||
                (bytes[0] == 100 && bytes[1] is >= 64 and <= 127) ||
                (bytes[0] == 169 && bytes[1] == 254);
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            return (bytes[0] & 0xFE) == 0xFC || address.IsIPv6LinkLocal;
        }

        return false;
    }

    [GeneratedRegex("^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex FingerprintPattern();
}
