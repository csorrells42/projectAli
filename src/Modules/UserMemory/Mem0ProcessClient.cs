using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ali.Modules.Embeddings;
using Ali.Modules.RAG;
using Ali.Modules.Runtime;

namespace Ali.Modules.UserMemory;

/// <summary>
/// Optional health boundary that gives a private worker one bounded cold start while
/// preserving the ordinary steady-state deadline after a correlated response proves it ready.
/// </summary>
internal interface IParticipantMemoryHealthTransport
{
    Task<Mem0Response> SendHealthAsync(
        object request,
        TimeSpan steadyStateTimeout,
        TimeSpan coldStartTimeout,
        CancellationToken cancellationToken);
}

internal sealed class Mem0ProcessClient :
    IParticipantMemoryTransport,
    IParticipantMemoryHealthTransport
{
    internal const string LoopbackNoProxy = "127.0.0.1,localhost,::1";
    internal const string DeadProxyUri = "http://127.0.0.1:1";
    internal const string WorkerApiKeyEnvironmentVariable = "ALI_MEM0_LLM_API_KEY";
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
    private readonly Func<OpenAiCompatibleRuntimeOptions, string?> _runtimeCredentialResolver;
    private readonly byte[] _credentialFingerprintKey = RandomNumberGenerator.GetBytes(32);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Queue<string> _stderr = new();
    private Process? _process;
    private KillOnCloseProcessJob? _processJob;
    private string? _processConfiguration;
    private string? _processEmbeddingSpaceId;
    private int _processReady;
    private int _disposed;

    public Mem0ProcessClient(
        string userDataRoot,
        QdrantServiceManager qdrant,
        Func<LocalVectorLibrarySettings> qdrantSettings,
        Func<UserMemorySettings> settings,
        Func<OpenAiCompatibleRuntimeOptions?> runtimeSettings,
        IParticipantMemoryEmbeddingIdentitySource? embeddingIdentitySource = null,
        Func<OpenAiCompatibleRuntimeOptions, string?>? runtimeCredentialResolver = null)
    {
        _dataRoot = Path.Combine(userDataRoot, FreshRelativeDataRoot);
        _qdrant = qdrant;
        _vectorSettings = qdrantSettings;
        _settings = settings;
        _runtimeSettings = runtimeSettings;
        _embeddingIdentitySource = embeddingIdentitySource
            ?? new ConfiguredParticipantMemoryEmbeddingIdentitySource();
        _runtimeCredentialResolver = runtimeCredentialResolver ?? (_ => null);
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

    public Task<Mem0Response> SendAsync(object request, CancellationToken cancellationToken) =>
        SendCoreAsync(request, healthTimeouts: null, cancellationToken);

    public Task<Mem0Response> SendHealthAsync(
        object request,
        TimeSpan steadyStateTimeout,
        TimeSpan coldStartTimeout,
        CancellationToken cancellationToken)
    {
        _ = SelectHealthRequestTimeout(
            workerReady: false,
            steadyStateTimeout,
            coldStartTimeout);
        return SendCoreAsync(
            request,
            new HealthRequestTimeouts(steadyStateTimeout, coldStartTimeout),
            cancellationToken);
    }

    internal static TimeSpan SelectHealthRequestTimeout(
        bool workerReady,
        TimeSpan steadyStateTimeout,
        TimeSpan coldStartTimeout)
    {
        if (steadyStateTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(steadyStateTimeout),
                "The steady-state participant-memory health timeout must be positive.");
        }
        if (coldStartTimeout < steadyStateTimeout)
        {
            throw new ArgumentOutOfRangeException(
                nameof(coldStartTimeout),
                "The participant-memory cold-start timeout cannot be shorter than the steady-state timeout.");
        }
        return workerReady ? steadyStateTimeout : coldStartTimeout;
    }

    private async Task<Mem0Response> SendCoreAsync(
        object request,
        HealthRequestTimeouts? healthTimeouts,
        CancellationToken cancellationToken)
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

        CancellationTokenSource? healthTimeout = null;
        var laneToken = cancellationToken;
        if (healthTimeouts is { } configuredTimeouts)
        {
            // Readiness is transport-owned and in memory. Budget selection must not reload
            // settings or probe disk state on every health request.
            healthTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            healthTimeout.CancelAfter(SelectHealthRequestTimeout(
                HasReadyWorkerForEmbeddingSpace(expectedEmbeddingSpaceId),
                configuredTimeouts.SteadyState,
                configuredTimeouts.ColdStart));
            laneToken = healthTimeout.Token;
        }

        try
        {
            await _gate.WaitAsync(laneToken).ConfigureAwait(false);
        }
        catch
        {
            healthTimeout?.Dispose();
            throw;
        }
        Process? process = null;
        var workerPipeMayBeDirty = false;
        try
        {
            process = await EnsureStartedAsync(
                    expectedEmbeddingSpaceId,
                    laneToken)
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
            await process.StandardInput.WriteLineAsync(requestJson.AsMemory(), laneToken).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(laneToken).ConfigureAwait(false);
            var line = await process.StandardOutput.ReadLineAsync(laneToken).ConfigureAwait(false);
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
            Volatile.Write(ref _processReady, 1);
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
            healthTimeout?.Dispose();
        }
    }

    private bool HasReadyWorkerForEmbeddingSpace(string expectedEmbeddingSpaceId)
    {
        if (Volatile.Read(ref _processReady) == 0)
        {
            return false;
        }
        var process = _process;
        try
        {
            return process is { HasExited: false }
                && string.Equals(
                    _processEmbeddingSpaceId,
                    expectedEmbeddingSpaceId,
                    StringComparison.Ordinal);
        }
        catch (InvalidOperationException)
        {
            return false;
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
        Volatile.Write(ref _processReady, 0);
    }

    private async Task<Process> EnsureStartedAsync(
        string expectedEmbeddingSpaceId,
        CancellationToken cancellationToken)
    {
        var vectorSettings = _vectorSettings();
        var settings = _settings().Normalize();
        var runtime = _runtimeSettings()
            ?? throw new InvalidOperationException("Mem0 requires Ali's selected runtime settings.");
        if (!runtime.Enabled || string.IsNullOrWhiteSpace(runtime.Model))
        {
            throw new InvalidOperationException("Mem0 requires an enabled selected runtime model.");
        }

        var authorization = ResolveRuntimeAuthorization(
            runtime,
            LocalEndpointPolicy.IsRemote(runtime.Endpoint)
                ? _runtimeCredentialResolver(runtime)
                : null,
            _credentialFingerprintKey);

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
        var thinkingControl = runtime.ThinkingControl;
        var processConfiguration = BuildProcessConfigurationFingerprint(
            runtime,
            thinkingControl,
            embedding,
            vectorSettings,
            embeddingSpace,
            authorization.CredentialRevision);
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
        ApplyRuntimeEnvironment(start, runtime, authorization);
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
        Volatile.Write(ref _processReady, 0);
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
                vectorSettings.EmbeddingProtocolIdentity,
                vectorSettings.EmbeddingContextTokens,
                vectorSettings.EmbeddingDocumentPromptMode,
                vectorSettings.EmbeddingQueryPromptMode,
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
                vectorSettings.EmbeddingProtocolIdentity,
                vectorSettings.EmbeddingContextTokens,
                vectorSettings.EmbeddingDocumentPromptMode,
                vectorSettings.EmbeddingQueryPromptMode,
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
            configuration.ProtocolIdentity,
            configuration.ContextTokens,
            configuration.DocumentPromptMode,
            configuration.QueryPromptMode,
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
            TransportProtocolIdentity = ProtocolIdentity,
            embedding.Provider,
            Endpoint = embedding.Endpoint.AbsoluteUri,
            embedding.Model,
            embedding.Dimensions,
            embedding.ProtocolIdentity,
            embedding.ContextTokens,
            embedding.DocumentPromptMode,
            embedding.QueryPromptMode,
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

    internal static Mem0RuntimeAuthorization ResolveRuntimeAuthorization(
        OpenAiCompatibleRuntimeOptions runtime,
        string? apiKey,
        ReadOnlySpan<byte> fingerprintKey)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (!runtime.Enabled || string.IsNullOrWhiteSpace(runtime.Model))
        {
            throw new InvalidOperationException(
                "Participant memory requires an enabled selected runtime model.");
        }

        var endpointValidation = LocalEndpointPolicy.Validate(
            runtime.Endpoint,
            runtime.AllowPrivateLanEndpoint,
            runtime.AllowRemoteHttpsEndpoint);
        if (!endpointValidation.IsAllowed)
        {
            throw new InvalidOperationException(endpointValidation.Reason);
        }

        var isRemote = LocalEndpointPolicy.IsRemote(runtime.Endpoint);
        if (isRemote
            && LocalRuntimeEngines.Normalize(runtime.Engine)
            != LocalRuntimeEngines.GenericOpenAi)
        {
            throw new InvalidOperationException(
                "Remote participant-memory inference requires the explicit OpenAI-compatible/Custom engine.");
        }

        if (!isRemote)
        {
            return new Mem0RuntimeAuthorization(
                isRemote: false,
                apiKey: null,
                credentialRevision: "local-no-credential");
        }

        var normalizedApiKey = apiKey?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedApiKey))
        {
            throw new InvalidOperationException(
                "The selected remote runtime requires an API key from Ali's protected credential store or configured environment variable before participant memory can start.");
        }
        if (fingerprintKey.IsEmpty)
        {
            throw new ArgumentException(
                "A non-empty in-memory credential fingerprint key is required.",
                nameof(fingerprintKey));
        }

        var secretBytes = Encoding.UTF8.GetBytes(normalizedApiKey);
        byte[] digest = [];
        try
        {
            digest = HMACSHA256.HashData(fingerprintKey, secretBytes);
            return new Mem0RuntimeAuthorization(
                isRemote: true,
                normalizedApiKey,
                Convert.ToHexString(digest.AsSpan(0, 16)).ToLowerInvariant());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secretBytes);
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    internal static void ApplyRuntimeEnvironment(
        ProcessStartInfo start,
        OpenAiCompatibleRuntimeOptions runtime,
        Mem0RuntimeAuthorization authorization)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(authorization);
        if (authorization.IsRemote != LocalEndpointPolicy.IsRemote(runtime.Endpoint))
        {
            throw new InvalidOperationException(
                "The participant-memory runtime authorization does not match the selected endpoint.");
        }

        foreach (var variable in new[]
                 {
                     "OPENAI_API_KEY",
                     "OPENAI_BASE_URL",
                     "OPENAI_API_BASE",
                     "OPENROUTER_API_KEY",
                     "OPENROUTER_API_BASE",
                     "OPENROUTER_BASE_URL"
                 })
        {
            start.Environment.Remove(variable);
        }
        var configuredCredentialVariable = runtime.ApiKeyEnvironmentVariable?.Trim();
        if (!string.IsNullOrWhiteSpace(configuredCredentialVariable)
            && !string.Equals(
                configuredCredentialVariable,
                WorkerApiKeyEnvironmentVariable,
                StringComparison.OrdinalIgnoreCase))
        {
            start.Environment.Remove(configuredCredentialVariable);
        }

        if (authorization.IsRemote)
        {
            start.Environment[WorkerApiKeyEnvironmentVariable] = authorization.ApiKey
                ?? throw new InvalidOperationException(
                    "Remote participant-memory authorization contains no API key.");
            start.Environment["NO_PROXY"] = LoopbackNoProxy;
            return;
        }

        start.Environment.Remove(WorkerApiKeyEnvironmentVariable);
        start.Environment["NO_PROXY"] = runtime.Endpoint.IsLoopback
            ? LoopbackNoProxy
            : MergeNoProxy(LoopbackNoProxy, runtime.Endpoint.Host);
        start.Environment["HTTP_PROXY"] = DeadProxyUri;
        start.Environment["HTTPS_PROXY"] = DeadProxyUri;
        start.Environment["ALL_PROXY"] = DeadProxyUri;
    }

    private static string MergeNoProxy(params string?[] values) =>
        string.Join(",", values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .SelectMany(value => value!.Split(',', StringSplitOptions.RemoveEmptyEntries))
            .Select(value => value.Trim())
            .Where(value => value.Length != 0)
            .Distinct(StringComparer.OrdinalIgnoreCase));

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
            "--llm-engine", LocalRuntimeEngines.Normalize(runtime.Engine),
            "--llm-model", runtime.Model,
            "--llm-output-tokens", runtime.OutputTokenLimit.ToString(),
            "--allow-private-lan-llm", runtime.AllowPrivateLanEndpoint ? "true" : "false",
            "--allow-remote-https-llm", runtime.AllowRemoteHttpsEndpoint ? "true" : "false",
            "--embedding-provider", embedding.Provider,
            "--embedding-api-base", embedding.ApiBaseUri.AbsoluteUri,
            "--embedding-model", embedding.Model,
            "--embedding-dimensions", embedding.Dimensions.ToString(),
            "--embedding-space-id", embeddingSpace.Id,
            "--embedding-protocol", embedding.ProtocolIdentity,
            "--embedding-resolved-model", embedding.Identity.ResolvedModel,
            "--embedding-quantization", embedding.Identity.Quantization,
            "--embedding-context-tokens", embedding.ContextTokens.ToString(),
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
        Mem0EmbeddingSpaceConfiguration embeddingSpace,
        string credentialRevision = "local-no-credential") =>
        JsonSerializer.Serialize(new
        {
            runtime.Endpoint,
            Engine = LocalRuntimeEngines.Normalize(runtime.Engine),
            runtime.Model,
            runtime.OutputTokenLimit,
            runtime.ReasoningEffort,
            runtime.ThinkingEnabled,
            runtime.AllowPrivateLanEndpoint,
            runtime.AllowRemoteHttpsEndpoint,
            CredentialRevision = credentialRevision,
            ThinkingControl = thinkingControl,
            ProtocolIdentity,
            EmbeddingProvider = embedding.Provider,
            EmbeddingEndpoint = embedding.Endpoint,
            EmbeddingModel = embedding.Model,
            EmbeddingDimensions = embedding.Dimensions,
            EmbeddingProtocol = embedding.ProtocolIdentity,
            EmbeddingContextTokens = embedding.ContextTokens,
            EmbeddingDocumentPromptMode = embedding.DocumentPromptMode,
            EmbeddingQueryPromptMode = embedding.QueryPromptMode,
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
            Volatile.Write(ref _processReady, 0);
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
            CryptographicOperations.ZeroMemory(_credentialFingerprintKey);
            if (_embeddingIdentitySource is IDisposable disposableIdentitySource)
            {
                disposableIdentitySource.Dispose();
            }
        }
    }

}

internal readonly record struct HealthRequestTimeouts(
    TimeSpan SteadyState,
    TimeSpan ColdStart);

internal sealed record Mem0EmbeddingProcessConfiguration(
    string Provider,
    Uri Endpoint,
    Uri ApiBaseUri,
    string Model,
    int Dimensions,
    string ProtocolIdentity,
    int ContextTokens,
    EmbeddingPromptMode DocumentPromptMode,
    EmbeddingPromptMode QueryPromptMode,
    ParticipantMemoryEmbeddingIdentity Identity);

internal sealed record Mem0EmbeddingSpaceConfiguration(
    string Id,
    string BaseCollectionName,
    string CollectionName,
    string DataRoot);

internal sealed class Mem0RuntimeAuthorization
{
    internal Mem0RuntimeAuthorization(
        bool isRemote,
        string? apiKey,
        string credentialRevision)
    {
        IsRemote = isRemote;
        ApiKey = apiKey;
        CredentialRevision = credentialRevision;
    }

    internal bool IsRemote { get; }

    internal string? ApiKey { get; }

    internal string CredentialRevision { get; }

    public override string ToString() =>
        $"Mem0RuntimeAuthorization {{ IsRemote = {IsRemote}, ApiKey = [redacted], CredentialRevision = {CredentialRevision} }}";
}

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
