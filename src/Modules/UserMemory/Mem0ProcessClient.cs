using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ali.Modules.Embeddings;
using Ali.Modules.RAG;
using Ali.Modules.Runtime;

namespace Ali.Modules.UserMemory;

internal sealed class Mem0ProcessClient : IAsyncDisposable
{
    internal const string LoopbackNoProxy = "127.0.0.1,localhost,::1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _dataRoot;
    private readonly QdrantServiceManager _qdrant;
    private readonly Func<LocalVectorLibrarySettings> _vectorSettings;
    private readonly Func<UserMemorySettings> _settings;
    private readonly Func<OpenAiCompatibleRuntimeOptions?> _runtimeSettings;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Queue<string> _stderr = new();
    private Process? _process;
    private string? _processConfiguration;

    public Mem0ProcessClient(
        string userDataRoot,
        QdrantServiceManager qdrant,
        Func<LocalVectorLibrarySettings> qdrantSettings,
        Func<UserMemorySettings> settings,
        Func<OpenAiCompatibleRuntimeOptions?> runtimeSettings)
    {
        _dataRoot = Path.Combine(userDataRoot, "Memory", "Mem0");
        _qdrant = qdrant;
        _vectorSettings = qdrantSettings;
        _settings = settings;
        _runtimeSettings = runtimeSettings;
    }

    internal string DataRoot => _dataRoot;

    public string LastDiagnostic
    {
        get { lock (_stderr) return string.Join(" | ", _stderr); }
    }

    public async Task<Mem0Response> SendAsync(object request, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        Process? process = null;
        var requestWasWritten = false;
        try
        {
            process = await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
            var id = Guid.NewGuid().ToString("N");
            var requestProperties = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = JsonSerializer.SerializeToElement(id, JsonOptions)
            };
            foreach (var property in JsonSerializer.SerializeToElement(request, JsonOptions).EnumerateObject())
            {
                requestProperties[property.Name] = property.Value.Clone();
            }
            var requestJson = JsonSerializer.Serialize(requestProperties, JsonOptions);
            await process.StandardInput.WriteLineAsync(requestJson.AsMemory(), cancellationToken).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            requestWasWritten = true;
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line))
            {
                throw new InvalidOperationException(
                    process.HasExited
                        ? $"Mem0 worker exited with code {process.ExitCode}. {LastDiagnostic}"
                        : $"Mem0 worker returned no response. {LastDiagnostic}");
            }
            var response = JsonSerializer.Deserialize<Mem0Response>(line, JsonOptions)
                ?? throw new InvalidOperationException("Mem0 worker returned invalid JSON.");
            if (!string.Equals(response.Id, id, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Mem0 worker response did not match the current request.");
            }
            return response;
        }
        catch
        {
            // Once a request is on the stdio pipe, abandoning its response would
            // leave that response queued for the next caller. Restart the private
            // worker so a timed-out recall can never corrupt a later request.
            if (requestWasWritten && process is not null)
            {
                ResetProcess(process);
            }
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ResetProcess(Process process)
    {
        if (!ReferenceEquals(_process, process)) return;
        try { process.StandardInput.Close(); } catch { }
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        try { process.Dispose(); } catch { }
        _process = null;
        _processConfiguration = null;
    }

    private async Task<Process> EnsureStartedAsync(CancellationToken cancellationToken)
    {
        var vectorSettings = _vectorSettings();
        var settings = _settings().Normalize();
        var runtime = _runtimeSettings()
            ?? throw new InvalidOperationException("Mem0 requires Ali's selected local runtime settings.");
        if (!runtime.Enabled || string.IsNullOrWhiteSpace(runtime.Model))
        {
            throw new InvalidOperationException("Mem0 requires an enabled selected local runtime model.");
        }

        // Resolve and validate the shared embedding settings before touching
        // Qdrant or attempting to create the private Python worker.
        var embedding = ResolveEmbeddingConfiguration(vectorSettings);
        var embeddingSpace = ResolveEmbeddingSpace(
            _dataRoot,
            settings.CollectionName,
            embedding,
            vectorSettings);
        var thinkingControl = runtime.ThinkingControl;
        var processConfiguration = BuildProcessConfigurationFingerprint(
            runtime,
            thinkingControl,
            embedding,
            vectorSettings,
            embeddingSpace);
        if (_process is { HasExited: false } running
            && string.Equals(_processConfiguration, processConfiguration, StringComparison.Ordinal))
        {
            return running;
        }

        if (_process is { } stale)
        {
            ResetProcess(stale);
        }

        await _qdrant.EnsureAvailableAsync(vectorSettings, cancellationToken).ConfigureAwait(false);
        if (!_qdrant.Status.IsReachable)
        {
            throw new InvalidOperationException(_qdrant.Status.Message);
        }

        var python = Path.Combine(AppContext.BaseDirectory, "runtime", "python", "python.exe");
        var script = Path.Combine(AppContext.BaseDirectory, "lib", "memory", "mem0_service.py");
        if (!File.Exists(python) || !File.Exists(script))
        {
            throw new FileNotFoundException("The portable Mem0 runtime is not installed. Restore Ali runtime assets and republish.");
        }
        Directory.CreateDirectory(embeddingSpace.DataRoot);
        var start = new ProcessStartInfo
        {
            FileName = python,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = embeddingSpace.DataRoot
        };
        foreach (var argument in BuildWorkerArgumentList(
                     script,
                     embeddingSpace,
                     runtime,
                     embedding,
                     vectorSettings))
        {
            start.ArgumentList.Add(argument);
        }
        start.Environment["MEM0_TELEMETRY"] = "false";
        start.Environment["POSTHOG_DISABLED"] = "true";
        start.Environment["FASTEMBED_CACHE_PATH"] = Path.Combine(AppContext.BaseDirectory, "runtime", "fastembed-cache");
        start.Environment["HF_HUB_OFFLINE"] = "1";
        start.Environment["HF_HUB_DISABLE_TELEMETRY"] = "1";
        start.Environment["NO_PROXY"] = LoopbackNoProxy;
        start.Environment["HTTP_PROXY"] = "http://127.0.0.1:1";
        start.Environment["HTTPS_PROXY"] = "http://127.0.0.1:1";
        start.Environment["ALI_MEM0_THINKING_CONTROL"] = thinkingControl.ToString();
        start.Environment["ALI_MEM0_THINKING_ENABLED"] = runtime.ThinkingEnabled.ToString();
        start.Environment["ALI_MEM0_REASONING_EFFORT"] = runtime.ReasoningEffort ?? string.Empty;

        var process = Process.Start(start) ?? throw new InvalidOperationException("Mem0 worker did not start.");
        process.ErrorDataReceived += OnErrorDataReceived;
        process.BeginErrorReadLine();
        _process = process;
        _processConfiguration = processConfiguration;
        return process;
    }

    internal static Mem0EmbeddingProcessConfiguration ResolveEmbeddingConfiguration(
        LocalVectorLibrarySettings vectorSettings)
    {
        ArgumentNullException.ThrowIfNull(vectorSettings);
        if (!LocalEmbeddingConfiguration.TryCreate(
                vectorSettings.EmbeddingProvider,
                vectorSettings.EmbeddingEndpoint,
                vectorSettings.EmbeddingModel,
                vectorSettings.EmbeddingDimensions,
                out var configuration,
                out var failure)
            || configuration is null)
        {
            throw new InvalidOperationException($"Mem0 embedding configuration is invalid: {failure}");
        }

        if (!configuration.TryGetOpenAiApiBaseUri(out var apiBaseUri, out failure)
            || apiBaseUri is null)
        {
            throw new InvalidOperationException($"Mem0 embedding configuration is invalid: {failure}");
        }

        return new Mem0EmbeddingProcessConfiguration(
            configuration.Provider,
            configuration.Endpoint,
            apiBaseUri,
            configuration.Model,
            configuration.Dimensions);
    }

    internal static Mem0EmbeddingSpaceConfiguration ResolveEmbeddingSpace(
        string mem0DataRoot,
        string baseCollectionName,
        Mem0EmbeddingProcessConfiguration embedding,
        LocalVectorLibrarySettings vectorSettings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mem0DataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseCollectionName);
        ArgumentNullException.ThrowIfNull(embedding);
        ArgumentNullException.ThrowIfNull(vectorSettings);

        // The namespace binds history to the exact embedding and vector-store
        // target. Restoring every choice deterministically selects that space.
        var identity = JsonSerializer.Serialize(new
        {
            BaseCollectionName = baseCollectionName,
            embedding.Provider,
            Endpoint = embedding.Endpoint.AbsoluteUri,
            embedding.Model,
            embedding.Dimensions,
            QdrantHost = vectorSettings.QdrantHost.Trim(),
            vectorSettings.QdrantHttpPort,
            vectorSettings.QdrantGrpcPort,
            vectorSettings.QdrantUseTls,
            QdrantApiKeyEnvironmentVariable = vectorSettings.QdrantApiKeyEnvironmentVariable.Trim()
        }, JsonOptions);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        var embeddingSpaceId = Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant();
        return new Mem0EmbeddingSpaceConfiguration(
            embeddingSpaceId,
            baseCollectionName,
            $"{baseCollectionName}__embedding_{embeddingSpaceId}",
            Path.Combine(mem0DataRoot, "embedding-spaces", embeddingSpaceId));
    }

    internal static IReadOnlyList<string> BuildWorkerArgumentList(
        string script,
        Mem0EmbeddingSpaceConfiguration embeddingSpace,
        OpenAiCompatibleRuntimeOptions runtime,
        Mem0EmbeddingProcessConfiguration embedding,
        LocalVectorLibrarySettings vectorSettings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(script);
        ArgumentNullException.ThrowIfNull(embeddingSpace);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(embedding);
        ArgumentNullException.ThrowIfNull(vectorSettings);

        return
        [
            script,
            "--data-root", embeddingSpace.DataRoot,
            "--collection", embeddingSpace.CollectionName,
            "--llm-endpoint", runtime.Endpoint.ToString().TrimEnd('/'),
            "--llm-model", runtime.Model,
            "--llm-output-tokens", runtime.OutputTokenLimit.ToString(),
            "--embedding-provider", embedding.Provider,
            "--embedding-api-base", embedding.ApiBaseUri.AbsoluteUri,
            "--embedding-model", embedding.Model,
            "--embedding-dimensions", embedding.Dimensions.ToString(),
            "--qdrant-host", vectorSettings.QdrantHost.Trim(),
            "--qdrant-port", vectorSettings.QdrantHttpPort.ToString(),
            "--qdrant-grpc-port", vectorSettings.QdrantGrpcPort.ToString(),
            "--qdrant-use-tls", vectorSettings.QdrantUseTls ? "true" : "false",
            "--qdrant-api-key-environment-variable", vectorSettings.QdrantApiKeyEnvironmentVariable.Trim()
        ];
    }

    internal static string BuildProcessConfigurationFingerprint(
        OpenAiCompatibleRuntimeOptions runtime,
        ModelThinkingControl thinkingControl,
        Mem0EmbeddingProcessConfiguration embedding,
        LocalVectorLibrarySettings vectorSettings,
        Mem0EmbeddingSpaceConfiguration embeddingSpace) =>
        JsonSerializer.Serialize(new
        {
            runtime.Endpoint,
            runtime.Model,
            runtime.OutputTokenLimit,
            runtime.ReasoningEffort,
            runtime.ThinkingEnabled,
            ThinkingControl = thinkingControl,
            EmbeddingProvider = embedding.Provider,
            EmbeddingEndpoint = embedding.Endpoint,
            EmbeddingModel = embedding.Model,
            EmbeddingDimensions = embedding.Dimensions,
            vectorSettings.QdrantHost,
            vectorSettings.QdrantHttpPort,
            vectorSettings.QdrantGrpcPort,
            vectorSettings.QdrantUseTls,
            vectorSettings.QdrantApiKeyEnvironmentVariable,
            embeddingSpace.BaseCollectionName,
            EmbeddingSpaceId = embeddingSpace.Id,
            CollectionName = embeddingSpace.CollectionName,
            DataRoot = embeddingSpace.DataRoot
        }, JsonOptions);

    private void OnErrorDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Data)) return;
        lock (_stderr)
        {
            _stderr.Enqueue(e.Data.ReplaceLineEndings(" ").Trim());
            while (_stderr.Count > 8) _stderr.Dequeue();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_process is not { } process) return;
            try { process.StandardInput.Close(); } catch { }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            process.Dispose();
            _process = null;
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

}

internal sealed record Mem0EmbeddingProcessConfiguration(
    string Provider,
    Uri Endpoint,
    Uri ApiBaseUri,
    string Model,
    int Dimensions);

internal sealed record Mem0EmbeddingSpaceConfiguration(
    string Id,
    string BaseCollectionName,
    string CollectionName,
    string DataRoot);

internal sealed record Mem0Response(
    string Id,
    bool Success,
    string Message,
    IReadOnlyList<UserMemory>? Memories,
    int Count,
    string? ErrorCode);
