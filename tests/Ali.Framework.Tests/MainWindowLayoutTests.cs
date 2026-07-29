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
        Assert.Contains("Text=\"Speech speed\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Read replies aloud\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Enable PTT\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MainChatVoiceStatusText", xaml, StringComparison.Ordinal);
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
