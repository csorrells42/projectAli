using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ali.Modules.Embeddings;
using Ali.Modules.RAG;
using Ali.Modules.Runtime;

namespace Ali.Modules.UserMemory;

internal sealed class Mem0ProcessClient : IParticipantMemoryTransport
{
    internal const string LoopbackNoProxy = "127.0.0.1,localhost,::1";
    internal const string ProtocolIdentity = "ali-participant-memory-stdio-v2";
    internal static readonly string FreshRelativeDataRoot =
        Path.Combine("Memory", "ParticipantAware", "Mem0");
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly string _dataRoot;
    private readonly QdrantServiceManager _qdrant;
    private readonly Func<LocalVectorLibrarySettings> _vectorSettings;
    private readonly Func<UserMemorySettings> _settings;
    private readonly Func<OpenAiCompatibleRuntimeOptions?> _runtimeSettings;
    private readonly IParticipantMemoryEmbeddingIdentitySource _embeddingIdentitySource;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Queue<string> _stderr = new();
    private Process? _process;
    private KillOnCloseProcessJob? _processJob;
    private string? _processConfiguration;
    private string? _processEmbeddingSpaceId;
    private int _disposed;

    public Mem0ProcessClient(
        string userDataRoot,
        QdrantServiceManager qdrant,
        Func<LocalVectorLibrarySettings> qdrantSettings,
        Func<UserMemorySettings> settings,
        Func<OpenAiCompatibleRuntimeOptions?> runtimeSettings,
        IParticipantMemoryEmbeddingIdentitySource? embeddingIdentitySource = null)
    {
        _dataRoot = Path.Combine(userDataRoot, FreshRelativeDataRoot);
        _qdrant = qdrant;
        _vectorSettings = qdrantSettings;
        _settings = settings;
        _runtimeSettings = runtimeSettings;
        _embeddingIdentitySource = embeddingIdentitySource
            ?? new ConfiguredParticipantMemoryEmbeddingIdentitySource();
    }

    internal string DataRoot => _dataRoot;

    string IParticipantMemoryTransport.DataRoot => DataRoot;

    internal async ValueTask<Mem0EmbeddingSpaceConfiguration> ResolveCurrentEmbeddingSpaceAsync(
        CancellationToken cancellationToken)
    {
        var vectorSettings = _vectorSettings();
        var settings = _settings().Normalize();
        var identity = await _embeddingIdentitySource
            .ResolveAsync(vectorSettings, cancellationToken)
            .ConfigureAwait(false);
        var embedding = ResolveEmbeddingConfiguration(vectorSettings, identity);
        return ResolveEmbeddingSpace(_dataRoot, settings.CollectionName, embedding, vectorSettings);
    }

    ValueTask<Mem0EmbeddingSpaceConfiguration>
        IParticipantMemoryTransport.ResolveCurrentEmbeddingSpaceAsync(
            CancellationToken cancellationToken) =>
        ResolveCurrentEmbeddingSpaceAsync(cancellationToken);

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    public string LastDiagnostic
    {
        get { lock (_stderr) return string.Join(" | ", _stderr); }
    }

    public async Task<Mem0Response> SendAsync(object request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(request);
        var requestProperties = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        var serializedRequest = JsonSerializer.SerializeToElement(request, JsonOptions);
        if (serializedRequest.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException(
                "A participant-memory worker request must be a JSON object.",
                nameof(request));
        }
        foreach (var property in serializedRequest.EnumerateObject())
        {
            requestProperties[property.Name] = property.Value.Clone();
        }
        if (!requestProperties.TryGetValue("embeddingSpaceId", out var expectedSpaceElement)
            || expectedSpaceElement.ValueKind != JsonValueKind.String
            || expectedSpaceElement.GetString() is not { } expectedEmbeddingSpaceId
            || expectedEmbeddingSpaceId.Length != 24
            || expectedEmbeddingSpaceId.Any(character =>
                character is not (>= '0' and <= '9')
                    and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "A participant-memory worker request requires an explicit exact embeddingSpaceId.",
                nameof(request));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        Process? process = null;
        var workerPipeMayBeDirty = false;
        try
        {
            process = await EnsureStartedAsync(
                    expectedEmbeddingSpaceId,
                    cancellationToken)
                .ConfigureAwait(false);
            var embeddingSpaceId = _processEmbeddingSpaceId
                ?? throw new InvalidOperationException(
                    "The Mem0 worker did not publish an embedding-space identity.");
            if (!string.Equals(
                    embeddingSpaceId,
                    expectedEmbeddingSpaceId,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The participant-memory embedding space changed before send; the request was not written.");
            }
            var id = Guid.NewGuid().ToString("N");
            // Correlation is transport-owned. The caller-owned expected space was
            // validated against the process-bound space before the request can be written.
            requestProperties["id"] = JsonSerializer.SerializeToElement(id, JsonOptions);
            requestProperties["embeddingSpaceId"] = JsonSerializer.SerializeToElement(
                expectedEmbeddingSpaceId,
                JsonOptions);
            var requestJson = JsonSerializer.Serialize(requestProperties, JsonOptions);
            // Mark the worker dirty before the first cancellable pipe write. A
            // WriteLine/Flush exception cannot prove that zero bytes reached stdin.
            workerPipeMayBeDirty = true;
            await process.StandardInput.WriteLineAsync(requestJson.AsMemory(), cancellationToken).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
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
            if (!string.Equals(
                    response.EmbeddingSpaceId,
                    embeddingSpaceId,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Mem0 worker response came from a different embedding space.");
            }
            return response;
        }
        catch
        {
            // Once a request is on the stdio pipe, abandoning its response would
            // leave that response queued for the next caller. Restart the private
            // worker so a timed-out recall can never corrupt a later request.
            if (workerPipeMayBeDirty && process is not null)
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
        try { _processJob?.Dispose(); } catch { }
        _process = null;
        _processJob = null;
        _processConfiguration = null;
        _processEmbeddingSpaceId = null;
    }

    private async Task<Process> EnsureStartedAsync(
        string expectedEmbeddingSpaceId,
        CancellationToken cancellationToken)
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
        var identity = await _embeddingIdentitySource
            .ResolveAsync(vectorSettings, cancellationToken)
            .ConfigureAwait(false);
        var embedding = ResolveEmbeddingConfiguration(vectorSettings, identity);
        var embeddingSpace = ResolveEmbeddingSpace(
            _dataRoot,
            settings.CollectionName,
            embedding,
            vectorSettings);
        if (!string.Equals(
                embeddingSpace.Id,
                expectedEmbeddingSpaceId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The participant-memory embedding space changed after it was resolved; the request was not written.");
        }
        var thinkingControl = ModelThinkingPolicy.Resolve(runtime.Model, runtime.Family);
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
        KillOnCloseProcessJob processJob;
        try
        {
            processJob = KillOnCloseProcessJob.Assign(process);
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            process.Dispose();
            throw;
        }
        process.ErrorDataReceived += OnErrorDataReceived;
        process.BeginErrorReadLine();
        _process = process;
        _processJob = processJob;
        _processConfiguration = processConfiguration;
        _processEmbeddingSpaceId = embeddingSpace.Id;
        return process;
    }

    internal static Mem0EmbeddingProcessConfiguration ResolveEmbeddingConfiguration(
        LocalVectorLibrarySettings vectorSettings) =>
        ResolveEmbeddingConfiguration(
            vectorSettings,
            new ConfiguredParticipantMemoryEmbeddingIdentitySource());

    internal static Mem0EmbeddingProcessConfiguration ResolveEmbeddingConfiguration(
        LocalVectorLibrarySettings vectorSettings,
        IParticipantMemoryEmbeddingIdentitySource identitySource)
    {
        ArgumentNullException.ThrowIfNull(vectorSettings);
        ArgumentNullException.ThrowIfNull(identitySource);
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

        var identity = identitySource.Resolve(vectorSettings).Normalize();
        return ResolveEmbeddingConfiguration(vectorSettings, identity);
    }

    internal static Mem0EmbeddingProcessConfiguration ResolveEmbeddingConfiguration(
        LocalVectorLibrarySettings vectorSettings,
        ParticipantMemoryEmbeddingIdentity resolvedIdentity)
    {
        ArgumentNullException.ThrowIfNull(vectorSettings);
        ArgumentNullException.ThrowIfNull(resolvedIdentity);
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

        var identity = resolvedIdentity.Normalize();
        if (!string.Equals(identity.Provider, configuration.Provider, StringComparison.Ordinal)
            || identity.Endpoint != configuration.Endpoint
            || identity.Dimensions != configuration.Dimensions
            || !string.Equals(identity.ConfiguredModel, configuration.Model, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Participant-memory embedding identity does not match the configured embedding endpoint.");
        }
        if (!identity.ProbeVerified
            || identity.ProbeVerifiedUtc is null
            || identity.ProbeVerifiedUtc > DateTimeOffset.UtcNow
            || string.Equals(identity.Quantization, "provider-not-reported", StringComparison.OrdinalIgnoreCase)
            || string.Equals(identity.ResolvedModel, "provider-not-reported", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Participant-memory embedding identity has not been verified against the live provider response.");
        }

        return new Mem0EmbeddingProcessConfiguration(
            configuration.Provider,
            configuration.Endpoint,
            apiBaseUri,
            configuration.Model,
            configuration.Dimensions,
            identity);
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
            ProtocolIdentity,
            embedding.Provider,
            Endpoint = embedding.Endpoint.AbsoluteUri,
            embedding.Model,
            embedding.Dimensions,
            EmbeddingIdentityFingerprint = embedding.Identity.Fingerprint,
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
            "--embedding-space-id", embeddingSpace.Id,
            "--embedding-protocol", embedding.Identity.Protocol,
            "--embedding-resolved-model", embedding.Identity.ResolvedModel,
            "--embedding-quantization", embedding.Identity.Quantization,
            "--embedding-context-tokens", embedding.Identity.MaximumContextTokens.ToString(),
            "--embedding-query-prompt-mode", embedding.Identity.QueryPromptMode,
            "--embedding-document-prompt-mode", embedding.Identity.DocumentPromptMode,
            "--embedding-query-prefix", embedding.Identity.QueryPromptPrefix,
            "--embedding-document-prefix", embedding.Identity.DocumentPromptPrefix,
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
            ProtocolIdentity,
            EmbeddingProvider = embedding.Provider,
            EmbeddingEndpoint = embedding.Endpoint,
            EmbeddingModel = embedding.Model,
            EmbeddingDimensions = embedding.Dimensions,
            embedding.Identity.Protocol,
            embedding.Identity.ResolvedModel,
            embedding.Identity.Quantization,
            embedding.Identity.MaximumContextTokens,
            embedding.Identity.QueryPromptMode,
            embedding.Identity.DocumentPromptMode,
            embedding.Identity.QueryPromptPrefix,
            embedding.Identity.DocumentPromptPrefix,
            EmbeddingIdentityFingerprint = embedding.Identity.Fingerprint,
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
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(e.Data));
        var diagnostic = $"worker-stderr:{Convert.ToHexString(bytes.AsSpan(0, 6)).ToLowerInvariant()}:{Math.Min(e.Data.Length, 999999)}";
        lock (_stderr)
        {
            _stderr.Enqueue(diagnostic);
            while (_stderr.Count > 8) _stderr.Dequeue();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_process is not { } process) return;
            try { process.StandardInput.Close(); } catch { }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            process.Dispose();
            _processJob?.Dispose();
            _process = null;
            _processJob = null;
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
            if (_embeddingIdentitySource is IDisposable disposableIdentitySource)
            {
                disposableIdentitySource.Dispose();
            }
        }
    }

}

internal sealed record Mem0EmbeddingProcessConfiguration(
    string Provider,
    Uri Endpoint,
    Uri ApiBaseUri,
    string Model,
    int Dimensions,
    ParticipantMemoryEmbeddingIdentity Identity);

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
    string? ErrorCode,
    string? EmbeddingSpaceId = null,
    string? RosterRevision = null,
    IReadOnlyList<ParticipantMemoryRecord>? ParticipantMemories = null,
    bool? EmbeddingAvailable = null,
    bool? Mem0Available = null,
    bool? QdrantAvailable = null,
    int DegradedPointCount = 0,
    IReadOnlyList<string>? FailedPointIds = null,
    int UpdatedPointCount = 0,
    string? MutationStatus = null,
    string? MutationRequestId = null,
    string? MutationOperation = null,
    bool? Reconciled = null,
    bool? DeletionFinalized = null,
    string? RepairRequestId = null,
    int RequestedPointCount = 0,
    int UnchangedPointCount = 0);
