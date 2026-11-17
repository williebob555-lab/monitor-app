namespace MonitorApp.Persistence;

public sealed record EnrollmentStoreOptions(
    string DatabasePath,
    byte[] SecretPepper,
    TimeSpan EnrollmentLifetime,
    TimeSpan DeviceChallengeLifetime)
{
    public static EnrollmentStoreOptions Create(string databasePath, byte[] secretPepper) =>
        new(databasePath, secretPepper, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(2));
}
