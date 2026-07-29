namespace Ali.Framework.Tests;

public sealed class MainWindowLayoutTests
{
    [Fact]
    public void ChatActions_AreCompactLinks_AndVoiceControlsAreGroupedAtRight()
    {
        var xaml = File.ReadAllText(FindRepositoryFile("src", "UI", "MainWindow.xaml"));

        Assert.Contains("x:Key=\"ChatActionLinkButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource ChatActionLinkButton}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Margin=\"0,3,0,0\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Visibility=\"{Binding AreActionsVisible, Converter={StaticResource BoolToVisibilityConverter}}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Speech speed\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Read replies aloud\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Enable PTT\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MainChatVoiceStatusText", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ChatMessageActions_AppearOnlyAfterStreamingCompletes()
    {
        var message = new Ali.UI.ViewModels.ChatMessageViewModel(
            "assistant-message",
            Ali.Modules.Runtime.ChatRole.Assistant,
            string.Empty,
            DateTimeOffset.UtcNow,
            Ali.Modules.Evidence.EvidenceStatus.Unknown,
            isResponseComplete: false);
        var changedProperties = new List<string?>();
        message.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        Assert.False(message.AreActionsVisible);

        message.IsResponseComplete = true;

        Assert.True(message.AreActionsVisible);
        Assert.Contains(nameof(message.AreActionsVisible), changedProperties);
    }

    [Fact]
    public void ReasoningEffort_HasCompactHeadingAboveButtonsAndStaysPinned()
    {
        var xaml = File.ReadAllText(FindRepositoryFile("src", "UI", "MainWindow.xaml"));
        var normalizedXaml = xaml.Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains("AutomationProperties.AutomationId=\"MainChatReasoningEffort\"", xaml, StringComparison.Ordinal);
        Assert.Contains("VerticalAlignment=\"Bottom\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Grid.RowSpan=\"2\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Grid.Column=\"1\"\n                      AutomationProperties.AutomationId=\"MainChatReasoningEffort\"", normalizedXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Effort\"", xaml, StringComparison.Ordinal);
        Assert.Contains("HorizontalAlignment=\"Center\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<Grid.RowDefinitions>", xaml, StringComparison.Ordinal);
        Assert.Contains("<RadioButton Grid.Row=\"1\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<RadioButton Grid.Row=\"2\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<RadioButton Grid.Row=\"3\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Low\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Medium\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"High\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void TopBar_ExposesLiveStatusForEveryManagedSidecar()
    {
        var xaml = File.ReadAllText(FindRepositoryFile("src", "UI", "MainWindow.xaml"));
        var viewModel = File.ReadAllText(FindRepositoryFile("src", "UI", "ViewModels", "MainWindowViewModel.cs"));

        Assert.Contains("MainChatStackStatus", xaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding StackComponents}\"", xaml, StringComparison.Ordinal);
        foreach (var label in new[] { "Memory", "RAG", "Speech", "MCP", "Bridge" })
        {
            Assert.Contains($"new(\"{label}\")", viewModel, StringComparison.Ordinal);
        }

        Assert.Contains("_services.UserMemories", viewModel, StringComparison.Ordinal);
        Assert.Contains(".TestAsync(_services.ActiveUsers.Current", viewModel, StringComparison.Ordinal);
        Assert.Contains("_services.Qdrant.Status", viewModel, StringComparison.Ordinal);
        Assert.Contains("McpServerSettings.IsRunning", viewModel, StringComparison.Ordinal);
        Assert.Contains("ConversationBridgeSettings.IsRunning", viewModel, StringComparison.Ordinal);
    }

    private static string FindRepositoryFile(params string[] segments)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine([current.FullName, .. segments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new FileNotFoundException($"Repository file was not found: {Path.Combine(segments)}");
    }
}
