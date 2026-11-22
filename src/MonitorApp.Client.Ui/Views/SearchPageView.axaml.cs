using Avalonia.Controls;
using Avalonia.Input;
using MonitorApp.Client.Core.ViewModels.Shell;

namespace MonitorApp.Client.Ui.Views;

public sealed partial class SearchPageView : UserControl
{
    public SearchPageView()
    {
        InitializeComponent();
        SearchPageBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && DataContext is SearchViewModel model && model.Results.Count > 0)
            {
                model.JumpToCommand.Execute(model.Results[0]);
                e.Handled = true;
            }
        };
    }

    /// <summary>Puts the keyboard in the search box (Ctrl+K on a phone-width shell, or when the Search mode opens).</summary>
    public void FocusBox() => SearchPageBox.Focus();
}
