using Avalonia;
using MonitorApp.Client.Core.Scene;

namespace MonitorApp.Client.Ui.Workspace;

/// <summary>Which picture represents a node. Derived only from what the node reports.</summary>
public enum Silhouette
{
    Server,
    Tower,
    Laptop,
    Phone,
    Tablet,
    Generic,
    Service,
}

public readonly record struct Fact(string Value, string Label);

public static class SchematicModel
{
    public static Silhouette SilhouetteOf(SceneNode node)
    {
        if (node.Kind == SceneNodeKind.Service)
        {
            return Silhouette.Service;
        }

        var readings = node.Layers.SelectMany(l => l.Readings).ToList();
        var label = node.Label;
        var android = readings.Any(r => r.SignalId.StartsWith("android.", StringComparison.Ordinal));
        if (android)
        {
            return Has(label, "tablet", "pad") ? Silhouette.Tablet : Silhouette.Phone;
        }

        var power = node.Layers.Any(l => l.Kind == SceneLayerKind.Power);
        var network = node.Layers.Any(l => l.Kind == SceneLayerKind.Network);
        if (node.Kind == SceneNodeKind.Device)
        {
            return Silhouette.Generic;
        }

        if (power && network)
        {
            return Silhouette.Laptop;
        }

        if (Has(label, "laptop", "notebook"))
        {
            return Silhouette.Laptop;
        }

        if (Has(label, "server", "nas"))
        {
            return Silhouette.Server;
        }

        if (Has(label, "box", "tower", "desktop", "pc"))
        {
            return Silhouette.Tower;
        }

        return node.Kind == SceneNodeKind.LocalPeer ? Silhouette.Tower : Silhouette.Server;
    }

    private static bool Has(string label, params string[] words) =>
        words.Any(w => label.Contains(w, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when the node reports a failure (a service.status reading of Failed/Error), as opposed to merely degraded.</summary>
    public static bool IsFailed(SceneNode node) =>
        node.Health == SceneHealth.Degraded
        && node.Layers.SelectMany(l => l.Readings).Any(r =>
            r.SignalId == "service.status"
            && (r.DisplayValue.Equals("Failed", StringComparison.OrdinalIgnoreCase)
                || r.DisplayValue.Equals("Error", StringComparison.OrdinalIgnoreCase)));

    /// <summary>Key facts from the node's layers, most meaningful first (processor, storage, power, memory, link).</summary>
    public static IReadOnlyList<Fact> Facts(SceneNode node, int max)
    {
        var result = new List<Fact>(max);
        if (max <= 0 || node.Kind == SceneNodeKind.Service)
        {
            return result;
        }

        SceneLayerKind[] order =
        [
            SceneLayerKind.Compute, SceneLayerKind.Storage, SceneLayerKind.Power, SceneLayerKind.Memory,
            SceneLayerKind.Connectivity, SceneLayerKind.Network,
        ];
        foreach (var kind in order)
        {
            SceneLayer? layer = null;
            foreach (var l in node.Layers)
            {
                if (l.Kind == kind)
                {
                    layer = l;
                    break;
                }
            }

            if (layer is null)
            {
                continue;
            }

            string? value = layer.Fill is { } fill
                ? SignalCatalog.Percent(fill)
                : (layer.Readings.Count > 0 ? layer.Readings[0].DisplayValue : null);
            if (value is null)
            {
                continue;
            }

            result.Add(new Fact(value, FactLabel(kind)));
            if (result.Count == max)
            {
                break;
            }
        }

        return result;
    }

    public static string FactLabel(SceneLayerKind kind) => kind switch
    {
        SceneLayerKind.Compute => SchematicStrings.FactCpu,
        SceneLayerKind.Memory => SchematicStrings.FactMemory,
        SceneLayerKind.Storage => SchematicStrings.FactDisk,
        SceneLayerKind.Power => SchematicStrings.FactBattery,
        SceneLayerKind.Network => SchematicStrings.FactNetwork,
        _ => SchematicStrings.FactLink,
    };

    /// <summary>Stable keyboard order: rows top to bottom (one world unit tall), left to right within a row.</summary>
    public static IReadOnlyList<string> KeyboardOrder(SceneGraph scene, Func<string, Point> position) =>
        scene.Nodes
            .Select(n => (n.Id, P: position(n.Id)))
            .OrderBy(t => Math.Round(t.P.Y))
            .ThenBy(t => t.P.X)
            .ThenBy(t => t.Id, StringComparer.Ordinal)
            .Select(t => t.Id)
            .ToList();
}

/// <summary>All user-visible text of the schematic in one place, ready for localisation.</summary>
public static class SchematicStrings
{
    public const string FactCpu = "cpu";
    public const string FactMemory = "memory";
    public const string FactDisk = "disk";
    public const string FactBattery = "battery";
    public const string FactNetwork = "network";
    public const string FactLink = "link";

    public static string HostedOn(string parent) => $"Hosted on {parent}";

    public static string Health(SceneNode node) => SchematicModel.IsFailed(node) ? "Failed" : Health(node.Health);

    public static string Health(SceneHealth health) => health switch
    {
        SceneHealth.Healthy => "Healthy",
        SceneHealth.Degraded => "Degraded",
        SceneHealth.Stale => "Stale",
        SceneHealth.Offline => "Offline",
        _ => "Unknown",
    };

    public static string Summary(SceneGraph scene, string? selectedNodeId)
    {
        if (scene.Nodes.Count == 0)
        {
            return "Ecosystem schematic, no objects";
        }

        var selected = scene.Find(selectedNodeId);
        var text = $"Ecosystem schematic, {scene.Nodes.Count} objects and {scene.Links.Count} connections. Arrow keys move between objects, Enter opens, Escape closes.";
        return selected is null ? text : $"{text} Selected: {selected.Label}, {Health(selected)}.";
    }

    public static string NodeName(SceneNode node) => $"{node.Label}, {Health(node)}. {node.HealthReason}";

    public const string ExplodedHint = "double-click or Esc to reassemble";

    public static string ExplodedTitle(SceneNode node) => $"{node.Label} · exploded";
}
