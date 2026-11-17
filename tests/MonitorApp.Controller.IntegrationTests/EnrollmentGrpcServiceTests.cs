using System.Security.Cryptography;
using Google.Protobuf;
using Grpc.Core;
using MonitorApp.Contracts;
using MonitorApp.Contracts.V1;
using MonitorApp.Controller;
using MonitorApp.Persistence;
using Xunit;

namespace MonitorApp.Controller.IntegrationTests;

public sealed class EnrollmentGrpcServiceTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "monitor-controller-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    [Trait("Category", "Controller")]
    public async Task OwnerOperationsRequireAdminTokenAndSafeGrpcErrorsRedactCredentials()
    {
        Directory.CreateDirectory(directory);
        var store = new EnrollmentStore(EnrollmentStoreOptions.Create(Path.Combine(directory, "controller.db"), RandomNumberGenerator.GetBytes(32)));
        await store.InitializeAsync();
        var admin = System.Text.Encoding.UTF8.GetBytes(new string('a', 32));
        var service = new EnrollmentGrpcService(store, admin);
        var denied = await Assert.ThrowsAsync<RpcException>(() => service.Create(Request(), new TestContext()));
        Assert.Equal(StatusCode.PermissionDenied, denied.StatusCode);

        var headers = new Metadata { { "x-monitor-admin-token", System.Text.Encoding.UTF8.GetString(admin) } };
        var created = await service.Create(Request(), new TestContext(headers));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var completion = Complete(created, key);
        completion.ProofSignature = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(64));
        var rejected = await Assert.ThrowsAsync<RpcException>(() => service.Complete(completion, new TestContext()));
        Assert.Equal(StatusCode.Unauthenticated, rejected.StatusCode);
        Assert.DoesNotContain(Convert.ToBase64String(created.EnrollmentSecret.ToByteArray()), rejected.Status.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(completion.ProofSignature.ToByteArray()), rejected.Status.Detail, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private static CreateEnrollmentRequest Request() => new()
    {
        ProtocolVersion = new ProtocolVersion { Major = 1 },
        Platform = Platform.Windows,
        DisplayName = "grpc-device"
    };

    private static CompleteEnrollmentRequest Complete(CreateEnrollmentResponse created, ECDsa key)
    {
        var publicKey = key.ExportSubjectPublicKeyInfo();
        var payload = ProofPayload.CreateEnrollment(created.EnrollmentId, created.ProofChallenge.Span, publicKey);
        return new CompleteEnrollmentRequest
        {
            ProtocolVersion = new ProtocolVersion { Major = 1 },
            EnrollmentId = created.EnrollmentId,
            EnrollmentSecret = created.EnrollmentSecret,
            Platform = Platform.Windows,
            DisplayName = "grpc-device",
            PublicKeySpki = ByteString.CopyFrom(publicKey),
            SignatureAlgorithm = SignatureAlgorithm.EcdsaP256Sha256,
            ProofSignature = ByteString.CopyFrom(key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
        };
    }

    private sealed class TestContext(Metadata? requestHeaders = null) : ServerCallContext
    {
        private Metadata responseTrailers = [];
        private Status status;
        private WriteOptions? writeOptions;
        protected override string MethodCore => "monitor.v1.EnrollmentService/Test";
        protected override string HostCore => "localhost";
        protected override string PeerCore => "inproc";
        protected override DateTime DeadlineCore => DateTime.MaxValue;
        protected override Metadata RequestHeadersCore => requestHeaders ?? [];
        protected override CancellationToken CancellationTokenCore => CancellationToken.None;
        protected override Metadata ResponseTrailersCore => responseTrailers;
        protected override Status StatusCore { get => status; set => status = value; }
        protected override WriteOptions? WriteOptionsCore { get => writeOptions; set => writeOptions = value; }
        protected override AuthContext AuthContextCore => new("anonymous", new Dictionary<string, List<AuthProperty>>());
        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) => throw new NotSupportedException();
        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
    }
}
