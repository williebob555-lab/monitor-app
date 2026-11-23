using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MonitorApp.Client.Core.Scene;

namespace MonitorApp.Client.Core.ViewModels.Shell;

/// <summary>One search hit as shown in the results list.</summary>
public sealed record SearchResultRow(SceneSearchResult Result, string KindText, string Text)
{
    public string AccessibleText => $"{KindText}: {Text}";
}

/// <summary>Global search over the current scene (<see cref="SceneSearch"/>), with jump-to-node and jump-to-metric.</summary>
public sealed partial class SearchViewModel : ObservableObject
{
    private readonly EnvironmentViewModel environment;

    [ObservableProperty]
    private string query = string.Empty;

    [ObservableProperty]
    private SearchResultRow? selectedResult;

    [ObservableProperty]
    private string status = "Type to search devices, services, layers and readings.";

    public SearchViewModel(EnvironmentViewModel environment)
    {
        this.environment = environment;
        environment.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(EnvironmentViewModel.Scene))
            {
                Run();
            }
        };
    }

    public ObservableCollection<SearchResultRow> Results { get; } = [];

    public bool HasQuery => !string.IsNullOrWhiteSpace(Query);

    /// <summary>Raised after a jump so the shell can leave the search page and show the object.</summary>
    public event EventHandler<SceneSearchResult>? Jumped;

    partial void OnQueryChanged(string value)
    {
        OnPropertyChanged(nameof(HasQuery));
        Run();
    }

    /// <summary>Selects the hit's node (and remembers the layer and metric so the inspector can emphasise them).</summary>
    [RelayCommand]
    public void JumpTo(SearchResultRow? row)
    {
        if (row is null)
        {
            return;
        }

        environment.SelectedNodeId = row.Result.NodeId;
        Jumped?.Invoke(this, row.Result);
    }

    [RelayCommand]
    public void Clear() => Query = string.Empty;

    private void Run()
    {
        Results.Clear();
        SelectedResult = null;
        if (!HasQuery)
        {
            Status = "Type to search devices, services, layers and readings.";
            return;
        }

        var hits = SceneSearch.Find(environment.Scene, Query);
        foreach (var hit in hits.Take(50))
        {
            Results.Add(new SearchResultRow(hit, hit.Kind switch
            {
                SceneSearchKind.Node => "Object",
                SceneSearchKind.Layer => "Layer",
                _ => "Reading",
            }, hit.DisplayText));
        }

        Status = hits.Count == 0
            ? $"No results for \"{Query.Trim()}\"."
            : hits.Count > Results.Count
                ? $"Showing {Results.Count} of {hits.Count} results."
                : hits.Count == 1 ? "1 result." : $"{hits.Count} results.";
    }
}
