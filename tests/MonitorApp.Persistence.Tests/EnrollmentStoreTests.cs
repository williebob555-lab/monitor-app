using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using MonitorApp.Contracts;
using MonitorApp.Contracts.V1;
using MonitorApp.Persistence;
using Xunit;

namespace MonitorApp.Persistence.Tests;

public sealed class EnrollmentStoreTests
{
    [Fact]
    [Trait("Category", "SQLite")]
    public async Task CompleteRestartAuthenticateAndRejectReplaysUseRealFileBackedSqlite()
    {
        using var database = new TestDatabase();
        var store = database.CreateStore();
        await store.InitializeAsync();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var created = await store.CreateAsync(Request());
        var completed = await store.CompleteAsync(Complete(created, key));

        var restarted = database.CreateStore();
        await restarted.InitializeAsync();
        var status = await restarted.GetStatusAsync(new EnrollmentStatusRequest { EnrollmentId = created.EnrollmentId });
        Assert.Equal(EnrollmentState.Completed, status.State);
        Assert.Equal(completed.AgentId, status.AgentId);

        var challenge = await restarted.CreateDeviceChallengeAsync(new CreateDeviceChallengeRequest
        {
            ProtocolVersion = Version(),
            AgentId = completed.AgentId
        });
        var payload = ProofPayload.CreateDevice(completed.AgentId, challenge.ChallengeId, challenge.ProofChallenge.Span);
        var signature = key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var proof = new VerifyDeviceProofRequest
        {
            ProtocolVersion = Version(),
            AgentId = completed.AgentId,
            ChallengeId = challenge.ChallengeId,
            SignatureAlgorithm = SignatureAlgorithm.EcdsaP256Sha256,
            ProofSignature = ByteString.CopyFrom(signature)
        };
        var verified = await restarted.VerifyDeviceProofAsync(proof);
        Assert.Equal(completed.NodeId, verified.NodeId);
        var replay = await Assert.ThrowsAsync<EnrollmentFailureException>(() => restarted.VerifyDeviceProofAsync(proof));
        Assert.Equal(EnrollmentFailure.AlreadyUsed, replay.Failure);
        var enrollmentReplay = await Assert.ThrowsAsync<EnrollmentFailureException>(() => restarted.CompleteAsync(Complete(created, key)));
        Assert.Equal(EnrollmentFailure.AlreadyUsed, enrollmentReplay.Failure);
    }

    [Fact]
    [Trait("Category", "SQLite")]
    public async Task SecretIsSaltedAndNeverStoredInDatabaseWalOrAudit()
    {
        using var database = new TestDatabase();
        var store = database.CreateStore();
        await store.InitializeAsync();
        var created = await store.CreateAsync(Request());
        var audit = await store.ReadAuditAsync();
        Assert.DoesNotContain(audit, item => item.EventType.Contains(Convert.ToBase64String(created.EnrollmentSecret.ToByteArray()), StringComparison.Ordinal));

        var secret = created.EnrollmentSecret.ToByteArray();
        var encodings = new[]
        {
            secret,
            Encoding.ASCII.GetBytes(Convert.ToHexString(secret)),
            Encoding.ASCII.GetBytes(Convert.ToBase64String(secret))
        };
        foreach (var path in Directory.EnumerateFiles(database.DirectoryPath, "monitor.db*"))
        {
            var bytes = await File.ReadAllBytesAsync(path);
            foreach (var encoded in encodings)
            {
                Assert.False(Contains(bytes, encoded), $"Enrollment secret was present in {Path.GetFileName(path)}.");
            }
        }
    }

    [Fact]
    [Trait("Category", "SQLite")]
    public async Task WrongPlatformDisplayNameSignatureAndExpiredEnrollmentFailClosed()
    {
        using var database = new TestDatabase();
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-11-16T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var store = database.CreateStore(clock, TimeSpan.FromMinutes(1));
        await store.InitializeAsync();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var platformEnrollment = await store.CreateAsync(Request());
        var wrongPlatform = Complete(platformEnrollment, key);
        wrongPlatform.Platform = Platform.Ubuntu;
        Assert.Equal(EnrollmentFailure.Unauthorized, (await Assert.ThrowsAsync<EnrollmentFailureException>(() => store.CompleteAsync(wrongPlatform))).Failure);

        var nameEnrollment = await store.CreateAsync(Request());
        var wrongName = Complete(nameEnrollment, key);
        wrongName.DisplayName = "substituted";
        Assert.Equal(EnrollmentFailure.Unauthorized, (await Assert.ThrowsAsync<EnrollmentFailureException>(() => store.CompleteAsync(wrongName))).Failure);

        var signatureEnrollment = await store.CreateAsync(Request());
        var wrongSignature = Complete(signatureEnrollment, key);
        wrongSignature.ProofSignature = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(64));
        Assert.Equal(EnrollmentFailure.Unauthorized, (await Assert.ThrowsAsync<EnrollmentFailureException>(() => store.CompleteAsync(wrongSignature))).Failure);

        var expiredEnrollment = await store.CreateAsync(Request());
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(EnrollmentFailure.Expired, (await Assert.ThrowsAsync<EnrollmentFailureException>(() => store.CompleteAsync(Complete(expiredEnrollment, key)))).Failure);
    }

    [Fact]
    [Trait("Category", "SQLite")]
    public async Task ConcurrentCompletionAcrossConnectionsHasExactlyOneWinner()
    {
        using var database = new TestDatabase();
        var first = database.CreateStore();
        var second = database.CreateStore();
        await first.InitializeAsync();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var created = await first.CreateAsync(Request());
        var request = Complete(created, key);
        var attempts = await Task.WhenAll(Attempt(first, request.Clone()), Attempt(second, request.Clone()));
        Assert.Single(attempts, result => result);
        Assert.Single(attempts, result => !result);
    }

    [Fact]
    [Trait("Category", "SQLite")]
    public async Task CancelledEnrollmentCannotComplete()
    {
        using var database = new TestDatabase();
        var store = database.CreateStore();
        await store.InitializeAsync();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var created = await store.CreateAsync(Request());
        await store.CancelAsync(new CancelEnrollmentRequest { EnrollmentId = created.EnrollmentId });
        var failure = await Assert.ThrowsAsync<EnrollmentFailureException>(() => store.CompleteAsync(Complete(created, key)));
        Assert.Equal(EnrollmentFailure.Cancelled, failure.Failure);
    }

    private static async Task<bool> Attempt(EnrollmentStore store, CompleteEnrollmentRequest request)
    {
        try
        {
            await store.CompleteAsync(request);
            return true;
        }
        catch (EnrollmentFailureException exception) when (exception.Failure == EnrollmentFailure.AlreadyUsed)
        {
            return false;
        }
    }

    internal static CreateEnrollmentRequest Request() => new()
    {
        ProtocolVersion = Version(),
        Platform = Platform.Windows,
        DisplayName = "test-device"
    };

    internal static ProtocolVersion Version() => new() { Major = 1, Minor = 0 };

    internal static CompleteEnrollmentRequest Complete(CreateEnrollmentResponse created, ECDsa key)
    {
        var publicKey = key.ExportSubjectPublicKeyInfo();
        var payload = ProofPayload.CreateEnrollment(created.EnrollmentId, created.ProofChallenge.Span, publicKey);
        return new CompleteEnrollmentRequest
        {
            ProtocolVersion = Version(),
            EnrollmentId = created.EnrollmentId,
            EnrollmentSecret = created.EnrollmentSecret,
            Platform = Platform.Windows,
            DisplayName = "test-device",
            PublicKeySpki = ByteString.CopyFrom(publicKey),
            SignatureAlgorithm = SignatureAlgorithm.EcdsaP256Sha256,
            ProofSignature = ByteString.CopyFrom(key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
        };
    }

    private static bool Contains(ReadOnlySpan<byte> source, ReadOnlySpan<byte> value) => source.IndexOf(value) >= 0;

    private sealed class TestDatabase : IDisposable
    {
        private readonly byte[] pepper = RandomNumberGenerator.GetBytes(32);

        public TestDatabase()
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), "monitor-app-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
        }

        public string DirectoryPath { get; }

        public EnrollmentStore CreateStore(TimeProvider? clock = null, TimeSpan? enrollmentLifetime = null) =>
            new(new EnrollmentStoreOptions(
                Path.Combine(DirectoryPath, "monitor.db"), pepper,
                enrollmentLifetime ?? TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(2)), clock);

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
        public void Advance(TimeSpan amount) => value += amount;
    }
}
