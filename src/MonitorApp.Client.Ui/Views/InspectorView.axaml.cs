using Avalonia.Controls;

namespace MonitorApp.Client.Ui.Views;

/// <summary>The inspector for the selected scene object; DataContext is the shell's InspectorViewModel.</summary>
public sealed partial class InspectorView : UserControl
{
    public InspectorView() => InitializeComponent();
}
