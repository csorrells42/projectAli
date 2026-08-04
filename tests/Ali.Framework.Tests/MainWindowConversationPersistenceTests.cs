using Ali.UI.ViewModels;

namespace Ali.Framework.Tests;

public sealed class MainWindowConversationPersistenceTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void TurnTeardown_SavesOnlyWhenFinalPublicationWasNotAcknowledged(
        bool finalPublicationAcknowledged,
        bool expectedSave)
    {
        Assert.Equal(
            expectedSave,
            MainWindowViewModel.ShouldSaveConversationAtTurnTeardown(finalPublicationAcknowledged));
    }
}
