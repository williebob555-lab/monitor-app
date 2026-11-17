using Google.Protobuf;

namespace MonitorApp.Android.Core;

public sealed record EnrollmentMaterial(
    string EnrollmentId,
    ByteString EnrollmentSecret,
    ByteString ProofChallenge,
    string DisplayName)
{
    public static EnrollmentMaterial Parse(
        string enrollmentId,
        string secretBase64,
        string challengeBase64,
        string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(enrollmentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        var secret = DecodeExact(secretBase64, 32, "Enrollment secret");
        var challenge = DecodeExact(challengeBase64, 32, "Proof challenge");
        return new EnrollmentMaterial(
            enrollmentId.Trim(),
            ByteString.CopyFrom(secret),
            ByteString.CopyFrom(challenge),
            displayName.Trim());
    }

    private static byte[] DecodeExact(string value, int expectedLength, string name)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(value);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException($"{name} must be valid base64.", exception);
        }

        if (bytes.Length != expectedLength)
        {
            throw new ArgumentException($"{name} must decode to exactly {expectedLength} bytes.");
        }

        return bytes;
    }
}
