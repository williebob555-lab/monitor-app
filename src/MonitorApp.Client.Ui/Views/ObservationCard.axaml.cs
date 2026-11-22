using Avalonia.Controls;

namespace MonitorApp.Client.Ui.Views;

/// <summary>One reading as a card: the same on phone and desktop, wrapping instead of scrolling sideways.</summary>
public sealed partial class ObservationCard : UserControl
{
    public ObservationCard() => InitializeComponent();
}
