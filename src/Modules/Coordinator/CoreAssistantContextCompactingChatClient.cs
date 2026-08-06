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

    private readonly IChatClient _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private static readonly AsyncLocal<IReadOnlySet<string>?> FocusedToolNames = new();
    private static readonly AsyncLocal<RequiredToolFocus?> RequiredFocusedTool = new();
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

        // Buffer only while the accumulated text could still become (or
        // already matches the start of) the textual tool-call marker; the
        // moment it provably can't, flush everything buffered as normal text
        // and stop intercepting for the rest of this response. This keeps the
        // added latency to at most the first few characters for ordinary
        // chat, not the whole response.
        var buffer = new System.Text.StringBuilder();
        var intercepting = registeredTools.Length > 0;

        await foreach (var update in _inner
                           .GetStreamingResponseAsync(
                               CompactForTurn(messages),
                               effectiveOptions,
                               cancellationToken)
                           .ConfigureAwait(false))
        {
            if (!intercepting)
            {
                yield return update;
                continue;
            }

            if (update.Contents.Any(content => content is not TextContent))
            {
                // A real structured tool call (or other content) already
                // arrived -- this response was never a textual marker.
                intercepting = false;
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
                continue;
            }
            buffer.Append(deltaText);
            var accumulated = buffer.ToString();
            if (TextualToolCallPrefix.StartsWith(accumulated, StringComparison.Ordinal)
                || accumulated.StartsWith(TextualToolCallPrefix, StringComparison.Ordinal))
            {
                continue; // still could become (or already matches) the marker
            }

            intercepting = false;
            yield return new ChatResponseUpdate(update.Role, accumulated);
            buffer.Clear();
        }

        if (buffer.Length == 0)
        {
            yield break;
        }

        var raw = buffer.ToString();
        var promotedCall = TryParseTextualToolCall(raw, registeredTools);
        yield return promotedCall is not null
            ? new ChatResponseUpdate(ChatRole.Assistant, [promotedCall])
            : new ChatResponseUpdate(ChatRole.Assistant, raw);
    }

    private static FunctionCallContent? TryParseTextualToolCall(
        string raw,
        IReadOnlyList<AIFunctionDeclaration> registeredTools)
    {
        var trimmed = raw.Trim();
        if (!trimmed.StartsWith(TextualToolCallPrefix, StringComparison.Ordinal)
            || trimmed.IndexOf(TextualToolCallPrefix, TextualToolCallPrefix.Length, StringComparison.Ordinal) >= 0)
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
        if (toolName.Length == 0
            || !registeredTools.Any(tool => string.Equals(tool.Name, toolName, StringComparison.Ordinal)))
        {
            return null;
        }

        var argumentsJson = trimmed[(argumentsMarkerIndex + TextualToolArgumentsMarker.Length)..].Trim();
        if (argumentsJson.Length == 0)
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
