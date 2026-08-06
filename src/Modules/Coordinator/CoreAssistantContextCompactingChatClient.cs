using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Ali.Modules.Coordinator;

/// <summary>
/// Keeps the core assistant's live native-tool loop inside the selected model window.
/// Exact current work stays in RAM; stale bulky tool exchanges are omitted and can be
/// recovered from the authoritative Workspace when the model needs them again.
/// </summary>
internal sealed class CoreAssistantContextCompactingChatClient(IChatClient inner) : IChatClient
{
    private const int MaximumRetainedNonSystemMessages = 12;
    private const int MaximumSystemCharacters = 12_000;
    private const int MaximumMessageCharacters = 4_000;
    private const int MaximumToolResultCharacters = 4_000;
    private const int MaximumArgumentCharacters = 1_600;
    private const int MaximumArgumentValueCharacters = 480;

    // Devstral sometimes emits a tool call as plain text instead of a native
    // structured call: "[TOOL_CALLS]toolName[ARGS]{...json...}". A prior fix
    // for this (AliToolCallingChatClient.PromoteRegisteredTextualToolCall) was
    // written but never actually wired into the live fast-path client chain --
    // this class is the one that's genuinely live, so the promotion has to
    // happen here, against the real streamed text, not a buffered response.
    private const string TextualToolCallPrefix = "[TOOL_CALLS]";
    private const string TextualToolArgumentsMarker = "[ARGS]";
    private const string QwenToolCallOpenMarker = "<tools>";
    private const string QwenToolCallCloseMarker = "</tools>";
    private const string FencedJsonToolCallOpenMarker = "```json";
    private const string FencedJsonToolCallCloseMarker = "```";
    private const string BareJsonToolNameProperty = "\"name\"";
    private const int MaximumBareJsonMarkerPrefixCharacters = 32;

    private readonly IChatClient _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private static readonly AsyncLocal<IReadOnlySet<string>?> FocusedToolNames = new();
    private static readonly AsyncLocal<RequiredToolFocus?> RequiredFocusedTool = new();
    private static readonly AsyncLocal<IReadOnlySet<string>?> BareJsonToolNames = new();
    private readonly object _turnAnchorSync = new();
    private ChatMessage? _turnAnchor;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        _inner.GetResponseAsync(
            CompactForTurn(messages),
            ApplyToolFocus(options),
            cancellationToken);

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var effectiveOptions = ApplyToolFocus(options);
        var registeredTools = effectiveOptions?.Tools?
            .OfType<AIFunctionDeclaration>()
            .ToArray() ?? [];
        var bareJsonToolNames = BareJsonToolNames.Value;
        var scanForBareJsonCall = bareJsonToolNames is { Count: > 0 };

        // Keep only enough trailing text to recognize a marker split across
        // streaming chunks. Ordinary text remains live with a delay of at most
        // the longest marker; once a marker appears anywhere, capture that call.
        var buffer = new System.Text.StringBuilder();
        var scanForTextualCall = registeredTools.Length > 0;
        var capturingToolCall = false;
        ChatResponseUpdate? deferredTerminalUpdate = null;

        await foreach (var update in _inner
                           .GetStreamingResponseAsync(
                               CompactForTurn(messages),
                               effectiveOptions,
                               cancellationToken)
                           .ConfigureAwait(false))
        {
            if (!scanForTextualCall)
            {
                yield return update;
                continue;
            }

            if (update.Contents.Any(content => content is not TextContent))
            {
                // A real structured tool call (or other content) already
                // arrived -- this response was never a textual marker.
                scanForTextualCall = false;
                if (buffer.Length > 0)
                {
                    yield return new ChatResponseUpdate(update.Role, buffer.ToString());
                    buffer.Clear();
                }
                yield return update;
                continue;
            }

            var deltaText = string.Concat(update.Contents.OfType<TextContent>().Select(c => c.Text));
            if (deltaText.Length == 0)
            {
                // OpenRouter commonly emits visible content in one SSE chunk
                // and the terminal finish_reason in a later empty chunk. Keep
                // that terminal update until any buffered text/tool envelope
                // has been published; dropping it makes the agent framework
                // discard the otherwise valid accumulated assistant answer.
                if (update.FinishReason is not null)
                {
                    deferredTerminalUpdate = update;
                }
                continue;
            }

            buffer.Append(deltaText);
            if (capturingToolCall)
            {
                continue;
            }

            var accumulated = buffer.ToString();
            var markerIndex = FindFirstTextualToolCallMarker(accumulated, scanForBareJsonCall);
            if (markerIndex >= 0)
            {
                if (markerIndex > 0)
                {
                    yield return new ChatResponseUpdate(update.Role, accumulated[..markerIndex]);
                    buffer.Remove(0, markerIndex);
                }

                capturingToolCall = true;
                continue;
            }

            var retainedCharacters = LongestMarkerPrefixSuffix(accumulated, scanForBareJsonCall);
            var charactersToPublish = accumulated.Length - retainedCharacters;
            if (charactersToPublish > 0)
            {
                yield return new ChatResponseUpdate(update.Role, accumulated[..charactersToPublish]);
                buffer.Remove(0, charactersToPublish);
            }
        }

        if (buffer.Length == 0)
        {
            if (deferredTerminalUpdate is not null)
            {
                yield return deferredTerminalUpdate;
            }
            yield break;
        }

        var raw = buffer.ToString();
        if (!capturingToolCall)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, raw);
            if (deferredTerminalUpdate is not null)
            {
                yield return deferredTerminalUpdate;
            }
            yield break;
        }

        var promotedCall = TryParseTextualToolCall(raw, registeredTools, bareJsonToolNames);
        yield return promotedCall is not null
            ? new ChatResponseUpdate(ChatRole.Assistant, [promotedCall])
            : new ChatResponseUpdate(ChatRole.Assistant, raw);
        if (deferredTerminalUpdate is not null)
        {
            yield return deferredTerminalUpdate;
        }
    }

    internal static FunctionCallContent? TryParseTextualToolCall(
        string raw,
        IReadOnlyList<AIFunctionDeclaration> registeredTools,
        IReadOnlySet<string>? bareJsonToolNames = null)
    {
        var trimmed = raw.Trim();
        if (trimmed.StartsWith(TextualToolCallPrefix, StringComparison.Ordinal))
        {
            return TryParseBracketedToolCall(trimmed, registeredTools);
        }

        if (trimmed.StartsWith(QwenToolCallOpenMarker, StringComparison.Ordinal))
        {
            return TryParseQwenToolCall(trimmed, registeredTools);
        }

        return trimmed.StartsWith('{') && bareJsonToolNames is not null
            ? TryParseNameArgumentsObject(trimmed, registeredTools, bareJsonToolNames)
            : TryParseFencedJsonToolCall(trimmed, registeredTools);
    }

    private static int FindFirstTextualToolCallMarker(string text, bool scanForBareJsonCall)
    {
        var indexes = new[]
        {
            text.IndexOf(TextualToolCallPrefix, StringComparison.Ordinal),
            text.IndexOf(QwenToolCallOpenMarker, StringComparison.Ordinal),
            text.IndexOf(FencedJsonToolCallOpenMarker, StringComparison.Ordinal)
        };
        var framedMarkerIndex = indexes.Where(index => index >= 0).DefaultIfEmpty(-1).Min();
        if (!scanForBareJsonCall)
        {
            return framedMarkerIndex;
        }

        var bareJsonMarkerIndex = FindFirstBareJsonToolCallMarker(text);
        return framedMarkerIndex < 0
            ? bareJsonMarkerIndex
            : bareJsonMarkerIndex < 0
                ? framedMarkerIndex
                : Math.Min(framedMarkerIndex, bareJsonMarkerIndex);
    }

    private static int FindFirstBareJsonToolCallMarker(string text)
    {
        for (var objectStart = text.IndexOf('{'); objectStart >= 0; objectStart = text.IndexOf('{', objectStart + 1))
        {
            var propertyStart = objectStart + 1;
            while (propertyStart < text.Length && char.IsWhiteSpace(text[propertyStart]))
            {
                propertyStart++;
            }

            if (text.AsSpan(propertyStart).StartsWith(BareJsonToolNameProperty, StringComparison.Ordinal))
            {
                return objectStart;
            }
        }

        return -1;
    }

    private static int LongestMarkerPrefixSuffix(string text, bool scanForBareJsonCall)
    {
        var markers = new[]
        {
            TextualToolCallPrefix,
            QwenToolCallOpenMarker,
            FencedJsonToolCallOpenMarker
        };
        var maximum = Math.Min(text.Length, markers.Max(marker => marker.Length) - 1);
        for (var length = maximum; length > 0; length--)
        {
            var suffix = text[^length..];
            if (markers.Any(marker => marker.StartsWith(suffix, StringComparison.Ordinal)))
            {
                return length;
            }
        }

        if (!scanForBareJsonCall)
        {
            return 0;
        }

        var lastObjectStart = text.LastIndexOf('{');
        if (lastObjectStart < 0
            || text.Length - lastObjectStart > MaximumBareJsonMarkerPrefixCharacters)
        {
            return 0;
        }

        var propertyStart = lastObjectStart + 1;
        while (propertyStart < text.Length && char.IsWhiteSpace(text[propertyStart]))
        {
            propertyStart++;
        }

        var possiblePropertyPrefix = text.AsSpan(propertyStart);
        return possiblePropertyPrefix.Length <= BareJsonToolNameProperty.Length
            && BareJsonToolNameProperty.AsSpan().StartsWith(possiblePropertyPrefix, StringComparison.Ordinal)
                ? text.Length - lastObjectStart
                : 0;
    }

    private static FunctionCallContent? TryParseBracketedToolCall(
        string trimmed,
        IReadOnlyList<AIFunctionDeclaration> registeredTools)
    {
        if (trimmed.IndexOf(TextualToolCallPrefix, TextualToolCallPrefix.Length, StringComparison.Ordinal) >= 0)
        {
            return null;
        }

        var argumentsMarkerIndex = trimmed.IndexOf(
            TextualToolArgumentsMarker,
            TextualToolCallPrefix.Length,
            StringComparison.Ordinal);
        if (argumentsMarkerIndex <= TextualToolCallPrefix.Length
            || trimmed.IndexOf(
                TextualToolArgumentsMarker,
                argumentsMarkerIndex + TextualToolArgumentsMarker.Length,
                StringComparison.Ordinal) >= 0)
        {
            return null;
        }

        var toolName = trimmed[TextualToolCallPrefix.Length..argumentsMarkerIndex].Trim();
        var argumentsJson = trimmed[(argumentsMarkerIndex + TextualToolArgumentsMarker.Length)..].Trim();
        return CreateRegisteredToolCall(toolName, argumentsJson, registeredTools);
    }

    private static FunctionCallContent? TryParseQwenToolCall(
        string trimmed,
        IReadOnlyList<AIFunctionDeclaration> registeredTools)
    {
        if (!trimmed.StartsWith(QwenToolCallOpenMarker, StringComparison.Ordinal)
            || !trimmed.EndsWith(QwenToolCallCloseMarker, StringComparison.Ordinal)
            || trimmed.IndexOf(
                QwenToolCallOpenMarker,
                QwenToolCallOpenMarker.Length,
                StringComparison.Ordinal) >= 0)
        {
            return null;
        }

        var envelopeJson = trimmed[
            QwenToolCallOpenMarker.Length..^QwenToolCallCloseMarker.Length].Trim();
        return TryParseNameArgumentsObject(envelopeJson, registeredTools);
    }

    private static FunctionCallContent? TryParseFencedJsonToolCall(
        string trimmed,
        IReadOnlyList<AIFunctionDeclaration> registeredTools)
    {
        if (!trimmed.StartsWith(FencedJsonToolCallOpenMarker, StringComparison.Ordinal))
        {
            return null;
        }

        var closingMarkerIndex = trimmed.IndexOf(
            FencedJsonToolCallCloseMarker,
            FencedJsonToolCallOpenMarker.Length,
            StringComparison.Ordinal);
        if (closingMarkerIndex <= FencedJsonToolCallOpenMarker.Length)
        {
            return null;
        }

        var envelopeJson = trimmed[
            FencedJsonToolCallOpenMarker.Length..closingMarkerIndex].Trim();
        return TryParseNameArgumentsObject(envelopeJson, registeredTools);
    }

    private static FunctionCallContent? TryParseNameArgumentsObject(
        string envelopeJson,
        IReadOnlyList<AIFunctionDeclaration> registeredTools,
        IReadOnlySet<string>? allowedToolNames = null)
    {
        try
        {
            using var document = JsonDocument.Parse(envelopeJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || root.EnumerateObject().Count() != 2
                || !root.TryGetProperty("name", out var name)
                || name.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("arguments", out var arguments)
                || arguments.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var toolName = name.GetString() ?? string.Empty;
            if (allowedToolNames is not null && !allowedToolNames.Contains(toolName))
            {
                return null;
            }

            return CreateRegisteredToolCall(
                toolName,
                arguments.GetRawText(),
                registeredTools);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static FunctionCallContent? CreateRegisteredToolCall(
        string toolName,
        string argumentsJson,
        IReadOnlyList<AIFunctionDeclaration> registeredTools)
    {
        if (toolName.Length == 0
            || argumentsJson.Length == 0
            || !registeredTools.Any(tool => string.Equals(tool.Name, toolName, StringComparison.Ordinal)))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                arguments[property.Name] = property.Value.Clone();
            }

            return new FunctionCallContent($"call_{Guid.NewGuid():N}", toolName, arguments);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this)
            ? this
            : _inner.GetService(serviceType, serviceKey);

    public void Dispose()
    {
        // The bound runtime snapshot owns the underlying client.
    }

    internal static IDisposable FocusTools(
        IReadOnlySet<string>? toolNames,
        string? requiredToolName = null)
    {
        var previousNames = FocusedToolNames.Value;
        var previousRequired = RequiredFocusedTool.Value;
        FocusedToolNames.Value = toolNames;
        RequiredFocusedTool.Value = string.IsNullOrWhiteSpace(requiredToolName)
            ? null
            : new RequiredToolFocus(requiredToolName);
        return new ToolFocusLease(previousNames, previousRequired);
    }

    internal static IDisposable AllowBareJsonToolCalls(IReadOnlySet<string>? toolNames)
    {
        var previousNames = BareJsonToolNames.Value;
        BareJsonToolNames.Value = toolNames;
        return new BareJsonToolCallLease(previousNames);
    }

    private static ChatOptions? ApplyToolFocus(ChatOptions? options)
    {
        var focusedNames = FocusedToolNames.Value;
        if (focusedNames is null || options?.Tools is null)
        {
            return options;
        }

        var focused = options.Clone();
        focused.Tools = options.Tools
            .Where(tool => tool is AIFunctionDeclaration function
                && focusedNames.Contains(function.Name))
            .ToList();
        var requiredFocus = RequiredFocusedTool.Value;
        if (requiredFocus is not null
            && Interlocked.Exchange(ref requiredFocus.Consumed, 1) == 0
            && focused.Tools.OfType<AIFunctionDeclaration>().Any(function =>
                string.Equals(function.Name, requiredFocus.ToolName, StringComparison.Ordinal)))
        {
            focused.ToolMode = ChatToolMode.RequireSpecific(requiredFocus.ToolName);
        }
        return focused;
    }

    private IReadOnlyList<ChatMessage> CompactForTurn(IEnumerable<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var source = messages.ToList();
        ChatMessage? anchor;
        lock (_turnAnchorSync)
        {
            _turnAnchor ??= source.LastOrDefault(message =>
                message.Role == ChatRole.User
                && !string.IsNullOrWhiteSpace(message.Text));
            anchor = _turnAnchor;
        }

        return Compact(source, anchor);
    }

    internal static IReadOnlyList<ChatMessage> Compact(IEnumerable<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        return Compact(messages.ToList(), anchor: null);
    }

    private static IReadOnlyList<ChatMessage> Compact(
        IReadOnlyList<ChatMessage> source,
        ChatMessage? anchor)
    {
        var nonSystem = source
            .Where(message => message.Role != ChatRole.System)
            .ToList();
        var omittedCount = Math.Max(0, nonSystem.Count - (MaximumRetainedNonSystemMessages + 1));
        var retained = omittedCount == 0
            ? nonSystem
            : RetainTurnAnchorAndTail(nonSystem, anchor);

        var systemText = string.Join(
            Environment.NewLine,
            source
                .Where(message => message.Role == ChatRole.System)
                .Select(message => message.Text)
                .Where(text => !string.IsNullOrWhiteSpace(text)));
        if (omittedCount > 0)
        {
            systemText = string.Join(
                Environment.NewLine,
                systemText,
                $"{omittedCount} older tool-loop message(s) were compacted to protect the active model window. "
                + "The original request and the most recent tool evidence remain. Inspect the current Workspace or rerun a read-only diagnostic whenever older source detail is needed.");
        }

        var result = new List<ChatMessage>
        {
            new(
                ChatRole.System,
                AliToolCallingChatClient.CompactContextText(
                    systemText,
                    MaximumSystemCharacters,
                    "core framework instructions"))
        };
        result.AddRange(retained.Select(message =>
            anchor is not null && SameMessage(message, anchor)
                ? message
                : CompactMessage(message)));
        return result;
    }

    private static List<ChatMessage> RetainTurnAnchorAndTail(
        IReadOnlyList<ChatMessage> nonSystem,
        ChatMessage? anchor)
    {
        var effectiveAnchor = anchor ?? nonSystem[0];
        var retained = new List<ChatMessage>(MaximumRetainedNonSystemMessages + 1)
        {
            effectiveAnchor
        };
        retained.AddRange(nonSystem
            .TakeLast(MaximumRetainedNonSystemMessages)
            .Where(message => !SameMessage(message, effectiveAnchor)));
        return retained;
    }

    private static bool SameMessage(ChatMessage left, ChatMessage right) =>
        ReferenceEquals(left, right)
        || (left.Role == right.Role
            && string.Equals(left.Text, right.Text, StringComparison.Ordinal));

    private sealed class ToolFocusLease(
        IReadOnlySet<string>? previousNames,
        RequiredToolFocus? previousRequired) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                FocusedToolNames.Value = previousNames;
                RequiredFocusedTool.Value = previousRequired;
            }
        }
    }

    private sealed class BareJsonToolCallLease(IReadOnlySet<string>? previousNames) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                BareJsonToolNames.Value = previousNames;
            }
        }
    }

    private sealed class RequiredToolFocus(string toolName)
    {
        internal string ToolName { get; } = toolName;

        internal int Consumed;
    }

    private static ChatMessage CompactMessage(ChatMessage message)
    {
        var contents = new List<AIContent>();
        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case TextContent text:
                    contents.Add(new TextContent(AliToolCallingChatClient.CompactContextText(
                        text.Text ?? string.Empty,
                        MaximumMessageCharacters,
                        message.Role == ChatRole.Tool
                            ? "core tool message"
                            : "core conversation message")));
                    break;
                case FunctionCallContent call:
                    contents.Add(new FunctionCallContent(
                        call.CallId,
                        call.Name,
                        CompactArguments(call.Arguments)));
                    break;
                case FunctionResultContent functionResult:
                    contents.Add(new FunctionResultContent(
                        functionResult.CallId,
                        CompactFunctionResult(functionResult)));
                    break;
                default:
                    contents.Add(content);
                    break;
            }
        }

        return new ChatMessage(message.Role, contents);
    }

    private static Dictionary<string, object?> CompactArguments(
        IDictionary<string, object?>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        }

        var serialized = JsonSerializer.Serialize(arguments);
        if (serialized.Length <= MaximumArgumentCharacters)
        {
            return new Dictionary<string, object?>(arguments, StringComparer.Ordinal);
        }

        return arguments.ToDictionary(
            pair => pair.Key,
            pair => CompactArgumentValue(pair.Value),
            StringComparer.Ordinal);
    }

    private static object? CompactArgumentValue(object? value)
    {
        if (value is null or bool
            || value is byte or sbyte or short or ushort or int or uint or long or ulong
            || value is float or double or decimal)
        {
            return value;
        }

        var text = value switch
        {
            string stringValue => stringValue,
            JsonElement element => element.GetRawText(),
            _ => JsonSerializer.Serialize(value)
        };
        return AliToolCallingChatClient.CompactContextText(
            text,
            MaximumArgumentValueCharacters,
            "executed tool argument");
    }

    private static string CompactFunctionResult(FunctionResultContent result)
    {
        if (result.Exception is not null)
        {
            var diagnostic = ExactExceptionMessage(result.Exception);
            return JsonSerializer.Serialize(new
            {
                success = false,
                status = "exception",
                exceptionType = result.Exception.GetType().Name,
                message = AliToolCallingChatClient.CompactContextText(
                    diagnostic,
                    MaximumToolResultCharacters,
                    "core tool exception")
            });
        }

        var serialized = AliToolCallingChatClient.SerializeToolResultForModel(result.Result);
        return AliToolCallingChatClient.CompactContextText(
            serialized,
            MaximumToolResultCharacters,
            "core tool result");
    }

    private static string ExactExceptionMessage(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var current = exception;
        while (current.InnerException is not null)
        {
            current = current.InnerException;
        }

        return string.IsNullOrWhiteSpace(current.Message)
            ? exception.Message
            : current.Message;
    }
}
