namespace Ali.Modules.Conversation;

public sealed record ConversationSessionState(
    string ConversationId,
    IReadOnlyList<StoredChatMessage> Messages,
    bool LoadedFromStorage);

public static class ConversationSessionFactory
{
    public static ConversationSessionState StartFresh() =>
        new($"conv_{Guid.NewGuid():N}", Array.Empty<StoredChatMessage>(), LoadedFromStorage: false);

    public static ConversationSessionState Reopen(StoredConversation conversation) =>
        new(
            conversation.ConversationId,
            conversation.Messages.OrderBy(message => message.CreatedAt).ToList(),
            LoadedFromStorage: true);
}
