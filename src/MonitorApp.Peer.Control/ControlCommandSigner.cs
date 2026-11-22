using System.Security.Cryptography;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using MonitorApp.Contracts.V1;
using MonitorApp.Peer.Core;

namespace MonitorApp.Peer.Control;

/// <summary>A monitoring control a phone can ask a desktop to perform. Deliberately has no power, service or script actions.</summary>
public sealed class ControlRequest
{
    private ControlRequest(Action<ControlCommand> apply, bool enrollsPresenceKey = false)
    {
        Apply = apply;
        EnrollsPresenceKey = enrollsPresenceKey;
    }

    internal Action<ControlCommand> Apply { get; }

    internal bool EnrollsPresenceKey { get; }

    /// <summary>Pauses collection (every collector when <paramref name="capabilityId"/> is empty). No duration pauses until resumed.</summary>
    public static ControlRequest PauseCollection(TimeSpan? duration = null, string? capabilityId = null) => new(command =>
        command.PauseCollection = new PauseCollection
        {
            CapabilityId = capabilityId ?? string.Empty,
            DurationSeconds = duration is { } value ? Seconds(value) : 0,
        });

    public static ControlRequest ResumeCollection(string? capabilityId = null) => new(command =>
        command.ResumeCollection = new ResumeCollection { CapabilityId = capabilityId ?? string.Empty });

    /// <summary>Sets the normal sampling interval (10 s to 1 h). A null interval clears the override.</summary>
    public static ControlRequest SetSamplingInterval(TimeSpan? interval, TimeSpan? duration = null) => new(command =>
        command.SetSamplingInterval = new SetSamplingInterval
        {
            IntervalSeconds = interval is { } value ? Seconds(value) : 0,
            DurationSeconds = duration is { } span ? Seconds(span) : 0,
        });

    public static ControlRequest AcknowledgeAlert(string alertId) => new(command =>
        command.AcknowledgeAlert = new AcknowledgeAlert { AlertId = alertId });

    /// <summary>Mutes delivery of alerts (every rule when <paramref name="ruleId"/> is empty) for a bounded time.</summary>
    public static ControlRequest MuteAlerts(TimeSpan duration, string? ruleId = null) => new(command =>
        command.MuteAlerts = new MuteAlerts { RuleId = ruleId ?? string.Empty, DurationSeconds = Seconds(duration) });

    public static ControlRequest UnmuteAlerts(string? ruleId = null) => new(command =>
        command.UnmuteAlerts = new UnmuteAlerts { RuleId = ruleId ?? string.Empty });

    /// <summary>Sets this phone's own alert preferences: lowest severity wanted (Unspecified = all) and rule ids to opt out of.</summary>
    public static ControlRequest SetAlertPreferences(AlertSeverity minimumSeverity, IEnumerable<string>? mutedRuleKinds = null) => new(command =>
    {
        var prefs = new SetAlertPreferences { MinSeverity = minimumSeverity };
        prefs.MutedRuleKinds.AddRange(mutedRuleKinds ?? []);
        command.SetAlertPreferences = prefs;
    });

    /// <summary>Approves or rejects a pending pairing the desktop reported. <paramref name="remoteFingerprintSha256"/> is what the user saw.</summary>
    public static ControlRequest ResolvePairing(string pairingId, byte[] remoteFingerprintSha256, bool approve) => new(command =>
        command.ResolvePairing = new ResolvePairing
        {
            PairingId = pairingId,
            RemotePublicKeySha256 = ByteString.CopyFrom(remoteFingerprintSha256),
            Approve = approve,
        });

    /// <summary>
    /// Approves installing the update the desktop has staged. Bound to the exact version and artifact SHA-256 the person
    /// was shown (from the desktop's update status); the desktop refuses if its staged update has since changed.
    /// </summary>
    public static ControlRequest ApproveUpdate(string version, byte[] artifactSha256) => new(command =>
        command.ApproveUpdate = new ApproveUpdate { Version = version, ArtifactSha256 = ByteString.CopyFrom(artifactSha256) });

    /// <summary>Registers this device's biometric presence key with the desktop (needs local approval there).</summary>
    public static ControlRequest EnrollPresenceKey() => new(_ => { }, enrollsPresenceKey: true);

    private static uint Seconds(TimeSpan value) =>
        value <= TimeSpan.Zero || value.TotalSeconds > uint.MaxValue
            ? throw new ArgumentOutOfRangeException(nameof(value))
            : (uint)Math.Ceiling(value.TotalSeconds);
}

/// <summary>The command needs biometric verification but this device has no usable presence signer.</summary>
public sealed class UserPresenceUnavailableException(string message) : InvalidOperationException(message);

/// <summary>
/// Builds and signs control commands on the issuing device: the identity-key signature always, and the user-presence
/// signature (one biometric prompt) when the command's risk tier requires it.
/// </summary>
public sealed class ControlCommandSigner(
    PeerPublicIdentity self,
    IPeerIdentityKey identityKey,
    IUserPresenceSigner? presence,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    /// <summary>The risk tier of a request, so a UI can explain why a biometric prompt will appear.</summary>
    public static ControlRiskTier TierOf(ControlRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var probe = new ControlCommand();
        request.Apply(probe);
        return request.EnrollsPresenceKey ? ControlRiskTier.High : ControlPolicy.Classify(probe);
    }

    public async Task<ControlCommand> CreateAsync(
        string targetPeerId,
        string sessionId,
        ControlRequest request,
        string? commandId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPeerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(request);
        var command = new ControlCommand
        {
            CommandId = commandId ?? Guid.NewGuid().ToString("N"),
            IssuerPeerId = self.PeerId,
            TargetPeerId = targetPeerId,
            SessionId = sessionId,
            Nonce = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(16)),
        };
        request.Apply(command);
        var needsPresence = request.EnrollsPresenceKey || ControlPolicy.RequiresPresence(command);
        if (needsPresence && presence is not { IsAvailable: true })
        {
            throw new UserPresenceUnavailableException("This command needs biometric verification, which is unavailable on this device.");
        }

        if (request.EnrollsPresenceKey)
        {
            var spki = await presence!.GetPublicKeySpkiAsync(cancellationToken).ConfigureAwait(false);
            var proof = await presence.SignAsync(
                ControlCanonical.EnrollmentStatement(self.PeerId, targetPeerId, spki),
                new UserPresenceChallenge("Register this phone", "Confirm to register this phone's fingerprint with the desktop."),
                cancellationToken).ConfigureAwait(false);
            command.EnrollPresenceKey = new EnrollPresenceKey
            {
                PresencePublicKeySpki = ByteString.CopyFrom(spki),
                ProofOfPossession = ByteString.CopyFrom(proof),
            };
        }

        var issued = clock.GetUtcNow();
        command.IssuedAt = Timestamp.FromDateTimeOffset(issued);
        // The window must outlast a person completing a biometric prompt.
        command.ExpiresAt = Timestamp.FromDateTimeOffset(
            issued + (needsPresence ? ControlPolicy.MaximumCommandLifetime : ControlPolicy.DefaultCommandLifetime));
        if (ControlPolicy.RequiresPresence(command))
        {
            var presenceSignature = await presence!.SignAsync(
                ControlCanonical.PresenceSigningInput(command),
                new UserPresenceChallenge("Confirm on desktop", Describe(command)),
                cancellationToken).ConfigureAwait(false);
            command.PresenceSignature = ByteString.CopyFrom(presenceSignature);
        }

        command.IssuerSignature = ByteString.CopyFrom(identityKey.Sign(ControlCanonical.IdentitySigningInput(command)));
        return command;
    }

    private static string Describe(ControlCommand command) => ControlPolicy.ActionName(command) switch
    {
        "approve_pairing" => "Approve a new device to join this monitoring network.",
        "approve_update" => "Install the update staged on the desktop.",
        "pause_collection" => "Pause monitoring on the desktop for a long time.",
        "mute_alerts" => "Mute alerts on the desktop for a long time.",
        var other => "Confirm " + other.Replace('_', ' ') + ".",
    };
}
