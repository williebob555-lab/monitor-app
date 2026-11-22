using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using MonitorApp.Contracts.V1;

namespace MonitorApp.Peer.Control;

/// <summary>
/// The canonical byte encoding that control signatures cover. It never depends on protobuf serialisation details:
/// every field is written explicitly, length-prefixed, in a fixed order, under a versioned domain label. The identity
/// signature binds issuer, target, session, validity window, nonce and the typed payload; the user-presence signature
/// covers the same bytes under a different label, so neither signature can be replayed as the other.
/// </summary>
public static class ControlCanonical
{
    private const string CommandDomain = "monitor.control.command.v1";
    private const string PresenceDomain = "monitor.control.presence.v1";
    private const string EnrollmentDomain = "monitor.control.presence-enrollment.v1";
    private const string P256Oid = "1.2.840.10045.3.1.7";

    public static byte[] IdentitySigningInput(ControlCommand command) => Encode(CommandDomain, command);

    public static byte[] PresenceSigningInput(ControlCommand command) => Encode(PresenceDomain, command);

    /// <summary>What the presence key signs to prove possession when it is enrolled.</summary>
    public static byte[] EnrollmentStatement(string issuerPeerId, string targetPeerId, ReadOnlySpan<byte> presencePublicKeySpki)
    {
        using var buffer = new MemoryStream();
        WriteString(buffer, EnrollmentDomain);
        WriteString(buffer, issuerPeerId);
        WriteString(buffer, targetPeerId);
        WriteBytes(buffer, presencePublicKeySpki);
        return buffer.ToArray();
    }

    /// <summary>Hash of the action and payload only, so an idempotent retry in a new session still matches.</summary>
    public static byte[] PayloadHash(ControlCommand command)
    {
        using var buffer = new MemoryStream();
        WriteString(buffer, ControlPolicy.ActionName(command));
        WritePayload(buffer, command);
        return SHA256.HashData(buffer.ToArray());
    }

    public static bool Verify(ReadOnlySpan<byte> publicKeySpki, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        if (publicKeySpki.IsEmpty || signature.Length != 64)
        {
            return false;
        }

        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(publicKeySpki, out var read);
            if (read != publicKeySpki.Length ||
                !string.Equals(key.ExportParameters(false).Curve.Oid.Value, P256Oid, StringComparison.Ordinal))
            {
                return false;
            }

            return key.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static byte[] Encode(string domain, ControlCommand command)
    {
        using var buffer = new MemoryStream();
        WriteString(buffer, domain);
        WriteString(buffer, command.CommandId);
        WriteString(buffer, command.IssuerPeerId);
        WriteString(buffer, command.TargetPeerId);
        WriteString(buffer, command.SessionId);
        WriteLong(buffer, command.IssuedAt?.ToDateTimeOffset().ToUnixTimeMilliseconds() ?? 0);
        WriteLong(buffer, command.ExpiresAt?.ToDateTimeOffset().ToUnixTimeMilliseconds() ?? 0);
        WriteBytes(buffer, command.Nonce.Span);
        WriteString(buffer, ControlPolicy.ActionName(command));
        WritePayload(buffer, command);
        return buffer.ToArray();
    }

    private static void WritePayload(MemoryStream buffer, ControlCommand command)
    {
        switch (command.PayloadCase)
        {
            case ControlCommand.PayloadOneofCase.PauseCollection:
                WriteString(buffer, command.PauseCollection.CapabilityId);
                WriteLong(buffer, command.PauseCollection.DurationSeconds);
                break;
            case ControlCommand.PayloadOneofCase.ResumeCollection:
                WriteString(buffer, command.ResumeCollection.CapabilityId);
                break;
            case ControlCommand.PayloadOneofCase.SetSamplingInterval:
                WriteLong(buffer, command.SetSamplingInterval.IntervalSeconds);
                WriteLong(buffer, command.SetSamplingInterval.DurationSeconds);
                break;
            case ControlCommand.PayloadOneofCase.AcknowledgeAlert:
                WriteString(buffer, command.AcknowledgeAlert.AlertId);
                break;
            case ControlCommand.PayloadOneofCase.MuteAlerts:
                WriteString(buffer, command.MuteAlerts.RuleId);
                WriteLong(buffer, command.MuteAlerts.DurationSeconds);
                break;
            case ControlCommand.PayloadOneofCase.UnmuteAlerts:
                WriteString(buffer, command.UnmuteAlerts.RuleId);
                break;
            case ControlCommand.PayloadOneofCase.SetAlertPreferences:
                WriteLong(buffer, (long)command.SetAlertPreferences.MinSeverity);
                WriteLong(buffer, command.SetAlertPreferences.MutedRuleKinds.Count);
                foreach (var kind in command.SetAlertPreferences.MutedRuleKinds)
                {
                    WriteString(buffer, kind);
                }

                break;
            case ControlCommand.PayloadOneofCase.ResolvePairing:
                WriteString(buffer, command.ResolvePairing.PairingId);
                WriteBytes(buffer, command.ResolvePairing.RemotePublicKeySha256.Span);
                WriteLong(buffer, command.ResolvePairing.Approve ? 1 : 0);
                break;
            case ControlCommand.PayloadOneofCase.ApproveUpdate:
                WriteString(buffer, command.ApproveUpdate.Version);
                WriteBytes(buffer, command.ApproveUpdate.ArtifactSha256.Span);
                break;
            case ControlCommand.PayloadOneofCase.EnrollPresenceKey:
                WriteBytes(buffer, command.EnrollPresenceKey.PresencePublicKeySpki.Span);
                WriteBytes(buffer, command.EnrollPresenceKey.ProofOfPossession.Span);
                break;
            default:
                break;
        }
    }

    private static void WriteString(MemoryStream buffer, string value) => WriteBytes(buffer, Encoding.UTF8.GetBytes(value));

    private static void WriteBytes(MemoryStream buffer, ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)value.Length);
        buffer.Write(length);
        buffer.Write(value);
    }

    private static void WriteLong(MemoryStream buffer, long value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        buffer.Write(bytes);
    }
}
