using System.Globalization;
using System.Security.Cryptography;
using Grpc.Core;
using MonitorApp.Agent.Core;

var agentId = Environment.GetEnvironmentVariable("MONITORAPP_AGENT_ID");
if (string.IsNullOrWhiteSpace(agentId))
{
    Console.Error.WriteLine("MONITORAPP_AGENT_ID is required. Complete enrollment before starting the agent service.");
    return 2;
}

var identity = HostIdentityProvider.ReadCurrent();
var controllerText = Environment.GetEnvironmentVariable("MONITORAPP_CONTROLLER_URL");
var keyPath = Environment.GetEnvironmentVariable("MONITORAPP_DEVICE_KEY_PATH");
GrpcAgentSessionTransport? transport = null;
ECDsa? deviceKey = null;
if (!string.IsNullOrWhiteSpace(controllerText))
{
    if (!Uri.TryCreate(controllerText, UriKind.Absolute, out var controllerUri) ||
        (controllerUri.Scheme != Uri.UriSchemeHttps &&
         !(controllerUri.Scheme == Uri.UriSchemeHttp && controllerUri.IsLoopback)))
    {
        Console.Error.WriteLine("MONITORAPP_CONTROLLER_URL must use HTTPS, except loopback development URLs.");
        return 2;
    }

    if (string.IsNullOrWhiteSpace(keyPath) || !File.Exists(keyPath))
    {
        Console.Error.WriteLine("MONITORAPP_DEVICE_KEY_PATH must name the enrolled device private-key PEM file.");
        return 2;
    }

    deviceKey = ECDsa.Create();
    deviceKey.ImportFromPem(await File.ReadAllTextAsync(keyPath));
    if (deviceKey.KeySize != 256)
    {
        Console.Error.WriteLine("The enrolled device key must be ECDSA P-256.");
        deviceKey.Dispose();
        return 2;
    }

    transport = new GrpcAgentSessionTransport(controllerUri, deviceKey);
}
var spoolPath = Environment.GetEnvironmentVariable("MONITORAPP_SPOOL_PATH");
if (string.IsNullOrWhiteSpace(spoolPath))
{
    spoolPath = Path.Combine(AppContext.BaseDirectory, "data", "agent-spool.db");
}

var intervalText = Environment.GetEnvironmentVariable("MONITORAPP_COLLECTION_INTERVAL_SECONDS");
var collectionInterval = 15;
if (!string.IsNullOrWhiteSpace(intervalText) &&
    (!int.TryParse(intervalText, NumberStyles.None, CultureInfo.InvariantCulture, out collectionInterval) ||
     collectionInterval is < 5 or > 3600))
{
    Console.Error.WriteLine("MONITORAPP_COLLECTION_INTERVAL_SECONDS must be between 5 and 3600.");
    return 2;
}

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

await using var spool = await SqliteTelemetrySpool.OpenAsync(
    new TelemetrySpoolOptions(spoolPath),
    shutdown.Token);
var collector = new SystemSnapshotCollector(identity.Platform);
var once = args.Contains("--once", StringComparer.Ordinal);
var coordinator = transport is null ? null : new AgentSessionCoordinator(spool, transport);

try
{
    do
    {
        try
        {
            var snapshot = await collector.CollectAsync(shutdown.Token);
            var envelopes = TelemetryEnvelopeFactory.Create(
                agentId,
                identity.BootId,
                "system",
                snapshot);
            foreach (var envelope in envelopes)
            {
                await spool.EnqueueAsync(envelope, shutdown.Token);
            }

            if (coordinator is not null)
            {
                await coordinator.FlushOnceAsync(
                    agentId,
                    identity.BootId,
                    "0.1.0",
                    identity.Platform,
                    snapshot.Health.Select(health => new MonitorApp.Contracts.V1.Capability
                    {
                        Id = health.CapabilityId,
                        State = health.State,
                    }),
                    shutdown.Token);
            }

            var state = await spool.GetStateAsync(shutdown.Token);
            Console.WriteLine(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{DateTimeOffset.UtcNow:O} collected {envelopes.Count} observations; {state.PendingCount} pending upload."));
        }
        catch (SpoolCapacityExceededException exception)
        {
            Console.Error.WriteLine(exception.Message);
        }
        catch (Exception exception) when (
            exception is RpcException or HttpRequestException or IOException or InvalidOperationException)
        {
            Console.Error.WriteLine($"Telemetry upload deferred: {exception.Message}");
        }

        if (!once)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(collectionInterval), shutdown.Token);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
            }
        }
    }
    while (!once && !shutdown.IsCancellationRequested);
}
finally
{
    if (transport is not null)
    {
        await transport.DisposeAsync();
    }

    deviceKey?.Dispose();
}

return 0;
