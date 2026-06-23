using System.Windows;

namespace Ali.App.Wpf;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        NativeTitleBarTheme.ApplyDarkTitleBar(this);
        InitializeComponent();
    }
}
