namespace MonitorApp.Update;

public enum UpdateVerificationError
{
    InvalidEnvelope,
    UntrustedKey,
    UnsupportedSignatureAlgorithm,
    InvalidSignature,
    InvalidManifest,
    NoMatchingArtifact,
    ChannelMismatch,
    DowngradeOrReinstall,
    ManualUpgradeRequired,
    ArtifactSizeMismatch,
    ArtifactHashMismatch,
}

public sealed class UpdateVerificationException : Exception
{
    public UpdateVerificationException(UpdateVerificationError error, string message)
        : base(message)
    {
        Error = error;
    }

    public UpdateVerificationException(
        UpdateVerificationError error,
        string message,
        Exception innerException)
        : base(message, innerException)
    {
        Error = error;
    }

    public UpdateVerificationError Error { get; }
}
