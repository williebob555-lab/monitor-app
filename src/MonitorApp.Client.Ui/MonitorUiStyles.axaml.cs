using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace MonitorApp.Client.Ui;

/// <summary>Theme and control styles shared by every head (desktop, Android). Include it in the head's Application.Styles.</summary>
public sealed class MonitorUiStyles : Styles
{
    public MonitorUiStyles() => AvaloniaXamlLoader.Load(this);
}
