using System.Windows;
using Ali.Modules.Coordinator;
using Ali.UI;

namespace Ali.Modules.Permissions;

public partial class AgentToolApprovalWindow : Window
{
    private bool _finished;

    private AgentToolApprovalWindow(AgentToolApprovalPrompt prompt)
    {
        InitializeComponent();
        NativeTitleBarTheme.ApplyDarkTitleBar(this);
        DataContext = new ApprovalViewModel(
            $"Run {prompt.ToolName.Replace('_', ' ')}?",
            prompt.Description,
            prompt.Arguments);
    }

    public AgentToolApprovalChoice Choice { get; private set; } = AgentToolApprovalChoice.Deny;

    public static AgentToolApprovalChoice Show(
        Window? owner,
        AgentToolApprovalPrompt prompt,
        CancellationToken cancellationToken = default)
    {
        var window = new AgentToolApprovalWindow(prompt)
        {
            Owner = owner
        };
        using var cancellationRegistration = cancellationToken.Register(() =>
            window.Dispatcher.BeginInvoke(new Action(() =>
                window.Finish(AgentToolApprovalChoice.Deny, false))));
        _ = window.ShowDialog();
        return window.Choice;
    }

    private void DenyButton_OnClick(object sender, RoutedEventArgs e) =>
        Finish(AgentToolApprovalChoice.Deny, false);

    private void AllowOnceButton_OnClick(object sender, RoutedEventArgs e) =>
        Finish(AgentToolApprovalChoice.AllowOnce, true);

    private void AlwaysArgumentsButton_OnClick(object sender, RoutedEventArgs e) =>
        Finish(AgentToolApprovalChoice.AlwaysAllowArguments, true);

    private void AlwaysToolButton_OnClick(object sender, RoutedEventArgs e) =>
        Finish(AgentToolApprovalChoice.AlwaysAllowTool, true);

    private void Finish(AgentToolApprovalChoice choice, bool dialogResult)
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        Choice = choice;
        if (IsVisible)
        {
            DialogResult = dialogResult;
        }
        Close();
    }

    private sealed record ApprovalViewModel(
        string TitleText,
        string DescriptionText,
        string ArgumentsText);
}
