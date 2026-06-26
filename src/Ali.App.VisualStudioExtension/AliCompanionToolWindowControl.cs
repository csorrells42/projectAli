using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Ali.App.VisualStudioExtension;

public sealed class AliCompanionToolWindowControl : UserControl
{
    private static readonly Uri HelperUri = new("http://127.0.0.1:8765/");
    private readonly WebBrowser _browser = new();
    private readonly TextBlock _status = new();

    public AliCompanionToolWindowControl()
    {
        Content = BuildLayout();
        Loaded += (_, _) => NavigateToHelper();
    }

    private UIElement BuildLayout()
    {
        var root = new DockPanel
        {
            LastChildFill = true,
            Background = new SolidColorBrush(Color.FromRgb(17, 22, 29))
        };

        var toolbar = new DockPanel
        {
            LastChildFill = true,
            Margin = new Thickness(8)
        };
        DockPanel.SetDock(toolbar, Dock.Top);

        var openButton = new Button
        {
            Content = "Open Ali Helper",
            MinWidth = 118,
            Height = 28,
            Margin = new Thickness(0, 0, 8, 0)
        };
        openButton.Click += (_, _) => NavigateToHelper();
        DockPanel.SetDock(openButton, Dock.Left);

        var refreshButton = new Button
        {
            Content = "Refresh",
            MinWidth = 78,
            Height = 28,
            Margin = new Thickness(0, 0, 8, 0)
        };
        refreshButton.Click += (_, _) => NavigateToHelper();
        DockPanel.SetDock(refreshButton, Dock.Left);

        _status.Text = "Ali helper: http://127.0.0.1:8765/";
        _status.Foreground = new SolidColorBrush(Color.FromRgb(203, 213, 225));
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.TextWrapping = TextWrapping.Wrap;

        toolbar.Children.Add(openButton);
        toolbar.Children.Add(refreshButton);
        toolbar.Children.Add(_status);

        _browser.Navigating += (_, _) => _status.Text = "Loading Ali helper...";
        _browser.LoadCompleted += (_, _) => _status.Text = "Ali helper loaded. Commands still use Ali's normal approval gates.";

        root.Children.Add(toolbar);
        root.Children.Add(_browser);
        return root;
    }

    private void NavigateToHelper()
    {
        try
        {
            _browser.Navigate(HelperUri);
        }
        catch (InvalidOperationException ex)
        {
            _status.Text = $"Could not load Ali helper: {ex.Message}";
        }
    }
}
