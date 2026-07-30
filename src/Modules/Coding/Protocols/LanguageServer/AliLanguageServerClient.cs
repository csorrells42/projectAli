using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ali.Modules.Coding.Protocols.LanguageServer;

public sealed record AliLspPosition(int Line, int Character);
public sealed record AliLspRange(AliLspPosition Start, AliLspPosition End);
public sealed record AliLspDiagnostic(
    string DocumentPath,
    AliLspRange Range,
    int? Severity,
    string? Code,
    string Source,
    string Message);
public sealed record AliLspLocation(string DocumentPath, AliLspRange Range);
public sealed record AliLspOperationResult(bool Success, string Summary, JsonNode? Result);

/// <summary>
/// Language-neutral JSON-RPC client for a trusted, provider-resolved LSP adapter.
/// It supplies the semantic operations Ali needs without exposing arbitrary process launch.
/// </summary>
internal sealed class AliLanguageServerClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly AliProtocolProcess _process;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly ConcurrentDictionary<string, AliLspDiagnostic[]> _diagnostics = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _reader;
    private long _requestId;
    private bool _initialized;

    private AliLanguageServerClient(AliProtocolProcess process)
    {
        _process = process;
        _reader = Task.Run(ReadLoopAsync);
    }

    public static async Task<AliLanguageServerClient> StartAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workspaceRoot,
        CancellationToken cancellationToken)
    {
        var process = AliProtocolProcess.Start(executable, workspaceRoot, arguments);
        var client = new AliLanguageServerClient(process);
        try
        {
            var rootUri = ToDocumentUri(workspaceRoot);
            await client.RequestAsync(
                "initialize",
                new
                {
                    processId = Environment.ProcessId,
                    clientInfo = new { name = "Ali", version = "1" },
                    rootUri,
                    workspaceFolders = new[] { new { uri = rootUri, name = Path.GetFileName(workspaceRoot) } },
                    capabilities = new
                    {
                        workspace = new { workspaceFolders = true, configuration = true },
                        textDocument = new
                        {
                            synchronization = new { didSave = true, dynamicRegistration = false },
                            completion = new { completionItem = new { snippetSupport = false } },
                            hover = new { contentFormat = new[] { "markdown", "plaintext" } },
                            definition = new { linkSupport = true },
                            references = new { },
                            rename = new { prepareSupport = false },
                            formatting = new { }
                        }
                    }
                },
                cancellationToken).ConfigureAwait(false);
            await client.NotifyAsync("initialized", new { }, cancellationToken).ConfigureAwait(false);
            client._initialized = true;
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task OpenDocumentAsync(
        string documentPath,
        string languageId,
        string text,
        int version,
        CancellationToken cancellationToken) =>
        NotifyAsync(
            "textDocument/didOpen",
            new { textDocument = new { uri = ToDocumentUri(documentPath), languageId, version, text } },
            cancellationToken);

    public Task ChangeDocumentAsync(
        string documentPath,
        string text,
        int version,
        CancellationToken cancellationToken) =>
        NotifyAsync(
            "textDocument/didChange",
            new
            {
                textDocument = new { uri = ToDocumentUri(documentPath), version },
                contentChanges = new[] { new { text } }
            },
            cancellationToken);

    public Task CloseDocumentAsync(string documentPath, CancellationToken cancellationToken) =>
        NotifyAsync(
            "textDocument/didClose",
            new { textDocument = new { uri = ToDocumentUri(documentPath) } },
            cancellationToken);

    public Task<JsonObject> CompletionAsync(string documentPath, int line, int character, CancellationToken cancellationToken) =>
        PositionRequestAsync("textDocument/completion", documentPath, line, character, cancellationToken);

    public Task<JsonObject> HoverAsync(string documentPath, int line, int character, CancellationToken cancellationToken) =>
        PositionRequestAsync("textDocument/hover", documentPath, line, character, cancellationToken);

    public Task<JsonObject> DefinitionAsync(string documentPath, int line, int character, CancellationToken cancellationToken) =>
        PositionRequestAsync("textDocument/definition", documentPath, line, character, cancellationToken);

    public Task<JsonObject> ReferencesAsync(string documentPath, int line, int character, CancellationToken cancellationToken) =>
        RequestAsync(
            "textDocument/references",
            new
            {
                textDocument = new { uri = ToDocumentUri(documentPath) },
                position = new { line, character },
                context = new { includeDeclaration = true }
            },
            cancellationToken);

    public Task<JsonObject> FormattingAsync(string documentPath, CancellationToken cancellationToken) =>
        RequestAsync(
            "textDocument/formatting",
            new
            {
                textDocument = new { uri = ToDocumentUri(documentPath) },
                options = new { tabSize = 4, insertSpaces = true, trimTrailingWhitespace = true, insertFinalNewline = true }
            },
            cancellationToken);

    public Task<JsonObject> RenameAsync(string documentPath, int line, int character, string newName, CancellationToken cancellationToken) =>
        RequestAsync(
            "textDocument/rename",
            new
            {
                textDocument = new { uri = ToDocumentUri(documentPath) },
                position = new { line, character },
                newName
            },
            cancellationToken);

    public Task<JsonObject> WorkspaceSymbolsAsync(string query, CancellationToken cancellationToken) =>
        RequestAsync("workspace/symbol", new { query }, cancellationToken);

    public IReadOnlyList<AliLspDiagnostic> GetDiagnostics(string? documentPath = null)
    {
        if (string.IsNullOrWhiteSpace(documentPath))
        {
            return _diagnostics.Values.SelectMany(value => value).ToArray();
        }

        return _diagnostics.TryGetValue(Path.GetFullPath(documentPath), out var diagnostics)
            ? diagnostics
            : [];
    }

    private Task<JsonObject> PositionRequestAsync(
        string method,
        string documentPath,
        int line,
        int character,
        CancellationToken cancellationToken) =>
        RequestAsync(
            method,
            new { textDocument = new { uri = ToDocumentUri(documentPath) }, position = new { line, character } },
            cancellationToken);

    internal async Task<JsonObject> RequestAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _requestId);
        var completion = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        var message = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = JsonSerializer.SerializeToNode(parameters, JsonOptions)
        };
        await _process.Transport.WriteAsync(message, cancellationToken).ConfigureAwait(false);
        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        var response = await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (response["error"] is JsonNode error)
        {
            throw new InvalidOperationException($"Language server method '{method}' failed: {error.ToJsonString()}.");
        }

        return response;
    }

    internal Task NotifyAsync(string method, object parameters, CancellationToken cancellationToken) =>
        _process.Transport.WriteAsync(
            new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = method,
                ["params"] = JsonSerializer.SerializeToNode(parameters, JsonOptions)
            },
            cancellationToken);

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var message = await _process.Transport.ReadAsync(_lifetime.Token).ConfigureAwait(false);
                if (message is null)
                {
                    break;
                }

                if (TryReadId(message, out var responseId)
                    && message["method"] is null
                    && _pending.TryRemove(responseId, out var pending))
                {
                    pending.TrySetResult(message);
                    continue;
                }

                var method = message["method"]?.GetValue<string>();
                if (method == "textDocument/publishDiagnostics")
                {
                    CaptureDiagnostics(message["params"] as JsonObject);
                }
                else if (method is not null && TryReadId(message, out var requestId))
                {
                    await ReplyToServerRequestAsync(requestId, method, _lifetime.Token).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or JsonException or OperationCanceledException)
        {
            foreach (var pending in _pending.Values)
            {
                pending.TrySetException(ex);
            }
        }
    }

    private Task ReplyToServerRequestAsync(long id, string method, CancellationToken cancellationToken)
    {
        JsonNode? result = method switch
        {
            "workspace/configuration" => new JsonArray(),
            "workspace/workspaceFolders" => new JsonArray(),
            "client/registerCapability" or "client/unregisterCapability" => null,
            _ => null
        };
        return _process.Transport.WriteAsync(
            new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result },
            cancellationToken);
    }

    private void CaptureDiagnostics(JsonObject? parameters)
    {
        var uri = parameters?["uri"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(uri) || !Uri.TryCreate(uri, UriKind.Absolute, out var parsedUri) || !parsedUri.IsFile)
        {
            return;
        }

        var path = Path.GetFullPath(parsedUri.LocalPath);
        var diagnostics = (parameters?["diagnostics"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(node => new AliLspDiagnostic(
                path,
                ParseRange(node["range"] as JsonObject),
                node["severity"]?.GetValue<int?>(),
                ReadScalar(node["code"]),
                node["source"]?.GetValue<string>() ?? "language-server",
                node["message"]?.GetValue<string>() ?? "Language server diagnostic"))
            .ToArray();
        _diagnostics[path] = diagnostics;
    }

    private static AliLspRange ParseRange(JsonObject? range) =>
        new(ParsePosition(range?["start"] as JsonObject), ParsePosition(range?["end"] as JsonObject));

    private static AliLspPosition ParsePosition(JsonObject? position) =>
        new(position?["line"]?.GetValue<int>() ?? 0, position?["character"]?.GetValue<int>() ?? 0);

    private static string? ReadScalar(JsonNode? node) => node switch
    {
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        JsonValue value when value.TryGetValue<int>(out var number) => number.ToString(),
        _ => null
    };

    private static bool TryReadId(JsonObject message, out long id)
    {
        id = 0;
        return message["id"] is JsonValue value && value.TryGetValue(out id);
    }

    internal static string ToDocumentUri(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (Directory.Exists(fullPath) && !fullPath.EndsWith(Path.DirectorySeparatorChar))
        {
            fullPath += Path.DirectorySeparatorChar;
        }
        return new Uri(fullPath).AbsoluteUri;
    }

    public async ValueTask DisposeAsync()
    {
        if (_initialized)
        {
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try
            {
                await RequestAsync("shutdown", new { }, shutdown.Token).ConfigureAwait(false);
                await NotifyAsync("exit", new { }, shutdown.Token).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        _lifetime.Cancel();
        try
        {
            await _reader.ConfigureAwait(false);
        }
        catch
        {
        }
        await _process.DisposeAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }
}
