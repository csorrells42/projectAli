using System.Windows;

namespace Ali.UI;

public partial class MaintenanceDashboardWindow : Window
{
    public MaintenanceDashboardWindow()
    {
        NativeTitleBarTheme.ApplyDarkTitleBar(this);
        InitializeComponent();
    }

    private void CloseButtonClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}

