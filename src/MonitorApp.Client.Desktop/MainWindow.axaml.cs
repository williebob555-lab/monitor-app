using Avalonia.Controls;

namespace MonitorApp.Client.Desktop;

/// <summary>Desktop window around the shared <see cref="Ui.Views.WorkspaceShell"/>; the data context flows down to it.</summary>
public sealed partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();
}
