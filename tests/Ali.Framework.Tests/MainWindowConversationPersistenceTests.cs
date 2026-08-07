using Ali.UI.ViewModels;

namespace Ali.Framework.Tests;

public sealed class MainWindowConversationPersistenceTests
{
    [Fact]
    public void CompletedVisibleTurnHistory_IsForwardedIntoANewModelRun()
    {
        var viewModel = File.ReadAllText(FindRepositoryFile(
            "src",
            "UI",
            "ViewModels",
            "MainWindowViewModel.cs"));

        Assert.Contains(
            ".Where(message => message.IsResponseComplete",
            viewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            ".Select(message => message.ToCoreMessage())",
            viewModel,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "IReadOnlyList<ChatMessage> history = Array.Empty<ChatMessage>();",
            viewModel,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void TurnTeardown_SavesUnlessFinalPublicationWasAlreadyPersisted(
        bool finalPublicationPersisted,
        bool expectedSave)
    {
        Assert.Equal(
            expectedSave,
            MainWindowViewModel.ShouldSaveConversationAtTurnTeardown(finalPublicationPersisted));
    }

    [Theory]
    [InlineData(false, "Ali could not complete this turn.\n\nThe model returned neither a final answer nor a tool result.\n\nThe command was not replayed.")]
    [InlineData(true, "\n\nAli could not complete this turn.\n\nThe model returned neither a final answer nor a tool result.\n\nThe command was not replayed.")]
    public void TurnFailure_IsRenderedInChatWithoutReplayingTheCommand(
        bool hasExistingResponse,
        string expected)
    {
        Assert.Equal(
            expected,
            MainWindowViewModel.FormatTurnFailureForChat(
                "The model returned neither a final answer nor a tool result.",
                hasExistingResponse));
    }

    private static string FindRepositoryFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(new[] { directory.FullName }.Concat(segments).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find repository file: {Path.Combine(segments)}");
    }
}
