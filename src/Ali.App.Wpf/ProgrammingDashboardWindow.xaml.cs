using System.Windows;

namespace Ali.App.Wpf;

public partial class ProgrammingDashboardWindow : Window
{
    public ProgrammingDashboardWindow()
    {
        NativeTitleBarTheme.ApplyDarkTitleBar(this);
        InitializeComponent();
    }

    private void CloseButtonClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
