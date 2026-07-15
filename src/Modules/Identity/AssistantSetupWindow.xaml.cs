using System.Windows;
using Ali.UI;

namespace Ali.Modules.Identity;

public partial class AssistantSetupWindow : Window
{
    public AssistantSetupWindow()
    {
        NativeTitleBarTheme.ApplyDarkTitleBar(this);
        InitializeComponent();
        AssistantNameBox.Focus();
        AssistantNameBox.SelectAll();
    }

    public AssistantProfile AssistantProfile { get; private set; } = AssistantProfile.CreateDefault();

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        AssistantProfile = AssistantProfile.Create(AssistantNameBox.Text);
        DialogResult = true;
    }
}

