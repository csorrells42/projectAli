using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using MeaiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using MeaiChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Ali.Modules.Runtime;

public sealed partial class OpenAiCompatibleLocalModelRuntime
{
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<MeaiChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (Interlocked.CompareExchange(ref _requestInFlight, 1, 0) != 0)
        {
            throw new InvalidOperationException(
                "The local model runtime already has one request in flight. Ali does not queue or overlap model generations.");
        }

        try
        {
            EnsureEndpointAllowed();
            await EnsureLemonadeModelLoadedAsync(cancellationToken).ConfigureAwait(false);
            var messageList = messages.ToList();
            var requestOptions = options?.Clone() ?? new ChatOptions();
            requestOptions.MaxOutputTokens = ResolveExtensionsAiMaxTokens(requestOptions.MaxOutputTokens);
            var useNativeOllama = IsNativeOllamaEndpoint();
            var uri = useNativeOllama ? BuildOllamaApiUri("chat") : BuildUri("chat/completions");
            cancellationToken.ThrowIfCancellationRequested();
            var payload = SerializeExtensionsAiPayload(messageList, requestOptions, useNativeOllama);
            using var request = new HttpRequestMessage(HttpMethod.Post, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            using var response = await SendRuntimeAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return ParseExtensionsAiResponse(body, useNativeOllama);
            }

            throw new HttpRequestException(
                FormatChatHttpError(response.StatusCode, body)
                + " Serialized message roles: "
                + DescribeExtensionsAiMessageRoles(payload));
        }
        finally
        {
            Volatile.Write(ref _requestInFlight, 0);
        }
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

        return serviceType == typeof(ChatClientMetadata)
            ? new ChatClientMetadata(
                LocalRuntimeEngines.Normalize(_options.Engine),
                _options.Endpoint,
                _options.Model)
            : null;
    }

    public void Dispose()
    {
        // AliServices owns the shared HttpClient and runtime lifecycle.
    }

    private string SerializeExtensionsAiPayload(
        IReadOnlyList<MeaiChatMessage> messages,
        ChatOptions? options,
        bool useNativeOllama)
    {
        var suppressPersona = options?.AdditionalProperties is { } properties
            && properties.TryGetValue(
                AliInternalModelRoutingProperties.SuppressInjectedPersona,
                out var internalRouting)
            && internalRouting is true;
        var boundReasoningEffort = ResolveBoundReasoningEffort(options);
        var serializedMessages = BuildExtensionsAiMessages(
            messages,
            suppressPersona,
            useNativeOllama,
            options?.Instructions);
        var lmStudioRequiredFunctionName = IsLmStudioEndpoint()
            && options?.ToolMode is RequiredChatToolMode { RequiredFunctionName: { Length: > 0 } requiredName }
                ? requiredName
                : null;
        var tools = BuildExtensionsAiTools(options, lmStudioRequiredFunctionName).ToArray();
        if (lmStudioRequiredFunctionName is not null && tools.Length != 1)
        {
            throw new InvalidOperationException(
                $"LM Studio required tool '{lmStudioRequiredFunctionName}' was not registered exactly once for this request.");
        }
        var maxTokens = ResolveExtensionsAiMaxTokens(options?.MaxOutputTokens);
        object payload = useNativeOllama
            ? new
            {
                model = _options.Model,
                messages = serializedMessages,
                tools = tools.Length == 0 ? null : tools,
                stream = false,
                think = ResolveNativeThinkingValue(boundReasoningEffort),
                keep_alive = OllamaRuntimeSafetyPolicy.KeepAlive,
                options = new
                {
                    num_ctx = _options.ContextTokens,
                    num_predict = maxTokens,
                    temperature = _options.Temperature,
                    top_p = _options.TopP
                }
            }
            : new
            {
                model = _options.Model,
                messages = serializedMessages,
                tools = tools.Length == 0 ? null : tools,
                tool_choice = ResolveToolChoice(
                    options,
                    tools.Length,
                    supportsNamedRequiredToolChoice: !IsLmStudioEndpoint()),
                parallel_tool_calls = tools.Length == 0
                    ? (bool?)null
                    : options?.AllowMultipleToolCalls ?? false,
                response_format = ResolveResponseFormat(options?.ResponseFormat),
                stream = false,
                max_tokens = maxTokens,
                temperature = _options.Temperature,
                top_p = _options.TopP,
                chat_template_kwargs = ResolveOpenAiChatTemplateKwargs(boundReasoningEffort),
                think = (bool?)null
            };

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        if (useNativeOllama)
        {
            ValidateNativeOllamaPayload(json, boundReasoningEffort);
        }
        else
        {
            ValidateOpenAiCompatiblePayload(json, boundReasoningEffort);
        }

        return json;
    }

    private static string? ResolveBoundReasoningEffort(ChatOptions? options)
    {
        if (options?.AdditionalProperties is not { } properties
            || !properties.TryGetValue(
                AliInternalModelRoutingProperties.BoundReasoningEffort,
                out var boundValue))
        {
            return null;
        }

        if (boundValue is not string boundReasoningEffort
            || string.IsNullOrWhiteSpace(boundReasoningEffort))
        {
            throw new InvalidOperationException(
                "A bound model dispatch supplied an invalid reasoning-effort snapshot.");
        }

        return boundReasoningEffort;
    }

    private int ResolveExtensionsAiMaxTokens(int? requestedMaxTokens)
    {
        var resolved = requestedMaxTokens ?? _options.OutputTokenLimit;
        if (resolved < 1)
        {
            throw new InvalidOperationException("A model request must reserve at least one output token.");
        }

        if (resolved > _options.OutputTokenLimit)
        {
            throw new InvalidOperationException(
                $"The request asked for {resolved:N0} output tokens, which exceeds the selected runtime limit of {_options.OutputTokenLimit:N0}.");
        }

        return resolved;
    }

    private object[] BuildExtensionsAiMessages(
        IReadOnlyList<MeaiChatMessage> messages,
        bool suppressPersona,
        bool useNativeOllama,
        string? requestInstructions)
    {
        var serialized = new List<ExtensionsAiMessagePayload>();
        var serializedToolCallIds = new HashSet<string>(StringComparer.Ordinal);
        var systemInstructions = new List<string>();
        if (!suppressPersona)
        {
            systemInstructions.Add(BuildPrimarySystemInstruction(BuildAssistantPersonaInstruction()));
            systemInstructions.Add(BuildCurrentDateInstruction());
        }
        if (!string.IsNullOrWhiteSpace(requestInstructions))
        {
            systemInstructions.Add(requestInstructions.Trim());
        }
        foreach (var message in messages.Where(message => message.Role == MeaiChatRole.System))
        {
            if (!string.IsNullOrWhiteSpace(message.Text))
            {
                systemInstructions.Add(message.Text.Trim());
            }
        }
        if (systemInstructions.Count > 0)
        {
            serialized.Add(new ExtensionsAiMessagePayload
            {
                Role = "system",
                Content = string.Join(Environment.NewLine + Environment.NewLine, systemInstructions)
            });
        }

        foreach (var message in messages)
        {
            if (message.Role == MeaiChatRole.System)
            {
                continue;
            }

            var functionCalls = message.Contents.OfType<FunctionCallContent>().ToArray();
            var functionResults = message.Contents.OfType<FunctionResultContent>().ToArray();
            if (functionResults.Length > 0)
            {
                foreach (var result in functionResults)
                {
                    if (serializedToolCallIds.Contains(result.CallId))
                    {
                        serialized.Add(new ExtensionsAiMessagePayload
                        {
                            Role = "tool",
                            ToolCallId = result.CallId,
                            Content = SerializeToolResult(result.Result)
                        });
                    }
                    else
                    {
                        AddPlainMessage(
                            serialized,
                            "user",
                            "AUTHORITATIVE TOOL RESULT (the earlier protocol call was compacted): "
                            + SerializeToolResult(result.Result));
                    }
                }

                continue;
            }

            if (functionCalls.Length > 0)
            {
                foreach (var call in functionCalls)
                {
                    serializedToolCallIds.Add(call.CallId);
                }

                object? preservedAssistantContent = null;
                if (serialized.Count > 0
                    && string.Equals(serialized[^1].Role, "assistant", StringComparison.Ordinal)
                    && serialized[^1].ToolCalls is null)
                {
                    preservedAssistantContent = serialized[^1].Content;
                    serialized.RemoveAt(serialized.Count - 1);
                }

                serialized.Add(new ExtensionsAiMessagePayload
                {
                    Role = "assistant",
                    Content = MergeAssistantContent(preservedAssistantContent, message.Text),
                    ToolCalls = functionCalls.Select(call => new
                    {
                        id = call.CallId,
                        type = "function",
                        function = new
                        {
                            name = call.Name,
                            arguments = JsonSerializer.Serialize(call.Arguments, JsonOptions)
                        }
                    }).ToArray()
                });
                continue;
            }

            if (message.Role == MeaiChatRole.Tool)
            {
                AddPlainMessage(
                    serialized,
                    "user",
                    "AUTHORITATIVE TOOL RESULT: " + (message.Text ?? string.Empty));
                continue;
            }

            var imageData = message.Contents
                .OfType<DataContent>()
                .Where(content => content.HasTopLevelMediaType("image"))
                .ToArray();
            var imageUris = message.Contents
                .OfType<UriContent>()
                .Where(content => content.HasTopLevelMediaType("image"))
                .ToArray();
            if (imageData.Length == 0 && imageUris.Length == 0)
            {
                AddPlainMessage(
                    serialized,
                    message.Role.ToString().ToLowerInvariant(),
                    message.Text ?? string.Empty);
                continue;
            }

            if (useNativeOllama)
            {
                if (imageUris.Length > 0)
                {
                    throw new NotSupportedException("Ollama image messages require in-memory image data, not remote image URIs.");
                }

                serialized.Add(new ExtensionsAiMessagePayload
                {
                    Role = message.Role.ToString().ToLowerInvariant(),
                    Content = message.Text ?? string.Empty,
                    Images = imageData.Select(content => content.Base64Data).ToArray()
                });
                continue;
            }

            var parts = new List<object>();
            if (!string.IsNullOrWhiteSpace(message.Text))
            {
                parts.Add(new { type = "text", text = message.Text });
            }

            parts.AddRange(imageData.Select(content => (object)new
            {
                type = "image_url",
                image_url = new { url = content.Uri }
            }));
            parts.AddRange(imageUris.Select(content => (object)new
            {
                type = "image_url",
                image_url = new { url = content.Uri }
            }));
            serialized.Add(new ExtensionsAiMessagePayload
            {
                Role = message.Role.ToString().ToLowerInvariant(),
                Content = parts
            });
        }

        EnsureConversationStartsWithUser(serialized);
        return serialized.Cast<object>().ToArray();
    }

    private static object? MergeAssistantContent(object? preservedContent, string? currentText)
    {
        var preserved = preservedContent as string;
        if (string.IsNullOrWhiteSpace(preserved))
        {
            return string.IsNullOrWhiteSpace(currentText) ? null : currentText;
        }

        return string.IsNullOrWhiteSpace(currentText)
            ? preserved
            : string.Join(Environment.NewLine + Environment.NewLine, preserved, currentText);
    }

    private static void EnsureConversationStartsWithUser(List<ExtensionsAiMessagePayload> serialized)
    {
        var firstConversationIndex = serialized.Count > 0
                                     && string.Equals(serialized[0].Role, "system", StringComparison.Ordinal)
            ? 1
            : 0;
        if (firstConversationIndex < serialized.Count
            && string.Equals(serialized[firstConversationIndex].Role, "assistant", StringComparison.Ordinal))
        {
            serialized.Insert(firstConversationIndex, new ExtensionsAiMessagePayload
            {
                Role = "user",
                Content = "Continue the preserved request."
            });
        }
    }

    private static void AddPlainMessage(
        List<ExtensionsAiMessagePayload> serialized,
        string role,
        string content)
    {
        if (serialized.Count > 0
            && string.Equals(role, "user", StringComparison.Ordinal)
            && string.Equals(serialized[^1].Role, "tool", StringComparison.Ordinal))
        {
            serialized.Add(new ExtensionsAiMessagePayload
            {
                Role = "assistant",
                Content = "Tool result received."
            });
        }

        if (serialized.Count > 0
            && string.Equals(serialized[^1].Role, role, StringComparison.Ordinal)
            && role is "user" or "assistant"
            && serialized[^1].ToolCalls is null
            && serialized[^1].Content is string previous)
        {
            serialized[^1].Content = string.Join(
                Environment.NewLine + Environment.NewLine,
                previous,
                content);
            return;
        }

        serialized.Add(new ExtensionsAiMessagePayload
        {
            Role = role,
            Content = content
        });
    }

    private static string DescribeExtensionsAiMessageRoles(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            return string.Join(
                " > ",
                document.RootElement
                    .GetProperty("messages")
                    .EnumerateArray()
                    .Select(message =>
                    {
                        var role = message.GetProperty("role").GetString() ?? "unknown";
                        return message.TryGetProperty("tool_calls", out var calls)
                               && calls.ValueKind == JsonValueKind.Array
                               && calls.GetArrayLength() > 0
                            ? role + "(tool-call)"
                            : role;
                    }));
        }
        catch (JsonException)
        {
            return "unavailable";
        }
    }

    private sealed class ExtensionsAiMessagePayload
    {
        public required string Role { get; init; }

        public object? Content { get; set; }

        [JsonPropertyName("tool_call_id")]
        public string? ToolCallId { get; init; }

        [JsonPropertyName("tool_calls")]
        public object? ToolCalls { get; init; }

        public ReadOnlyMemory<char>[]? Images { get; init; }
    }

    private static IEnumerable<object> BuildExtensionsAiTools(
        ChatOptions? options,
        string? requiredFunctionName = null)
    {
        if (options?.Tools is null || options.ToolMode is NoneChatToolMode)
        {
            yield break;
        }

        foreach (var function in options.Tools.OfType<AIFunctionDeclaration>())
        {
            if (requiredFunctionName is not null
                && !string.Equals(function.Name, requiredFunctionName, StringComparison.Ordinal))
            {
                continue;
            }

            yield return new
            {
                type = "function",
                function = new
                {
                    name = function.Name,
                    description = function.Description,
                    parameters = function.JsonSchema
                }
            };
        }
    }

    private static object? ResolveToolChoice(
        ChatOptions? options,
        int toolCount,
        bool supportsNamedRequiredToolChoice)
    {
        if (toolCount == 0)
        {
            return null;
        }

        return options?.ToolMode switch
        {
            NoneChatToolMode => "none",
            RequiredChatToolMode { RequiredFunctionName: { Length: > 0 } functionName }
                when supportsNamedRequiredToolChoice => new
            {
                type = "function",
                function = new { name = functionName }
            },
            RequiredChatToolMode => "required",
            _ => "auto"
        };
    }

    private static object? ResolveResponseFormat(ChatResponseFormat? responseFormat) => responseFormat switch
    {
        ChatResponseFormatJson { Schema: { } schema } json => new
        {
            type = "json_schema",
            json_schema = new
            {
                name = string.IsNullOrWhiteSpace(json.SchemaName) ? "response" : json.SchemaName,
                description = json.SchemaDescription,
                strict = true,
                schema
            }
        },
        ChatResponseFormatJson => new { type = "json_object" },
        _ => null
    };

    private static string SerializeToolResult(object? result) =>
        result switch
        {
            null => "null",
            string text => text,
            JsonElement element => element.GetRawText(),
            _ => JsonSerializer.Serialize(result, JsonOptions)
        };

    private static ChatResponse ParseExtensionsAiResponse(string json, bool nativeOllama)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        JsonElement message;
        string? finishReason;
        if (nativeOllama)
        {
            if (!root.TryGetProperty("message", out message))
            {
                throw new JsonException("Ollama tool response did not contain a message.");
            }

            finishReason = root.TryGetProperty("done_reason", out var nativeFinish)
                && nativeFinish.ValueKind == JsonValueKind.String
                    ? nativeFinish.GetString()
                    : null;
        }
        else
        {
            if (!root.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0
                || !choices[0].TryGetProperty("message", out message))
            {
                throw new JsonException("OpenAI-compatible tool response did not contain a choice message.");
            }

            finishReason = choices[0].TryGetProperty("finish_reason", out var openAiFinish)
                && openAiFinish.ValueKind == JsonValueKind.String
                    ? openAiFinish.GetString()
                    : null;
        }

        var contents = new List<AIContent>();
        if (message.TryGetProperty("content", out var content)
            && content.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(content.GetString()))
        {
            contents.Add(new TextContent(content.GetString()!));
        }

        if (message.TryGetProperty("tool_calls", out var toolCalls)
            && toolCalls.ValueKind == JsonValueKind.Array)
        {
            foreach (var toolCall in toolCalls.EnumerateArray())
            {
                var callId = toolCall.TryGetProperty("id", out var id)
                    && id.ValueKind == JsonValueKind.String
                        ? id.GetString()
                        : null;
                var function = toolCall.TryGetProperty("function", out var nestedFunction)
                    ? nestedFunction
                    : toolCall;
                var name = function.TryGetProperty("name", out var functionName)
                    && functionName.ValueKind == JsonValueKind.String
                        ? functionName.GetString()
                        : null;
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var arguments = function.TryGetProperty("arguments", out var functionArguments)
                    ? ParseFunctionArguments(functionArguments)
                    : new Dictionary<string, object?>();
                contents.Add(new FunctionCallContent(
                    callId ?? $"call_{Guid.NewGuid():N}",
                    name,
                    arguments));
            }
        }

        var response = new ChatResponse(new MeaiChatMessage(MeaiChatRole.Assistant, contents));
        if (!string.IsNullOrWhiteSpace(finishReason))
        {
            response.FinishReason = new ChatFinishReason(finishReason);
        }

        return response;
    }

    private static Dictionary<string, object?> ParseFunctionArguments(JsonElement arguments)
    {
        JsonElement root = arguments;
        JsonDocument? parsed = null;
        if (arguments.ValueKind == JsonValueKind.String)
        {
            var text = arguments.GetString();
            if (string.IsNullOrWhiteSpace(text))
            {
                return new Dictionary<string, object?>();
            }

            parsed = JsonDocument.Parse(text);
            root = parsed.RootElement;
        }

        try
        {
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new Dictionary<string, object?>();
            }

            return root.EnumerateObject().ToDictionary(
                property => property.Name,
                property => (object?)property.Value.Clone(),
                StringComparer.Ordinal);
        }
        finally
        {
            parsed?.Dispose();
        }
    }
}
