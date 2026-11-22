using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MonitorApp.Client.Core.ViewModels;

/// <summary>One headline reading on a device card, for example "CPU" and "12 %".</summary>
public sealed record HeadlineMetric(string Label, string Value);

/// <summary>A known device (this one or a trusted peer) with its freshness and headline readings.</summary>
public sealed record DeviceRow(
    string PeerId,
    string Name,
    string Role,
    bool IsOnline,
    string Status,
    string LastSeen,
    IReadOnlyList<HeadlineMetric> Headline)
{
    public bool HasHeadline => Headline.Count > 0;

    public string HeadlineText => string.Join("   ", Headline.Select(metric => $"{metric.Label} {metric.Value}"));
}

/// <summary>
/// The command-center home screen: every device this peer knows (itself and trusted peers) with online state,
/// last-seen time and live headline metrics, derived from the current state across all origins.
/// </summary>
public sealed partial class DevicesViewModel : LiveSectionViewModel
{
    /// <summary>A device with no reading newer than this is shown as offline.</summary>
    public static readonly TimeSpan OnlineWindow = TimeSpan.FromMinutes(2);

    [ObservableProperty]
    private string summary = "Not loaded";

    public DevicesViewModel(NodeConnection connection)
        : base(connection, "Devices")
    {
    }

    public ObservableCollection<DeviceRow> Devices { get; } = [];

    [RelayCommand]
    public override async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await RunAsync(async () =>
        {
            var runtime = await Client.GetRuntimeAsync(cancellationToken);
            var peers = await Client.GetPeersAsync(cancellationToken);
            var state = await Client.GetStateAsync(ct: cancellationToken);
            var rows = Build(runtime.LocalPeer?.PeerId, peers, state.Points);
            Devices.Clear();
            foreach (var row in rows)
            {
                Devices.Add(row);
            }

            var online = rows.Count(row => row.IsOnline);
            Summary = rows.Count == 0
                ? "No devices yet. Pair a desktop or phone to see it here."
                : $"{online} of {rows.Count} device(s) online.";
        });
    }

    public static IReadOnlyList<DeviceRow> Build(
        string? localPeerId, IReadOnlyList<TrustedPeerDto> peers, IReadOnlyList<HistoryPointDto> points)
    {
        var names = peers.ToDictionary(peer => peer.PeerId, peer => peer.DisplayName ?? peer.PeerId, StringComparer.Ordinal);
        var ids = new List<string>();
        if (localPeerId is not null)
        {
            ids.Add(localPeerId);
        }

        ids.AddRange(peers.Where(peer => string.Equals(peer.TrustState, "Active", StringComparison.Ordinal))
            .Select(peer => peer.PeerId));
        ids.AddRange(points.Select(point => point.OriginPeerId));
        var rows = new List<DeviceRow>();
        foreach (var id in ids.Distinct(StringComparer.Ordinal))
        {
            var own = points.Where(point => string.Equals(point.OriginPeerId, id, StringComparison.Ordinal)).ToArray();
            var isLocal = string.Equals(id, localPeerId, StringComparison.Ordinal);
            var name = isLocal ? "This device" : names.GetValueOrDefault(id, id);
            var fresh = own.Where(point => ValueFormatter.HasValue(point)).ToArray();
            var newest = own.Length == 0 ? (DateTimeOffset?)null : own.Max(point => point.ObservedAt);
            var age = own.Length == 0 ? (double?)null : own.Min(point => point.AgeSeconds);
            var online = age is { } seconds && seconds <= OnlineWindow.TotalSeconds;
            rows.Add(new DeviceRow(
                id,
                name,
                isLocal ? "This device" : "Paired peer",
                online,
                newest is null ? "No data yet" : online ? "Online" : "Offline",
                newest is null ? "never" : $"{Format.Age(age!.Value)} ago ({Format.Time(newest.Value)})",
                Headlines(fresh)));
        }

        return rows;
    }

    private static List<HeadlineMetric> Headlines(HistoryPointDto[] points)
    {
        var result = new List<HeadlineMetric>();
        void Add(string label, string signalId, Func<HistoryPointDto, string?> format)
        {
            var point = points.FirstOrDefault(item => string.Equals(item.SignalId, signalId, StringComparison.Ordinal));
            if (point is not null && format(point) is { } value)
            {
                result.Add(new HeadlineMetric(label, value));
            }
        }

        Add("CPU", "system.cpu.utilization", Percent);
        var used = points.FirstOrDefault(item => item.SignalId == "system.memory.used")?.Value?.Number;
        var total = points.FirstOrDefault(item => item.SignalId == "system.memory.total")?.Value?.Number;
        if (used is { } usedBytes && total is > 0)
        {
            result.Add(new HeadlineMetric("Memory", $"{usedBytes / total.Value * 100:0}%"));
        }

        Add("Battery", "android.battery.level", Percent);
        Add("Charging", "android.battery.charging", point => point.Value?.Boolean is { } flag ? (flag ? "yes" : "no") : null);
        Add("Network", "android.network.connected", point => point.Value?.Boolean is { } flag ? (flag ? "connected" : "offline") : null);
        return result;
    }

    private static string? Percent(HistoryPointDto point)
    {
        if (point.Value?.Number is not { } number)
        {
            return null;
        }

        var ratio = point.Unit?.Contains("ratio", StringComparison.OrdinalIgnoreCase) == true;
        return ratio
            ? string.Create(CultureInfo.InvariantCulture, $"{number * 100:0}%")
            : $"{number.ToString("0.#", CultureInfo.InvariantCulture)} {point.Unit}".TrimEnd();
    }
}
