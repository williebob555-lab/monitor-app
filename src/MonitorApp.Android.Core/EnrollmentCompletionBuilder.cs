using Google.Protobuf;
using MonitorApp.Contracts;
using MonitorApp.Contracts.V1;

namespace MonitorApp.Android.Core;

public static class EnrollmentCompletionBuilder
{
    public static CompleteEnrollmentRequest Build(
        EnrollmentMaterial material,
        IDeviceKeySigner signer)
    {
        ArgumentNullException.ThrowIfNull(material);
        ArgumentNullException.ThrowIfNull(signer);

        var publicKey = signer.ExportPublicKeySpki();
        var payload = ProofPayload.CreateEnrollment(
            material.EnrollmentId,
            material.ProofChallenge.Span,
            publicKey);
        var signature = signer.SignP1363Sha256(payload);
        if (signature.Length != EnrollmentContractRules.EcdsaP256SignatureSize)
        {
            throw new InvalidOperationException("Device key returned an invalid P-256 signature length.");
        }

        return new CompleteEnrollmentRequest
        {
            ProtocolVersion = new ProtocolVersion { Major = 1, Minor = 0 },
            EnrollmentId = material.EnrollmentId,
            EnrollmentSecret = material.EnrollmentSecret,
            Platform = Platform.Android,
            DisplayName = material.DisplayName,
            PublicKeySpki = ByteString.CopyFrom(publicKey),
            SignatureAlgorithm = SignatureAlgorithm.EcdsaP256Sha256,
            ProofSignature = ByteString.CopyFrom(signature)
        };
    }
}
