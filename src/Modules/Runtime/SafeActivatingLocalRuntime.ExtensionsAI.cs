using System.Runtime.CompilerServices;
using Ali.Modules.Evidence;
using Microsoft.Extensions.AI;
using MeaiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using MeaiChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Ali.Modules.Runtime;

public sealed partial class SafeActivatingLocalRuntime
{
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<MeaiChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        await _dispatchTransitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Volatile.Write(ref _activeRuntimeUnloadedForCandidate, false);
            var runtime = Volatile.Read(ref _activeRuntime);
            return runtime is IChatClient chatClient
                ? await chatClient
                    .GetResponseAsync(messages, options, cancellationToken)
                    .ConfigureAwait(false)
                : await GetLegacyResponseAsync(runtime, messages, cancellationToken)
                    .ConfigureAwait(false);
        }
        finally
        {
            _dispatchTransitionGate.Release();
        }
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<MeaiChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await _dispatchTransitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        IChatClient? chatClient;
        ILocalModelRuntime? legacyRuntime;
        try
        {
            Volatile.Write(ref _activeRuntimeUnloadedForCandidate, false);
            var runtime = Volatile.Read(ref _activeRuntime);
            if (runtime is IChatClient asChatClient)
            {
                chatClient = asChatClient;
                legacyRuntime = null;
            }
            else
            {
                chatClient = null;
                legacyRuntime = runtime;
            }
        }
        finally
        {
            _dispatchTransitionGate.Release();
        }

        if (chatClient is not null)
        {
            await foreach (var update in chatClient
                               .GetStreamingResponseAsync(messages, options, cancellationToken)
                               .ConfigureAwait(false))
            {
                yield return update;
            }

            yield break;
        }

        await foreach (var update in GetLegacyStreamingResponseAsync(
                           legacyRuntime!,
                           messages,
                           cancellationToken)
                           .ConfigureAwait(false))
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

        var runtime = Volatile.Read(ref _activeRuntime);
        if (runtime is not IChatClient inner
            || CanExposeDispatchThroughServiceType(serviceType, runtime, inner)
            || !CanForwardDiscoveredServiceType(serviceType))
        {
            return null;
        }

        return KeepOnlyInertDiscoveredService(
            inner.GetService(serviceType, serviceKey),
            runtime,
            inner);
    }

    public void Dispose()
    {
        // Runtime release is explicit because the active model server is shared.
    }

    private IChatClient CreatePinnedBoundDispatchClient(
        ILocalModelRuntime pinnedRuntime,
        IChatClient inner) =>
        new PinnedBoundDispatchChatClient(this, pinnedRuntime, inner);

    private static async Task<ChatResponse> GetLegacyResponseAsync(
        ILocalModelRuntime runtime,
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
        await foreach (var token in runtime.StreamChatAsync(request, cancellationToken).ConfigureAwait(false))
        {
            if (!token.IsThinking)
            {
                answer.Append(token.Text);
            }
        }

        return new ChatResponse(new MeaiChatMessage(MeaiChatRole.Assistant, answer.ToString()));
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> GetLegacyStreamingResponseAsync(
        ILocalModelRuntime runtime,
        IEnumerable<MeaiChatMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken)
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

        await foreach (var token in runtime.StreamChatAsync(request, cancellationToken).ConfigureAwait(false))
        {
            if (!token.IsThinking && !string.IsNullOrEmpty(token.Text))
            {
                yield return new ChatResponseUpdate(MeaiChatRole.Assistant, token.Text);
            }
        }
    }

    /// <summary>
    /// Preserves the exact concrete client chosen by a bound snapshot while retaining the one
    /// switching-facade side effect that a direct underlying dispatch would otherwise bypass.
    /// It revalidates active-runtime identity under the shared dispatch/transition gate, never
    /// redirects to another client, and never owns or disposes the pinned client.
    /// </summary>
    private sealed class PinnedBoundDispatchChatClient(
        SafeActivatingLocalRuntime owner,
        ILocalModelRuntime pinnedRuntime,
        IChatClient inner) : IChatClient
    {
        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<MeaiChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            await owner._dispatchTransitionGate.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            try
            {
                owner.NotifyPinnedBoundDispatchStarting(pinnedRuntime);
                return await inner
                    .GetResponseAsync(messages, options, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                owner._dispatchTransitionGate.Release();
            }
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<MeaiChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await owner._dispatchTransitionGate.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            try
            {
                owner.NotifyPinnedBoundDispatchStarting(pinnedRuntime);
                await foreach (var update in inner
                                   .GetStreamingResponseAsync(messages, options, cancellationToken)
                                   .ConfigureAwait(false))
                {
                    yield return update;
                }
            }
            finally
            {
                owner._dispatchTransitionGate.Release();
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

            if (CanExposeDispatchThroughServiceType(serviceType, pinnedRuntime, inner)
                || !CanForwardDiscoveredServiceType(serviceType))
            {
                return null;
            }

            return KeepOnlyInertDiscoveredService(
                inner.GetService(serviceType, serviceKey),
                pinnedRuntime,
                inner);
        }

        public void Dispose()
        {
            // The runtime owner owns the pinned concrete client and its transport lifetime.
        }
    }

    private static bool CanExposeDispatchThroughServiceType(
        Type serviceType,
        ILocalModelRuntime runtime,
        IChatClient inner) =>
        typeof(IChatClient).IsAssignableFrom(serviceType)
        || typeof(ILocalModelRuntime).IsAssignableFrom(serviceType)
        || serviceType.IsAssignableFrom(runtime.GetType())
        || serviceType.IsAssignableFrom(inner.GetType());

    private static bool CanForwardDiscoveredServiceType(Type serviceType) =>
        serviceType == typeof(ChatClientMetadata);

    private static object? KeepOnlyInertDiscoveredService(
        object? service,
        ILocalModelRuntime runtime,
        IChatClient inner) =>
        service is IChatClient
        || service is ILocalModelRuntime
        || ReferenceEquals(service, runtime)
        || ReferenceEquals(service, inner)
            ? null
            : service;

}
