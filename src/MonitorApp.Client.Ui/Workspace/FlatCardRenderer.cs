using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using MonitorApp.Client.Core.Scene;

namespace MonitorApp.Client.Ui.Workspace;

/// <summary>
/// The flat 2.5D card look: dot grid, device cards with a small picture and key facts, hosted-service capsules in
/// dashed regions, typed orthogonal wires with pulses, and an isometric exploded view. Colours come from the theme
/// tokens when present and fall back to the approved graphite palette.
/// </summary>
public sealed class FlatCardRenderer : ISchematicRenderer
{
    private enum Dash { Solid, Wire, Dotted, Down, Group, Fine, Short }

    private enum Role { Lit, Mid, Dark, Screen, Vent }

    private readonly record struct Face(Role Role, Point[] Points);

    private readonly record struct PenKey(uint Color, int Width, Dash Dash);

    private readonly record struct TextKey(string Text, int Size, uint Color, bool Mono, bool Medium, int MaxWidth);

    private Color ink = Color.Parse("#0c0e11");
    private Color s1 = Color.Parse("#121519");
    private Color s2 = Color.Parse("#171b20");
    private Color s3 = Color.Parse("#1e232a");
    private Color line = Color.Parse("#2a3038");
    private Color lineStrong = Color.Parse("#353c46");
    private Color text = Color.Parse("#e4e7eb");
    private Color textDim = Color.Parse("#8b939e");
    private Color textFaint = Color.Parse("#5c646f");
    private Color healthy = Color.Parse("#43c17e");
    private Color transfer = Color.Parse("#38b6e8");
    private Color warning = Color.Parse("#e9a93a");
    private Color fault = Color.Parse("#e5534b");
    private Color muted = Color.Parse("#6b737d");
    private readonly Color vpn = Color.Parse("#5a8fb0");

    private FontFamily sans = new("Inter, Segoe UI, Roboto, Noto Sans, DejaVu Sans, Liberation Sans, sans-serif");
    private FontFamily mono = new("JetBrains Mono, Cascadia Mono, Consolas, DejaVu Sans Mono, Liberation Mono, monospace");

    private readonly Dictionary<uint, SolidColorBrush> brushes = [];
    private readonly Dictionary<PenKey, Pen> pens = [];
    private readonly Dictionary<TextKey, FormattedText> texts = [];
    private readonly Dictionary<Silhouette, List<(Geometry Geometry, Role Role)>> pictures = [];
    private Geometry? triangle;

    public Size NodeSize(SceneNode node) => node.Kind == SceneNodeKind.Service ? new Size(1.3, 0.5) : new Size(1.8, 1.0);

    public bool NeedsContinuousFrames(SchematicState state)
    {
        if (state.ReducedMotion)
        {
            return false;
        }

        if (state.Scene.Links.Any(l => state.IsLinkVisible(l) && PulsePlanner.ShouldPulse(l, HealthOf(state, l.FromId), HealthOf(state, l.ToId))))
        {
            return true;
        }

        return state.Nodes.Any(n => SchematicModel.IsFailed(n) || n.Activity > 0);
    }

    private static SceneHealth HealthOf(SchematicState s, string id) => s.NodeById.TryGetValue(id, out var n) ? n.Health : SceneHealth.Unknown;

    public void UpdateTheme(Control host)
    {
        Color Res(string key, Color fallback)
        {
            if (host.TryFindResource(key, host.ActualThemeVariant, out var v))
            {
                return v switch { Color c => c, ISolidColorBrush b => b.Color, _ => fallback };
            }

            return fallback;
        }

        ink = Res("Color.Ink", ink);
        s1 = Res("Color.Surface1", s1);
        s2 = Res("Color.Surface2", s2);
        s3 = Res("Color.Surface3", s3);
        line = Res("Color.Line", line);
        lineStrong = Res("Color.LineStrong", lineStrong);
        text = Res("Color.Text", text);
        textDim = Res("Color.TextSecondary", textDim);
        textFaint = Res("Color.TextMuted", textFaint);
        healthy = Res("Color.Healthy", healthy);
        transfer = Res("Color.Transfer", transfer);
        warning = Res("Color.Warning", warning);
        fault = Res("Color.Fault", fault);
        muted = Res("Color.Muted", muted);
        if (host.TryFindResource("Font.Sans", host.ActualThemeVariant, out var f1) && f1 is FontFamily ff1)
        {
            sans = ff1;
        }

        if (host.TryFindResource("Font.Mono", host.ActualThemeVariant, out var f2) && f2 is FontFamily ff2)
        {
            mono = ff2;
        }

        brushes.Clear();
        pens.Clear();
        texts.Clear();
    }

    // ---------- caches ----------
    private SolidColorBrush B(Color c, double alpha = 1)
    {
        var a = alpha >= 1 ? c : Color.FromArgb((byte)Math.Clamp(c.A * alpha, 0, 255), c.R, c.G, c.B);
        var key = a.ToUInt32();
        if (!brushes.TryGetValue(key, out var b))
        {
            b = new SolidColorBrush(a);
            brushes[key] = b;
        }

        return b;
    }

    private Pen P(Color c, double width, Dash dash = Dash.Solid, double alpha = 1)
    {
        var a = alpha >= 1 ? c : Color.FromArgb((byte)Math.Clamp(c.A * alpha, 0, 255), c.R, c.G, c.B);
        var key = new PenKey(a.ToUInt32(), (int)Math.Round(width * 10), dash);
        if (!pens.TryGetValue(key, out var pen))
        {
            double[]? d = dash switch
            {
                Dash.Wire => [8, 5],
                Dash.Dotted => [1.5, 4.5],
                Dash.Down => [2, 6],
                Dash.Group => [4, 4],
                Dash.Fine => [1.5, 1.5],
                Dash.Short => [3, 3],
                _ => null,
            };
            pen = new Pen(B(a), width, d is null ? null : new DashStyle(d.Select(x => x / width), 0), PenLineCap.Round, PenLineJoin.Round);
            pens[key] = pen;
        }

        return pen;
    }

    private FormattedText T(string value, double size, Color color, bool monoFont = false, bool medium = false, double maxWidth = double.PositiveInfinity)
    {
        var key = new TextKey(value, (int)Math.Round(size * 10), color.ToUInt32(), monoFont, medium,
            double.IsInfinity(maxWidth) ? -1 : (int)Math.Round(maxWidth));
        if (!texts.TryGetValue(key, out var ft))
        {
            if (texts.Count > 700)
            {
                texts.Clear();
            }

            ft = new FormattedText(value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(monoFont ? mono : sans, FontStyle.Normal, medium ? FontWeight.Medium : FontWeight.Normal), size, B(color));
            if (!double.IsInfinity(maxWidth))
            {
                ft.MaxTextWidth = Math.Max(1, maxWidth);
                ft.MaxLineCount = 1;
                ft.Trimming = TextTrimming.CharacterEllipsis;
            }

            texts[key] = ft;
        }

        return ft;
    }

    /// <summary>Draws text with its vertical centre at y. align: 0 left, 1 centre, 2 right of x. Returns the width.</summary>
    private double Txt(DrawingContext g, string value, double x, double y, double size, Color color, int align = 0,
        bool monoFont = false, bool medium = false, double maxWidth = double.PositiveInfinity)
    {
        var ft = T(value, size, color, monoFont, medium, maxWidth);
        var left = align switch { 1 => x - (ft.Width / 2), 2 => x - ft.Width, _ => x };
        g.DrawText(ft, new Point(left, y - (ft.Height / 2)));
        return ft.Width;
    }

    private Color StatusColor(SceneNode n) => SchematicModel.IsFailed(n) ? fault : n.Health switch
    {
        SceneHealth.Healthy => healthy,
        SceneHealth.Degraded => warning,
        SceneHealth.Stale => muted,
        _ => textFaint,
    };

    // ---------- frame ----------
    public void Render(DrawingContext context, SchematicState state) => RenderCore(context, state);

    private void RenderCore(DrawingContext g, SchematicState st)
    {
        var size = st.Size;
        g.DrawRectangle(B(s1), null, new Rect(size));
        st.EnsureRoutes();
        var scale = st.Camera.Scale;
        DrawGrid(g, st);
        foreach (var guide in st.Guides)
        {
            var a = guide.Vertical ? st.Camera.ToScreen(new Point(guide.Value, 0)).X : st.Camera.ToScreen(new Point(0, guide.Value)).Y;
            g.DrawLine(P(transfer, 1, Dash.Short, 0.55), guide.Vertical ? new Point(a, 0) : new Point(0, a), guide.Vertical ? new Point(a, size.Height) : new Point(size.Width, a));
        }

        var focus = st.FocusSet();
        var exploded = st.ExplodeProgress > 0.01 ? st.ExplodedNodeId : null;
        DrawGroups(g, st, focus);
        foreach (var route in st.Routes)
        {
            if (st.IsLinkVisible(route.Link))
            {
                DrawLink(g, st, route, focus);
            }
        }

        foreach (var node in st.Nodes)
        {
            if (node.Id == exploded)
            {
                continue;
            }

            var r = st.ScreenRect(node.Id);
            if (r.Right < -20 || r.Bottom < -20 || r.X > size.Width + 20 || r.Y > size.Height + 20)
            {
                continue;
            }

            DrawCard(g, st, node, r, Dim(st, focus, node.Id));
        }

        if (exploded is not null && st.NodeById.TryGetValue(exploded, out var en))
        {
            DrawExploded(g, st, en, Dim(st, focus, en.Id));
        }

        if (st.ShowFocusRing && st.SelectedNodeId is null && st.SelectedLinkId is null)
        {
            g.DrawRectangle(null, P(transfer, 1.5, Dash.Short), new Rect(3, 3, size.Width - 6, size.Height - 6));
        }

        if (st.IsHistorical)
        {
            g.DrawRectangle(null, P(warning, 2, Dash.Solid, 0.6), new Rect(1, 1, size.Width - 2, size.Height - 2));
        }

        _ = scale;
    }

    private static double Dim(SchematicState st, HashSet<string>? focus, string id)
    {
        if (st.ExplodedNodeId is not null && st.ExplodeProgress > 0.3 && id != st.ExplodedNodeId)
        {
            return 0.18;
        }

        return focus is not null && !focus.Contains(id) ? 0.32 : 1;
    }

    private void DrawGrid(DrawingContext g, SchematicState st)
    {
        var cam = st.Camera;
        var s = cam.Scale;
        var o = cam.ToScreen(new Point(0, 0));
        var w = st.Size.Width;
        var h = st.Size.Height;
        var step = s / 4;
        var minor = B(Colors.White, 0.06);
        var major = B(Colors.White, 0.13);
        if (step >= 10 && (w / step) * (h / step) < 9000)
        {
            for (var x = Mod(o.X, step); x < w; x += step)
            {
                for (var y = Mod(o.Y, step); y < h; y += step)
                {
                    g.DrawRectangle(minor, null, new Rect(x - 0.5, y - 0.5, 1, 1));
                }
            }
        }

        if (s >= 12)
        {
            for (var x = Mod(o.X, s); x < w; x += s)
            {
                for (var y = Mod(o.Y, s); y < h; y += s)
                {
                    g.DrawRectangle(major, null, new Rect(x - 1, y - 1, 2, 2));
                }
            }
        }
    }

    private static double Mod(double a, double b) => ((a % b) + b) % b;

    // ---------- groups ----------
    private void DrawGroups(DrawingContext g, SchematicState st, HashSet<string>? focus)
    {
        foreach (var (parent, kids) in st.Children)
        {
            var rects = kids.Select(k => st.ScreenRect(k)).ToList();
            if (rects.Count == 0)
            {
                continue;
            }

            var box = rects.Aggregate((a, b) => a.Union(b));
            var pad = Math.Max(10, 0.18 * st.Camera.Scale);
            var region = new Rect(box.X - pad, box.Y - pad - 14, box.Width + (pad * 2), box.Height + (pad * 2) + 14);
            var dim = focus is not null && !kids.Any(focus.Contains) ? 0.35 : 1;
            using (g.PushOpacity(dim))
            {
                g.DrawRectangle(B(Colors.White, 0.018), P(lineStrong, 1, Dash.Group), region, 8, 8);
                var label = SchematicStrings.HostedOn(st.NodeById[parent].Label).ToUpperInvariant();
                Txt(g, label, region.X + 8, region.Y + 9, 10, textFaint, 0, true, false, region.Width - 12);
            }
        }
    }

    // ---------- links ----------
    private void DrawLink(DrawingContext g, SchematicState st, LinkRoute route, HashSet<string>? focus)
    {
        var link = route.Link;
        var pts = route.Points;
        if (pts.Count < 2)
        {
            return;
        }

        st.NodeById.TryGetValue(link.FromId, out var from);
        st.NodeById.TryGetValue(link.ToId, out var to);
        var fh = from?.Health ?? SceneHealth.Unknown;
        var th = to?.Health ?? SceneHealth.Unknown;
        var down = fh == SceneHealth.Offline || th == SceneHealth.Offline;
        var stale = fh == SceneHealth.Stale || th == SceneHealth.Stale;
        var degraded = fh == SceneHealth.Degraded || th == SceneHealth.Degraded || link.Health == SceneHealth.Degraded;
        var col = link.Kind switch
        {
            SceneLinkKind.Replication => degraded ? warning : vpn,
            SceneLinkKind.Dependency => to is not null && SchematicModel.IsFailed(to) ? fault : muted,
            _ => muted,
        };
        if (down)
        {
            col = textFaint;
        }

        var selected = st.SelectedLinkId == link.Id;
        var hover = st.HoverLinkId == link.Id;
        var dim = Math.Min(Dim(st, focus, link.FromId), Dim(st, focus, link.ToId)) * (stale ? 0.5 : 1);
        var dash = down ? Dash.Down : link.Kind == SceneLinkKind.Replication ? Dash.Wire : Dash.Solid;
        var width = 1.4 * (selected || hover ? 1.8 : 1);
        var radius = Math.Max(6, 0.12 * st.Camera.Scale);
        using var _ = g.PushOpacity(dim);
        g.DrawGeometry(null, P(col, width, dash), RoundedPath(pts, radius));
        var active = link.Activity > 0 && !down;
        foreach (var port in new[] { route.Ports.From.Position, route.Ports.To.Position })
        {
            g.DrawEllipse(B(s1), P(col, 1.4), port, 3.6, 3.6);
            if (active)
            {
                g.DrawEllipse(B(col), null, port, 1.6, 1.6);
            }
        }

        if (link.Kind == SceneLinkKind.Dependency)
        {
            var m = SchematicGeometry.Along(pts, 0.55);
            var matrix = Matrix.CreateRotation(m.Angle) * Matrix.CreateTranslation(m.Position.X, m.Position.Y);
            using (g.PushTransform(matrix))
            {
                var pen = P(col, 1.5);
                g.DrawLine(pen, new Point(-3, -4), new Point(2, 0));
                g.DrawLine(pen, new Point(2, 0), new Point(-3, 4));
            }
        }

        if (PulsePlanner.ShouldPulse(link, fh, th))
        {
            var n = PulsePlanner.Count(link.Activity);
            var pulse = link.Kind == SceneLinkKind.Replication ? (degraded ? warning : transfer) : textDim;
            for (var i = 0; i < n; i++)
            {
                var f = PulsePlanner.Fraction(i, n, st.Seconds, link.Activity, st.ReducedMotion);
                var q = SchematicGeometry.Along(pts, f).Position;
                if (!st.ReducedMotion)
                {
                    g.DrawEllipse(B(pulse, 0.25), null, q, 4.5, 4.5);
                }

                g.DrawEllipse(B(pulse), null, q, 2.2, 2.2);
            }
        }

        var level = st.Level;
        if (selected || hover || (level == SemanticLevel.Device && dim >= 1))
        {
            var m = SchematicGeometry.Along(pts, 0.5).Position;
            var label = link.Description.Length > 60 && !(selected || hover) ? link.Description[..57] + "…" : link.Description;
            var ft = T(label, 10.5, selected ? text : textDim, true, false, 260);
            g.DrawRectangle(B(s1, 0.9), null, new Rect(m.X - (ft.Width / 2) - 5, m.Y - 19, ft.Width + 10, 15));
            g.DrawText(ft, new Point(m.X - (ft.Width / 2), m.Y - 18));
        }
    }

    private static StreamGeometry RoundedPath(IReadOnlyList<Point> pts, double radius)
    {
        var geometry = new StreamGeometry();
        using var c = geometry.Open();
        c.BeginFigure(pts[0], false);
        for (var i = 1; i < pts.Count - 1; i++)
        {
            Point p0 = pts[i - 1], p1 = pts[i], p2 = pts[i + 1];
            double l1 = SchematicGeometry.Distance(p0, p1), l2 = SchematicGeometry.Distance(p1, p2);
            var r = Math.Min(radius, Math.Min(l1, l2) / 2);
            var d1 = new Vector((p1.X - p0.X) / l1, (p1.Y - p0.Y) / l1);
            var d2 = new Vector((p2.X - p1.X) / l2, (p2.Y - p1.Y) / l2);
            c.LineTo(new Point(p1.X - (d1.X * r), p1.Y - (d1.Y * r)));
            var cross = (d1.X * d2.Y) - (d1.Y * d2.X);
            c.ArcTo(new Point(p1.X + (d2.X * r), p1.Y + (d2.Y * r)), new Size(r, r), 0, false,
                cross > 0 ? SweepDirection.Clockwise : SweepDirection.CounterClockwise);
        }

        c.LineTo(pts[^1]);
        c.EndFigure(false);
        return geometry;
    }

    // ---------- cards ----------
    private void DrawCard(DrawingContext g, SchematicState st, SceneNode node, Rect r, double dim)
    {
        var S = st.Camera.Scale;
        var level = st.Level;
        var failed = SchematicModel.IsFailed(node);
        var off = node.Health == SceneHealth.Offline;
        var sil = SchematicModel.SilhouetteOf(node);
        var svc = sil == Silhouette.Service;
        var selected = st.SelectedNodeId == node.Id;
        var hover = st.HoverNodeId == node.Id;
        var rad = Math.Max(4, 0.08 * S);
        var alpha = dim * (node.Health == SceneHealth.Stale ? 0.75 : 1);
        using var _ = g.PushOpacity(alpha);
        var border = selected ? transfer : hover ? textDim : lineStrong;
        g.DrawRectangle(B(s2), P(border, selected ? 1.8 : 1.1, off ? Dash.Short : Dash.Solid), r, rad, rad);
        var hh = svc ? r.Height : Math.Max(16, 0.26 * S);
        if (!svc)
        {
            g.DrawRectangle(B(s3), null, new RoundedRect(new Rect(r.X + 1, r.Y + 1, r.Width - 2, hh - 1), rad, rad, 0, 0));
            g.DrawLine(P(line, 1), new Point(r.X, r.Y + hh), new Point(r.Right, r.Y + hh));
        }

        var fs = Math.Max(10, Math.Min(14, 0.15 * S));
        var iconX = r.X + Math.Max(8, 0.1 * S);
        DrawKindIcon(g, sil, iconX + 5, r.Y + (hh / 2), Math.Max(9, 0.12 * S), textDim);
        var nameX = iconX + Math.Max(14, 0.2 * S);
        Txt(g, node.Label, nameX, r.Y + (hh / 2), fs, off ? textDim : text, 0, false, true, Math.Max(10, r.Right - nameX - Math.Max(20, 0.2 * S)));
        DrawStatusMark(g, st, node, r.Right - Math.Max(10, 0.13 * S), r.Y + (hh / 2), Math.Max(4, 0.055 * S), failed);

        if (!svc && level != SemanticLevel.Overview)
        {
            var bx = r.X + 7;
            var by = r.Y + hh + 6;
            var bh = r.Height - hh - 12;
            var bw = Math.Min(bh * 1.3, r.Width * 0.38);
            if (bh > 8)
            {
                g.DrawRectangle(B(ink), null, new Rect(bx, by, bw, bh), 4, 4);
                DrawPicture(g, node, sil, bx + (bw / 2), by + (bh / 2), Math.Min(bw, bh * 1.3) * 0.82, off, st);
                var facts = SchematicModel.Facts(node, SemanticZoom.FactCount(level));
                var fx = bx + bw + 10;
                var fw = r.Right - 8 - fx;
                var lh = Math.Min(bh / Math.Max(2, facts.Count), fs * 1.45);
                if (fw > 28)
                {
                    for (var i = 0; i < facts.Count; i++)
                    {
                        var y = by + (lh * (i + 0.5)) + ((bh - (lh * facts.Count)) / 2);
                        var w = Txt(g, facts[i].Value, fx, y, fs - 1, off || node.Health == SceneHealth.Stale ? textDim : text, 0, true, true, fw);
                        if (fw - w > 30)
                        {
                            Txt(g, facts[i].Label, fx + w + 5, y, fs - 3, textFaint, 0, false, false, fw - w - 5);
                        }
                    }
                }
            }
        }
        else if (svc && level != SemanticLevel.Overview && r.Width > 110)
        {
            var status = node.Layers.SelectMany(l => l.Readings).FirstOrDefault(x => x.SignalId == "service.status")?.DisplayValue;
            if (status is not null)
            {
                Txt(g, status.ToLowerInvariant(), r.Right - Math.Max(22, 0.3 * S), r.Y + (r.Height / 2), fs - 3, failed ? fault : textDim, 2, true);
            }
        }

        if (!svc && level != SemanticLevel.Overview && node.Health != SceneHealth.Healthy)
        {
            Txt(g, node.HealthReason, r.X + 2, r.Bottom + (fs * 0.9), fs - 3, StatusColor(node), 0, true, false, r.Width * 1.3);
        }

        if (level == SemanticLevel.Device)
        {
            foreach (var route in st.Routes)
            {
                if (!st.IsLinkVisible(route.Link))
                {
                    continue;
                }

                var isFrom = route.Link.FromId == node.Id;
                if (!isFrom && route.Link.ToId != node.Id)
                {
                    continue;
                }

                var port = isFrom ? route.Ports.From : route.Ports.To;
                var otherId = isFrom ? route.Link.ToId : route.Link.FromId;
                if (!st.NodeById.TryGetValue(otherId, out var other))
                {
                    continue;
                }

                var (dx, dy, al) = port.Side switch
                {
                    PortSide.Left => (-7.0, -8.0, 2),
                    PortSide.Right => (7.0, -8.0, 0),
                    PortSide.Top => (0.0, -9.0, 1),
                    _ => (0.0, 10.0, 1),
                };
                Txt(g, "→ " + other.Label, port.Position.X + dx, port.Position.Y + dy, 9.5, textFaint, al, true, false, 120);
            }
        }

        if (selected && st.ShowFocusRing)
        {
            g.DrawRectangle(null, P(transfer, 1.2, Dash.Short), r.Inflate(6));
        }
    }

    private void DrawKindIcon(DrawingContext g, Silhouette k, double x, double y, double s, Color color)
    {
        var pen = P(color, 1.3);
        switch (k)
        {
            case Silhouette.Server:
                for (var i = 0; i < 3; i++)
                {
                    g.DrawRectangle(null, pen, new Rect(x - (s * 0.55), y - (s * 0.5) + (i * s * 0.35), s * 1.1, s * 0.27));
                }

                break;
            case Silhouette.Laptop:
                g.DrawRectangle(null, pen, new Rect(x - (s * 0.42), y - (s * 0.45), s * 0.84, s * 0.6));
                g.DrawLine(pen, new Point(x - (s * 0.6), y + (s * 0.3)), new Point(x + (s * 0.6), y + (s * 0.3)));
                break;
            case Silhouette.Phone:
                g.DrawRectangle(null, pen, new Rect(x - (s * 0.25), y - (s * 0.55), s * 0.5, s * 1.1), 2, 2);
                break;
            case Silhouette.Tablet:
                g.DrawRectangle(null, pen, new Rect(x - (s * 0.45), y - (s * 0.55), s * 0.9, s * 1.1), 2, 2);
                break;
            case Silhouette.Tower:
                g.DrawRectangle(null, pen, new Rect(x - (s * 0.3), y - (s * 0.55), s * 0.6, s * 1.1));
                g.DrawLine(pen, new Point(x - (s * 0.15), y - (s * 0.3)), new Point(x + (s * 0.15), y - (s * 0.3)));
                break;
            case Silhouette.Service:
                g.DrawRectangle(null, pen, new Rect(x - (s * 0.45), y - (s * 0.45), s * 0.9, s * 0.9), 2, 2);
                g.DrawLine(pen, new Point(x - (s * 0.2), y), new Point(x + (s * 0.2), y));
                break;
            default:
                g.DrawEllipse(null, pen, new Point(x, y), s * 0.5, s * 0.5);
                break;
        }
    }

    private void DrawStatusMark(DrawingContext g, SchematicState st, SceneNode node, double x, double y, double r, bool failed)
    {
        var col = StatusColor(node);
        var pen = P(col, 1.3);
        switch (node.Health)
        {
            case SceneHealth.Healthy:
                g.DrawEllipse(B(col), null, new Point(x, y), r * 0.6, r * 0.6);
                break;
            case SceneHealth.Degraded:
                if (failed && !st.ReducedMotion)
                {
                    var p = (Math.Sin(st.Seconds * 3) + 1) / 2;
                    g.DrawEllipse(null, P(col, 1.3, Dash.Solid, 0.2 + (0.35 * p)), new Point(x, y), r * 2, r * 2);
                }
                else if (failed)
                {
                    g.DrawEllipse(null, P(col, 1.3, Dash.Solid, 0.4), new Point(x, y), r * 2, r * 2);
                }

                triangle ??= Triangle();
                using (g.PushTransform(Matrix.CreateScale(r, r) * Matrix.CreateTranslation(x, y)))
                {
                    g.DrawGeometry(B(col), null, triangle);
                }

                g.DrawRectangle(B(ink), null, new Rect(x - 0.7, y - (r * 0.35), 1.4, r * 0.6));
                g.DrawRectangle(B(ink), null, new Rect(x - 0.7, y + (r * 0.38), 1.4, 1.4));
                break;
            case SceneHealth.Stale:
                g.DrawEllipse(B(s3), pen, new Point(x, y), r * 0.85, r * 0.85);
                g.DrawLine(pen, new Point(x, y - (r * 0.45)), new Point(x, y));
                g.DrawLine(pen, new Point(x, y), new Point(x + (r * 0.35), y + (r * 0.2)));
                break;
            case SceneHealth.Offline:
                g.DrawEllipse(B(s3), P(col, 1.3, Dash.Fine), new Point(x, y), r * 0.85, r * 0.85);
                g.DrawLine(pen, new Point(x - (r * 0.35), y - (r * 0.35)), new Point(x + (r * 0.35), y + (r * 0.35)));
                g.DrawLine(pen, new Point(x + (r * 0.35), y - (r * 0.35)), new Point(x - (r * 0.35), y + (r * 0.35)));
                break;
            default:
                g.DrawEllipse(null, P(col, 1.3, Dash.Fine), new Point(x, y), r * 0.85, r * 0.85);
                break;
        }
    }

    private static StreamGeometry Triangle()
    {
        var geometry = new StreamGeometry();
        using var c = geometry.Open();
        c.BeginFigure(new Point(0, -1.05), true);
        c.LineTo(new Point(1, 0.8));
        c.LineTo(new Point(-1, 0.8));
        c.EndFigure(true);
        return geometry;
    }

    // ---------- pictures ----------
    private static Color RoleColor(Role role, bool off) => role switch
    {
        Role.Lit => off ? Color.Parse("#3a3f47") : Color.Parse("#5b6572"),
        Role.Mid => off ? Color.Parse("#2a2f36") : Color.Parse("#3c444f"),
        Role.Dark => Color.Parse("#22272e"),
        Role.Screen => off ? Color.Parse("#14181d") : Color.Parse("#0f2733"),
        _ => Color.Parse("#1a1e24"),
    };

    private static Face[] Faces(Silhouette k)
    {
        static Point[] Q(params double[] v) => Enumerable.Range(0, v.Length / 2).Select(i => new Point(v[2 * i], v[(2 * i) + 1])).ToArray();
        switch (k)
        {
            case Silhouette.Server:
                var server = new List<Face>
                {
                    new(Role.Lit, Q(-.45, -.12, 0, -.32, .45, -.12, 0, .08)),
                    new(Role.Mid, Q(-.45, -.12, 0, .08, 0, .3, -.45, .1)),
                    new(Role.Dark, Q(0, .08, .45, -.12, .45, .1, 0, .3)),
                };
                for (var i = 0; i < 4; i++)
                {
                    var yy = .11 + (i * .045);
                    server.Add(new Face(Role.Vent, Q(-.41, yy - .02, -.04, yy + .15, -.04, yy + .17, -.41, yy)));
                }

                return [.. server];
            case Silhouette.Laptop:
                return
                [
                    new(Role.Mid, Q(-.42, -.05, .05, -.28, .42, -.1, -.05, .13)),
                    new(Role.Dark, Q(-.42, -.05, -.05, .13, -.05, .17, -.42, -.01)),
                    new(Role.Lit, Q(-.05, .13, .42, -.1, .42, -.42, -.05, -.2)),
                    new(Role.Screen, Q(-.01, .07, .38, -.12, .38, -.38, -.01, -.18)),
                ];
            case Silhouette.Phone:
            case Silhouette.Tablet:
                var w = k == Silhouette.Phone ? .2 : .34;
                return
                [
                    new(Role.Lit, Q(-w, -.05, 0, -.05 - (w * .6), w * 1.1, 0, 0, w * .6)),
                    new(Role.Dark, Q(-w, -.05, 0, w * .6, 0, (w * .6) + .03, -w, -.02)),
                    new(Role.Screen, Q(-w * .8, -.05, 0, -.05 - (w * .48), w * .9, 0, 0, w * .48)),
                ];
            case Silhouette.Tower:
                return
                [
                    new(Role.Lit, Q(-.2, -.38, .05, -.48, .28, -.4, .03, -.3)),
                    new(Role.Mid, Q(-.2, -.38, .03, -.3, .03, .32, -.2, .24)),
                    new(Role.Dark, Q(.03, -.3, .28, -.4, .28, .22, .03, .32)),
                    new(Role.Vent, Q(-.16, -.26, -.01, -.2, -.01, -.15, -.16, -.21)),
                ];
            default:
                return
                [
                    new(Role.Lit, Q(-.45, 0, 0, -.2, .45, 0, 0, .2)),
                    new(Role.Mid, Q(-.45, 0, 0, .2, 0, .28, -.45, .08)),
                    new(Role.Dark, Q(0, .2, .45, 0, .45, .08, 0, .28)),
                ];
        }
    }

    private void DrawPicture(DrawingContext g, SceneNode node, Silhouette sil, double x, double y, double s, bool off, SchematicState st)
    {
        if (!pictures.TryGetValue(sil, out var list))
        {
            list = [];
            foreach (var face in Faces(sil))
            {
                var geo = new StreamGeometry();
                using (var c = geo.Open())
                {
                    c.BeginFigure(face.Points[0], true);
                    foreach (var p in face.Points.Skip(1))
                    {
                        c.LineTo(p);
                    }

                    c.EndFigure(true);
                }

                list.Add((geo, face.Role));
            }

            pictures[sil] = list;
        }

        using (g.PushTransform(Matrix.CreateScale(s, s) * Matrix.CreateTranslation(x, y)))
        {
            foreach (var (geo, role) in list)
            {
                g.DrawGeometry(B(RoleColor(role, off)), null, geo);
            }
        }

        // Dynamic details only from real data: LEDs blink only when the node reports activity; battery bar is the real level.
        var blinking = !st.ReducedMotion && node.Activity > 0;
        if (sil == Silhouette.Server && !off)
        {
            for (var i = 0; i < 3; i++)
            {
                var on = !blinking || (((st.Seconds * 3) + i) % 1) < 0.35;
                g.DrawRectangle(B(on ? transfer : Color.Parse("#24546a")), null, new Rect(x + ((-.36 + (i * .1)) * s), y + ((.06 + (i * .045)) * s), 2, 2));
            }
        }

        if (sil is Silhouette.Phone or Silhouette.Tablet && !off && node.Layers.FirstOrDefault(l => l.Kind == SceneLayerKind.Power)?.Fill is { } level)
        {
            g.DrawRectangle(B(level < 0.2 ? warning : healthy), null, new Rect(x - (.06 * s), y - (.07 * s), Math.Max(1, .12 * s * level), 3));
        }
    }

    // ---------- exploded ----------
    private static (double W, double D, double H) Dims(Silhouette s) => s switch
    {
        Silhouette.Phone => (1.0, 1.9, 0.14),
        Silhouette.Tablet => (1.6, 2.0, 0.14),
        Silhouette.Laptop => (2.4, 1.6, 0.2),
        _ => (2.4, 1.5, 0.28),
    };

    private static double Ease(double x) => 1 - Math.Pow(1 - Math.Clamp(x, 0, 1), 3);

    private static IsoProjector Iso(SchematicState st, SceneNode node)
    {
        var r = st.ScreenRect(node.Id);
        return new IsoProjector(new Point(r.Center.X, r.Center.Y + (r.Height * 0.25)), st.Camera.Scale * 0.5);
    }

    public Point? ExplodedLanding(SchematicState state)
    {
        if (state.ExplodedNodeId is not { } id || !state.NodeById.TryGetValue(id, out var node)
            || !ExplodedPlanner.Plan(node).Any(c => c.Part == ExplodedPart.Nic))
        {
            return null;
        }

        var (w, d, _) = Dims(SchematicModel.SilhouetteOf(node));
        return Iso(state, node).Project((w / 2) - 0.35, (-d / 2) - 0.3, 0.25);
    }

    private void Poly(DrawingContext g, Color fill, Color stroke, params Point[] pts)
    {
        var geo = new StreamGeometry();
        using (var c = geo.Open())
        {
            c.BeginFigure(pts[0], true);
            foreach (var p in pts.Skip(1))
            {
                c.LineTo(p);
            }

            c.EndFigure(true);
        }

        g.DrawGeometry(B(fill), P(stroke, 1), geo);
    }

    private Point Box(DrawingContext g, IsoProjector iso, double x, double y, double z, double w, double d, double h,
        Color top, Color left, Color right, Color? stroke = null)
    {
        var sk = stroke ?? Color.FromArgb(128, 0, 0, 0);
        Point p0 = iso.Project(x, y, z + h), p1 = iso.Project(x + w, y, z + h), p2 = iso.Project(x + w, y + d, z + h), p3 = iso.Project(x, y + d, z + h);
        Point p4 = iso.Project(x, y + d, z), p5 = iso.Project(x + w, y + d, z), p6 = iso.Project(x + w, y, z);
        Poly(g, top, sk, p0, p1, p2, p3);
        Poly(g, left, sk, p3, p2, p5, p4);
        Poly(g, right, sk, p2, p1, p6, p5);
        return iso.Project(x + (w / 2), y + (d / 2), z + h);
    }

    private void DrawExploded(DrawingContext g, SchematicState st, SceneNode node, double dim)
    {
        var plan = ExplodedPlanner.Plan(node);
        if (plan.Count == 0)
        {
            return;
        }

        var S = st.Camera.Scale;
        var e = Ease(st.ExplodeProgress);
        var iso = Iso(st, node);
        var (W, D, H) = Dims(SchematicModel.SilhouetteOf(node));
        var hasBattery = plan.Any(c => c.Part == ExplodedPart.Battery);
        var hasBoard = plan.Any(c => c.Part == ExplodedPart.Board);
        var bx1 = (W / 2) - 0.1 - (hasBattery && hasBoard ? 0.6 : 0);
        var anchors = new List<(Point Anchor, ExplodedComponent Part)>();
        var hex = (string s) => Color.Parse(s);
        using var _ = g.PushOpacity(Math.Min(1, st.ExplodeProgress * 1.4) * dim);
        foreach (var c in plan)
        {
            var lift = c.Lift * e;
            var bz = H + (0.55 * e);
            switch (c.Part)
            {
                case ExplodedPart.Chassis:
                    Box(g, iso, -W / 2, -D / 2, 0, W, D, H, hex("#2b3139"), hex("#20252c"), hex("#262b33"));
                    for (var i = 0; i < 6; i++)
                    {
                        var vx = (-W / 2) + 0.15 + (i * (W - 0.3) / 6);
                        Poly(g, hex("#15191e"), Colors.Transparent, iso.Project(vx, D / 2, H * 0.25), iso.Project(vx + 0.15, D / 2, H * 0.25),
                            iso.Project(vx + 0.15, D / 2, H * 0.7), iso.Project(vx, D / 2, H * 0.7));
                    }

                    break;
                case ExplodedPart.Nic:
                    var nic = iso.Project((W / 2) - 0.35, (-D / 2) - 0.3, 0.25);
                    Box(g, iso, (W / 2) - 0.5, (-D / 2) - 0.02 - (0.3 * e), 0.06 + (0.1 * e), 0.3, 0.08, 0.14, hex("#3a414b"), hex("#2a3038"), hex("#323842"));
                    g.DrawEllipse(B(c.Layer!.Health == SceneHealth.Healthy ? healthy : textFaint), null, nic, 2.6, 2.6);
                    anchors.Add((nic, c));
                    break;
                case ExplodedPart.Radio:
                    var top = Box(g, iso, (-W / 2) + 0.2, (D / 2) - 0.55, H + lift, 0.4, 0.3, 0.08, hex("#2f4a5a"), hex("#25394a"), hex("#2b4150"));
                    g.DrawLine(P(textDim, 1.5), top, new Point(top.X, top.Y - (S * 0.25)));
                    anchors.Add((new Point(top.X, top.Y - (S * 0.25)), c));
                    break;
                case ExplodedPart.Board:
                    Box(g, iso, (-W / 2) + 0.1, (-D / 2) + 0.1, bz, bx1 + (W / 2) - 0.1, D - 0.2, 0.05, hex("#1f3a32"), hex("#18302a"), hex("#1c352e"), Color.FromArgb(64, 67, 193, 126));
                    break;
                case ExplodedPart.Battery:
                    var x0 = hasBoard ? bx1 + 0.08 : (-W / 2) + 0.12;
                    var w0 = hasBoard ? (W / 2) - 0.1 - x0 : W - 0.24;
                    var bz2 = H + (0.25 * e);
                    var bh = 0.32;
                    var tp = Box(g, iso, x0, (-D / 2) + 0.2, bz2, w0, D - 0.5, bh, hex("#2a323c"), hex("#202730"), hex("#262d36"));
                    if (c.Layer!.Fill is { } fill)
                    {
                        Box(g, iso, x0 + 0.04, (-D / 2) + 0.24, bz2 + 0.02, w0 - 0.08, D - 0.58, Math.Max(0.02, (bh - 0.04) * fill),
                            fill < 0.2 ? warning : healthy, Color.FromArgb(200, 40, 110, 70), Color.FromArgb(200, 50, 140, 90), Colors.Transparent);
                    }

                    anchors.Add((tp, c));
                    break;
                case ExplodedPart.Memory:
                    var rz = bz + 0.05 + (0.55 * e);
                    var ram = Box(g, iso, 0.1, (-D / 2) + 0.25, rz, 0.12, 0.75, 0.2, hex("#3b4a7a"), hex("#2c3860"), hex("#33406c"));
                    anchors.Add((ram, c));
                    break;
                case ExplodedPart.Cpu:
                    var cz = bz + 0.05 + (0.3 * e);
                    Box(g, iso, -0.95, -0.35, cz, 0.75, 0.7, 0.12, hex("#3a3f47"), hex("#2b3037"), hex("#33383f"));
                    var sink = Box(g, iso, -0.92, -0.32, cz + 0.12, 0.69, 0.64, 0.28, hex("#8c95a1"), hex("#6c7480"), hex("#7b8490"));
                    var finPen = P(s2, 1, Dash.Solid, 0.7);
                    for (var i = 1; i < 7; i++)
                    {
                        g.DrawLine(finPen, iso.Project(-0.92 + (i * 0.1), -0.32, cz + 0.4), iso.Project(-0.92 + (i * 0.1), 0.32, cz + 0.4));
                    }

                    anchors.Add((sink, c));
                    break;
                case ExplodedPart.Drive:
                    var sy = (D / 2) - 0.05 + (c.Slide * e);
                    var sled = Box(g, iso, -0.45, sy - 0.25, H, 0.9, 0.55, 0.12, hex("#3a414b"), hex("#2a3038"), hex("#323842"));
                    var ledCol = c.Layer!.Health == SceneHealth.Healthy ? healthy : warning;
                    var led = iso.Project(-0.37, sy + 0.32, H + 0.06);
                    g.DrawRectangle(B(ledCol), null, new Rect(led.X - 4, led.Y - 1.5, 10, 3));
                    if (c.Layer.Fill is { } used)
                    {
                        Box(g, iso, -0.4, sy - 0.2, H + 0.12, 0.8 * used, 0.1, 0.02, transfer, hex("#24546a"), hex("#24546a"), Colors.Transparent);
                    }

                    anchors.Add((sled, c));
                    break;
            }
        }

        if (e > 0.6)
        {
            using var __ = g.PushOpacity((e - 0.6) / 0.4);
            DrawExplodedLabels(g, st, anchors, iso.Origin);
            var title = iso.Project((-W / 2) - 0.2, (-D / 2) - 0.2, H + 2.6);
            Txt(g, SchematicStrings.ExplodedTitle(node), title.X, title.Y, 13, text, 1, false, true);
            Txt(g, SchematicStrings.ExplodedHint, title.X, title.Y + 15, 10.5, textFaint, 1, true);
        }
    }

    private void DrawExplodedLabels(DrawingContext g, SchematicState st, List<(Point Anchor, ExplodedComponent Part)> anchors, Point origin)
    {
        var reach = Math.Max(60, st.Camera.Scale * 0.9);
        foreach (var side in new[] { -1, 1 })
        {
            var group = anchors.Where(a => (a.Anchor.X < origin.X ? -1 : 1) == side).ToList();
            if (group.Count == 0)
            {
                continue;
            }

            var ys = LabelLayout.Spread(group.Select(a => a.Anchor.Y).ToList(), 54, 24, st.Size.Height - 48);
            for (var i = 0; i < group.Count; i++)
            {
                var (anchor, comp) = group[i];
                var layer = comp.Layer!;
                var maxX = group.Where(a => true).Max(a => side > 0 ? a.Anchor.X : -a.Anchor.X);
                var x2 = side > 0 ? Math.Max(anchor.X, origin.X) + reach : Math.Min(anchor.X, origin.X) - reach;
                _ = maxX;
                var ly = ys[i];
                g.DrawLine(P(lineStrong, 1), anchor, new Point(anchor.X + (side * 12), ly));
                g.DrawLine(P(lineStrong, 1), new Point(anchor.X + (side * 12), ly), new Point(x2, ly));
                g.DrawEllipse(B(textDim), null, anchor, 2, 2);
                var tx = x2 + (side * 4);
                var al = side > 0 ? 0 : 2;
                var room = side > 0 ? st.Size.Width - tx - 6 : tx - 6;
                Txt(g, layer.Title.ToUpperInvariant(), tx, ly - 6, 10, textFaint, al, true, false, room);
                Txt(g, layer.Summary, tx, ly + 8, 12, layer.Health == SceneHealth.Healthy ? text : warning, al, true, true, room);
                var extra = 0;
                foreach (var reading in layer.Readings)
                {
                    if (layer.Summary.Contains(reading.DisplayValue, StringComparison.Ordinal) || extra >= 2)
                    {
                        continue;
                    }

                    Txt(g, $"{reading.Label} {reading.DisplayValue}", tx, ly + 21 + (extra * 12), 10, textDim, al, true, false, room);
                    extra++;
                }

                if (layer.Fill is { } f)
                {
                    var barW = Math.Min(80, room);
                    var bxx = al == 0 ? tx : tx - barW;
                    var by = ly + 21 + (extra * 12) + 3;
                    g.DrawRectangle(B(s3), null, new Rect(bxx, by, barW, 3), 1.5, 1.5);
                    g.DrawRectangle(B(f > 0.8 ? warning : transfer), null, new Rect(bxx, by, Math.Max(1, barW * f), 3), 1.5, 1.5);
                }
            }
        }
    }
}
