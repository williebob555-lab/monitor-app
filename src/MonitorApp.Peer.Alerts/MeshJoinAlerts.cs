using MonitorApp.Contracts.V1;

namespace MonitorApp.Peer.Alerts;

/// <summary>
/// "A new device joined" (T086). A computer raises it when it starts trusting a device that another member added, so
/// the owner notices a device they did not add. Every computer that learns the device raises its own, so a phone that
/// hears it from several computers shows and notifies it once.
/// </summary>
public static class MeshJoinAlerts
{
    public const string RuleId = "mesh.device_joined";

    /// <summary>Raises the alert for one device admitted by another member. False when it was already raised here.</summary>
    public static async Task<bool> RaiseAsync(AlertStore alerts, string localPeerId, IReadOnlyCollection<string> recipients,
        string joinedPeerId, string? joinedName, string? addedByName, TimeSpan outboxTtl, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alerts);
        ArgumentException.ThrowIfNullOrWhiteSpace(localPeerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(joinedPeerId);
        var name = Name(joinedName, "A device");
        var by = Name(addedByName, "another of your devices");
        var alert = new Alert(Alert.CreateId(localPeerId, RuleId, joinedPeerId, now), 1, RuleId, joinedPeerId, localPeerId,
            AlertSeverity.Warning, AlertState.Raised, $"New device joined: {name}",
            $"{name} was added by {by} and is now trusted by all your devices. If you did not add it, remove it on any device.",
            now, null);
        return await alerts.RaiseAsync(alert, recipients, outboxTtl, now, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Keeps the earliest "new device joined" alert per device and drops the ones about this device itself (a device
    /// that joins is handed the alerts already active on the computers it reaches); every other alert is kept.
    /// </summary>
    public static IReadOnlyList<InboxAlert> Collapse(IEnumerable<InboxAlert> inbox, string localPeerId)
    {
        ArgumentNullException.ThrowIfNull(inbox);
        var items = inbox.Where(item => item.Alert.RuleId != RuleId || item.Alert.SubjectId != localPeerId).ToArray();
        var first = items.Where(item => item.Alert.RuleId == RuleId)
            .GroupBy(item => item.Alert.SubjectId, StringComparer.Ordinal)
            .Select(group => group.OrderBy(item => item.Alert.RaisedAt).ThenBy(item => item.Alert.OriginPeerId, StringComparer.Ordinal).First())
            .ToHashSet();
        return items.Where(item => item.Alert.RuleId != RuleId || first.Contains(item)).ToArray();
    }

    private static string Name(string? value, string fallback)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text)) return fallback;
        return text.Length > 64 ? text[..64] : text;
    }
}
