using System.Security.Cryptography;
using System.Text;
using Grpc.Core;
using MonitorApp.Contracts.V1;
using MonitorApp.Persistence;

namespace MonitorApp.Controller;

public sealed class EnrollmentGrpcService(EnrollmentStore store, byte[] adminToken) : EnrollmentService.EnrollmentServiceBase
{
    public override async Task<CreateEnrollmentResponse> Create(CreateEnrollmentRequest request, ServerCallContext context)
    {
        RequireAdmin(context);
        return await InvokeAsync(() => store.CreateAsync(request, context.CancellationToken)).ConfigureAwait(false);
    }

    public override async Task<EnrollmentStatusResponse> GetStatus(EnrollmentStatusRequest request, ServerCallContext context)
    {
        RequireAdmin(context);
        return await InvokeAsync(() => store.GetStatusAsync(request, context.CancellationToken)).ConfigureAwait(false);
    }

    public override async Task<CancelEnrollmentResponse> Cancel(CancelEnrollmentRequest request, ServerCallContext context)
    {
        RequireAdmin(context);
        return await InvokeAsync(() => store.CancelAsync(request, context.CancellationToken)).ConfigureAwait(false);
    }

    public override Task<CompleteEnrollmentResponse> Complete(CompleteEnrollmentRequest request, ServerCallContext context) =>
        InvokeAsync(() => store.CompleteAsync(request, context.CancellationToken));

    public override Task<CreateDeviceChallengeResponse> CreateDeviceChallenge(CreateDeviceChallengeRequest request, ServerCallContext context) =>
        InvokeAsync(() => store.CreateDeviceChallengeAsync(request, context.CancellationToken));

    public override Task<VerifyDeviceProofResponse> VerifyDeviceProof(VerifyDeviceProofRequest request, ServerCallContext context) =>
        InvokeAsync(() => store.VerifyDeviceProofAsync(request, context.CancellationToken));

    private void RequireAdmin(ServerCallContext context)
    {
        var supplied = context.RequestHeaders.GetValue("x-monitor-admin-token");
        var suppliedBytes = supplied is null ? [] : Encoding.UTF8.GetBytes(supplied);
        if (suppliedBytes.Length != adminToken.Length ||
            !CryptographicOperations.FixedTimeEquals(suppliedBytes, adminToken))
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Owner authorization is required."));
        }
    }

    private static async Task<T> InvokeAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (EnrollmentFailureException exception)
        {
            var code = exception.Failure switch
            {
                EnrollmentFailure.InvalidRequest => StatusCode.InvalidArgument,
                EnrollmentFailure.NotFound => StatusCode.NotFound,
                EnrollmentFailure.Expired => StatusCode.FailedPrecondition,
                EnrollmentFailure.AlreadyUsed => StatusCode.AlreadyExists,
                EnrollmentFailure.Cancelled => StatusCode.FailedPrecondition,
                EnrollmentFailure.Unauthorized => StatusCode.Unauthenticated,
                _ => StatusCode.Internal
            };
            throw new RpcException(new Status(code, exception.Message));
        }
    }
}
