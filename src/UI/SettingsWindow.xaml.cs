using System.Windows;
using System.Windows.Input;
using Ali.UI.ViewModels;

namespace Ali.UI;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        NativeTitleBarTheme.ApplyDarkTitleBar(this);
        InitializeComponent();
        MoveRuntimeTabToEnd();
        MoveInternetTabToEnd();
        PreviewKeyDown += SettingsWindow_OnPreviewKeyDown;
    }

    private void MoveRuntimeTabToEnd()
    {
        if (!SettingsTabs.Items.Contains(RuntimeTab))
        {
            return;
        }

        SettingsTabs.Items.Remove(RuntimeTab);
        SettingsTabs.Items.Add(RuntimeTab);
        SettingsTabs.SelectedIndex = 0;
    }

    private void MoveInternetTabToEnd()
    {
        if (!SettingsTabs.Items.Contains(InternetTab))
        {
            return;
        }

        SettingsTabs.Items.Remove(InternetTab);
        SettingsTabs.Items.Add(InternetTab);
        SettingsTabs.SelectedIndex = 0;
    }

    private void SettingsWindow_OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel || !viewModel.IsAssigningPushToTalkKey)
        {
            return;
        }

        e.Handled = true;
        viewModel.AssignPushToTalkKey(e.Key);
    }
}

