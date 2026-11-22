using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MonitorApp.Client.Core.ViewModels;
using MonitorApp.Client.Core.ViewModels.Shell;

namespace MonitorApp.Client.Ui.Views;

/// <summary>
/// The workspace shell (UI spec 3, 19): navigation rail or bottom bar, thin toolbar, centre workspace, contextual
/// inspector (docked, overlay or bottom sheet by width) and the timeline strip. DataContext is a <see cref="MainViewModel"/>;
/// everything on screen is bound through <see cref="MainViewModel.Shell"/> and the section view models.
/// </summary>
/// <remarks>
/// Plugging in the schematic canvas: set <see cref="WorkspaceTemplate"/> to a data template whose data is the
/// <see cref="EnvironmentViewModel"/>. It replaces the outline inside the ecosystem page's <c>WorkspaceHost</c>
/// ContentControl, and the outline moves to a side panel so it remains the keyboard and screen-reader alternative.
/// </remarks>
public partial class WorkspaceShell : UserControl, IDisposable
{
    /// <summary>How often the ecosystem is re-read when the source cannot push changes.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    /// <summary>Styled so a head can set it from markup or code.</summary>
    public static readonly StyledProperty<IDataTemplate?> WorkspaceTemplateProperty =
        AvaloniaProperty.Register<WorkspaceShell, IDataTemplate?>(nameof(WorkspaceTemplate));

    private CancellationTokenSource? live;
    private SearchViewModel? hookedSearch;
    private TopLevel? keyHost;

    public WorkspaceShell()
    {
        InitializeComponent();
        SearchBox.LostFocus += OnSearchBoxLostFocus;
        SearchBox.KeyDown += OnSearchBoxKeyDown;
    }

    /// <summary>The schematic canvas template (DataContext: EnvironmentViewModel); null shows the outline alone.</summary>
    public IDataTemplate? WorkspaceTemplate
    {
        get => GetValue(WorkspaceTemplateProperty);
        set => SetValue(WorkspaceTemplateProperty, value);
    }

    /// <summary>Keep the ecosystem current while the shell is on screen. Tests and screenshot tools switch it off to stay deterministic.</summary>
    public bool RunEnvironmentLive { get; set; } = true;

    /// <summary>The layout class from the current width.</summary>
    public ShellLayoutClass LayoutClass { get; private set; } = ShellLayoutClass.Wide;

    /// <summary>True while the phone layout (bottom bar, bottom-sheet inspector) is active.</summary>
    public bool IsCompact => LayoutClass == ShellLayoutClass.Narrow;

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        ApplyLayout(e.NewSize.Width);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Shortcuts work even when nothing inside the shell has focus, so listen on the window.
        keyHost = TopLevel.GetTopLevel(this);
        keyHost?.AddHandler(KeyDownEvent, OnKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        ApplyLayout(Bounds.Width);
        Model?.Shell.Inspector.SetLiveViewingAllowed(true);
        StartLive();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        StopLive();
        Model?.Shell.Inspector.SetLiveViewingAllowed(false);
        keyHost?.RemoveHandler(KeyDownEvent, OnKeyDown);
        keyHost = null;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (hookedSearch is not null)
        {
            hookedSearch.Jumped -= OnSearchJumped;
            hookedSearch = null;
        }

        if (DataContext is MainViewModel model)
        {
            hookedSearch = model.Shell.Search;
            hookedSearch.Jumped += OnSearchJumped;
            model.Shell.PropertyChanged += OnShellChanged;
            ApplyLayout(Bounds.Width);
        }

        if (this.GetVisualRoot() is not null)
        {
            StartLive();
        }
    }

    /// <summary>Stops the live loop; also done on detach.</summary>
    public void Dispose()
    {
        StopLive();
        Model?.Shell.Inspector.SetLiveViewingAllowed(false);
        GC.SuppressFinalize(this);
    }

    private MainViewModel? Model => DataContext as MainViewModel;

    private void StartLive()
    {
        StopLive();
        if (!RunEnvironmentLive || Model is not { } model)
        {
            return;
        }

        live = new CancellationTokenSource();
        _ = model.Environment.RunLiveAsync(PollInterval, live.Token);
    }

    private void StopLive()
    {
        live?.Cancel();
        live?.Dispose();
        live = null;
    }

    private void ApplyLayout(double width)
    {
        var layout = ShellLayout.Classify(width);
        LayoutClass = layout;
        Model?.Shell.ApplyWidth(width);
        Root.Classes.Set("narrow", layout == ShellLayoutClass.Narrow);
        Root.Classes.Set("medium", layout == ShellLayoutClass.Medium);
        Root.Classes.Set("wide", layout == ShellLayoutClass.Wide);
        PlaceInspector(layout);
    }

    /// <summary>Docked right on wide; overlay on the right on medium; a bottom sheet on narrow.</summary>
    private void PlaceInspector(ShellLayoutClass layout)
    {
        switch (layout)
        {
            case ShellLayoutClass.Wide:
                Grid.SetColumn(InspectorHost, 2);
                Grid.SetColumnSpan(InspectorHost, 1);
                InspectorHost.HorizontalAlignment = HorizontalAlignment.Stretch;
                InspectorHost.VerticalAlignment = VerticalAlignment.Stretch;
                InspectorHost.Width = 340;
                InspectorHost.MaxHeight = double.PositiveInfinity;
                InspectorHost.BorderThickness = new Thickness(1, 0, 0, 0);
                break;
            case ShellLayoutClass.Medium:
                Grid.SetColumn(InspectorHost, 1);
                Grid.SetColumnSpan(InspectorHost, 2);
                InspectorHost.HorizontalAlignment = HorizontalAlignment.Right;
                InspectorHost.VerticalAlignment = VerticalAlignment.Stretch;
                InspectorHost.Width = 340;
                InspectorHost.MaxHeight = double.PositiveInfinity;
                InspectorHost.BorderThickness = new Thickness(1, 0, 0, 0);
                break;
            default:
                Grid.SetColumn(InspectorHost, 0);
                Grid.SetColumnSpan(InspectorHost, 3);
                InspectorHost.HorizontalAlignment = HorizontalAlignment.Stretch;
                InspectorHost.VerticalAlignment = VerticalAlignment.Bottom;
                InspectorHost.Width = double.NaN;
                InspectorHost.MaxHeight = Math.Max(260, Bounds.Height * 0.62);
                InspectorHost.BorderThickness = new Thickness(0, 1, 0, 0);
                break;
        }

        InspectorHost.Classes.Set("sheet", layout == ShellLayoutClass.Narrow);
        UpdateSheetState();
    }

    private void UpdateSheetState() =>
        InspectorHost.Classes.Set("open", LayoutClass == ShellLayoutClass.Narrow && Model?.Shell.ShowInspectorSheet == true);

    private void OnShellChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.ShowInspectorSheet))
        {
            UpdateSheetState();
        }
    }

    private void OnSearchJumped(object? sender, Core.Scene.SceneSearchResult e) => SearchBox.Focus(NavigationMethod.Unspecified);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.K && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            FocusSearch();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && Model is { } model)
        {
            model.Shell.Dismiss();
            e.Handled = true;
        }
    }

    /// <summary>Ctrl+K: the toolbar box on wide and medium, the search page on a phone.</summary>
    public void FocusSearch()
    {
        if (Model is not { } model)
        {
            return;
        }

        if (model.Shell.IsNarrow || model.Shell.Mode == ShellMode.Search)
        {
            model.Shell.GoTo(ShellMode.Search);
            Dispatcher.UIThread.Post(
                () => this.GetVisualDescendants().OfType<SearchPageView>().FirstOrDefault()?.FocusBox(),
                DispatcherPriority.Loaded);
        }
        else
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        }
    }

    private void OnSearchBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (Model is not { } model)
        {
            return;
        }

        if (e.Key == Key.Enter && model.Shell.Search.Results.Count > 0)
        {
            model.Shell.Search.JumpToCommand.Execute(model.Shell.Search.Results[0]);
            e.Handled = true;
        }
        else if (e.Key == Key.Down && model.Shell.SearchDropdownOpen)
        {
            Dispatcher.UIThread.Post(
                () => SearchDropdown.GetVisualDescendants().OfType<Button>().FirstOrDefault()?.Focus(),
                DispatcherPriority.Loaded);
            e.Handled = true;
        }
    }

    /// <summary>The drop-down stays while focus moves into it; it closes when focus goes anywhere else.</summary>
    private void OnSearchBoxLostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        Dispatcher.UIThread.Post(
            () =>
            {
                var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Visual;
                if (focused is null || !SearchDropdown.IsVisualAncestorOf(focused))
                {
                    Model?.Shell.Search.Clear();
                    if (Model is { } model)
                    {
                        model.Shell.SearchDropdownOpen = false;
                    }
                }
            },
            DispatcherPriority.Background);
}
