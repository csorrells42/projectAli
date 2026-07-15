using System.Windows;
using Ali.UI;

namespace Ali.Modules.Runtime;

public partial class RuntimeOptimizationWindow : Window
{
    public RuntimeOptimizationWindow(string report, string assistantName)
    {
        NativeTitleBarTheme.ApplyDarkTitleBar(this);
        InitializeComponent();
        Title = $"{assistantName} Runtime Recommendations";
        ReportTextBox.Text = report;
    }

    private void CopyButton_OnClick(object sender, RoutedEventArgs e)
    {
        System.Windows.Clipboard.SetText(ReportTextBox.Text);
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}

