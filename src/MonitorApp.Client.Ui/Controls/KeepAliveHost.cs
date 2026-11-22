using Avalonia;
using Avalonia.Controls;

namespace MonitorApp.Client.Ui.Controls;

/// <summary>
/// Shows <see cref="Content"/> like a <see cref="ContentControl"/>, but keeps the page built for each content object alive and only
/// switches which one is visible. Switching back to a mode (Ecosystem, Alerts, History, ...) therefore reuses its page instead of
/// rebuilding the whole view tree, which on a phone took long enough to make every menu tap stutter.
/// </summary>
public sealed class KeepAliveHost : Panel
{
    public static readonly StyledProperty<object?> ContentProperty =
        AvaloniaProperty.Register<KeepAliveHost, object?>(nameof(Content));

    /// <summary>At most this many pages are kept; the least recently shown is dropped first.</summary>
    public const int MaxPages = 8;

    private readonly List<ContentControl> recent = [];

    public object? Content
    {
        get => GetValue(ContentProperty);
        set => SetValue(ContentProperty, value);
    }

    /// <summary>The page showing the current content (tests and focus handling use it).</summary>
    public ContentControl? Current { get; private set; }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ContentProperty)
        {
            Show(change.NewValue);
        }
    }

    private void Show(object? content)
    {
        ContentControl? page = null;
        if (content is not null)
        {
            page = recent.FirstOrDefault(p => ReferenceEquals(p.Content, content));
            if (page is null)
            {
                page = new ContentControl { Content = content };
                Children.Add(page);
            }

            recent.Remove(page);
            recent.Insert(0, page);
            while (recent.Count > MaxPages)
            {
                var oldest = recent[^1];
                recent.RemoveAt(recent.Count - 1);
                Children.Remove(oldest);
            }
        }

        foreach (var child in recent)
        {
            child.IsVisible = ReferenceEquals(child, page);
        }

        Current = page;
    }
}
