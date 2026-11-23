using System.Windows.Input;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using MonitorApp.Client.Core.Scene;

namespace MonitorApp.Client.Ui.Workspace;

/// <summary>
/// The living schematic: devices, hosted services and typed wires drawn natively, with pan/zoom, drag with
/// snapping, selection, an exploded architecture view, touch gestures and keyboard navigation. All drawing is
/// delegated to an <see cref="ISchematicRenderer"/>; the control owns state, input and the animation loop.
/// Telemetry updates (a new <see cref="Scene"/> with the same layout) never reset the camera, dragged positions
/// or the exploded state (UI spec 22).
/// </summary>
public sealed partial class SchematicCanvas : Control
{
    public static readonly StyledProperty<SceneGraph> SceneProperty =
        AvaloniaProperty.Register<SchematicCanvas, SceneGraph>(nameof(Scene), SceneGraph.Empty);

    public static readonly StyledProperty<string?> SelectedNodeIdProperty =
        AvaloniaProperty.Register<SchematicCanvas, string?>(nameof(SelectedNodeId), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<string?> SelectedLinkIdProperty =
        AvaloniaProperty.Register<SchematicCanvas, string?>(nameof(SelectedLinkId), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    /// <summary>Node whose internals are shown exploded (two-way). Only nodes with real layers can explode.</summary>
    public static readonly StyledProperty<string?> ExplodedNodeIdProperty =
        AvaloniaProperty.Register<SchematicCanvas, string?>(nameof(ExplodedNodeId), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    /// <summary>Link kinds drawn. Null draws all; an empty collection draws none.</summary>
    public static readonly StyledProperty<IReadOnlyCollection<SceneLinkKind>?> VisibleLinkKindsProperty =
        AvaloniaProperty.Register<SchematicCanvas, IReadOnlyCollection<SceneLinkKind>?>(nameof(VisibleLinkKinds));

    public static readonly StyledProperty<bool> ReducedMotionProperty =
        AvaloniaProperty.Register<SchematicCanvas, bool>(nameof(ReducedMotion));

    public static readonly StyledProperty<bool> IsHistoricalProperty =
        AvaloniaProperty.Register<SchematicCanvas, bool>(nameof(IsHistorical));

    /// <summary>Executed with a <see cref="SceneNodeMoved"/> when the user releases a dragged node.</summary>
    public static readonly StyledProperty<ICommand?> NodeMovedCommandProperty =
        AvaloniaProperty.Register<SchematicCanvas, ICommand?>(nameof(NodeMovedCommand));

    private readonly Dictionary<string, Point> overrides = new(StringComparer.Ordinal);
    private ISchematicRenderer renderer = new FlatCardRenderer();
    private bool loopRunning;
    private TimeSpan? lastFrame;
    private bool fitPending = true;
    private bool suppressEnsureVisible;
    private double explodeTarget;
    private (Point Center, double Zoom)? savedCamera;

    static SchematicCanvas()
    {
        FocusableProperty.OverrideDefaultValue<SchematicCanvas>(true);
        ClipToBoundsProperty.OverrideDefaultValue<SchematicCanvas>(true);
        AffectsRender<SchematicCanvas>(SceneProperty, SelectedNodeIdProperty, SelectedLinkIdProperty, ReducedMotionProperty, IsHistoricalProperty, VisibleLinkKindsProperty);
    }

    public SchematicCanvas()
    {
        State.NodeSize = n => renderer.NodeSize(n);
        State.ExplodedLanding = s => renderer.ExplodedLanding(s);
    }

    public SceneGraph Scene { get => GetValue(SceneProperty); set => SetValue(SceneProperty, value); }

    public string? SelectedNodeId { get => GetValue(SelectedNodeIdProperty); set => SetValue(SelectedNodeIdProperty, value); }

    public string? SelectedLinkId { get => GetValue(SelectedLinkIdProperty); set => SetValue(SelectedLinkIdProperty, value); }

    public string? ExplodedNodeId { get => GetValue(ExplodedNodeIdProperty); set => SetValue(ExplodedNodeIdProperty, value); }

    public IReadOnlyCollection<SceneLinkKind>? VisibleLinkKinds { get => GetValue(VisibleLinkKindsProperty); set => SetValue(VisibleLinkKindsProperty, value); }

    public bool ReducedMotion { get => GetValue(ReducedMotionProperty); set => SetValue(ReducedMotionProperty, value); }

    public bool IsHistorical { get => GetValue(IsHistoricalProperty); set => SetValue(IsHistoricalProperty, value); }

    public ICommand? NodeMovedCommand { get => GetValue(NodeMovedCommandProperty); set => SetValue(NodeMovedCommandProperty, value); }

    /// <summary>Everything the renderer draws; also useful for diagnostics and tests.</summary>
    public SchematicState State { get; } = new();

    public SchematicCamera Camera => State.Camera;

    /// <summary>Replaces the look. The control keeps all state, input and animation.</summary>
    public ISchematicRenderer Renderer
    {
        get => renderer;
        set
        {
            renderer = value;
            State.NodeSize = n => renderer.NodeSize(n);
            State.ExplodedLanding = s => renderer.ExplodedLanding(s);
            renderer.UpdateTheme(this);
            State.MarkLayoutDirty();
            RequestFrame();
        }
    }

    // ---------- public commands ----------
    public void FitToContent(bool animated = true)
    {
        State.Camera.Fit(State.ContentBounds(), 40, animated && !ReducedMotion);
        RequestFrame();
    }

    public void FocusNode(string nodeId, bool animated = true)
    {
        if (!State.NodeById.ContainsKey(nodeId))
        {
            return;
        }

        var c = State.Position(nodeId);
        var zoom = Math.Max(State.Camera.Zoom, 1.0);
        if (animated && !ReducedMotion)
        {
            State.Camera.AnimateTo(c, zoom);
        }
        else
        {
            State.Camera.Jump(c, zoom);
        }

        RequestFrame();
    }

    public void ZoomBy(double factor)
    {
        State.Camera.ZoomAt(new Point(State.Size.Width / 2, State.Size.Height / 2), factor);
        State.MarkRoutesDirty();
        RequestFrame();
    }

    /// <summary>Opens or closes the exploded view of a node. Nodes without any layer just get selected.</summary>
    public void ToggleExplode(string nodeId)
    {
        SetCurrentValue(SelectedNodeIdProperty, nodeId);
        if (ExplodedNodeId == nodeId)
        {
            SetCurrentValue(ExplodedNodeIdProperty, null);
        }
        else if (State.NodeById.TryGetValue(nodeId, out var node) && ExplodedPlanner.CanExplode(node))
        {
            SetCurrentValue(ExplodedNodeIdProperty, nodeId);
        }
    }

    /// <summary>Advances animations by a time step; returns true while more frames are needed. Used by the frame loop and tests.</summary>
    public bool Advance(double seconds)
    {
        State.Seconds += seconds;
        var reduced = ReducedMotion;
        var progress = State.ExplodeProgress;
        if (reduced)
        {
            progress = explodeTarget;
        }
        else if (progress < explodeTarget)
        {
            progress = Math.Min(explodeTarget, progress + (seconds / 0.7));
        }
        else if (progress > explodeTarget)
        {
            progress = Math.Max(explodeTarget, progress - (seconds / 0.7));
        }

        var exploding = Math.Abs(progress - State.ExplodeProgress) > 1e-9 || progress != explodeTarget;
        State.ExplodeProgress = progress;
        if (progress <= 0 && explodeTarget <= 0 && State.ExplodedNodeId is not null)
        {
            State.ExplodedNodeId = null;
            State.MarkLayoutDirty();
        }

        var camera = State.Camera.Step(seconds, reduced);
        return exploding || camera || renderer.NeedsContinuousFrames(State);
    }

    /// <summary>Finishes any running animation immediately (deterministic screenshots and tests).</summary>
    public void CompleteAnimations()
    {
        State.ExplodeProgress = explodeTarget;
        while (State.Camera.Step(1, true))
        {
        }

        Advance(0);
        InvalidateVisual();
    }

    // ---------- scene and properties ----------
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SceneProperty)
        {
            OnSceneChanged((SceneGraph)change.OldValue!, (SceneGraph)change.NewValue!);
        }
        else if (change.Property == SelectedNodeIdProperty)
        {
            State.SelectedNodeId = SelectedNodeId;
            if (SelectedNodeId is not null && SelectedLinkId is not null)
            {
                SetCurrentValue(SelectedLinkIdProperty, null);
            }

            if (!suppressEnsureVisible && SelectedNodeId is { } id)
            {
                EnsureVisible(id);
            }

            State.MarkRoutesDirty();
        }
        else if (change.Property == SelectedLinkIdProperty)
        {
            State.SelectedLinkId = SelectedLinkId;
            if (SelectedLinkId is not null && SelectedNodeId is not null)
            {
                SetCurrentValue(SelectedNodeIdProperty, null);
            }
        }
        else if (change.Property == ExplodedNodeIdProperty)
        {
            OnExplodedChanged(ExplodedNodeId);
        }
        else if (change.Property == VisibleLinkKindsProperty)
        {
            State.VisibleLinkKinds = VisibleLinkKinds;
        }
        else if (change.Property == ReducedMotionProperty)
        {
            State.ReducedMotion = ReducedMotion;
        }
        else if (change.Property == IsHistoricalProperty)
        {
            State.IsHistorical = IsHistorical;
        }
        else if (change.Property == IsVisibleProperty)
        {
            if (IsEffectivelyVisible)
            {
                RequestFrame();
            }

            return;
        }
        else
        {
            return;
        }

        RequestFrame();
    }

    private void OnSceneChanged(SceneGraph oldScene, SceneGraph scene)
    {
        foreach (var key in overrides.Keys.ToList())
        {
            var gone = !scene.Layout.ContainsKey(key);
            var external = oldScene.Layout.TryGetValue(key, out var was) && scene.Layout.TryGetValue(key, out var now) && was != now;
            if (gone || external)
            {
                overrides.Remove(key);
            }
        }

        var wasEmpty = oldScene.Nodes.Count == 0;
        State.SetScene(scene, Placement);
        State.IsHistorical = IsHistorical;
        if (wasEmpty && scene.Nodes.Count > 0)
        {
            fitPending = true;
            TryInitialFit();
        }

        if (ExplodedNodeId is { } e && !State.NodeById.ContainsKey(e))
        {
            SetCurrentValue(ExplodedNodeIdProperty, null);
        }
    }

    private Point Placement(string id)
    {
        if (overrides.TryGetValue(id, out var o))
        {
            return o;
        }

        if (Scene.Layout.TryGetValue(id, out var p))
        {
            return new Point(p.X, p.Y);
        }

        var index = Scene.Nodes.ToList().FindIndex(n => n.Id == id);
        return new Point(index % 4 * 2.0, index / 4 * 1.6);
    }

    private void TryInitialFit()
    {
        if (fitPending && State.Size.Width > 20 && State.Nodes.Count > 0)
        {
            State.Camera.Fit(State.ContentBounds(), 40, false);
            fitPending = false;
        }
    }

    private void EnsureVisible(string id)
    {
        if (!State.NodeById.ContainsKey(id) || State.Size.Width < 20)
        {
            return;
        }

        var r = State.ScreenRect(id);
        var inner = new Rect(State.Size).Deflate(30);
        if (inner.Contains(r.TopLeft) && inner.Contains(r.BottomRight))
        {
            return;
        }

        if (ReducedMotion)
        {
            State.Camera.Jump(State.Position(id), State.Camera.Zoom);
        }
        else
        {
            State.Camera.AnimateTo(State.Position(id), State.Camera.Zoom);
        }

        savedCamera = null;
    }

    private void OnExplodedChanged(string? id)
    {
        if (id is not null && State.NodeById.TryGetValue(id, out var node) && ExplodedPlanner.CanExplode(node))
        {
            savedCamera ??= (State.Camera.Center, State.Camera.Zoom);
            State.ExplodedNodeId = id;
            explodeTarget = 1;
            var c = State.Position(id);
            var zoom = Math.Max(State.Camera.Zoom, State.Size.Width < 600 ? 1.25 : 1.35);
            var target = new Point(c.X + (State.Size.Width < 600 ? 0 : 0.5), c.Y - 0.2);
            if (ReducedMotion)
            {
                State.Camera.Jump(target, zoom);
            }
            else
            {
                State.Camera.AnimateTo(target, zoom);
            }
        }
        else
        {
            explodeTarget = 0;
            if (id is not null)
            {
                SetCurrentValue(ExplodedNodeIdProperty, null);
            }

            if (savedCamera is { } saved)
            {
                if (ReducedMotion)
                {
                    State.Camera.Jump(saved.Center, saved.Zoom);
                }
                else
                {
                    State.Camera.AnimateTo(saved.Center, saved.Zoom);
                }

                savedCamera = null;
            }
        }

        State.MarkLayoutDirty();
    }

    // ---------- layout ----------
    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 400 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 300 : availableSize.Height);

    protected override Size ArrangeOverride(Size finalSize)
    {
        State.Size = finalSize;
        State.Camera.Viewport = finalSize;
        State.Camera.Unit = finalSize.Width < 600 ? 64 : 100;
        State.MarkRoutesDirty();
        TryInitialFit();
        RequestFrame();
        return finalSize;
    }

    // ---------- attach / animation loop ----------
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        renderer.UpdateTheme(this);
        ActualThemeVariantChanged += OnThemeChanged;
        RequestFrame();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ActualThemeVariantChanged -= OnThemeChanged;
        loopRunning = false;
        lastFrame = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        renderer.UpdateTheme(this);
        InvalidateVisual();
    }

    /// <summary>Starts the frame loop if it is not running (only while attached and visible) and repaints.</summary>
    private void RequestFrame()
    {
        InvalidateVisual();
        if (loopRunning || !IsEffectivelyVisible || TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        loopRunning = true;
        top.RequestAnimationFrame(OnFrame);
    }

    private void OnFrame(TimeSpan time)
    {
        loopRunning = false;
        if (!IsEffectivelyVisible || TopLevel.GetTopLevel(this) is not { } top)
        {
            lastFrame = null;
            return;
        }

        var dt = lastFrame is { } last ? Math.Clamp((time - last).TotalSeconds, 0, 0.05) : 0;
        lastFrame = time;
        var more = Advance(dt);
        InvalidateVisual();
        if (more)
        {
            loopRunning = true;
            top.RequestAnimationFrame(OnFrame);
        }
        else
        {
            lastFrame = null;
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        State.Size = Bounds.Size;
        renderer.Render(context, State);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new SchematicAutomationPeer(this);

    internal string AutomationSummary => SchematicStrings.Summary(Scene, SelectedNodeId);

    private sealed class SchematicAutomationPeer(SchematicCanvas owner) : ControlAutomationPeer(owner)
    {
        protected override string? GetNameCore() =>
            string.IsNullOrEmpty(base.GetNameCore()) ? owner.AutomationSummary : base.GetNameCore();

        protected override string GetClassNameCore() => "Schematic";
    }

    private void RaiseNodeMoved(string id)
    {
        var p = State.Position(id);
        var moved = new SceneNodeMoved(id, new ScenePoint(p.X, p.Y));
        if (NodeMovedCommand is { } command && command.CanExecute(moved))
        {
            command.Execute(moved);
        }
    }

    internal void CommitOverride(string id, Point world) => overrides[id] = world;
}
