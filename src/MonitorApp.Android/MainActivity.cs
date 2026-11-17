using Android.App;
using Android.OS;
using Android.Text;
using Android.Views;
using Android.Widget;
using Grpc.Core;
using MonitorApp.Android.Core;

namespace MonitorApp.Android;

[Activity(Label = "Monitor App", MainLauncher = true, Exported = true)]
public sealed class MainActivity : Activity
{
    private EditText controllerAddress = null!;
    private EditText enrollmentId = null!;
    private EditText enrollmentSecret = null!;
    private EditText proofChallenge = null!;
    private EditText displayName = null!;
    private TextView status = null!;
    private Button refreshButton = null!;
    private Button healthButton = null!;
    private Button pairButton = null!;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Title = "Monitor App";

        var content = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical
        };
        var padding = (int)(16 * Resources!.DisplayMetrics!.Density);
        content.SetPadding(padding, padding, padding, padding);

        content.AddView(CreateHeading("Monitor App functional alpha"));
        content.AddView(CreateDescription(
            "Foreground Android status and secure application pairing. " +
            "No background monitoring or live telemetry upload is active yet."));

        status = CreateDescription(string.Empty);
        content.AddView(status);

        refreshButton = CreateButton("Refresh device status");
        refreshButton.Click += (_, _) => RefreshDeviceStatus();
        content.AddView(refreshButton);

        controllerAddress = CreateInput("Controller HTTPS address", InputTypes.ClassText | InputTypes.TextVariationUri);
        content.AddView(controllerAddress);

        healthButton = CreateButton("Check controller health");
        healthButton.Click += async (_, _) => await CheckControllerHealthAsync().ConfigureAwait(true);
        content.AddView(healthButton);

        content.AddView(CreateHeading("Pair this Android device"));
        content.AddView(CreateDescription(
            "Enter the short-lived values produced by an authorized controller owner. " +
            "The secret and challenge are cleared after every attempt and are never saved."));

        displayName = CreateInput("Display name", InputTypes.ClassText | InputTypes.TextFlagCapSentences);
        enrollmentId = CreateInput("Enrollment ID", InputTypes.ClassText | InputTypes.TextVariationVisiblePassword);
        enrollmentSecret = CreateInput("Enrollment secret (base64)", InputTypes.ClassText | InputTypes.TextVariationPassword);
        proofChallenge = CreateInput("Proof challenge (base64)", InputTypes.ClassText | InputTypes.TextVariationPassword);
        content.AddView(displayName);
        content.AddView(enrollmentId);
        content.AddView(enrollmentSecret);
        content.AddView(proofChallenge);

        pairButton = CreateButton("Complete secure pairing");
        pairButton.Click += async (_, _) => await PairAsync().ConfigureAwait(true);
        content.AddView(pairButton);

        var scroll = new ScrollView(this);
        scroll.AddView(content);
        SetContentView(scroll);
        RefreshDeviceStatus();
    }

    private void RefreshDeviceStatus()
    {
        try
        {
            var snapshot = AndroidDeviceSnapshot.Capture(this);
            var battery = snapshot.BatteryPercent.IsAvailable
                ? $"{snapshot.BatteryPercent.Value}%"
                : "Unavailable";
            var receipt = new PairingReceiptStore(this).Load();
            var identity = receipt is null
                ? "Not paired"
                : $"Paired as {receipt.AgentId} / {receipt.NodeId}";
            status.Text =
                $"Device: {snapshot.DeviceIdentity}\n" +
                $"Platform: {snapshot.Platform}\n" +
                $"Battery: {battery} ({snapshot.ChargingState})\n" +
                $"Network: {snapshot.Network.DisplayText}\n" +
                $"VPN detected: {(snapshot.Network.IsVpn ? "Yes" : "No")}\n" +
                $"Identity: {identity}\n" +
                $"Freshness: foreground snapshot at {snapshot.SourceTime:O}";
        }
        catch (Exception exception)
        {
            status.Text = $"Device status unavailable: {exception.Message}";
        }
    }

    private async Task CheckControllerHealthAsync()
    {
        SetBusy(true);
        try
        {
            var controller = ControllerUriPolicy.ParseHttps(controllerAddress.Text ?? string.Empty);
            _ = await ControllerClient.CheckHealthAsync(controller, CancellationToken.None).ConfigureAwait(true);
            status.Text = $"Controller healthy at {controller} ({DateTimeOffset.UtcNow:O}).";
        }
        catch (Exception exception) when (exception is ArgumentException or HttpRequestException or TaskCanceledException)
        {
            status.Text = $"Controller health check failed: {exception.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task PairAsync()
    {
        SetBusy(true);
        try
        {
            var controller = ControllerUriPolicy.ParseHttps(controllerAddress.Text ?? string.Empty);
            var material = EnrollmentMaterial.Parse(
                enrollmentId.Text ?? string.Empty,
                enrollmentSecret.Text ?? string.Empty,
                proofChallenge.Text ?? string.Empty,
                displayName.Text ?? string.Empty);
            var signer = new AndroidKeystoreDeviceKeySigner("monitor-app-device-v1");
            var receipt = await ControllerClient.CompleteEnrollmentAsync(
                controller,
                material,
                signer,
                CancellationToken.None).ConfigureAwait(true);
            new PairingReceiptStore(this).Save(receipt);
            status.Text =
                $"Pairing complete. Agent {receipt.AgentId}, node {receipt.NodeId}, " +
                $"completed {receipt.CompletedAt:O}.";
        }
        catch (RpcException exception)
        {
            status.Text = $"Pairing rejected: {exception.StatusCode} — {exception.Status.Detail}";
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            status.Text = $"Pairing failed: {exception.Message}";
        }
        finally
        {
            enrollmentSecret.Text = string.Empty;
            proofChallenge.Text = string.Empty;
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        refreshButton.Enabled = !busy;
        healthButton.Enabled = !busy;
        pairButton.Enabled = !busy;
    }

    private TextView CreateHeading(string text) => new(this)
    {
        Text = text,
        TextSize = 22,
        ImportantForAccessibility = ImportantForAccessibility.Yes
    };

    private TextView CreateDescription(string text) => new(this)
    {
        Text = text,
        TextSize = 16,
        ImportantForAccessibility = ImportantForAccessibility.Yes
    };

    private EditText CreateInput(string hint, InputTypes inputType) => new(this)
    {
        Hint = hint,
        ContentDescription = hint,
        InputType = inputType,
        ImportantForAccessibility = ImportantForAccessibility.Yes
    };

    private Button CreateButton(string text) => new(this)
    {
        Text = text,
        ContentDescription = text,
        ImportantForAccessibility = ImportantForAccessibility.Yes
    };
}
