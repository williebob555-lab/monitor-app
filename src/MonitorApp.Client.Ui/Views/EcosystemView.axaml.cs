using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.VisualTree;

namespace MonitorApp.Client.Ui.Views;

/// <summary>The ecosystem mode's page; see the slot description in the markup.</summary>
public sealed partial class EcosystemView : UserControl
{
    public EcosystemView() => InitializeComponent();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        var shell = this.FindAncestorOfType<WorkspaceShell>();
        if (shell?.WorkspaceTemplate is { } template)
        {
            UseCanvas(template);
        }
    }

    /// <summary>Puts <paramref name="template"/> (data type: EnvironmentViewModel) into WorkspaceHost and shows the outline beside it.</summary>
    internal void UseCanvas(IDataTemplate template)
    {
        var host = this.FindControl<ContentControl>("WorkspaceHost")!;
        host.ContentTemplate = template;
        this.FindControl<Border>("OutlinePanel")!.IsVisible = true;
    }
}
