using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Ali.UI;

public sealed class ExpandedViewportWindow : Window
{
    private readonly ContentControl _viewport = new()
    {
        HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch,
        VerticalContentAlignment = System.Windows.VerticalAlignment.Stretch
    };

    public ExpandedViewportWindow(FrameworkElement viewport)
    {
        Title = "Expanded Webcam";
        Width = 1280;
        Height = 760;
        MinWidth = 640;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(5, 7, 10));
        ResizeMode = ResizeMode.CanResize;
        NativeTitleBarTheme.ApplyDarkTitleBar(this);
        _viewport.Content = viewport;
        Content = _viewport;
    }

    public FrameworkElement? DetachViewport()
    {
        var viewport = _viewport.Content as FrameworkElement;
        _viewport.Content = null;
        return viewport;
    }
}
