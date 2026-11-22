using Avalonia.Headless;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using MonitorApp.Client.Core.ViewModels;
using MonitorApp.Client.Ui.Views;
using Xunit;

namespace MonitorApp.Client.Desktop.Tests;

public sealed class MainWindowSmokeTests
{
    [AvaloniaFact]
    public async Task MainWindowBindsToARealNodeAndRendersEverySection()
    {
        await using var node = await MonitorApp.Client.Core.Tests.NodeFixture.CreateAsync();
        await node.AddNumberAsync(MonitorApp.Client.Core.Tests.NodeFixture.Now.AddMinutes(-1), 0.5);
        using var model = new MainViewModel(node.Address);
        var window = new MainWindow { DataContext = model };
        window.Show();

        await model.ConnectAsync();
        var nav = window.GetVisualDescendants().OfType<ListBox>().Single(box => box.Name == "NavList");
        foreach (var section in model.Sections)
        {
            model.SelectedSection = section;
            await section.RefreshAsync();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Null(section.ErrorMessage);
        }

        Assert.Equal(model.Sections.Count, nav.ItemCount);
        Assert.StartsWith("Connected", model.ConnectionStatus, StringComparison.Ordinal);
        Assert.Equal("peer-local", model.Overview.LocalPeerId);

        // A frame really renders through Skia.
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Assert.True(frame!.PixelSize.Width > 0 && frame.PixelSize.Height > 0);
        window.Close();
    }
}
