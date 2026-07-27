using System.Runtime.CompilerServices;
using Ali.Modules.Evidence;
using Microsoft.Extensions.AI;
using MeaiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using MeaiChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Ali.Modules.Runtime;

public sealed partial class SafeActivatingLocalRuntime
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<MeaiChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        _activeRuntimeUnloadedForCandidate = false;
        return _activeRuntime is IChatClient chatClient
            ? chatClient.GetResponseAsync(messages, options, cancellationToken)
            : GetLegacyResponseAsync(messages, cancellationToken);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<MeaiChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        foreach (var update in response.ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is not null)
        {
            return null;
        }

        if (serviceType.IsInstanceOfType(this))
        {
            return this;
        }

        return (_activeRuntime as IChatClient)?.GetService(serviceType, serviceKey);
    }

    public void Dispose()
    {
        // Runtime release is explicit because the active model server is shared.
    }

    private async Task<ChatResponse> GetLegacyResponseAsync(
        IEnumerable<MeaiChatMessage> messages,
        CancellationToken cancellationToken)
    {
        var messageList = messages.ToList();
        var user = messageList.LastOrDefault(message => message.Role == MeaiChatRole.User);
        var history = messageList
            .Where(message => !ReferenceEquals(message, user))
            .Select(message => new ChatMessage(
                $"msg_meai_{Guid.NewGuid():N}",
                message.Role == MeaiChatRole.System
                    ? ChatRole.System
                    : message.Role == MeaiChatRole.Assistant
                        ? ChatRole.Assistant
                        : ChatRole.User,
                message.Text ?? string.Empty,
                DateTimeOffset.UtcNow,
                EvidenceStatus.Unverified))
            .ToList();
        var request = new ChatRequest(
            "extensions_ai_fallback",
            $"msg_user_{Guid.NewGuid():N}",
            user?.Text ?? string.Empty,
            history);
        var answer = new System.Text.StringBuilder();
        await foreach (var token in _activeRuntime.StreamChatAsync(request, cancellationToken).ConfigureAwait(false))
        {
            if (!token.IsThinking)
            {
                answer.Append(token.Text);
            }
        }

        return new ChatResponse(new MeaiChatMessage(MeaiChatRole.Assistant, answer.ToString()));
    }
}
