using System.Security.Cryptography;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using MonitorApp.Contracts.V1;

namespace MonitorApp.Contracts;

public static partial class ContractValidator
{
    public static ContractValidationResult Validate(CreateEnrollmentRequest? request)
    {
        var errors = new List<string>();
        if (request is null)
        {
            errors.Add("Create-enrollment request is required.");
            return CreateResult(errors);
        }

        ValidateProtocolVersion(request.ProtocolVersion, errors);
        ValidatePlatform(request.Platform, errors);
        RequireText(request.DisplayName, "Display name", errors);
        return CreateResult(errors);
    }

    public static ContractValidationResult Validate(CreateEnrollmentResponse? response)
    {
        var errors = new List<string>();
        if (response is null)
        {
            errors.Add("Create-enrollment response is required.");
            return CreateResult(errors);
        }

        RequireText(response.EnrollmentId, "Enrollment id", errors);
        RequireBytes(
            response.EnrollmentSecret,
            EnrollmentContractRules.EnrollmentSecretSize,
            "Enrollment secret",
            errors);
        RequireBytes(
            response.ProofChallenge,
            EnrollmentContractRules.ProofChallengeSize,
            "Proof challenge",
            errors);
        ValidateSignatureAlgorithm(response.SignatureAlgorithm, errors);
        ValidateTimestampRange(
            response.CreatedAt,
            response.ExpiresAt,
            "Enrollment",
            errors);
        return CreateResult(errors);
    }

    public static ContractValidationResult Validate(CompleteEnrollmentRequest? request)
    {
        var errors = new List<string>();
        if (request is null)
        {
            errors.Add("Complete-enrollment request is required.");
            return CreateResult(errors);
        }

        ValidateProtocolVersion(request.ProtocolVersion, errors);
        RequireText(request.EnrollmentId, "Enrollment id", errors);
        RequireBytes(
            request.EnrollmentSecret,
            EnrollmentContractRules.EnrollmentSecretSize,
            "Enrollment secret",
            errors);
        ValidatePlatform(request.Platform, errors);
        RequireText(request.DisplayName, "Display name", errors);
        ValidateSignatureAlgorithm(request.SignatureAlgorithm, errors);
        ValidateP256PublicKey(request.PublicKeySpki, errors);
        RequireBytes(
            request.ProofSignature,
            EnrollmentContractRules.EcdsaP256SignatureSize,
            "Proof signature",
            errors);
        return CreateResult(errors);
    }

    public static ContractValidationResult Validate(CompleteEnrollmentResponse? response)
    {
        var errors = new List<string>();
        if (response is null)
        {
            errors.Add("Complete-enrollment response is required.");
            return CreateResult(errors);
        }

        RequireText(response.AgentId, "Agent id", errors);
        RequireText(response.NodeId, "Node id", errors);
        RequireBytes(
            response.PublicKeySha256,
            EnrollmentContractRules.PublicKeySha256Size,
            "Public key fingerprint",
            errors);
        ValidateTimestamp(response.CompletedAt, "completed_at", errors, required: true);
        return CreateResult(errors);
    }

    public static ContractValidationResult Validate(EnrollmentStatusRequest? request)
    {
        var errors = new List<string>();
        if (request is null)
        {
            errors.Add("Enrollment-status request is required.");
            return CreateResult(errors);
        }

        RequireText(request.EnrollmentId, "Enrollment id", errors);
        return CreateResult(errors);
    }

    public static ContractValidationResult Validate(EnrollmentStatusResponse? response)
    {
        var errors = new List<string>();
        if (response is null)
        {
            errors.Add("Enrollment-status response is required.");
            return CreateResult(errors);
        }

        RequireText(response.EnrollmentId, "Enrollment id", errors);
        ValidateEnrollmentState(response.State, errors);
        ValidateTimestampRange(response.CreatedAt, response.ExpiresAt, "Enrollment", errors);

        if (response.State == EnrollmentState.Completed)
        {
            RequireText(response.AgentId, "Agent id", errors);
            RequireText(response.NodeId, "Node id", errors);
        }

        return CreateResult(errors);
    }

    public static ContractValidationResult Validate(CancelEnrollmentRequest? request)
    {
        var errors = new List<string>();
        if (request is null)
        {
            errors.Add("Cancel-enrollment request is required.");
            return CreateResult(errors);
        }

        RequireText(request.EnrollmentId, "Enrollment id", errors);
        return CreateResult(errors);
    }

    public static ContractValidationResult Validate(CancelEnrollmentResponse? response)
    {
        var errors = new List<string>();
        if (response is null)
        {
            errors.Add("Cancel-enrollment response is required.");
            return CreateResult(errors);
        }

        RequireText(response.EnrollmentId, "Enrollment id", errors);
        if (response.State != EnrollmentState.Cancelled)
        {
            errors.Add("Cancelled enrollment state is required.");
        }

        ValidateTimestamp(response.CancelledAt, "cancelled_at", errors, required: true);
        return CreateResult(errors);
    }

    public static ContractValidationResult Validate(CreateDeviceChallengeRequest? request)
    {
        var errors = new List<string>();
        if (request is null)
        {
            errors.Add("Create-device-challenge request is required.");
            return CreateResult(errors);
        }

        ValidateProtocolVersion(request.ProtocolVersion, errors);
        RequireText(request.AgentId, "Agent id", errors);
        return CreateResult(errors);
    }

    public static ContractValidationResult Validate(CreateDeviceChallengeResponse? response)
    {
        var errors = new List<string>();
        if (response is null)
        {
            errors.Add("Create-device-challenge response is required.");
            return CreateResult(errors);
        }

        RequireText(response.AgentId, "Agent id", errors);
        RequireText(response.ChallengeId, "Challenge id", errors);
        RequireBytes(
            response.ProofChallenge,
            EnrollmentContractRules.ProofChallengeSize,
            "Proof challenge",
            errors);
        ValidateSignatureAlgorithm(response.SignatureAlgorithm, errors);
        ValidateTimestampRange(response.CreatedAt, response.ExpiresAt, "Device challenge", errors);
        return CreateResult(errors);
    }

    public static ContractValidationResult Validate(VerifyDeviceProofRequest? request)
    {
        var errors = new List<string>();
        if (request is null)
        {
            errors.Add("Verify-device-proof request is required.");
            return CreateResult(errors);
        }

        ValidateProtocolVersion(request.ProtocolVersion, errors);
        RequireText(request.AgentId, "Agent id", errors);
        RequireText(request.ChallengeId, "Challenge id", errors);
        ValidateSignatureAlgorithm(request.SignatureAlgorithm, errors);
        RequireBytes(
            request.ProofSignature,
            EnrollmentContractRules.EcdsaP256SignatureSize,
            "Proof signature",
            errors);
        return CreateResult(errors);
    }

    public static ContractValidationResult Validate(VerifyDeviceProofResponse? response)
    {
        var errors = new List<string>();
        if (response is null)
        {
            errors.Add("Verify-device-proof response is required.");
            return CreateResult(errors);
        }

        RequireText(response.AgentId, "Agent id", errors);
        RequireText(response.NodeId, "Node id", errors);
        ValidateTimestamp(response.VerifiedAt, "verified_at", errors, required: true);
        return CreateResult(errors);
    }

    private static void ValidateP256PublicKey(ByteString publicKeySpki, List<string> errors)
    {
        if (publicKeySpki.IsEmpty)
        {
            errors.Add("Public key SPKI is required.");
            return;
        }

        try
        {
            using var key = ECDsa.Create();
            var keyBytes = publicKeySpki.ToByteArray();
            key.ImportSubjectPublicKeyInfo(keyBytes, out var bytesRead);
            var parameters = key.ExportParameters(false);
            if (bytesRead != keyBytes.Length ||
                key.KeySize != 256 ||
                !string.Equals(parameters.Curve.Oid.Value, "1.2.840.10045.3.1.7", StringComparison.Ordinal))
            {
                errors.Add("Public key SPKI must contain one ECDSA P-256 public key.");
            }
        }
        catch (CryptographicException)
        {
            errors.Add("Public key SPKI must contain one ECDSA P-256 public key.");
        }
    }

    private static void ValidateSignatureAlgorithm(
        SignatureAlgorithm algorithm,
        List<string> errors)
    {
        if (algorithm != SignatureAlgorithm.EcdsaP256Sha256)
        {
            errors.Add("Signature algorithm must be ECDSA P-256 with SHA-256.");
        }
    }

    private static void ValidateEnrollmentState(EnrollmentState state, List<string> errors)
    {
        if (state == EnrollmentState.Unspecified || !System.Enum.IsDefined(state))
        {
            errors.Add("Enrollment state must be a recognized, specified value.");
        }
    }

    private static void RequireBytes(
        ByteString value,
        int expectedLength,
        string fieldName,
        List<string> errors)
    {
        if (value.Length != expectedLength)
        {
            errors.Add($"{fieldName} must contain exactly {expectedLength} bytes.");
        }
    }

    private static void ValidateTimestampRange(
        Timestamp? createdAt,
        Timestamp? expiresAt,
        string subject,
        List<string> errors)
    {
        ValidateTimestamp(createdAt, "created_at", errors, required: true);
        ValidateTimestamp(expiresAt, "expires_at", errors, required: true);
        if (!TryGetTimestamp(createdAt, out var created) ||
            !TryGetTimestamp(expiresAt, out var expires))
        {
            return;
        }

        if (expires <= created)
        {
            errors.Add($"{subject} expiration must be later than creation.");
        }
    }

    private static bool TryGetTimestamp(Timestamp? timestamp, out DateTimeOffset value)
    {
        if (timestamp is null)
        {
            value = default;
            return false;
        }

        try
        {
            value = timestamp.ToDateTimeOffset();
            return true;
        }
        catch (InvalidOperationException)
        {
            value = default;
            return false;
        }
    }
}
