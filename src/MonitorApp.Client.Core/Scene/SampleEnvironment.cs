namespace MonitorApp.Client.Core.Scene;

/// <summary>
/// SAMPLE DATA ONLY. A fixed, invented environment matching the common prototype data of spec 17.1, used for
/// demo mode and tests. Nothing here came from a peer; the app must show it only when demo mode is on and label
/// it as sample data. Contents: a home server hosting Syncthing, Media server and Backup services; this laptop
/// syncing to the server over a direct link; a phone on a relayed (degraded) link; a stale tablet; an offline
/// Fedora box; and a failed Backup service with the Media server degraded because it depends on it.
/// </summary>
public static class SampleEnvironment
{
    public const string Laptop = "sample-laptop";
    public const string Server = "sample-server";
    public const string Phone = "sample-phone";
    public const string Tablet = "sample-tablet";
    public const string Fedora = "sample-fedora";
    public const string Syncthing = "sample-service-syncthing";
    public const string MediaServer = "sample-service-media";
    public const string Backup = "sample-service-backup";

    public const double GB = 1e9;

    public static SceneGraph Create(DateTimeOffset now)
    {
        var nodes = new List<SceneNode>
        {
            Node(Laptop, "Laptop", SceneNodeKind.LocalPeer, SceneHealth.Healthy, "Updated 4 s ago", null, now, 4,
                Cpu(Laptop, 0.31, 4, now), Memory(Laptop, 11.2 * GB, 16 * GB, 4, now), Storage(Laptop, 212 * GB, 512 * GB, 4, now)),
            Node(Server, "Home server", SceneNodeKind.Peer, SceneHealth.Healthy, "Updated 6 s ago", null, now, 6,
                Cpu(Server, 0.18, 6, now), Memory(Server, 12.4 * GB, 32 * GB, 6, now), Storage(Server, 3.1e12, 8e12, 6, now)),
            Node(Phone, "Pixel phone", SceneNodeKind.Peer, SceneHealth.Degraded, "Network connection is relayed", null, now, 20,
                Power(Phone, 0.41, false, 20, now), Connectivity(Phone, 20, now)),
            Node(Tablet, "Tablet", SceneNodeKind.Peer, SceneHealth.Stale, "Last reading 38 min ago", null, now, 38 * 60,
                Power(Tablet, 0.77, true, 38 * 60, now)),
            Node(Fedora, "Fedora box", SceneNodeKind.Peer, SceneHealth.Offline, "Reported offline; last reading 2 h ago", null, now, 2 * 3600,
                Cpu(Fedora, 0.05, 2 * 3600, now, "Offline")),
            Service(now, Syncthing, "Syncthing", SceneHealth.Healthy, "Running; syncing 1 folder", "Running"),
            Service(now, MediaServer, "Media server", SceneHealth.Degraded, "Degraded: depends on Backup, which failed", "Degraded"),
            Service(now, Backup, "Backup", SceneHealth.Degraded, "Last backup failed 12 min ago", "Failed"),
        };

        var links = new List<SceneLink>
        {
            new($"replication:{Laptop}:{Server}", Laptop, Server, SceneLinkKind.Replication, SceneHealth.Healthy, 0.7,
                "Direct private-network link; Syncthing folder syncing laptop to server storage"),
            new($"replication:{Laptop}:{Phone}", Laptop, Phone, SceneLinkKind.Replication, SceneHealth.Degraded, 0.15,
                "Relayed connection with high latency"),
            new($"replication:{Laptop}:{Tablet}", Laptop, Tablet, SceneLinkKind.Replication, SceneHealth.Stale, 0,
                "No recent data from the tablet"),
            new($"replication:{Laptop}:{Fedora}", Laptop, Fedora, SceneLinkKind.Replication, SceneHealth.Offline, 0,
                "Fedora box reported offline"),
            new($"hosting:{Server}:{Syncthing}", Server, Syncthing, SceneLinkKind.Hosting, SceneHealth.Healthy, 0.7, "Home server hosts Syncthing"),
            new($"hosting:{Server}:{MediaServer}", Server, MediaServer, SceneLinkKind.Hosting, SceneHealth.Degraded, 0.1, "Home server hosts Media server"),
            new($"hosting:{Server}:{Backup}", Server, Backup, SceneLinkKind.Hosting, SceneHealth.Degraded, 0, "Home server hosts Backup"),
            new($"dependency:{MediaServer}:{Backup}", MediaServer, Backup, SceneLinkKind.Dependency, SceneHealth.Degraded, 0,
                "Media server depends on Backup storage"),
            new($"dependency:{Laptop}:{Syncthing}", Laptop, Syncthing, SceneLinkKind.Dependency, SceneHealth.Healthy, 0.7,
                "Laptop folders sync through Syncthing"),
        };

        var layout = new Dictionary<string, ScenePoint>
        {
            [Laptop] = new(0, 0),
            [Server] = new(4.5, 0),
            [Syncthing] = new(5.5, -1.8),
            [MediaServer] = new(6.5, 0),
            [Backup] = new(5.5, 1.8),
            [Phone] = new(-3.5, 3.5),
            [Tablet] = new(-3.5, -3.5),
            [Fedora] = new(0, -5),
        };
        return new SceneGraph(now, false, nodes, links, layout);
    }

    private static SceneNode Node(
        string id, string label, SceneNodeKind kind, SceneHealth health, string reason, string? parent,
        DateTimeOffset now, double ageSeconds, params SceneLayer[] layers) =>
        new(id, label, kind, health, reason, parent, layers, 0, now.AddSeconds(-ageSeconds));

    private static SceneNode Service(DateTimeOffset now, string id, string label, SceneHealth health, string reason, string status) =>
        new(id, label, SceneNodeKind.Service, health, reason, Server,
            [new SceneLayer("other", SignalCatalog.LayerTitle(SceneLayerKind.Other), SceneLayerKind.Other, health, null,
                $"Status {status}",
                [new SceneReading(id, "service.status", "Service status", status, null, null,
                    "Valid", "Fresh", now, 0, Server, "sample")])],
            0, now);

    private static SceneReading Reading(
        string subject, string signal, double? number, string? unit, bool? flag, double age, DateTimeOffset now, string freshness = "Fresh")
    {
        var display = SignalCatalog.FormatValue(signal, unit, number, flag, null);
        return new SceneReading(subject, signal, SignalCatalog.Describe(signal).Label, display, number, unit,
            "Valid", freshness, now.AddSeconds(-age), age, subject, "sample");
    }

    private static SceneLayer Layer(SceneLayerKind kind, params SceneReading[] readings)
    {
        var classifier = new HealthClassifier();
        return new SceneLayer(
            kind.ToString().ToLowerInvariant(), SignalCatalog.LayerTitle(kind), kind,
            HealthClassifier.Aggregate(readings.Select(classifier.Reading)),
            LayerBuilder.Fill(kind, readings), LayerBuilder.Summary(kind, readings), readings);
    }

    private static SceneLayer Cpu(string s, double ratio, double age, DateTimeOffset now, string freshness = "Fresh") =>
        Layer(SceneLayerKind.Compute, Reading(s, SignalIds.CpuUtilization, ratio, "Ratio", null, age, now, freshness));

    private static SceneLayer Memory(string s, double used, double total, double age, DateTimeOffset now) =>
        Layer(SceneLayerKind.Memory,
            Reading(s, SignalIds.MemoryUsed, used, "Bytes", null, age, now),
            Reading(s, SignalIds.MemoryTotal, total, "Bytes", null, age, now));

    private static SceneLayer Storage(string s, double free, double total, double age, DateTimeOffset now) =>
        Layer(SceneLayerKind.Storage,
            Reading(s, SignalIds.StorageFree, free, "Bytes", null, age, now),
            Reading(s, SignalIds.StorageTotal, total, "Bytes", null, age, now));

    private static SceneLayer Power(string s, double level, bool charging, double age, DateTimeOffset now) =>
        Layer(SceneLayerKind.Power,
            Reading(s, SignalIds.BatteryLevel, level, "Ratio", null, age, now),
            Reading(s, SignalIds.BatteryCharging, null, "Boolean", charging, age, now));

    private static SceneLayer Connectivity(string s, double age, DateTimeOffset now) =>
        Layer(SceneLayerKind.Connectivity,
            Reading(s, SignalIds.NetworkConnected, null, "Boolean", true, age, now),
            Reading(s, SignalIds.NetworkVpn, null, "Boolean", true, age, now));
}
