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
        var viewModel = File.ReadAllText(FindRepositoryFile("src", "UI", "ViewModels", "MainWindowViewModel.cs"));
        var normalizedXaml = xaml.Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains("AutomationProperties.AutomationId=\"MainChatReasoningEffort\"", xaml, StringComparison.Ordinal);
        Assert.Contains("VerticalAlignment=\"Bottom\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Grid.RowSpan=\"2\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Grid.Column=\"1\"\n                            Orientation=\"Horizontal\"", normalizedXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("MainChatCodingExecutor", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("GroupName=\"CodingExecutor\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("IsCodingExecutorAli", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("IsCodingExecutorAider", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("IsCodingExecutorOpenHands", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("IsCodingExecutorAli", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("IsCodingExecutorAider", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("IsCodingExecutorOpenHands", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("_selectedProgrammingAgentMode", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("SynchronizeCodingExecutorSelection", viewModel, StringComparison.Ordinal);
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

    [Fact]
    public void ActivityLog_RemainsExpanded_AutoScrolls_AndCanCopyCompleteHistory()
    {
        var xaml = File.ReadAllText(FindRepositoryFile("src", "UI", "MainWindow.xaml"));
        var codeBehind = File.ReadAllText(FindRepositoryFile("src", "UI", "MainWindow.xaml.cs"));
        var viewModel = File.ReadAllText(FindRepositoryFile("src", "UI", "ViewModels", "MainWindowViewModel.cs"));
        var panelStart = xaml.IndexOf(
            "AutomationProperties.AutomationId=\"AliActivityPanel\"",
            StringComparison.Ordinal);
        var panelEnd = xaml.IndexOf("</Expander>", panelStart, StringComparison.Ordinal);
        Assert.True(panelStart >= 0 && panelEnd > panelStart);
        var activityPanel = xaml[panelStart..panelEnd];

        Assert.Contains("AutomationProperties.AutomationId=\"MainChatCopyActivityLog\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding CopyAgentActivityCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AgentActivityScrollViewer\"", xaml, StringComparison.Ordinal);
        Assert.Contains("HorizontalScrollBarVisibility=\"Disabled\"", activityPanel, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding Headline}\"", activityPanel, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding DisplayDetail}\"", activityPanel, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding ReceiptText}\"", activityPanel, StringComparison.Ordinal);
        Assert.Contains("TextWrapping=\"Wrap\"", activityPanel, StringComparison.Ordinal);
        Assert.DoesNotContain("TextWrapping=\"NoWrap\"", activityPanel, StringComparison.Ordinal);
        Assert.DoesNotContain("TextTrimming=\"CharacterEllipsis\"", activityPanel, StringComparison.Ordinal);
        Assert.Contains("AgentActivities.CollectionChanged", codeBehind, StringComparison.Ordinal);
        Assert.Contains("AgentActivityScrollViewer.ScrollToEnd()", codeBehind, StringComparison.Ordinal);
        Assert.Contains("private bool _isAgentActivityExpanded = true;", viewModel, StringComparison.Ordinal);
        Assert.Contains("AgentActivitySummary = item.SummaryText;", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("AgentActivitySummary = chunk.Text;", viewModel, StringComparison.Ordinal);
        Assert.Contains("System.Windows.Clipboard.SetText(log.ToString())", viewModel, StringComparison.Ordinal);
        Assert.Contains("log.Append(activity.ReceiptText)", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ClearAgentActivity();\r\n        IsAgentActivityExpanded = false;",
            viewModel,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Composer_ShowsPastedImagePreviewAndWrappedReceiptStatusWithoutOverflow()
    {
        var xaml = File.ReadAllText(FindRepositoryFile("src", "UI", "MainWindow.xaml"));
        var viewModel = File.ReadAllText(
            FindRepositoryFile("src", "UI", "ViewModels", "MainWindowViewModel.cs"));

        Assert.Contains("MainChatAttachScreenshotButton", xaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding PasteImageCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding Attachments}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<Image Source=\"{Binding FilePath}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<WrapPanel />", xaml, StringComparison.Ordinal);
        Assert.Contains("ClipToBounds=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("HorizontalScrollBarVisibility=\"Disabled\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding AgentActivitySummary}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding ReceiptText}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("TextWrapping=\"Wrap\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AddClipboardImageAsync", viewModel, StringComparison.Ordinal);
        Assert.Contains("SynchronizeVisibleStackComponents", viewModel, StringComparison.Ordinal);
        Assert.Contains("McpSettings.Enabled || McpServerSettings.Enabled", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void RecoveryDecision_UsesTwoWrappedChoices_AndLocksOrdinaryComposerIngress()
    {
        var xaml = File.ReadAllText(FindRepositoryFile("src", "UI", "MainWindow.xaml"));
        var viewModel = File.ReadAllText(
            FindRepositoryFile("src", "UI", "ViewModels", "MainWindowViewModel.cs"));
        var panelStart = xaml.IndexOf(
            "AutomationProperties.AutomationId=\"MainChatRecoveryPanel\"",
            StringComparison.Ordinal);
        var panelEnd = xaml.IndexOf("</Border>", panelStart, StringComparison.Ordinal);
        Assert.True(panelStart >= 0 && panelEnd > panelStart);
        var recoveryPanel = xaml[panelStart..panelEnd];

        Assert.Contains(
            "Visibility=\"{Binding IsRecoveryDecisionRequired, Converter={StaticResource BoolToVisibilityConverter}}\"",
            recoveryPanel,
            StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(recoveryPanel, "MainChatRecoveryPrimaryButton"));
        Assert.Equal(1, CountOccurrences(recoveryPanel, "MainChatRecoverySecondaryButton"));
        Assert.Equal(1, CountOccurrences(recoveryPanel, "ResolvePrimaryRecoveryDecisionCommand"));
        Assert.Equal(1, CountOccurrences(recoveryPanel, "ResolveSecondaryRecoveryDecisionCommand"));
        Assert.Equal(2, CountOccurrences(recoveryPanel, "<ColumnDefinition Width=\"*\" />"));
        Assert.True(CountOccurrences(recoveryPanel, "TextWrapping=\"Wrap\"") >= 3);
        Assert.DoesNotContain("HorizontalScrollBarVisibility", recoveryPanel, StringComparison.Ordinal);
        Assert.Contains("IsReadOnly=\"{Binding IsComposerReadOnly}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("!IsRecoveryDecisionRequired\n                && !IsRecording", viewModel.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Equal(
            2,
            CountOccurrences(
                viewModel,
                "Resolve or cancel the recovered turn before changing attachments."));

        foreach (var hiddenIdentityName in new[]
                 {
                     "DurableIdentity",
                     "ExpectedStateRevision",
                     "PromptPublicationId",
                     "PromptTextDigest",
                     "SubjectId",
                     "SubjectPreparedRevision"
                 })
        {
            Assert.DoesNotContain(hiddenIdentityName, recoveryPanel, StringComparison.Ordinal);
        }
    }

    private static int CountOccurrences(string value, string expected) =>
        value.Split(expected, StringSplitOptions.None).Length - 1;

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
