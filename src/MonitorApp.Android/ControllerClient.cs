using Google.Protobuf;
using Grpc.Net.Client;
using MonitorApp.Android.Core;
using MonitorApp.Contracts.V1;

namespace MonitorApp.Android;

public static class ControllerClient
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    public static async Task<string> CheckHealthAsync(Uri controller, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(controller);
        using var client = new HttpClient { BaseAddress = controller, Timeout = RequestTimeout };
        using var response = await client.GetAsync("health", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<PairingReceipt> CompleteEnrollmentAsync(
        Uri controller,
        EnrollmentMaterial material,
        IDeviceKeySigner signer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(controller);
        var request = EnrollmentCompletionBuilder.Build(material, signer);
        using var channel = GrpcChannel.ForAddress(controller);
        var client = new EnrollmentService.EnrollmentServiceClient(channel);
        var deadline = DateTime.UtcNow.Add(RequestTimeout);
        var response = await client.CompleteAsync(
            request,
            deadline: deadline,
            cancellationToken: cancellationToken).ResponseAsync.ConfigureAwait(false);
        return new PairingReceipt(
            response.AgentId,
            response.NodeId,
            Convert.ToBase64String(response.PublicKeySha256.ToByteArray()),
            response.CompletedAt.ToDateTimeOffset());
    }
}
