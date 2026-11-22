namespace MonitorApp.Client.Core.Scene;

/// <summary>What a search result points at.</summary>
public enum SceneSearchKind
{
    Node,
    Layer,
    Metric,
}

/// <summary>One search hit. <see cref="SignalId"/> is set for metric hits, <see cref="LayerId"/> for layer and metric hits.</summary>
public sealed record SceneSearchResult(
    string NodeId,
    string? LayerId,
    string? SignalId,
    SceneSearchKind Kind,
    string DisplayText,
    int Score);

/// <summary>
/// Search over node labels, layer titles and reading labels or signal ids. Every whitespace-separated term must
/// match (case-insensitive substring) in the node label, layer title, or reading label/signal id of the
/// candidate, so "laptop cpu" finds the CPU reading of the node labelled "Laptop". Ranking: metric hits where
/// terms are split across node and reading first (most specific), then exact label matches, prefix matches,
/// substring matches; ties break by display text for a stable order.
/// </summary>
public static class SceneSearch
{
    public static IReadOnlyList<SceneSearchResult> Find(SceneGraph graph, string? query)
    {
        var terms = (query ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0)
        {
            return [];
        }

        var results = new List<SceneSearchResult>();
        foreach (var node in graph.Nodes)
        {
            var nodeScore = Score(terms, node.Label, null, null);
            if (nodeScore > 0)
            {
                results.Add(new SceneSearchResult(node.Id, null, null, SceneSearchKind.Node, node.Label, nodeScore + 100));
            }

            foreach (var layer in node.Layers)
            {
                var layerScore = Score(terms, node.Label, layer.Title, null);
                if (layerScore > 0)
                {
                    results.Add(new SceneSearchResult(
                        node.Id, layer.Id, null, SceneSearchKind.Layer, $"{node.Label} · {layer.Title}", layerScore + 50));
                }

                foreach (var reading in layer.Readings)
                {
                    var score = Score(terms, node.Label, layer.Title, reading.Label + " " + reading.SignalId);
                    if (score > 0)
                    {
                        results.Add(new SceneSearchResult(
                            node.Id, layer.Id, reading.SignalId, SceneSearchKind.Metric,
                            $"{node.Label} · {reading.Label}: {reading.DisplayValue}", score + 150));
                    }
                }
            }
        }

        return results
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.DisplayText, StringComparer.Ordinal)
            .ThenBy(r => r.NodeId, StringComparer.Ordinal)
            .ToList();
    }

    // Terms must each be found somewhere in the combined haystack. Score rewards a term matching a field whole
    // or at its start, and metric-level matches that involve the reading label itself.
    private static int Score(string[] terms, string node, string? layer, string? reading)
    {
        var fields = new[] { node, layer, reading }.Where(f => f is not null).Cast<string>().ToArray();
        var total = 0;
        var ownMatch = fields.Length == 1;
        foreach (var term in terms)
        {
            var best = 0;
            foreach (var field in fields)
            {
                var own = field != node;
                if (field.Equals(term, StringComparison.OrdinalIgnoreCase))
                {
                    best = Math.Max(best, 30);
                    ownMatch |= own;
                }
                else if (field.StartsWith(term, StringComparison.OrdinalIgnoreCase))
                {
                    best = Math.Max(best, 20);
                    ownMatch |= own;
                }
                else if (field.Contains(term, StringComparison.OrdinalIgnoreCase))
                {
                    best = Math.Max(best, 10);
                    ownMatch |= own;
                }
            }

            if (best == 0)
            {
                return 0;
            }

            total += best;
        }

        // A layer or metric hit must be about the layer or reading itself, not only its node.
        return ownMatch ? total : 0;
    }
}
