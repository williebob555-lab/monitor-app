using System.Globalization;

namespace MonitorApp.Client.Core.ViewModels;

public sealed record TrustedPeerRow(
    string PeerId,
    string Name,
    string Fingerprint,
    string TrustState,
    string PairedAt,
    string? RevokedAt,
    string? Provenance = null)
{
    public bool CanRevoke => string.Equals(TrustState, "Active", StringComparison.Ordinal);

    public bool HasProvenance => !string.IsNullOrEmpty(Provenance);

    public static TrustedPeerRow From(TrustedPeerDto peer) => new(
        peer.PeerId,
        peer.DisplayName ?? peer.PeerId,
        peer.FingerprintSha256,
        peer.TrustState,
        Format.Time(peer.PairedAt),
        peer.RevokedAt is { } revoked ? Format.Time(revoked) : null,
        peer.Provenance);
}

public sealed record PendingPairingRow(
    string PairingId,
    string Name,
    string PeerId,
    string Fingerprint,
    string ApprovalState,
    string ExpiresAt)
{
    /// <summary>Only undecided pairings can be approved or rejected; an approved one waits for the peer to finish.</summary>
    public bool CanDecide => string.Equals(ApprovalState, "PendingApproval", StringComparison.Ordinal);

    public static PendingPairingRow From(PendingPairingDto pairing) => new(
        pairing.PairingId,
        pairing.DisplayName ?? pairing.RemotePeerId,
        pairing.RemotePeerId,
        pairing.FingerprintSha256,
        pairing.ApprovalState,
        Format.Time(pairing.ExpiresAt));
}

/// <summary>
/// One observation as shown in tables: a plain label and a formatted value with its unit ("Wi-Fi link: Up", "Battery level: 96%").
/// <see cref="Signal"/> keeps the raw id for filtering; it is not shown.
/// </summary>
public sealed record ObservationRow(
    string Subject,
    string Signal,
    string Value,
    string Unit,
    string Freshness,
    string Quality,
    string ObservedAt,
    string Age,
    string Detail,
    bool HasValue)
{
    public string Label => Scene.SignalCatalog.Describe(Signal).Label;

    /// <summary>"Updated 12s ago", "Out of date · 3h ago" or "Not available": the freshness and quality codes in plain words.</summary>
    public string Status => !HasValue
        ? Quality switch
        {
            "PermissionDenied" => "No permission to read this",
            "Error" => "Could not be read",
            _ => "Not available on this device",
        }
        : Freshness is "Stale" or "Offline" ? $"Out of date · {Age} ago" : $"Updated {Age} ago";

    public static ObservationRow From(HistoryPointDto point)
    {
        var hasValue = ValueFormatter.HasValue(point);
        return new ObservationRow(
            point.SubjectEntityId,
            point.SignalId,
            Scene.SignalCatalog.Format(point),
            string.Empty, // the formatted value already carries its unit
            point.Freshness,
            point.Quality,
            Format.Time(point.ObservedAt),
            Format.Age(point.AgeSeconds),
            point.Detail ?? string.Empty,
            hasValue);
    }
}

public static class ValueFormatter
{
    public const string Unavailable = "Unavailable";

    /// <summary>True only when the node supplied a usable value for a reading that is not Unavailable.</summary>
    public static bool HasValue(HistoryPointDto point) =>
        point.Value is not null && !string.Equals(point.Freshness, Unavailable, StringComparison.Ordinal);

    public static string Format(HistoryPointDto point)
    {
        if (!HasValue(point))
        {
            return point.Quality switch
            {
                "PermissionDenied" => Unavailable + " (no permission)",
                "Error" => Unavailable + " (read error)",
                _ => Unavailable,
            };
        }

        var value = point.Value!;
        return value.Kind switch
        {
            "number" when value.Number is { } number => number.ToString("G10", CultureInfo.InvariantCulture),
            "boolean" when value.Boolean is { } flag => flag ? "True" : "False",
            "text" when value.Text is { } text => text,
            _ => Unavailable,
        };
    }
}

public static class Format
{
    public static string Time(DateTimeOffset value) =>
        value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);

    public static string Age(double seconds) => seconds switch
    {
        < 0 => "in the future",
        < 90 => $"{seconds:0}s",
        < 5400 => $"{seconds / 60:0}m",
        < 129600 => $"{seconds / 3600:0.#}h",
        _ => $"{seconds / 86400:0.#}d",
    };
}
