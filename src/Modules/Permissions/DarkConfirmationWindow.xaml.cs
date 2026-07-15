using System.Windows;
using Ali.UI;

namespace Ali.Modules.Permissions;

public partial class DarkConfirmationWindow : Window
{
    private DarkConfirmationWindow(
        string title,
        string message,
        string confirmText,
        string cancelText)
    {
        NativeTitleBarTheme.ApplyDarkTitleBar(this);
        InitializeComponent();
        DataContext = new ConfirmationViewModel(title, message, confirmText, cancelText);
    }

    public static bool Show(
        Window? owner,
        string title,
        string message,
        string confirmText = "Confirm",
        string cancelText = "Cancel")
    {
        var window = new DarkConfirmationWindow(title, message, confirmText, cancelText)
        {
            Owner = owner
        };
        return window.ShowDialog() == true;
    }

    private void ConfirmButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private sealed record ConfirmationViewModel(
        string TitleText,
        string MessageText,
        string ConfirmText,
        string CancelText);
}

