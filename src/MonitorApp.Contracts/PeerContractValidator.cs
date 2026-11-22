using System.Security.Cryptography;
using Google.Protobuf.WellKnownTypes;
using MonitorApp.Contracts.V1;

namespace MonitorApp.Contracts;

public static class PeerContractRules
{
    public const uint ProtocolMajor = 1;

    public const uint BilateralSessionProofMinor = 1;

    public const int Sha256Size = 32;

    public const int MaximumPort = 65_535;

    public const uint AlertSchemaVersion = 1;

    public const int MaximumAlertsPerBatch = 100;

    public const int MaximumAlertTextBytes = 1024;
}

public static class PeerContractValidator
{
    public static ContractValidationResult Validate(DiscoveryHint? hint)
    {
        var errors = new List<string>();
        if (hint is null)
        {
            return ContractValidationResult.Failure(["Discovery hint is required."]);
        }

        RequireText(hint.Host, "Host", errors);
        if (hint.Port is 0 or > PeerContractRules.MaximumPort)
        {
            errors.Add("Port must be between 1 and 65535.");
        }

        ValidateTimestamp(hint.CreatedAt, "created_at", errors, required: true);
        return Result(errors);
    }

    public static ContractValidationResult Validate(PairingHello? hello)
    {
        var errors = new List<string>();
        if (hello is null)
        {
            return ContractValidationResult.Failure(["Pairing hello is required."]);
        }

        ValidateVersion(hello.ProtocolVersion, errors);
        RequireText(hello.PairingId, "Pairing id", errors);
        RequireText(hello.PeerId, "Peer id", errors);
        ValidateP256PublicKey(hello.PublicKeySpki.Span, errors);
        RequireLength(
            hello.PublicKeySha256.Length,
            PeerContractRules.Sha256Size,
            "Public key fingerprint",
            errors);
        if (hello.PublicKeySpki.Length > 0 && hello.PublicKeySha256.Length == PeerContractRules.Sha256Size)
        {
            var actual = SHA256.HashData(hello.PublicKeySpki.Span);
            if (!CryptographicOperations.FixedTimeEquals(actual, hello.PublicKeySha256.Span))
            {
                errors.Add("Public key fingerprint does not match the supplied public key.");
            }
        }

        return Result(errors);
    }

    public static ContractValidationResult Validate(PeerChallenge? challenge)
    {
        var errors = new List<string>();
        if (challenge is null)
        {
            return ContractValidationResult.Failure(["Peer challenge is required."]);
        }

        RequireText(challenge.SessionId, "Session id", errors);
        RequireText(challenge.ChallengeId, "Challenge id", errors);
        RequireText(challenge.ChallengerPeerId, "Challenger peer id", errors);
        RequireText(challenge.ChallengedPeerId, "Challenged peer id", errors);
        if (challenge.ChallengerPeerId == challenge.ChallengedPeerId &&
            !string.IsNullOrWhiteSpace(challenge.ChallengerPeerId))
        {
            errors.Add("Challenge peer identities must be different.");
        }

        RequireLength(
            challenge.ProofChallenge.Length,
            EnrollmentContractRules.ProofChallengeSize,
            "Proof challenge",
            errors);
        ValidateSignatureAlgorithm(challenge.SignatureAlgorithm, errors);
        ValidateTimestamp(challenge.CreatedAt, "created_at", errors, required: true);
        ValidateTimestamp(challenge.ExpiresAt, "expires_at", errors, required: true);
        if (TryTimestamp(challenge.CreatedAt, out var createdAt) &&
            TryTimestamp(challenge.ExpiresAt, out var expiresAt) &&
            expiresAt <= createdAt)
        {
            errors.Add("Peer challenge expiration must be later than creation.");
        }

        return Result(errors);
    }

    public static ContractValidationResult Validate(PeerProof? proof)
    {
        var errors = new List<string>();
        if (proof is null)
        {
            return ContractValidationResult.Failure(["Peer proof is required."]);
        }

        RequireText(proof.SessionId, "Session id", errors);
        RequireText(proof.ChallengeId, "Challenge id", errors);
        RequireText(proof.SignerPeerId, "Signer peer id", errors);
        RequireText(proof.VerifierPeerId, "Verifier peer id", errors);
        if (proof.SignerPeerId == proof.VerifierPeerId && !string.IsNullOrWhiteSpace(proof.SignerPeerId))
        {
            errors.Add("Proof peer identities must be different.");
        }

        ValidateSignatureAlgorithm(proof.SignatureAlgorithm, errors);
        RequireLength(
            proof.ProofSignature.Length,
            EnrollmentContractRules.EcdsaP256SignatureSize,
            "Proof signature",
            errors);
        return Result(errors);
    }

    public static ContractValidationResult Validate(PairingApproval? approval)
    {
        var errors = new List<string>();
        if (approval is null)
        {
            return ContractValidationResult.Failure(["Pairing approval is required."]);
        }

        RequireText(approval.PairingId, "Pairing id", errors);
        if (approval.Disposition == PairingApprovalDisposition.Unspecified ||
            !System.Enum.IsDefined(approval.Disposition))
        {
            errors.Add("Pairing approval disposition is required and must be recognized.");
        }

        ValidateTimestamp(approval.ExpiresAt, "expires_at", errors, required: true);
        RequireText(approval.SafeMessage, "Safe message", errors);
        return Result(errors);
    }

    public static ContractValidationResult Validate(PeerHello? hello)
    {
        var errors = new List<string>();
        if (hello is null)
        {
            return ContractValidationResult.Failure(["Peer hello is required."]);
        }

        ValidateVersion(hello.ProtocolVersion, errors);
        RequireText(hello.PeerId, "Peer id", errors);
        RequireText(hello.SessionId, "Session id", errors);
        RequireLength(
            hello.PublicKeySha256.Length,
            PeerContractRules.Sha256Size,
            "Public key fingerprint",
            errors);
        foreach (var capability in hello.Capabilities)
        {
            ValidateCapability(capability, errors);
        }

        foreach (var cursor in hello.Cursors)
        {
            ValidateCursor(cursor, errors);
        }

        return Result(errors);
    }

    public static ContractValidationResult Validate(OriginRangeRequest? request)
    {
        var errors = new List<string>();
        if (request is null)
        {
            return ContractValidationResult.Failure(["Origin range request is required."]);
        }

        RequireText(request.RequestId, "Request id", errors);
        RequireText(request.OriginPeerId, "Origin peer id", errors);
        RequireText(request.OriginStreamId, "Origin stream id", errors);
        if (request.FirstSequence == 0)
        {
            errors.Add("First sequence must be greater than zero.");
        }

        if (request.LastSequence != 0 && request.LastSequence < request.FirstSequence)
        {
            errors.Add("Last sequence must be zero or greater than or equal to first sequence.");
        }

        if (request.MaximumRecordCount == 0 || request.MaximumBatchBytes == 0)
        {
            errors.Add("Range requests require nonzero record-count and byte limits.");
        }

        return Result(errors);
    }

    public static ContractValidationResult Validate(OriginRecordBatch? batch)
    {
        var errors = new List<string>();
        if (batch is null)
        {
            return ContractValidationResult.Failure(["Origin record batch is required."]);
        }

        RequireText(batch.RequestId, "Request id", errors);
        string? peerId = null;
        string? streamId = null;
        ulong previous = 0;
        foreach (var record in batch.Records)
        {
            ValidateRecord(record, errors);
            if (peerId is null)
            {
                peerId = record.OriginPeerId;
                streamId = record.OriginStreamId;
            }
            else if (record.OriginPeerId != peerId || record.OriginStreamId != streamId)
            {
                errors.Add("A batch may contain records from only one origin stream.");
            }

            if (previous != 0 && record.Sequence != previous + 1)
            {
                errors.Add("Origin record sequences must be strictly contiguous.");
            }

            previous = record.Sequence;
        }

        if (batch.HasMore && batch.Records.Count == 0)
        {
            errors.Add("A batch marked has_more must contain at least one record.");
        }

        return Result(errors);
    }

    public static ContractValidationResult Validate(OriginAck? ack)
    {
        var errors = new List<string>();
        if (ack is null)
        {
            return ContractValidationResult.Failure(["Origin acknowledgement is required."]);
        }

        ValidateCursor(ack.Cursor, errors);
        return Result(errors);
    }

    public static ContractValidationResult Validate(AlertPull? pull)
    {
        var errors = new List<string>();
        if (pull is null)
        {
            return ContractValidationResult.Failure(["Alert pull is required."]);
        }

        RequireText(pull.RequestId, "Request id", errors);
        if (pull.MaximumCount is 0 or > PeerContractRules.MaximumAlertsPerBatch)
        {
            errors.Add($"Maximum alert count must be 1 through {PeerContractRules.MaximumAlertsPerBatch}.");
        }

        return Result(errors);
    }

    public static ContractValidationResult Validate(AlertBatch? batch)
    {
        var errors = new List<string>();
        if (batch is null)
        {
            return ContractValidationResult.Failure(["Alert batch is required."]);
        }

        // request_id echoes the pull; it is empty for an unsolicited push on a live session.
        if (batch.Alerts.Count > PeerContractRules.MaximumAlertsPerBatch)
        {
            errors.Add("Alert batch exceeds the maximum alert count.");
        }

        if (batch.HasMore && batch.Alerts.Count == 0)
        {
            errors.Add("An alert batch marked has_more must contain at least one alert.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var alert in batch.Alerts)
        {
            ValidateAlert(alert, errors);
            if (!ids.Add(alert.AlertId))
            {
                errors.Add("An alert batch may contain each alert id once.");
            }
        }

        return Result(errors);
    }

    public static ContractValidationResult Validate(AlertAck? ack)
    {
        var errors = new List<string>();
        if (ack is null)
        {
            return ContractValidationResult.Failure(["Alert acknowledgement is required."]);
        }

        if (ack.Entries.Count == 0 || ack.Entries.Count > PeerContractRules.MaximumAlertsPerBatch)
        {
            errors.Add("Alert acknowledgement must list 1 through the maximum alert count entries.");
        }

        foreach (var entry in ack.Entries)
        {
            RequireText(entry.AlertId, "Alert id", errors);
            if (entry.Revision == 0)
            {
                errors.Add("Alert acknowledgement revision must be positive.");
            }
        }

        return Result(errors);
    }

    private static void ValidateAlert(AlertRecord alert, List<string> errors)
    {
        if (alert.SchemaVersion == 0 || alert.SchemaVersion > PeerContractRules.AlertSchemaVersion)
        {
            errors.Add("Alert schema version is unsupported.");
        }

        RequireText(alert.AlertId, "Alert id", errors);
        RequireText(alert.RuleId, "Alert rule id", errors);
        RequireText(alert.SubjectId, "Alert subject id", errors);
        RequireText(alert.OriginPeerId, "Alert origin peer id", errors);
        RequireText(alert.Title, "Alert title", errors);
        if (alert.Revision == 0)
        {
            errors.Add("Alert revision must be positive.");
        }

        if (alert.Severity is AlertSeverity.Unspecified || !System.Enum.IsDefined(alert.Severity))
        {
            errors.Add("Alert severity is invalid.");
        }

        if (alert.State is AlertState.Unspecified || !System.Enum.IsDefined(alert.State))
        {
            errors.Add("Alert state is invalid.");
        }

        if (alert.Title.Length > PeerContractRules.MaximumAlertTextBytes ||
            alert.Detail.Length > PeerContractRules.MaximumAlertTextBytes ||
            alert.AlertId.Length > 128 || alert.RuleId.Length > 128 || alert.SubjectId.Length > 512 ||
            alert.OriginPeerId.Length > 128)
        {
            errors.Add("Alert text exceeds the maximum length.");
        }

        ValidateTimestamp(alert.RaisedAt, "Alert raised-at", errors, required: true);
        ValidateTimestamp(alert.ResolvedAt, "Alert resolved-at", errors, required: alert.State == AlertState.Resolved);
    }

    public static ContractValidationResult Validate(CompatibilityFailure? failure)
    {
        var errors = new List<string>();
        if (failure is null)
        {
            return ContractValidationResult.Failure(["Compatibility failure is required."]);
        }

        ValidateCompatibility(failure.Supported, errors);
        ValidateVersion(failure.Received, errors, requireCurrentMajor: false);
        RequireText(failure.SafeMessage, "Safe message", errors);
        return Result(errors);
    }

    private static void ValidateRecord(OriginRecord record, List<string> errors)
    {
        RequireText(record.OriginPeerId, "Origin peer id", errors);
        RequireText(record.OriginStreamId, "Origin stream id", errors);
        RequireText(record.SubjectEntityId, "Subject entity id", errors);
        RequireText(record.CapabilityId, "Capability id", errors);
        RequireText(record.SignalId, "Signal id", errors);
        if (record.Sequence == 0)
        {
            errors.Add("Origin sequence must be greater than zero.");
        }

        if (record.RecordBytes.IsEmpty)
        {
            errors.Add("Origin record bytes are required.");
        }

        RequireLength(record.ContentSha256.Length, PeerContractRules.Sha256Size, "Content hash", errors);
        if (!record.RecordBytes.IsEmpty && record.ContentSha256.Length == PeerContractRules.Sha256Size)
        {
            var actual = SHA256.HashData(record.RecordBytes.Span);
            if (!CryptographicOperations.FixedTimeEquals(actual, record.ContentSha256.Span))
            {
                errors.Add("Content hash does not match origin record bytes.");
            }
        }

        ValidateSignatureAlgorithm(record.SignatureAlgorithm, errors);
        RequireLength(
            record.OriginSignature.Length,
            EnrollmentContractRules.EcdsaP256SignatureSize,
            "Origin signature",
            errors);

        ValidateTimestamp(record.ObservedAt, "observed_at", errors, required: true);
        if (record.Quality == ObservationQuality.Unspecified || !System.Enum.IsDefined(record.Quality))
        {
            errors.Add("Observation quality must be specified.");
        }
    }

    private static void ValidateCapability(CapabilityDescriptor capability, List<string> errors)
    {
        RequireText(capability.ModuleId, "Capability module id", errors);
        RequireText(capability.ModuleVersion, "Capability module version", errors);
        RequireLength(
            capability.DeclarationSha256.Length,
            PeerContractRules.Sha256Size,
            "Capability declaration hash",
            errors);
    }

    private static void ValidateCursor(OriginCursor? cursor, List<string> errors)
    {
        if (cursor is null)
        {
            errors.Add("Origin cursor is required.");
            return;
        }

        RequireText(cursor.OriginPeerId, "Cursor origin peer id", errors);
        RequireText(cursor.OriginStreamId, "Cursor origin stream id", errors);
        ValidateTimestamp(cursor.UpdatedAt, "cursor updated_at", errors, required: true);
    }

    private static void ValidateCompatibility(ProtocolCompatibility? compatibility, List<string> errors)
    {
        if (compatibility is null || compatibility.MinimumMajor == 0 || compatibility.MaximumMajor == 0)
        {
            errors.Add("Supported protocol range is required.");
            return;
        }

        if (compatibility.MaximumMajor < compatibility.MinimumMajor ||
            (compatibility.MaximumMajor == compatibility.MinimumMajor &&
             compatibility.MaximumMinor < compatibility.MinimumMinor))
        {
            errors.Add("Supported protocol range is invalid.");
        }
    }

    private static void ValidateVersion(
        ProtocolVersion? version,
        List<string> errors,
        bool requireCurrentMajor = true)
    {
        if (version is null || version.Major == 0)
        {
            errors.Add("Protocol version major must be greater than zero.");
        }
        else if (requireCurrentMajor && version.Major != PeerContractRules.ProtocolMajor)
        {
            errors.Add($"Protocol major version {version.Major} is unsupported.");
        }
    }

    private static void ValidateP256PublicKey(ReadOnlySpan<byte> publicKeySpki, List<string> errors)
    {
        if (publicKeySpki.IsEmpty)
        {
            errors.Add("Public key SPKI is required.");
            return;
        }

        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(publicKeySpki, out var bytesRead);
            if (bytesRead != publicKeySpki.Length || key.KeySize != 256)
            {
                errors.Add("Public key SPKI must contain one ECDSA P-256 public key.");
            }
        }
        catch (CryptographicException)
        {
            errors.Add("Public key SPKI must contain one ECDSA P-256 public key.");
        }
    }

    private static void ValidateSignatureAlgorithm(SignatureAlgorithm algorithm, List<string> errors)
    {
        if (algorithm != SignatureAlgorithm.EcdsaP256Sha256)
        {
            errors.Add("Signature algorithm must be ECDSA P-256 with SHA-256.");
        }
    }

    private static void ValidateTimestamp(
        Timestamp? timestamp,
        string fieldName,
        List<string> errors,
        bool required)
    {
        if (timestamp is null)
        {
            if (required)
            {
                errors.Add($"{fieldName} timestamp is required.");
            }

            return;
        }

        if (!TryTimestamp(timestamp, out _))
        {
            errors.Add($"{fieldName} timestamp is invalid.");
        }
    }

    private static bool TryTimestamp(Timestamp? timestamp, out DateTimeOffset value)
    {
        value = default;
        if (timestamp is null)
        {
            return false;
        }

        try
        {
            value = timestamp.ToDateTimeOffset();
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void RequireText(string? value, string fieldName, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{fieldName} is required.");
        }
    }

    private static void RequireLength(int actual, int expected, string fieldName, List<string> errors)
    {
        if (actual != expected)
        {
            errors.Add($"{fieldName} must contain exactly {expected} bytes.");
        }
    }

    private static ContractValidationResult Result(List<string> errors) =>
        errors.Count == 0
            ? ContractValidationResult.Success()
            : ContractValidationResult.Failure(errors);
}
