using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.VisualTree;
using MonitorApp.Client.Core.ViewModels;
using MonitorApp.Client.Core.ViewModels.Shell;

namespace MonitorApp.Client.Ui.Views;

/// <summary>
/// The ecosystem mode's page; see the slot description in the markup. A head's <see cref="WorkspaceShell.WorkspaceTemplate"/> (a canvas)
/// wins; otherwise the host shows the Dive, the map while the shell's map switch is on, or the outline while its outline switch is on.
/// </summary>
public sealed partial class EcosystemView : UserControl
{
    private ShellViewModel? shell;
    private IDataTemplate? outlineTemplate;
    private IDataTemplate? diveTemplate;

    public EcosystemView() => InitializeComponent();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        var workspace = this.FindAncestorOfType<WorkspaceShell>();
        if (workspace?.WorkspaceTemplate is { } template)
        {
            UseCanvas(template);
            return;
        }

        if (workspace?.DataContext is MainViewModel main)
        {
            shell = main.Shell;
            shell.PropertyChanged += OnShellChanged;
            ApplyPresentation();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (shell is not null)
        {
            shell.PropertyChanged -= OnShellChanged;
            shell = null;
        }

        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>Puts <paramref name="template"/> (data type: EnvironmentViewModel) into WorkspaceHost and shows the outline beside it.</summary>
    internal void UseCanvas(IDataTemplate template)
    {
        var host = this.FindControl<ContentControl>("WorkspaceHost")!;
        host.ContentTemplate = template;
        this.FindControl<Border>("OutlinePanel")!.IsVisible = true;
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellViewModel.IsDive) or nameof(ShellViewModel.IsMap))
        {
            ApplyPresentation();
        }
    }

    /// <summary>The Dive has no header row and no side panels; the outline presentation keeps the summary and the tree.</summary>
    private void ApplyPresentation()
    {
        if (shell is not { } current)
        {
            return;
        }

        var host = this.FindControl<ContentControl>("WorkspaceHost")!;
        outlineTemplate ??= host.ContentTemplate;
        var dive = current.IsDive;
        this.FindControl<StackPanel>("Header")!.IsVisible = !dive;
        diveTemplate ??= new FuncDataTemplate<EnvironmentViewModel>((_, _) => new DiveView { DataContext = current.Dive });
        var mapTemplate = (IDataTemplate)Resources["MapTemplate"]!;
        var wanted = dive ? diveTemplate : current.IsMap ? mapTemplate : outlineTemplate;
        if (!ReferenceEquals(host.ContentTemplate, wanted))
        {
            host.ContentTemplate = wanted;
        }
    }
}
