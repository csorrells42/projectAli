using System.Windows;
using System.Windows.Input;
using Ali.App.Wpf.ViewModels;

namespace Ali.App.Wpf;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        NativeTitleBarTheme.ApplyDarkTitleBar(this);
        InitializeComponent();
        MoveRuntimeTabToEnd();
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
