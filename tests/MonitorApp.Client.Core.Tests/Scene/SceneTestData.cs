using MonitorApp.Client.Core.Scene;

namespace MonitorApp.Client.Core.Tests.Scene;

/// <summary>Builders for DTOs shaped like what the local API returns.</summary>
internal static class SceneTestData
{
    public static readonly DateTimeOffset Now = new(2026, 11, 21, 16, 0, 0, TimeSpan.Zero);

    public static HistoryPointDto Number(
        string origin, string subject, string signal, double value, string unit,
        double ageSeconds = 5, string quality = "Valid", string freshness = "Fresh") =>
        new(origin, "stream-" + origin, 1, subject, signal, Now.AddSeconds(-ageSeconds), Now, "adapter", "LocalOrigin",
            quality, freshness, ageSeconds, new ObservationValueDto("number", value, null, null), unit, null);

    public static HistoryPointDto Flag(
        string origin, string subject, string signal, bool value, double ageSeconds = 5) =>
        new(origin, "stream-" + origin, 1, subject, signal, Now.AddSeconds(-ageSeconds), Now, "adapter", "LocalOrigin",
            "Valid", "Fresh", ageSeconds, new ObservationValueDto("boolean", null, null, value), "Boolean", null);

    /// <summary>The node withholds the value of unavailable readings.</summary>
    public static HistoryPointDto Unavailable(
        string origin, string subject, string signal, string unit, string quality = "Unavailable", double ageSeconds = 5) =>
        new(origin, "stream-" + origin, 1, subject, signal, Now.AddSeconds(-ageSeconds), Now, "adapter", "LocalOrigin",
            quality, "Unavailable", ageSeconds, null, unit, null);

    public static RuntimeDto Runtime(string localId = "peer-local") =>
        new("running", "local", new LocalPeerDto(localId, "FP"), 0, 0);

    public static TrustedPeerDto PeerDto(string id, string? name = null, string state = "Active") =>
        new(id, name, "FP-" + id, state, Now.AddDays(-2), state == "Active" ? null : Now.AddDays(-1));

    public static StateDto State(params HistoryPointDto[] points) =>
        new(Now, Now.AddMinutes(-30), 1000, "raw", false, points);

    public static SceneReading Reading(
        string signal, double? number, string? unit, string display = "x", string quality = "Valid",
        string freshness = "Fresh", double age = 5) =>
        new("s", signal, SignalCatalog.Describe(signal).Label, display, number, unit, quality, freshness, Now.AddSeconds(-age), age, "o", "adapter");
}
