using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
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
            var messageList = messages.ToList();
            var useNativeOllama = IsNativeOllamaEndpoint();
            var uri = useNativeOllama ? BuildOllamaApiUri("chat") : BuildUri("chat/completions");
            var payload = SerializeExtensionsAiPayload(messageList, options, useNativeOllama);

            using var request = new HttpRequestMessage(HttpMethod.Post, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Tool-capable chat completion failed with HTTP {(int)response.StatusCode}. {TrimForUser(body)}");
            }

            return ParseExtensionsAiResponse(body, useNativeOllama);
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
                LocalRuntimeEngines.Normalize(_options.Engine, _options.Endpoint),
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
            && properties.TryGetValue("ali.internalRouting", out var internalRouting)
            && internalRouting is true;
        var serializedMessages = BuildExtensionsAiMessages(messages, suppressPersona);
        var tools = BuildExtensionsAiTools(options).ToArray();
        var maxTokens = options?.MaxOutputTokens ?? _options.OutputTokenLimit;
        object payload = useNativeOllama
            ? new
            {
                model = _options.Model,
                messages = serializedMessages,
                tools = tools.Length == 0 ? null : tools,
                stream = false,
                think = ResolveNativeThinkingValue(),
                keep_alive = OllamaRuntimeSafetyPolicy.KeepAlive,
                options = new
                {
                    num_ctx = ResolveSafeOllamaContextTokens(),
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
                tool_choice = ResolveToolChoice(options, tools.Length),
                parallel_tool_calls = tools.Length == 0
                    ? (bool?)null
                    : options?.AllowMultipleToolCalls ?? false,
                response_format = ResolveResponseFormat(options?.ResponseFormat),
                stream = false,
                max_tokens = maxTokens,
                temperature = _options.Temperature,
                top_p = _options.TopP,
                chat_template_kwargs = ResolveOpenAiChatTemplateKwargs(),
                think = ShouldDisableThinking() ? false : (bool?)null
            };

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        if (useNativeOllama)
        {
            ValidateNativeOllamaPayload(json);
        }
        else
        {
            ValidateOpenAiCompatiblePayload(json);
        }

        return json;
    }

    private object[] BuildExtensionsAiMessages(
        IReadOnlyList<MeaiChatMessage> messages,
        bool suppressPersona)
    {
        var serialized = new List<object>();
        if (!suppressPersona)
        {
            serialized.Add(new { role = "system", content = (object)BuildAssistantPersonaInstruction() });
            serialized.Add(new { role = "system", content = (object)BuildCurrentDateInstruction() });
        }

        foreach (var message in messages)
        {
            var functionCalls = message.Contents.OfType<FunctionCallContent>().ToArray();
            var functionResults = message.Contents.OfType<FunctionResultContent>().ToArray();
            if (functionResults.Length > 0)
            {
                foreach (var result in functionResults)
                {
                    serialized.Add(new
                    {
                        role = "tool",
                        tool_call_id = result.CallId,
                        content = SerializeToolResult(result.Result)
                    });
                }

                continue;
            }

            if (functionCalls.Length > 0)
            {
                serialized.Add(new
                {
                    role = "assistant",
                    content = string.IsNullOrWhiteSpace(message.Text) ? null : message.Text,
                    tool_calls = functionCalls.Select(call => new
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

            serialized.Add(new
            {
                role = message.Role.ToString().ToLowerInvariant(),
                content = (object)(message.Text ?? string.Empty)
            });
        }

        return serialized.ToArray();
    }

    private static IEnumerable<object> BuildExtensionsAiTools(ChatOptions? options)
    {
        if (options?.Tools is null || options.ToolMode is NoneChatToolMode)
        {
            yield break;
        }

        foreach (var function in options.Tools.OfType<AIFunctionDeclaration>())
        {
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

    private static object? ResolveToolChoice(ChatOptions? options, int toolCount)
    {
        if (toolCount == 0)
        {
            return null;
        }

        return options?.ToolMode switch
        {
            NoneChatToolMode => "none",
            RequiredChatToolMode { RequiredFunctionName: { Length: > 0 } functionName } => new
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
