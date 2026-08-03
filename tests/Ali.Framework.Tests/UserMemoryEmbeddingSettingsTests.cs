using System.Text.Json;
using Ali.Modules.Embeddings;
using Ali.Modules.RAG;
using Ali.Modules.Runtime;
using Ali.Modules.UserMemory;

namespace Ali.Framework.Tests;

public sealed class UserMemoryEmbeddingSettingsTests
{
    [Fact]
    public async Task FreshParticipantMem0HistoryRoot_IsPlacedUnderTheExplicitUserDataRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "ali-mem0-data-root", Guid.NewGuid().ToString("N"));
        var settingsRoot = Path.Combine(root, "Settings");
        var userDataRoot = Path.Combine(root, "Data");
        Directory.CreateDirectory(settingsRoot);
        Directory.CreateDirectory(userDataRoot);
        try
        {
            await using var qdrant = new QdrantServiceManager(settingsRoot);
            await using var client = new Mem0ProcessClient(
                userDataRoot,
                qdrant,
                SharedEmbeddingSettings,
                static () => new UserMemorySettings(),
                RuntimeSettings);
            Assert.Equal(
                Path.Combine(userDataRoot, "Memory", "ParticipantAware", "Mem0"),
                client.DataRoot);
            Assert.False(client.DataRoot.StartsWith(settingsRoot, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void UserMemorySettings_KeepOnlyMemoryPolicyAndCollectionAuthority()
    {
        Assert.Null(typeof(UserMemorySettings).GetProperty("EmbeddingProvider"));
        Assert.Null(typeof(UserMemorySettings).GetProperty("EmbeddingEndpoint"));
        Assert.Null(typeof(UserMemorySettings).GetProperty("EmbeddingModel"));
        Assert.Null(typeof(UserMemorySettings).GetProperty("EmbeddingDimensions"));
        Assert.Null(typeof(UserMemorySettings).GetProperty("QdrantHost"));
        Assert.Null(typeof(UserMemorySettings).GetProperty("QdrantHttpPort"));

        var normalized = new UserMemorySettings
        {
            RecallMaximumResults = 20,
            CollectionName = "  ali_test_memories  "
        }.Normalize();

        Assert.Equal(8, normalized.RecallMaximumResults);
        Assert.Equal("ali_test_memories", normalized.CollectionName);
    }

    [Fact]
    public void Mem0EmbeddingConfiguration_ComesFromTheSharedVectorSettings()
    {
        var resolved = Mem0ProcessClient.ResolveEmbeddingConfiguration(
            new LocalVectorLibrarySettings
            {
                EmbeddingProvider = LocalEmbeddingProviders.Custom,
                EmbeddingEndpoint = "http://127.0.0.1:9123/custom/v2/embeddings",
                EmbeddingModel = "custom-embedding-model",
                EmbeddingDimensions = 1536
            },
            VerifiedIdentitySource.Instance);

        Assert.Equal(LocalEmbeddingProviders.Custom, resolved.Provider);
        Assert.Equal("http://127.0.0.1:9123/custom/v2/embeddings", resolved.Endpoint.AbsoluteUri);
        Assert.Equal("http://127.0.0.1:9123/custom/v2/", resolved.ApiBaseUri.AbsoluteUri);
        Assert.Equal("custom-embedding-model", resolved.Model);
        Assert.Equal(1536, resolved.Dimensions);
        Assert.Equal(LocalEmbeddingProtocolIdentities.OpenAiCompatibleV1, resolved.ProtocolIdentity);
        Assert.Equal(8192, resolved.ContextTokens);
        Assert.Equal(EmbeddingPromptMode.SearchDocument, resolved.DocumentPromptMode);
        Assert.Equal(EmbeddingPromptMode.SearchQuery, resolved.QueryPromptMode);
    }

    [Fact]
    public void Mem0EmbeddingConfiguration_RejectsConfiguredStringsWithoutProviderVerification()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => Mem0ProcessClient.ResolveEmbeddingConfiguration(SharedEmbeddingSettings()));

        Assert.Contains("not been verified", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("http://127.0.0.1:9123/v1/embed")]
    [InlineData("http://127.0.0.1:9123/v1/embeddings/extra")]
    [InlineData("http://127.0.0.1:9123/v1/embeddings?tenant=ali")]
    [InlineData("http://127.0.0.1:9123/v1/embeddings#ali")]
    [InlineData("http://127.0.0.1:9123/v1/EMBEDDINGS")]
    public void Mem0EmbeddingConfiguration_RejectsEndpointsThatCannotYieldAnExactApiBase(
        string endpoint)
    {
        var settings = new LocalVectorLibrarySettings
        {
            EmbeddingProvider = LocalEmbeddingProviders.Custom,
            EmbeddingEndpoint = endpoint,
            EmbeddingModel = "custom-embedding-model",
            EmbeddingDimensions = 768
        };

        var error = Assert.Throws<InvalidOperationException>(
            () => Mem0ProcessClient.ResolveEmbeddingConfiguration(settings));

        Assert.Contains("embedding configuration is invalid", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Mem0EmbeddingSpace_IsStableAndChangesForEveryEmbeddingChoice()
    {
        var mem0DataRoot = Path.Combine("ali-data", "Memory", "Mem0");
        var vectorSettings = SharedEmbeddingSettings();
        var baselineEmbedding = Mem0ProcessClient.ResolveEmbeddingConfiguration(
            vectorSettings,
            VerifiedIdentitySource.Instance);
        var baseline = Mem0ProcessClient.ResolveEmbeddingSpace(
            mem0DataRoot,
            "ali_user_memories",
            baselineEmbedding,
            vectorSettings);
        var repeated = Mem0ProcessClient.ResolveEmbeddingSpace(
            mem0DataRoot,
            "ali_user_memories",
            Mem0ProcessClient.ResolveEmbeddingConfiguration(
                vectorSettings with { },
                VerifiedIdentitySource.Instance),
            vectorSettings with { });

        Assert.Equal(baseline, repeated);
        Assert.Equal(24, baseline.Id.Length);
        Assert.Matches("^[0-9a-f]{24}$", baseline.Id);
        Assert.Equal($"ali_user_memories__embedding_{baseline.Id}", baseline.CollectionName);
        Assert.Equal(
            Path.Combine(mem0DataRoot, "embedding-spaces", baseline.Id),
            baseline.DataRoot);

        var alternatives = new[]
        {
            vectorSettings with { EmbeddingProvider = LocalEmbeddingProviders.LmStudio },
            vectorSettings with { EmbeddingEndpoint = "http://127.0.0.1:9124/v1/embeddings" },
            vectorSettings with { EmbeddingModel = "other-embedding-model" },
            vectorSettings with { EmbeddingDimensions = 1024 },
            vectorSettings with { EmbeddingContextTokens = 16_384 },
            vectorSettings with { EmbeddingDocumentPromptMode = EmbeddingPromptMode.Plain },
            vectorSettings with { EmbeddingQueryPromptMode = EmbeddingPromptMode.Plain }
        };

        Assert.All(alternatives, alternative =>
        {
            var embedding = Mem0ProcessClient.ResolveEmbeddingConfiguration(
                alternative,
                VerifiedIdentitySource.Instance);
            var space = Mem0ProcessClient.ResolveEmbeddingSpace(
                mem0DataRoot,
                "ali_user_memories",
                embedding,
                alternative);
            Assert.NotEqual(baseline.Id, space.Id);
            Assert.NotEqual(baseline.CollectionName, space.CollectionName);
            Assert.NotEqual(baseline.DataRoot, space.DataRoot);
        });

        var vectorTargetAlternatives = new (string Collection, LocalVectorLibrarySettings Settings)[]
        {
            ("ali_other_memories", vectorSettings),
            ("ali_user_memories", vectorSettings with { QdrantHost = "localhost" }),
            ("ali_user_memories", vectorSettings with { QdrantHttpPort = 7333 }),
            ("ali_user_memories", vectorSettings with { QdrantGrpcPort = 7334 }),
            ("ali_user_memories", vectorSettings with { QdrantUseTls = true }),
            ("ali_user_memories", vectorSettings with { QdrantApiKeyEnvironmentVariable = "ALI_OTHER_QDRANT_KEY" })
        };

        Assert.All(vectorTargetAlternatives, alternative =>
        {
            var space = Mem0ProcessClient.ResolveEmbeddingSpace(
                mem0DataRoot,
                alternative.Collection,
                baselineEmbedding,
                alternative.Settings);
            Assert.NotEqual(baseline.Id, space.Id);
            Assert.NotEqual(baseline.CollectionName, space.CollectionName);
            Assert.NotEqual(baseline.DataRoot, space.DataRoot);
        });
    }

    [Fact]
    public void Mem0WorkerArgumentsAndFingerprint_UseTheEffectiveEmbeddingSpaceNames()
    {
        var runtime = RuntimeSettings();
        var vectorSettings = SharedEmbeddingSettings() with
        {
            QdrantUseTls = true,
            QdrantApiKeyEnvironmentVariable = "ALI_TEST_QDRANT_KEY"
        };
        var embedding = Mem0ProcessClient.ResolveEmbeddingConfiguration(
            vectorSettings,
            VerifiedIdentitySource.Instance);
        var space = Mem0ProcessClient.ResolveEmbeddingSpace(
            Path.Combine("ali-data", "Memory", "Mem0"),
            "ali_user_memories",
            embedding,
            vectorSettings);

        var arguments = Mem0ProcessClient.BuildWorkerArgumentList(
            "mem0_service.py",
            space,
            runtime,
            embedding,
            vectorSettings);
        Assert.Equal(space.DataRoot, ArgumentValue(arguments, "--data-root"));
        Assert.Equal(space.CollectionName, ArgumentValue(arguments, "--collection"));
        Assert.Equal(space.Id, ArgumentValue(arguments, "--embedding-space-id"));
        Assert.Equal(embedding.Identity.Protocol, ArgumentValue(arguments, "--embedding-protocol"));
        Assert.Equal(embedding.Identity.ResolvedModel, ArgumentValue(arguments, "--embedding-resolved-model"));
        Assert.Equal(embedding.Identity.Quantization, ArgumentValue(arguments, "--embedding-quantization"));
        Assert.Equal(
            embedding.Identity.MaximumContextTokens.ToString(),
            ArgumentValue(arguments, "--embedding-context-tokens"));
        Assert.Equal(
            embedding.Identity.QueryPromptMode,
            ArgumentValue(arguments, "--embedding-query-prompt-mode"));
        Assert.Equal(
            embedding.Identity.DocumentPromptMode,
            ArgumentValue(arguments, "--embedding-document-prompt-mode"));
        Assert.Equal(
            embedding.Identity.QueryPromptPrefix,
            ArgumentValue(arguments, "--embedding-query-prefix"));
        Assert.Equal(
            embedding.Identity.DocumentPromptPrefix,
            ArgumentValue(arguments, "--embedding-document-prefix"));
        Assert.Equal(vectorSettings.QdrantHost, ArgumentValue(arguments, "--qdrant-host"));
        Assert.Equal(vectorSettings.QdrantHttpPort.ToString(), ArgumentValue(arguments, "--qdrant-port"));
        Assert.Equal(vectorSettings.QdrantGrpcPort.ToString(), ArgumentValue(arguments, "--qdrant-grpc-port"));
        Assert.Equal("true", ArgumentValue(arguments, "--qdrant-use-tls"));
        Assert.Equal(
            LocalEmbeddingProtocolIdentities.OpenAiCompatibleV1,
            ArgumentValue(arguments, "--embedding-protocol"));
        Assert.Equal("8192", ArgumentValue(arguments, "--embedding-context-tokens"));
        Assert.Equal(EmbeddingPromptMode.SearchDocument, embedding.DocumentPromptMode);
        Assert.Equal(EmbeddingPromptMode.SearchQuery, embedding.QueryPromptMode);
        Assert.Equal(
            vectorSettings.QdrantApiKeyEnvironmentVariable,
            ArgumentValue(arguments, "--qdrant-api-key-environment-variable"));

        var fingerprint = Mem0ProcessClient.BuildProcessConfigurationFingerprint(
            runtime,
            runtime.ThinkingControl,
            embedding,
            vectorSettings,
            space);
        using var document = System.Text.Json.JsonDocument.Parse(fingerprint);
        var root = document.RootElement;
        Assert.Equal("ali_user_memories", root.GetProperty("baseCollectionName").GetString());
        Assert.Equal(space.Id, root.GetProperty("embeddingSpaceId").GetString());
        Assert.Equal(space.CollectionName, root.GetProperty("collectionName").GetString());
        Assert.Equal(space.DataRoot, root.GetProperty("dataRoot").GetString());
        Assert.Equal(Mem0ProcessClient.ProtocolIdentity, root.GetProperty("protocolIdentity").GetString());
        Assert.Equal(embedding.Identity.Protocol, root.GetProperty("protocol").GetString());
        Assert.Equal(embedding.Identity.ResolvedModel, root.GetProperty("resolvedModel").GetString());
        Assert.Equal(embedding.Identity.Quantization, root.GetProperty("quantization").GetString());
        Assert.Equal(
            embedding.Identity.MaximumContextTokens,
            root.GetProperty("maximumContextTokens").GetInt32());
        Assert.Equal(embedding.Identity.QueryPromptMode, root.GetProperty("queryPromptMode").GetString());
        Assert.Equal(
            embedding.Identity.DocumentPromptMode,
            root.GetProperty("documentPromptMode").GetString());
        Assert.Equal(
            embedding.Identity.QueryPromptPrefix,
            root.GetProperty("queryPromptPrefix").GetString());
        Assert.Equal(
            embedding.Identity.DocumentPromptPrefix,
            root.GetProperty("documentPromptPrefix").GetString());
        Assert.Equal(
            embedding.Identity.Fingerprint,
            root.GetProperty("embeddingIdentityFingerprint").GetString());
        Assert.Equal(vectorSettings.QdrantHost, root.GetProperty("qdrantHost").GetString());
        Assert.Equal(vectorSettings.QdrantHttpPort, root.GetProperty("qdrantHttpPort").GetInt32());
        Assert.Equal(vectorSettings.QdrantGrpcPort, root.GetProperty("qdrantGrpcPort").GetInt32());
        Assert.True(root.GetProperty("qdrantUseTls").GetBoolean());
        Assert.Equal(
            LocalEmbeddingProtocolIdentities.OpenAiCompatibleV1,
            root.GetProperty("embeddingProtocol").GetString());
        Assert.Equal(8192, root.GetProperty("embeddingContextTokens").GetInt32());
        Assert.Equal(
            "searchDocument",
            root.GetProperty("embeddingDocumentPromptMode").GetString());
        Assert.Equal(
            "searchQuery",
            root.GetProperty("embeddingQueryPromptMode").GetString());
        Assert.Equal(
            vectorSettings.QdrantApiKeyEnvironmentVariable,
            root.GetProperty("qdrantApiKeyEnvironmentVariable").GetString());
    }

    [Fact]
    public async Task ProbedEmbeddingIdentity_ReverifiesEveryResolutionWithoutCaching()
    {
        var handler = new FixedEmbeddingHandler();
        using var httpClient = new HttpClient(handler);
        using var source = new ProbedParticipantMemoryEmbeddingIdentitySource(httpClient);
        var settings = SharedEmbeddingSettings() with { EmbeddingDimensions = 3 };

        var first = await source.ResolveAsync(settings, TestContext.Current.CancellationToken);
        var repeated = await Task.WhenAll(Enumerable.Range(0, 32).Select(
            _ => source.ResolveAsync(settings, TestContext.Current.CancellationToken).AsTask()));

        Assert.Equal(66, handler.Count);
        Assert.All(repeated, identity =>
        {
            Assert.NotSame(first, identity);
            Assert.Equal(first.Fingerprint, identity.Fingerprint);
        });
        Assert.All(handler.Bodies, body =>
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            Assert.Equal("float", root.GetProperty("encoding_format").GetString());
            Assert.Equal(JsonValueKind.Array, root.GetProperty("input").ValueKind);
            Assert.Single(root.GetProperty("input").EnumerateArray());
        });
    }

    [Fact]
    public async Task Mem0CompatibleEmbeddingProbe_MatchesPinnedWorkerEnvelopeAndNormalization()
    {
        var handler = new FixedEmbeddingHandler();
        using var httpClient = new HttpClient(handler);
        var client = new OpenAiCompatibleEmbeddingClient(httpClient);
        var configuration = new LocalEmbeddingConfiguration(
            LocalEmbeddingProviders.Custom,
            new Uri("http://127.0.0.1:9123/v1/embeddings"),
            "custom-embedding-model",
            3,
            LocalEmbeddingProtocolIdentities.OpenAiCompatibleV1,
            8_192,
            EmbeddingPromptMode.Plain,
            EmbeddingPromptMode.Plain);

        var result = await client.CreateMem0CompatibleEmbeddingAsync(
            configuration,
            "query:\nAlice",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Message);
        using var document = JsonDocument.Parse(Assert.Single(handler.Bodies));
        var root = document.RootElement;
        Assert.Equal("custom-embedding-model", root.GetProperty("model").GetString());
        Assert.Equal("float", root.GetProperty("encoding_format").GetString());
        var input = Assert.Single(root.GetProperty("input").EnumerateArray());
        Assert.Equal("query: Alice", input.GetString());
        Assert.Equal("Bearer ali-local-only", Assert.Single(handler.AuthorizationHeaders));
    }

    [Fact]
    public async Task Mem0Send_RejectsSettingsChangeAfterSpaceResolutionBeforeWorkerStart()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "ali-mem0-space-race",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var vectorSettings = SharedEmbeddingSettings();
        try
        {
            await using var qdrant = new QdrantServiceManager(root);
            await using var client = new Mem0ProcessClient(
                root,
                qdrant,
                () => vectorSettings,
                static () => new UserMemorySettings(),
                RuntimeSettings,
                VerifiedIdentitySource.Instance);
            var expected = await client.ResolveCurrentEmbeddingSpaceAsync(
                TestContext.Current.CancellationToken);
            vectorSettings = vectorSettings with { EmbeddingModel = "changed-embedding-model" };

            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => client.SendAsync(
                    new
                    {
                        operation = "participant_health",
                        embeddingSpaceId = expected.Id
                    },
                    TestContext.Current.CancellationToken));

            Assert.Contains("changed after it was resolved", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("Stopped", qdrant.Status.State);
            Assert.False(qdrant.Status.IsOwnedProcess);
            Assert.False(Directory.Exists(client.DataRoot));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Mem0Send_RequiresCallerExpectedSpaceBeforeStartingDependencies()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "ali-mem0-missing-space",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var qdrant = new QdrantServiceManager(root);
            await using var client = new Mem0ProcessClient(
                root,
                qdrant,
                SharedEmbeddingSettings,
                static () => new UserMemorySettings(),
                RuntimeSettings,
                VerifiedIdentitySource.Instance);

            var error = await Assert.ThrowsAsync<ArgumentException>(
                () => client.SendAsync(
                    new { operation = "participant_health" },
                    TestContext.Current.CancellationToken));

            Assert.Contains("explicit exact embeddingSpaceId", error.Message, StringComparison.Ordinal);
            Assert.Equal("Stopped", qdrant.Status.State);
            Assert.False(qdrant.Status.IsOwnedProcess);
            Assert.False(Directory.Exists(client.DataRoot));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task InvalidSharedEmbeddingConfiguration_FailsBeforeQdrantOrMem0CanStart()
    {
        var root = Path.Combine(Path.GetTempPath(), "ali-mem0-invalid-settings", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var invalidSettings = SharedEmbeddingSettings() with
            {
                EmbeddingEndpoint = "http://127.0.0.1:9123/v1/embed"
            };
            await using var qdrant = new QdrantServiceManager(root);
            await using var client = new Mem0ProcessClient(
                root,
                qdrant,
                () => invalidSettings,
                static () => new UserMemorySettings(),
                RuntimeSettings);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => client.SendAsync(
                    new
                    {
                        operation = "health",
                        embeddingSpaceId = new string('0', 24)
                    },
                    TestContext.Current.CancellationToken));

            Assert.Contains("embedding configuration is invalid", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("Stopped", qdrant.Status.State);
            Assert.False(qdrant.Status.IsOwnedProcess);
            Assert.False(Directory.Exists(Path.Combine(root, "Memory", "Mem0")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Mem0Worker_UsesTheGenericLocalOpenAiCompatibleAdapter()
    {
        var worker = File.ReadAllText(FindRepositoryFile(
            "src", "Modules", "UserMemory", "Tools", "mem0_service.py"));
        var adapter = File.ReadAllText(FindRepositoryFile(
            "src", "Modules", "UserMemory", "Tools", "openai_compatible_llm.py"));

        Assert.Contains("--embedding-provider", worker, StringComparison.Ordinal);
        Assert.Contains("--embedding-api-base", worker, StringComparison.Ordinal);
        Assert.Contains("class RoleAwareOpenAIEmbedding", worker, StringComparison.Ordinal);
        Assert.Contains("search_document: ", worker, StringComparison.Ordinal);
        Assert.Contains("search_query: ", worker, StringComparison.Ordinal);
        Assert.Contains("memory_action == \"search\"", worker, StringComparison.Ordinal);
        Assert.Contains("127.0.0.1,localhost,::1", worker, StringComparison.Ordinal);
        Assert.Contains("qdrant_config[\"api_key\"] = qdrant_api_key", worker, StringComparison.Ordinal);
        Assert.Contains("\"https\": args.qdrant_use_tls == \"true\"", worker, StringComparison.Ordinal);
        Assert.Contains("openai_compatible_llm.LocalOpenAICompatibleLLM", worker, StringComparison.Ordinal);
        Assert.DoesNotContain("LemonadeLLM", worker, StringComparison.Ordinal);
        Assert.Contains("class LocalOpenAICompatibleLLM", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("class LemonadeLLM", adapter, StringComparison.Ordinal);
        Assert.Equal("127.0.0.1,localhost,::1", Mem0ProcessClient.LoopbackNoProxy);
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

    private static OpenAiCompatibleRuntimeOptions RuntimeSettings() => new(
        Enabled: true,
        Endpoint: new Uri("http://127.0.0.1:1234/v1/"),
        Model: "local-chat-model",
        DisplayName: "Local chat model",
        Family: "Generic",
        Size: "test",
        Quantization: "test",
        ContextTokens: 8192,
        OutputTokenLimit: 1024,
        Temperature: 0.1,
        TopP: null,
        StreamingEnabled: false,
        SupportsVision: false,
        SupportsToolCalls: true,
        AllowPrivateLanEndpoint: false);

    private sealed class VerifiedIdentitySource : IParticipantMemoryEmbeddingIdentitySource
    {
        public static VerifiedIdentitySource Instance { get; } = new();

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
            DateTimeOffset.Parse("2025-01-01T00:00:00Z"));
    }

    private sealed class FixedEmbeddingHandler : HttpMessageHandler
    {
        private int _count;
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _bodies = new();
        private readonly System.Collections.Concurrent.ConcurrentQueue<string?> _authorizationHeaders = new();

        public int Count => Volatile.Read(ref _count);

        public IReadOnlyList<string> Bodies => _bodies.ToArray();

        public IReadOnlyList<string?> AuthorizationHeaders => _authorizationHeaders.ToArray();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);
            _bodies.Enqueue(await request.Content!.ReadAsStringAsync(cancellationToken));
            _authorizationHeaders.Enqueue(request.Headers.Authorization?.ToString());
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"data\":[{\"embedding\":[0.25,-0.5,0.75]}]}",
                    System.Text.Encoding.UTF8,
                    "application/json")
            };
        }
    }

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
}
