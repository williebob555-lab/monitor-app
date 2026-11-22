using System.Globalization;
using System.Text;
using Android;
using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Text;
using Android.Views;
using Android.Widget;
using MonitorApp.Peer.Mobile;
using MonitorApp.Peer.Transport;

namespace MonitorApp.Android;

/// <summary>Thin shell over the peer runtime: health summary, pairing, alert placeholder, service control.</summary>
[Activity(Label = "Monitor App", MainLauncher = true, Exported = true)]
public sealed class MainActivity : Activity
{
    private TextView health = null!;
    private TextView pairingStatus = null!;
    private EditText invitation = null!;
    private Button pairButton = null!;
    private Handler? ui;
    private Java.Lang.IRunnable? tick;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Title = "Monitor App";
        var content = new LinearLayout(this) { Orientation = Orientation.Vertical };
        var padding = (int)(16 * Resources!.DisplayMetrics!.Density);
        content.SetPadding(padding, padding, padding, padding);

        content.AddView(Heading("Health"));
        health = Text(string.Empty);
        content.AddView(health);
        var start = Button("Start monitoring service");
        start.Click += (_, _) => StartService();
        var stop = Button("Stop monitoring service");
        stop.Click += (_, _) => PeerForegroundService.Stop(this);
        content.AddView(start);
        content.AddView(stop);

        content.AddView(Heading("Pair with a desktop"));
        content.AddView(Text("Paste the pairing invitation shown by the desktop. The desktop user must approve this phone."));
        invitation = new EditText(this)
        {
            Hint = "Pairing invitation (monitor-pair1.…)",
            ContentDescription = "Pairing invitation",
            InputType = InputTypes.ClassText | InputTypes.TextVariationUri,
        };
        content.AddView(invitation);
        pairButton = Button("Pair with desktop");
        pairButton.Click += async (_, _) => await PairAsync().ConfigureAwait(true);
        content.AddView(pairButton);
        pairingStatus = Text(string.Empty);
        content.AddView(pairingStatus);

        content.AddView(Heading("Alerts"));
        content.AddView(Text("No alerts. Alert rules are not implemented yet."));

        var scroll = new ScrollView(this);
        scroll.AddView(content);
        SetContentView(scroll);
        if (OperatingSystem.IsAndroidVersionAtLeast(33) &&
            CheckSelfPermission(Manifest.Permission.PostNotifications) != Permission.Granted)
        {
            RequestPermissions([Manifest.Permission.PostNotifications], 1);
        }
    }

    protected override void OnResume()
    {
        base.OnResume();
        ui = new Handler(Looper.MainLooper!);
        tick = new Java.Lang.Runnable(() =>
        {
            _ = RefreshAsync();
            ui?.PostDelayed(tick!, 2000);
        });
        ui.Post(tick);
    }

    protected override void OnPause()
    {
        ui?.RemoveCallbacksAndMessages(null);
        ui = null;
        base.OnPause();
    }

    private void StartService() => PeerForegroundService.Start(this);

    private async Task RefreshAsync()
    {
        try
        {
            var runtime = await MobileRuntimeHost.GetAsync(this).ConfigureAwait(true);
            var status = runtime.GetStatus();
            var text = new StringBuilder()
                .AppendLine(CultureInfo.InvariantCulture, $"Peer id: {status.PeerId}")
                .AppendLine(CultureInfo.InvariantCulture, $"Fingerprint (SHA-256): {status.FingerprintHex}")
                .AppendLine(CultureInfo.InvariantCulture, $"Service: {(status.Running ? "running" : "stopped")}")
                .Append(CultureInfo.InvariantCulture,
                    $"Last collection: {status.Collection.LastObservedAt?.ToString("O", CultureInfo.InvariantCulture) ?? "none"}")
                .Append(CultureInfo.InvariantCulture, $" ({status.Collection.LastAppendedRecords} records)");
            if (status.Collection.LastError is not null)
            {
                text.Append(CultureInfo.InvariantCulture, $", error {status.Collection.LastError}");
            }

            text.AppendLine();
            foreach (var peer in status.Peers)
            {
                text.Append(CultureInfo.InvariantCulture, $"Desktop {peer.PeerId}: {peer.State}");
                if (peer.LastSuccess is { } at)
                {
                    text.Append(CultureInfo.InvariantCulture, $", last sync {at:O}");
                }

                if (peer.LastError is not null)
                {
                    text.Append(CultureInfo.InvariantCulture, $" ({peer.LastError})");
                }

                text.AppendLine();
            }

            if (status.Peers.Count == 0)
            {
                text.AppendLine("Not paired with any desktop.");
            }

            health.Text = text.ToString();
            if (status.PendingPairingId is { } pending)
            {
                pairingStatus.Text = $"Waiting for desktop approval (pairing {pending}).";
            }
        }
        catch (Exception exception)
        {
            health.Text = $"Peer runtime unavailable: {exception.Message}";
        }
    }

    private async Task PairAsync()
    {
        pairButton.Enabled = false;
        try
        {
            var parsed = PeerPairingInvitation.Decode(invitation.Text ?? string.Empty);
            var runtime = await MobileRuntimeHost.GetAsync(this).ConfigureAwait(true);
            pairingStatus.Text = "Contacting desktop. Approve this phone on the desktop.";
            var desktop = await runtime.PairAsync(parsed).ConfigureAwait(true);
            pairingStatus.Text = $"Paired with {desktop}.";
            invitation.Text = string.Empty;
        }
        catch (Exception exception) when (exception is FormatException or MobilePairingFailedException)
        {
            pairingStatus.Text = exception.Message;
        }
        finally
        {
            pairButton.Enabled = true;
        }
    }

    private TextView Heading(string text) => new(this)
    {
        Text = text,
        TextSize = 22,
        ImportantForAccessibility = ImportantForAccessibility.Yes,
    };

    private TextView Text(string text) => new(this)
    {
        Text = text,
        TextSize = 16,
        ImportantForAccessibility = ImportantForAccessibility.Yes,
    };

    private Button Button(string text) => new(this)
    {
        Text = text,
        ContentDescription = text,
        ImportantForAccessibility = ImportantForAccessibility.Yes,
    };
}
