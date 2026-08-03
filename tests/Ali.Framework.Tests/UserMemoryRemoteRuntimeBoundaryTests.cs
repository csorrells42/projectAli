using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Ali.Modules.Embeddings;
using Ali.Modules.RAG;
using Ali.Modules.Runtime;
using Ali.Modules.UserMemory;

namespace Ali.Framework.Tests;

public sealed class UserMemoryRemoteRuntimeBoundaryTests
{
    private const string RemoteSecret = "ali-remote-memory-secret-canary";
    private static readonly byte[] FingerprintKey = SHA256.HashData(
        Encoding.UTF8.GetBytes("ali-remote-memory-test-fingerprint-key"));

    [Fact]
    public void ApprovedRemoteRuntime_UsesExactNonSecretArgumentsAndAChildOnlyCredential()
    {
        var runtime = RemoteRuntime();
        var authorization = Mem0ProcessClient.ResolveRuntimeAuthorization(
            runtime,
            RemoteSecret,
            FingerprintKey);
        var (vectorSettings, embedding, space) = MemoryConfiguration();

        Assert.True(authorization.IsRemote);
        Assert.Equal(RemoteSecret, authorization.ApiKey);
        Assert.NotEmpty(authorization.CredentialRevision);
        Assert.DoesNotContain(RemoteSecret, authorization.CredentialRevision, StringComparison.Ordinal);
        Assert.DoesNotContain(RemoteSecret, authorization.ToString(), StringComparison.Ordinal);

        var arguments = Mem0ProcessClient.BuildWorkerArgumentList(
            "mem0_service.py",
            space,
            runtime,
            embedding,
            vectorSettings);

        Assert.Equal(
            runtime.Endpoint.ToString().TrimEnd('/'),
            ArgumentValue(arguments, "--llm-endpoint"));
        Assert.Equal(LocalRuntimeEngines.GenericOpenAi, ArgumentValue(arguments, "--llm-engine"));
        Assert.Equal("false", ArgumentValue(arguments, "--allow-private-lan-llm"));
        Assert.Equal("true", ArgumentValue(arguments, "--allow-remote-https-llm"));
        Assert.DoesNotContain(arguments, argument => argument.Contains(RemoteSecret, StringComparison.Ordinal));

        var fingerprint = Mem0ProcessClient.BuildProcessConfigurationFingerprint(
            runtime,
            runtime.ThinkingControl,
            embedding,
            vectorSettings,
            space,
            authorization.CredentialRevision);
        Assert.Contains(authorization.CredentialRevision, fingerprint, StringComparison.Ordinal);
        Assert.DoesNotContain(RemoteSecret, fingerprint, StringComparison.Ordinal);

        var start = StartInfoWithAmbientRuntimeOverrides(runtime.ApiKeyEnvironmentVariable);
        var httpProxy = start.Environment["HTTP_PROXY"];
        var httpsProxy = start.Environment["HTTPS_PROXY"];
        var allProxy = start.Environment["ALL_PROXY"];

        Mem0ProcessClient.ApplyRuntimeEnvironment(start, runtime, authorization);

        Assert.Equal(httpProxy, start.Environment["HTTP_PROXY"]);
        Assert.Equal(httpsProxy, start.Environment["HTTPS_PROXY"]);
        Assert.Equal(allProxy, start.Environment["ALL_PROXY"]);
        Assert.Equal(Mem0ProcessClient.LoopbackNoProxy, start.Environment["NO_PROXY"]);
        Assert.Equal(RemoteSecret, start.Environment[Mem0ProcessClient.WorkerApiKeyEnvironmentVariable]);
        AssertAmbientRuntimeOverridesRemoved(start, runtime.ApiKeyEnvironmentVariable);
    }

    [Theory]
    [InlineData("http://runtime.example.test/tenant/openai/v1/", true, LocalRuntimeEngines.GenericOpenAi, true, "HTTPS")]
    [InlineData("https://runtime.example.test/tenant/openai/v1/", false, LocalRuntimeEngines.GenericOpenAi, true, "loopback")]
    [InlineData("https://runtime.example.test/tenant/openai/v1/", true, LocalRuntimeEngines.LmStudio, true, "Custom")]
    [InlineData("https://runtime.example.test/tenant/openai/v1/", true, LocalRuntimeEngines.GenericOpenAi, false, "API key")]
    public async Task InvalidRemoteRuntime_IsRejectedBeforeEmbeddingQdrantOrWorkerState(
        string endpoint,
        bool allowRemoteHttps,
        string engine,
        bool provideCredential,
        string expectedMessage)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "ali-mem0-remote-boundary",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var runtime = RemoteRuntime(new Uri(endpoint)) with
        {
            Engine = engine,
            AllowRemoteHttpsEndpoint = allowRemoteHttps
        };
        var identitySource = new FailIfResolvedEmbeddingIdentitySource();

        try
        {
            await using var qdrant = new QdrantServiceManager(root);
            await using var client = new Mem0ProcessClient(
                root,
                qdrant,
                SharedEmbeddingSettings,
                static () => new UserMemorySettings(),
                () => runtime,
                identitySource,
                _ => provideCredential ? RemoteSecret : null);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => client.SendAsync(
                    new
                    {
                        operation = "participant_health",
                        embeddingSpaceId = new string('0', 24)
                    },
                    TestContext.Current.CancellationToken));

            Assert.Contains(expectedMessage, error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, identitySource.ResolveCount);
            Assert.Equal("Stopped", qdrant.Status.State);
            Assert.False(qdrant.Status.IsOwnedProcess);
            Assert.False(Directory.Exists(client.DataRoot));
            Assert.DoesNotContain(RemoteSecret, error.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(RemoteSecret, client.LastDiagnostic, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void LocalRuntime_DropsCredentialsAndConfinesWorkerNetworkingToItsExactHost()
    {
        var runtime = LocalRuntime();
        var withCredential = Mem0ProcessClient.ResolveRuntimeAuthorization(
            runtime,
            RemoteSecret,
            FingerprintKey);
        var withoutCredential = Mem0ProcessClient.ResolveRuntimeAuthorization(
            runtime,
            null,
            FingerprintKey);
        var start = StartInfoWithAmbientRuntimeOverrides(runtime.ApiKeyEnvironmentVariable);

        Assert.False(withCredential.IsRemote);
        Assert.Null(withCredential.ApiKey);
        Assert.Equal(withoutCredential.CredentialRevision, withCredential.CredentialRevision);
        Assert.DoesNotContain(RemoteSecret, withCredential.ToString(), StringComparison.Ordinal);

        Mem0ProcessClient.ApplyRuntimeEnvironment(start, runtime, withCredential);

        Assert.Equal(Mem0ProcessClient.DeadProxyUri, start.Environment["HTTP_PROXY"]);
        Assert.Equal(Mem0ProcessClient.DeadProxyUri, start.Environment["HTTPS_PROXY"]);
        Assert.Contains("127.0.0.1", start.Environment["NO_PROXY"], StringComparison.Ordinal);
        Assert.False(start.Environment.ContainsKey(Mem0ProcessClient.WorkerApiKeyEnvironmentVariable));
        AssertAmbientRuntimeOverridesRemoved(start, runtime.ApiKeyEnvironmentVariable);
        Assert.DoesNotContain(
            start.Environment,
            variable => string.Equals(variable.Value, RemoteSecret, StringComparison.Ordinal));
    }

    [Fact]
    public void PrivateLanRuntime_AddsOnlyTheExactRuntimeHostToNoProxyAndUsesNoCredential()
    {
        var runtime = LocalRuntime(new Uri("http://192.168.50.14:1234/team/v1/")) with
        {
            AllowPrivateLanEndpoint = true
        };
        var authorization = Mem0ProcessClient.ResolveRuntimeAuthorization(
            runtime,
            RemoteSecret,
            FingerprintKey);
        var start = StartInfoWithAmbientRuntimeOverrides(runtime.ApiKeyEnvironmentVariable);

        Mem0ProcessClient.ApplyRuntimeEnvironment(start, runtime, authorization);

        Assert.False(authorization.IsRemote);
        Assert.Null(authorization.ApiKey);
        Assert.Equal(Mem0ProcessClient.DeadProxyUri, start.Environment["HTTP_PROXY"]);
        Assert.Equal(Mem0ProcessClient.DeadProxyUri, start.Environment["HTTPS_PROXY"]);
        Assert.Equal(
            $"{Mem0ProcessClient.LoopbackNoProxy},192.168.50.14",
            start.Environment["NO_PROXY"]);
        Assert.DoesNotContain(
            "192.168.50.1",
            start.Environment["NO_PROXY"]!.Split(','),
            StringComparer.Ordinal);
        Assert.False(start.Environment.ContainsKey(Mem0ProcessClient.WorkerApiKeyEnvironmentVariable));
    }

    [Fact]
    public void CredentialRotation_ChangesOnlyTheNonSecretProcessIdentity()
    {
        const string rotatedSecret = "ali-remote-memory-rotated-secret-canary";
        var runtime = RemoteRuntime();
        var first = Mem0ProcessClient.ResolveRuntimeAuthorization(runtime, RemoteSecret, FingerprintKey);
        var repeated = Mem0ProcessClient.ResolveRuntimeAuthorization(runtime, RemoteSecret, FingerprintKey);
        var rotated = Mem0ProcessClient.ResolveRuntimeAuthorization(runtime, rotatedSecret, FingerprintKey);
        var (vectorSettings, embedding, space) = MemoryConfiguration();

        Assert.Equal(first.CredentialRevision, repeated.CredentialRevision);
        Assert.NotEqual(first.CredentialRevision, rotated.CredentialRevision);

        var firstFingerprint = Mem0ProcessClient.BuildProcessConfigurationFingerprint(
            runtime,
            runtime.ThinkingControl,
            embedding,
            vectorSettings,
            space,
            first.CredentialRevision);
        var rotatedFingerprint = Mem0ProcessClient.BuildProcessConfigurationFingerprint(
            runtime,
            runtime.ThinkingControl,
            embedding,
            vectorSettings,
            space,
            rotated.CredentialRevision);

        Assert.NotEqual(firstFingerprint, rotatedFingerprint);
        Assert.DoesNotContain(RemoteSecret, firstFingerprint, StringComparison.Ordinal);
        Assert.DoesNotContain(rotatedSecret, rotatedFingerprint, StringComparison.Ordinal);
        Assert.DoesNotContain(RemoteSecret, first.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(rotatedSecret, rotated.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void WorkerClients_PinTheConfiguredEndpointAndDisableRedirectsAndAmbientOverrides()
    {
        var worker = File.ReadAllText(FindRepositoryFile(
            "src", "Modules", "UserMemory", "Tools", "mem0_service.py"));
        var adapter = File.ReadAllText(FindRepositoryFile(
            "src", "Modules", "UserMemory", "Tools", "openai_compatible_llm.py"));
        var combined = worker + "\n" + adapter;

        Assert.Contains("--llm-engine", worker, StringComparison.Ordinal);
        Assert.Contains("--allow-remote-https-llm", worker, StringComparison.Ordinal);
        Assert.Contains("--allow-private-lan-llm", worker, StringComparison.Ordinal);
        Assert.Contains(Mem0ProcessClient.WorkerApiKeyEnvironmentVariable, worker, StringComparison.Ordinal);
        Assert.Contains("follow_redirects=False", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("trust_env=False", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("allow_redirects=True", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("follow_redirects=True", combined, StringComparison.Ordinal);
        Assert.DoesNotContain(RemoteSecret, combined, StringComparison.Ordinal);
    }

    private static ProcessStartInfo StartInfoWithAmbientRuntimeOverrides(string configuredKeyName)
    {
        var start = new ProcessStartInfo
        {
            FileName = "python.exe",
            UseShellExecute = false
        };
        start.Environment["HTTP_PROXY"] = "http://enterprise-http-proxy.example.test:8080";
        start.Environment["HTTPS_PROXY"] = "http://enterprise-https-proxy.example.test:8443";
        start.Environment["ALL_PROXY"] = "socks5://enterprise-all-proxy.example.test:1080";
        start.Environment["NO_PROXY"] = "ambient.example.test";
        start.Environment["OPENAI_API_KEY"] = "ambient-openai-key";
        start.Environment["OPENAI_BASE_URL"] = "https://ambient-openai.example.test/v1/";
        start.Environment["OPENAI_API_BASE"] = "https://ambient-api-base.example.test/v1/";
        start.Environment["OPENROUTER_API_KEY"] = "ambient-openrouter-key";
        start.Environment["OPENROUTER_BASE_URL"] = "https://ambient-openrouter.example.test/v1/";
        start.Environment[configuredKeyName] = "ambient-configured-key";
        start.Environment[Mem0ProcessClient.WorkerApiKeyEnvironmentVariable] = "ambient-worker-key";
        return start;
    }

    private static void AssertAmbientRuntimeOverridesRemoved(
        ProcessStartInfo start,
        string configuredKeyName)
    {
        Assert.False(start.Environment.ContainsKey("OPENAI_API_KEY"));
        Assert.False(start.Environment.ContainsKey("OPENAI_BASE_URL"));
        Assert.False(start.Environment.ContainsKey("OPENAI_API_BASE"));
        Assert.False(start.Environment.ContainsKey("OPENROUTER_API_KEY"));
        Assert.False(start.Environment.ContainsKey("OPENROUTER_BASE_URL"));
        Assert.False(start.Environment.ContainsKey(configuredKeyName));
    }

    private static (LocalVectorLibrarySettings VectorSettings,
        Mem0EmbeddingProcessConfiguration Embedding,
        Mem0EmbeddingSpaceConfiguration Space) MemoryConfiguration()
    {
        var vectorSettings = SharedEmbeddingSettings();
        var embedding = Mem0ProcessClient.ResolveEmbeddingConfiguration(
            vectorSettings,
            VerifiedEmbeddingIdentitySource.Instance);
        var space = Mem0ProcessClient.ResolveEmbeddingSpace(
            Path.Combine("ali-data", "Memory", "Mem0"),
            "ali_user_memories",
            embedding,
            vectorSettings);
        return (vectorSettings, embedding, space);
    }

    private static LocalVectorLibrarySettings SharedEmbeddingSettings() => new()
    {
        EmbeddingProvider = LocalEmbeddingProviders.Custom,
        EmbeddingEndpoint = "http://127.0.0.1:9123/v1/embeddings",
        EmbeddingModel = "custom-embedding-model",
        EmbeddingDimensions = 768,
        UseManagedLocalQdrant = false,
        AutoStartQdrant = false
    };

    private static OpenAiCompatibleRuntimeOptions RemoteRuntime(Uri? endpoint = null) =>
        Runtime(endpoint ?? new Uri("https://runtime.example.test/tenant-42/openai/v1/")) with
        {
            Engine = LocalRuntimeEngines.GenericOpenAi,
            AllowRemoteHttpsEndpoint = true,
            ApiKeyEnvironmentVariable = "ALI_TEST_REMOTE_MEMORY_API_KEY"
        };

    private static OpenAiCompatibleRuntimeOptions LocalRuntime(Uri? endpoint = null) =>
        Runtime(endpoint ?? new Uri("http://127.0.0.1:1234/v1/")) with
        {
            Engine = LocalRuntimeEngines.LmStudio,
            AllowRemoteHttpsEndpoint = false,
            ApiKeyEnvironmentVariable = "ALI_TEST_UNUSED_LOCAL_API_KEY"
        };

    private static OpenAiCompatibleRuntimeOptions Runtime(Uri endpoint) => new(
        Enabled: true,
        Endpoint: endpoint,
        Model: "provider/model-revision-1",
        DisplayName: "Provider model revision 1",
        Family: "provider-reported",
        Size: "provider-reported",
        Quantization: "provider-reported",
        ContextTokens: 8_192,
        OutputTokenLimit: 1_024,
        Temperature: 0.2,
        TopP: 0.9,
        StreamingEnabled: false,
        SupportsVision: false,
        SupportsToolCalls: false,
        AllowPrivateLanEndpoint: false);

    private static string ArgumentValue(IReadOnlyList<string> arguments, string name)
    {
        var index = Enumerable.Range(0, arguments.Count)
            .FirstOrDefault(candidate => string.Equals(arguments[candidate], name, StringComparison.Ordinal), -1);
        Assert.InRange(index, 0, arguments.Count - 2);
        return arguments[index + 1];
    }

    private static string FindRepositoryFile(params string[] segments)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Repository file was not found: {Path.Combine(segments)}");
    }

    private sealed class FailIfResolvedEmbeddingIdentitySource :
        IParticipantMemoryEmbeddingIdentitySource
    {
        private int _resolveCount;

        public int ResolveCount => Volatile.Read(ref _resolveCount);

        public ParticipantMemoryEmbeddingIdentity Resolve(LocalVectorLibrarySettings settings)
        {
            Interlocked.Increment(ref _resolveCount);
            throw new InvalidOperationException(
                "Embedding identity must not resolve before remote runtime authorization.");
        }
    }

    private sealed class VerifiedEmbeddingIdentitySource :
        IParticipantMemoryEmbeddingIdentitySource
    {
        public static VerifiedEmbeddingIdentitySource Instance { get; } = new();

        public ParticipantMemoryEmbeddingIdentity Resolve(LocalVectorLibrarySettings settings) => new(
            settings.EmbeddingProvider,
            "openai-compatible-embeddings-v1",
            new Uri(settings.EmbeddingEndpoint),
            settings.EmbeddingModel,
            settings.EmbeddingModel,
            "verified-test-quantization",
            settings.EmbeddingDimensions,
            8192,
            "none-v1",
            "none-v1",
            string.Empty,
            string.Empty,
            "verified-test-provider-probe",
            true,
            DateTimeOffset.Parse("2026-08-03T00:00:00Z"));
    }
}
