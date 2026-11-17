namespace MonitorApp.Update;

public sealed record UpdateArtifact(
    string Component,
    string Platform,
    string Architecture,
    Uri Url,
    long Size,
    string Sha256);

public sealed record UpdateManifest(
    int SchemaVersion,
    string Channel,
    ReleaseVersion Version,
    ReleaseVersion MinimumCompatibleVersion,
    DateTimeOffset PublishedAtUtc,
    IReadOnlyList<UpdateArtifact> Artifacts);

public sealed record TrustedUpdateKey(
    string KeyId,
    byte[] PublicKeySpki);
