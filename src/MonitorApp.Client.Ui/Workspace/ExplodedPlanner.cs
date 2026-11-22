using MonitorApp.Client.Core.Scene;

namespace MonitorApp.Client.Ui.Workspace;

public enum ExplodedPart
{
    Chassis,
    Nic,
    Radio,
    Board,
    Battery,
    Memory,
    Cpu,
    Drive,
}

/// <summary>One drawn part of an exploded node. <see cref="Layer"/> is the real layer that justifies it (null only for the chassis and board).</summary>
/// <param name="Lift">How far (iso units) the part rises when exploded.</param>
/// <param name="Slide">How far (iso units) the part slides out toward the front when exploded.</param>
public sealed record ExplodedComponent(ExplodedPart Part, SceneLayer? Layer, double Lift, double Slide);

/// <summary>
/// Decides which components an exploded node shows. Only layers the node really reports produce a component;
/// a node with only power and network layers yields a battery and a network card, never a processor. The chassis
/// and the mainboard are structural: the board is drawn only when a processor or memory layer needs one to sit on.
/// Returned in paint order (back to front).
/// </summary>
public static class ExplodedPlanner
{
    public static IReadOnlyList<ExplodedComponent> Plan(SceneNode node)
    {
        SceneLayer? Layer(SceneLayerKind kind) => node.Layers.FirstOrDefault(l => l.Kind == kind);
        var compute = Layer(SceneLayerKind.Compute);
        var memory = Layer(SceneLayerKind.Memory);
        var storage = Layer(SceneLayerKind.Storage);
        var network = Layer(SceneLayerKind.Network);
        var power = Layer(SceneLayerKind.Power);
        var radio = Layer(SceneLayerKind.Connectivity);
        if (compute is null && memory is null && storage is null && network is null && power is null && radio is null)
        {
            return [];
        }

        var parts = new List<ExplodedComponent> { new(ExplodedPart.Chassis, null, 0, 0) };
        if (network is not null)
        {
            parts.Add(new(ExplodedPart.Nic, network, 0.15, 0));
        }

        if (radio is not null)
        {
            parts.Add(new(ExplodedPart.Radio, radio, 0.9, 0));
        }

        if (compute is not null || memory is not null)
        {
            parts.Add(new(ExplodedPart.Board, null, 0.55, 0));
        }

        if (power is not null)
        {
            parts.Add(new(ExplodedPart.Battery, power, 0.45, 0));
        }

        if (memory is not null)
        {
            parts.Add(new(ExplodedPart.Memory, memory, 1.1, 0));
        }

        if (compute is not null)
        {
            parts.Add(new(ExplodedPart.Cpu, compute, 1.1, 0));
        }

        if (storage is not null)
        {
            parts.Add(new(ExplodedPart.Drive, storage, 0, 1.3));
        }

        return parts;
    }

    public static bool CanExplode(SceneNode node) => Plan(node).Count > 0;
}
