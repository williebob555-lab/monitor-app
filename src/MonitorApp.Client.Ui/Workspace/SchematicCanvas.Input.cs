using Avalonia;
using Avalonia.Input;
using MonitorApp.Client.Core.Scene;

namespace MonitorApp.Client.Ui.Workspace;

public sealed partial class SchematicCanvas
{
    private const double DragThreshold = 4;
    private static readonly TimeSpan DoubleTap = TimeSpan.FromMilliseconds(350);

    private readonly Dictionary<int, Point> pointers = [];
    private DownState? down;
    private (double Distance, Point Mid)? pinch;
    private DateTime lastTap = DateTime.MinValue;

    /// <summary>Clock used for double-tap detection; tests replace it so slow rendering cannot split a double tap.</summary>
    public TimeProvider Clock { get; set; } = TimeProvider.System;
    private string? lastTapNode;
    private Point lastPointer;

    private sealed class DownState(Point start, string? node, string? link)
    {
        public Point Start { get; } = start;

        public string? Node { get; } = node;

        public string? Link { get; } = link;

        public bool Moved { get; set; }

        public bool Panning { get; set; }

        public Dictionary<string, Point> Dragged { get; } = new(StringComparer.Ordinal);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var props = e.GetCurrentPoint(this).Properties;
        if (!props.IsLeftButtonPressed && !props.IsMiddleButtonPressed)
        {
            return;
        }

        var pos = e.GetPosition(this);
        pointers[e.Pointer.Id] = pos;
        lastPointer = pos;
        e.Pointer.Capture(this);
        if (pointers.Count == 2)
        {
            BeginPinch();
            down = null;
            e.Handled = true;
            return;
        }

        var (node, link) = props.IsMiddleButtonPressed ? (null, null) : State.Hit(pos);
        var now = Clock.GetUtcNow().UtcDateTime;
        if (node is not null && node == lastTapNode && now - lastTap < DoubleTap)
        {
            lastTapNode = null;
            ToggleExplode(node);
            down = null;
            e.Handled = true;
            return;
        }

        lastTap = now;
        lastTapNode = node;
        down = new DownState(pos, node, link);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pos = e.GetPosition(this);
        if (pointers.ContainsKey(e.Pointer.Id))
        {
            pointers[e.Pointer.Id] = pos;
        }

        if (pinch is not null && pointers.Count == 2)
        {
            UpdatePinch();
            return;
        }

        if (down is null)
        {
            UpdateHover(pos);
            return;
        }

        var delta = pos - down.Start;
        if (!down.Moved && Math.Sqrt((delta.X * delta.X) + (delta.Y * delta.Y)) > DragThreshold)
        {
            down.Moved = true;
            if (down.Node is { } id && id != State.ExplodedNodeId)
            {
                BeginNodeDrag(down, id);
            }
            else
            {
                down.Panning = true;
            }
        }

        if (!down.Moved)
        {
            return;
        }

        if (down.Panning)
        {
            State.Camera.Pan(pos - lastPointer);
            savedCamera = null;
            State.MarkRoutesDirty();
        }
        else if (down.Node is { } primary)
        {
            DragNode(down, primary, delta);
        }

        lastPointer = pos;
        RequestFrame();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        pointers.Remove(e.Pointer.Id);
        if (pinch is not null)
        {
            if (pointers.Count < 2)
            {
                pinch = null;
            }

            down = null;
            return;
        }

        FinishPointer();
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        pointers.Remove(e.Pointer.Id);
        pinch = null;
        FinishPointer();
    }

    private void FinishPointer()
    {
        if (down is not { } d)
        {
            return;
        }

        down = null;
        if (!d.Moved)
        {
            suppressEnsureVisible = true;
            try
            {
                if (d.Node is { } n)
                {
                    SetCurrentValue(SelectedNodeIdProperty, n);
                }
                else if (d.Link is { } l)
                {
                    SetCurrentValue(SelectedLinkIdProperty, l);
                }
                else
                {
                    SetCurrentValue(SelectedNodeIdProperty, null);
                    SetCurrentValue(SelectedLinkIdProperty, null);
                }
            }
            finally
            {
                suppressEnsureVisible = false;
            }
        }
        else if (d.Dragged.Count > 0)
        {
            foreach (var id in d.Dragged.Keys)
            {
                CommitOverride(id, State.Position(id));
                RaiseNodeMoved(id);
            }
        }

        State.Guides = [];
        RequestFrame();
    }

    private void BeginNodeDrag(DownState d, string id)
    {
        d.Dragged[id] = State.Position(id);
        if (State.Children.TryGetValue(id, out var kids))
        {
            foreach (var k in kids)
            {
                d.Dragged[k] = State.Position(k);
            }
        }

        suppressEnsureVisible = true;
        SetCurrentValue(SelectedNodeIdProperty, id);
        suppressEnsureVisible = false;
    }

    private void DragNode(DownState d, string primary, Vector screenDelta)
    {
        var s = State.Camera.Scale;
        var start = d.Dragged[primary];
        var proposed = new Point(start.X + (screenDelta.X / s), start.Y + (screenDelta.Y / s));
        var others = State.Nodes.Where(n => !d.Dragged.ContainsKey(n.Id)).Select(n => State.Position(n.Id));
        var snap = SchematicSnapper.Snap(proposed, others, 8 / s);
        var shift = snap.Position - start;
        foreach (var (id, origin) in d.Dragged)
        {
            State.SetPosition(id, origin + shift);
        }

        State.Guides = snap.Guides;
    }

    private void UpdateHover(Point pos)
    {
        var (node, link) = State.Hit(pos);
        if (node != State.HoverNodeId || link != State.HoverLinkId)
        {
            State.HoverNodeId = node;
            State.HoverLinkId = link;
            Cursor = node is not null || link is not null ? new Cursor(StandardCursorType.Hand) : null;
            RequestFrame();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (State.HoverNodeId is not null || State.HoverLinkId is not null)
        {
            State.HoverNodeId = null;
            State.HoverLinkId = null;
            RequestFrame();
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        State.Camera.ZoomAt(e.GetPosition(this), Math.Pow(1.15, e.Delta.Y));
        savedCamera = null;
        State.MarkRoutesDirty();
        RequestFrame();
        e.Handled = true;
    }

    private void BeginPinch()
    {
        var pts = pointers.Values.ToArray();
        pinch = (SchematicGeometry.Distance(pts[0], pts[1]), Mid(pts));
    }

    private void UpdatePinch()
    {
        var pts = pointers.Values.ToArray();
        if (pts.Length < 2 || pinch is not { } p)
        {
            return;
        }

        var dist = SchematicGeometry.Distance(pts[0], pts[1]);
        var mid = Mid(pts);
        if (p.Distance > 1)
        {
            State.Camera.ZoomAt(mid, dist / p.Distance);
        }

        State.Camera.Pan(mid - p.Mid);
        pinch = (dist, mid);
        savedCamera = null;
        State.MarkRoutesDirty();
        RequestFrame();
    }

    private static Point Mid(Point[] pts) => new((pts[0].X + pts[1].X) / 2, (pts[0].Y + pts[1].Y) / 2);

    // ---------- keyboard ----------
    protected override void OnGotFocus(GotFocusEventArgs e)
    {
        base.OnGotFocus(e);
        State.ShowFocusRing = e.NavigationMethod is NavigationMethod.Tab or NavigationMethod.Directional;
        RequestFrame();
    }

    protected override void OnLostFocus(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        State.ShowFocusRing = false;
        RequestFrame();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        State.ShowFocusRing = true;
        switch (e.Key)
        {
            case Key.Right or Key.Down:
                Step(1);
                break;
            case Key.Left or Key.Up:
                Step(-1);
                break;
            case Key.Enter when SelectedNodeId is { } id:
                ToggleExplode(id);
                break;
            case Key.Escape:
                if (ExplodedNodeId is not null)
                {
                    SetCurrentValue(ExplodedNodeIdProperty, null);
                }
                else
                {
                    SetCurrentValue(SelectedNodeIdProperty, null);
                    SetCurrentValue(SelectedLinkIdProperty, null);
                }

                break;
            case Key.Add or Key.OemPlus:
                ZoomBy(1.25);
                break;
            case Key.Subtract or Key.OemMinus:
                ZoomBy(0.8);
                break;
            case Key.D0 or Key.NumPad0:
                FitToContent();
                break;
            default:
                return;
        }

        e.Handled = true;
        RequestFrame();
    }

    private void Step(int direction)
    {
        var order = SchematicModel.KeyboardOrder(Scene, State.Position);
        if (order.Count == 0)
        {
            return;
        }

        var i = SelectedNodeId is null ? -1 : order.ToList().IndexOf(SelectedNodeId);
        var next = i < 0 ? (direction > 0 ? 0 : order.Count - 1) : (i + direction + order.Count) % order.Count;
        SetCurrentValue(SelectedNodeIdProperty, order[next]);
    }
}
