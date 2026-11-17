using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using MonitorApp.Persistence;

namespace MonitorApp.Controller;

public static class ControllerApplication
{
    public static async Task<WebApplication> CreateAsync(
        string[] args,
        CancellationToken cancellationToken = default)
    {
        var builder = WebApplication.CreateBuilder(args);
        var databasePath = builder.Configuration["MonitorApp:DatabasePath"] ?? "data/monitor-app.db";
        var pepper = ReadBase64Secret(
            builder.Configuration["MonitorApp:SecretPepper"],
            "MonitorApp:SecretPepper");
        var adminTokenText = builder.Configuration["MonitorApp:AdminToken"];
        if (string.IsNullOrWhiteSpace(adminTokenText) || Encoding.UTF8.GetByteCount(adminTokenText) < 32)
        {
            throw new InvalidOperationException(
                "MonitorApp:AdminToken must contain at least 32 UTF-8 bytes.");
        }

        if (int.TryParse(
                builder.Configuration["MonitorApp:InsecureHttp2Port"],
                out var insecurePort))
        {
            builder.WebHost.ConfigureKestrel(options =>
                options.Listen(
                    IPAddress.Loopback,
                    insecurePort,
                    listen => listen.Protocols = HttpProtocols.Http2));
        }

        var adminToken = Encoding.UTF8.GetBytes(adminTokenText);
        builder.Services.AddGrpc(options => options.MaxReceiveMessageSize = 1024 * 1024);
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddFixedWindowLimiter("enrollment", limiter =>
            {
                limiter.PermitLimit = 30;
                limiter.Window = TimeSpan.FromMinutes(1);
                limiter.QueueLimit = 0;
            });
        });
        builder.Services.AddSingleton(new EnrollmentStoreOptions(
            databasePath,
            pepper,
            TimeSpan.FromMinutes(10),
            TimeSpan.FromMinutes(2)));
        builder.Services.AddSingleton<EnrollmentStore>();
        builder.Services.AddSingleton(new TelemetryStore(databasePath));
        builder.Services.AddSingleton(adminToken);
        builder.Services.AddSingleton<EnrollmentGrpcService>();
        builder.Services.AddSingleton<AgentGrpcService>();

        var app = builder.Build();
        await app.Services.GetRequiredService<EnrollmentStore>()
            .InitializeAsync(cancellationToken).ConfigureAwait(false);
        await app.Services.GetRequiredService<TelemetryStore>()
            .InitializeAsync(cancellationToken).ConfigureAwait(false);

        app.UseRateLimiter();
        app.MapGet("/health", () => Results.Ok(new { status = "healthy", version = "0.1.0" }));
        app.MapGet("/api/inventory", async (
            HttpContext context,
            TelemetryStore store,
            CancellationToken stoppingToken) =>
        {
            if (!IsOwner(context, adminToken))
            {
                return Results.Unauthorized();
            }

            return Results.Ok(await store.ReadInventoryAsync(stoppingToken).ConfigureAwait(false));
        });
        app.MapGet("/api/metrics/current", async (
            HttpContext context,
            TelemetryStore store,
            string? agentId,
            CancellationToken stoppingToken) =>
        {
            if (!IsOwner(context, adminToken))
            {
                return Results.Unauthorized();
            }

            return Results.Ok(await store.ReadCurrentMetricsAsync(
                agentId,
                stoppingToken).ConfigureAwait(false));
        });
        app.MapGrpcService<EnrollmentGrpcService>().RequireRateLimiting("enrollment");
        app.MapGrpcService<AgentGrpcService>();
        return app;
    }

    private static bool IsOwner(HttpContext context, byte[] expected)
    {
        var value = context.Request.Headers["x-monitor-admin-token"].ToString();
        var supplied = Encoding.UTF8.GetBytes(value);
        return supplied.Length == expected.Length &&
            CryptographicOperations.FixedTimeEquals(supplied, expected);
    }

    private static byte[] ReadBase64Secret(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{name} is required.");
        }

        try
        {
            var bytes = Convert.FromBase64String(value);
            if (bytes.Length < 32)
            {
                throw new InvalidOperationException($"{name} must decode to at least 32 bytes.");
            }

            return bytes;
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException($"{name} must be valid base64.", exception);
        }
    }
}
