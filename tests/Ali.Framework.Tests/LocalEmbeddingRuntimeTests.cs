using System.Net;
using System.Text;
using System.Text.Json;
using Ali.Modules.Embeddings;
using Ali.Modules.RAG;

namespace Ali.Framework.Tests;

public sealed class LocalEmbeddingRuntimeTests
{
    [Fact]
    public void NewSettings_SelectNomicRolesAndContextWithoutChoosingALoadedModel()
    {
        var settings = new LocalVectorLibrarySettings();

        Assert.Equal(LocalEmbeddingProviders.Custom, settings.EmbeddingProvider);
        Assert.Equal(string.Empty, settings.EmbeddingEndpoint);
        Assert.Equal(string.Empty, settings.EmbeddingModel);
        Assert.Equal(768, settings.EmbeddingDimensions);
        Assert.Equal(8192, settings.EmbeddingContextTokens);
        Assert.Equal(EmbeddingPromptMode.SearchDocument, settings.EmbeddingDocumentPromptMode);
        Assert.Equal(EmbeddingPromptMode.SearchQuery, settings.EmbeddingQueryPromptMode);
        Assert.True(settings.Enabled);
        Assert.False(settings.SemanticToolRetrievalEnabled);
    }

    [Fact]
    public void ProviderCatalog_ExposesLabelsWithoutSupplyingEndpointsPortsOrModels()
    {
        Assert.Equal(
            [
                LocalEmbeddingProviders.LmStudio,
                LocalEmbeddingProviders.Ollama,
                LocalEmbeddingProviders.LlamaCpp,
                LocalEmbeddingProviders.Lemonade,
                LocalEmbeddingProviders.Custom
            ],
            LocalEmbeddingProviders.Choices);

        Assert.True(LocalEmbeddingConfiguration.TryCreate(
            LocalEmbeddingProviders.Custom,
            "http://127.0.0.1:1234/v1/embeddings",
            "user-selected-model",
            768,
            LocalEmbeddingProtocolIdentities.OpenAiCompatibleV1,
            8192,
            EmbeddingPromptMode.SearchDocument,
            EmbeddingPromptMode.SearchQuery,
            out var custom,
            out var customFailure), customFailure);
        Assert.Equal(LocalEmbeddingProviders.Custom, custom!.Provider);

        Assert.True(LocalEmbeddingConfiguration.TryCreate(
            LocalEmbeddingProviders.LmStudio,
            "http://127.0.0.1:11434/v1/embeddings",
            "user-selected-model",
            768,
            LocalEmbeddingProtocolIdentities.OpenAiCompatibleV1,
            8192,
            EmbeddingPromptMode.SearchDocument,
            EmbeddingPromptMode.SearchQuery,
            out var explicitLmStudio,
            out var lmStudioFailure), lmStudioFailure);
        Assert.Equal(LocalEmbeddingProviders.LmStudio, explicitLmStudio!.Provider);
    }

    [Fact]
    public void LegacySettings_LoadVerbatimAsCustomWithoutRewritingTheFile()
    {
        var root = TemporaryRoot();
        var path = LocalVectorLibrarySettingsStore.GetSettingsPath(root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        const string existing =
            "{\r\n"
            + "  \"embeddingEndpoint\": \"http://127.0.0.1:13305/api/v1/embeddings\",\r\n"
            + "  \"embeddingModel\": \"user-selected-legacy-model\"\r\n"
            + "}";
        File.WriteAllText(path, existing);
        var timestamp = File.GetLastWriteTimeUtc(path);
        try
        {
            var loaded = LocalVectorLibrarySettingsStore.LoadOrDefault(root);

            Assert.Equal(LocalEmbeddingProviders.Custom, loaded.EmbeddingProvider);
            Assert.Equal("http://127.0.0.1:13305/api/v1/embeddings", loaded.EmbeddingEndpoint);
            Assert.Equal("user-selected-legacy-model", loaded.EmbeddingModel);
            Assert.Equal(768, loaded.EmbeddingDimensions);
            Assert.Equal(8192, loaded.EmbeddingContextTokens);
            Assert.Equal(EmbeddingPromptMode.SearchDocument, loaded.EmbeddingDocumentPromptMode);
            Assert.Equal(EmbeddingPromptMode.SearchQuery, loaded.EmbeddingQueryPromptMode);
            Assert.Equal(existing, File.ReadAllText(path));
            Assert.Equal(timestamp, File.GetLastWriteTimeUtc(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LegacyRootMove_DoesNotRewriteAFileMissingCurrentEmbeddingFields()
    {
        var root = TemporaryRoot();
        var path = LocalVectorLibrarySettingsStore.GetSettingsPath(root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var existing = JsonSerializer.Serialize(new
        {
            rootDirectory = LocalVectorLibrarySettings.LegacyDefaultRootDirectory(),
            embeddingEndpoint = "http://127.0.0.1:13305/api/v1/embeddings",
            embeddingModel = "user-selected-legacy-model"
        });
        File.WriteAllText(path, existing);
        var timestamp = File.GetLastWriteTimeUtc(path);
        try
        {
            LocalVectorLibrarySettingsStore.MoveLegacyDefaultRootIfNeeded(root);
            var loaded = LocalVectorLibrarySettingsStore.LoadOrDefault(root);

            Assert.Equal(LocalVectorLibrarySettings.LegacyDefaultRootDirectory(), loaded.RootDirectory);
            Assert.Equal(LocalEmbeddingProviders.Custom, loaded.EmbeddingProvider);
            Assert.Equal("http://127.0.0.1:13305/api/v1/embeddings", loaded.EmbeddingEndpoint);
            Assert.Equal("user-selected-legacy-model", loaded.EmbeddingModel);
            Assert.Equal(768, loaded.EmbeddingDimensions);
            Assert.Equal(existing, File.ReadAllText(path));
            Assert.Equal(timestamp, File.GetLastWriteTimeUtc(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("{ definitely-not-valid-json")]
    [InlineData("{\"embeddingProvider\":\"LM Studio\"}")]
    public async Task CorruptOrIncompleteSettings_DoNotRewriteOrAttemptEmbeddingNetwork(string corrupt)
    {
        var root = TemporaryRoot();
        var settingsRoot = Path.Combine(root, "Settings");
        var library = Path.Combine(root, "Library");
        Directory.CreateDirectory(library);
        await File.WriteAllTextAsync(
            Path.Combine(library, "notes.txt"),
            "corrupt-settings-marker remains available through exact text search",
            TestContext.Current.CancellationToken);
        var path = LocalVectorLibrarySettingsStore.GetSettingsPath(settingsRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, corrupt);
        var timestamp = File.GetLastWriteTimeUtc(path);
        var handler = new RecordingHandler(
            HttpStatusCode.OK,
            "{\"data\":[{\"embedding\":[1.0,0.5,0.25]}]}");
        using var httpClient = new HttpClient(handler);
        await using var qdrant = new QdrantServiceManager(settingsRoot);
        try
        {
            var loaded = LocalVectorLibrarySettingsStore.LoadOrDefault(settingsRoot);
            Assert.False(LocalEmbeddingConfiguration.TryCreate(
                loaded.EmbeddingProvider,
                loaded.EmbeddingEndpoint,
                loaded.EmbeddingModel,
                loaded.EmbeddingDimensions,
                loaded.EmbeddingProtocolIdentity,
                loaded.EmbeddingContextTokens,
                loaded.EmbeddingDocumentPromptMode,
                loaded.EmbeddingQueryPromptMode,
                out _,
                out _));

            var retriever = new LocalVectorLibraryRetriever(
                settingsRoot,
                httpClient,
                loaded with { RootDirectory = library },
                qdrant);
            var result = await retriever.RetrieveAsync(
                "corrupt-settings-marker local document",
                TestContext.Current.CancellationToken);
            var status = await retriever.GetStatusAsync(TestContext.Current.CancellationToken);
            var scan = await retriever.ScanAsync(force: true, TestContext.Current.CancellationToken);
            var rebuildFailure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                retriever.RebuildAsync(TestContext.Current.CancellationToken));

            Assert.Contains(result.Warnings, warning =>
                warning.Contains("embeddings are unavailable", StringComparison.OrdinalIgnoreCase));
            Assert.False(status.ServerReachable);
            Assert.Contains("embeddings are unavailable", status.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(scan.ServerReachable);
            Assert.Contains("embeddings are unavailable", scan.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("embeddings are unavailable", rebuildFailure.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, handler.Count);
            Assert.Equal(corrupt, File.ReadAllText(path));
            Assert.Equal(timestamp, File.GetLastWriteTimeUtc(path));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Save_RoundTripsExactEmbeddingSelectionWithoutLeavingATemporaryFile()
    {
        var root = TemporaryRoot();
        var settings = new LocalVectorLibrarySettings
        {
            EmbeddingProvider = LocalEmbeddingProviders.Custom,
            EmbeddingEndpoint = "http://127.0.0.1:9001/custom/embeddings",
            EmbeddingModel = "exact-user-model",
            EmbeddingDimensions = 1024,
            EmbeddingContextTokens = 16_384,
            EmbeddingDocumentPromptMode = EmbeddingPromptMode.SearchDocument,
            EmbeddingQueryPromptMode = EmbeddingPromptMode.SearchQuery
        };
        try
        {
            LocalVectorLibrarySettingsStore.Save(root, settings);
            var loaded = LocalVectorLibrarySettingsStore.LoadOrDefault(root);

            Assert.Equal(settings.EmbeddingProvider, loaded.EmbeddingProvider);
            Assert.Equal(settings.EmbeddingEndpoint, loaded.EmbeddingEndpoint);
            Assert.Equal(settings.EmbeddingModel, loaded.EmbeddingModel);
            Assert.Equal(settings.EmbeddingDimensions, loaded.EmbeddingDimensions);
            Assert.Equal(settings.EmbeddingContextTokens, loaded.EmbeddingContextTokens);
            Assert.Equal(settings.EmbeddingDocumentPromptMode, loaded.EmbeddingDocumentPromptMode);
            Assert.Equal(settings.EmbeddingQueryPromptMode, loaded.EmbeddingQueryPromptMode);
            var directory = Path.GetDirectoryName(LocalVectorLibrarySettingsStore.GetSettingsPath(root))!;
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ApiBase_IsDerivedOnlyFromTheExactFinalEmbeddingsSegment()
    {
        Assert.True(LocalEmbeddingConfiguration.TryCreate(
            LocalEmbeddingProviders.Lemonade,
            "http://127.0.0.1:13305/api/v1/embeddings",
            "model",
            768,
            LocalEmbeddingProtocolIdentities.OpenAiCompatibleV1,
            8192,
            EmbeddingPromptMode.SearchDocument,
            EmbeddingPromptMode.SearchQuery,
            out var valid,
            out var createFailure), createFailure);
        Assert.True(valid!.TryGetOpenAiApiBaseUri(out var apiBase, out var baseFailure), baseFailure);
        Assert.Equal("http://127.0.0.1:13305/api/v1/", apiBase!.ToString());

        Assert.True(LocalEmbeddingConfiguration.TryCreate(
            LocalEmbeddingProviders.Custom,
            "http://127.0.0.1:1234/v1/embed",
            "model",
            768,
            LocalEmbeddingProtocolIdentities.OpenAiCompatibleV1,
            8192,
            EmbeddingPromptMode.SearchDocument,
            EmbeddingPromptMode.SearchQuery,
            out var invalidBase,
            out createFailure), createFailure);
        Assert.False(invalidBase!.TryGetOpenAiApiBaseUri(out _, out baseFailure));
        Assert.Contains("end with /embeddings", baseFailure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Client_PostsTypedNomicQueryRoleAndBindsTheEffectiveIdentity()
    {
        var handler = new RecordingHandler(
            HttpStatusCode.OK,
            "{\"data\":[{\"embedding\":[1.0,0.5,0.25]}]}");
        using var httpClient = new HttpClient(handler);
        var client = new OpenAiCompatibleEmbeddingClient(httpClient);
        var configuration = CreateConfiguration(dimensions: 3);

        var result = await client.CreateEmbeddingAsync(
            configuration,
            "exact input",
            EmbeddingInputRole.RetrievalQuery,
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Message);
        var vector = Assert.IsType<float[]>(result.Vector);
        Assert.Equal([1f, 0.5f, 0.25f], vector);
        Assert.Equal(1, handler.Count);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal(configuration.Endpoint, handler.RequestUri);
        using var request = JsonDocument.Parse(handler.Body!);
        Assert.Equal(configuration.Model, request.RootElement.GetProperty("model").GetString());
        Assert.Equal("search_query: exact input", request.RootElement.GetProperty("input").GetString());
        Assert.False(request.RootElement.TryGetProperty("prompt", out _));
        Assert.Equal(EmbeddingPromptMode.SearchQuery, result.PromptMode);
        Assert.Equal(8192, result.EffectiveContextTokens);
        Assert.Equal(configuration.CaptureBindingIdentity(EmbeddingInputRole.RetrievalQuery), result.BindingIdentity);
    }

    [Fact]
    public async Task Client_RejectsWrongDimensionsWithoutTryingAnotherEndpoint()
    {
        var handler = new RecordingHandler(
            HttpStatusCode.OK,
            "{\"data\":[{\"embedding\":[1.0,0.5]}]}");
        using var httpClient = new HttpClient(handler);
        var client = new OpenAiCompatibleEmbeddingClient(httpClient);

        var result = await client.CreateEmbeddingAsync(
            CreateConfiguration(dimensions: 3),
            "dimension gate",
            EmbeddingInputRole.RetrievalQuery,
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Null(result.Vector);
        Assert.Contains("returned 2 dimensions; exactly 3", result.Message, StringComparison.Ordinal);
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task Client_BoundsHttpFailureAndDoesNotProbeALegacyPath()
    {
        var handler = new RecordingHandler(
            HttpStatusCode.BadRequest,
            "{\"error\":{\"message\":\"" + new string('x', 1000) + "\"}}");
        using var httpClient = new HttpClient(handler);
        var client = new OpenAiCompatibleEmbeddingClient(httpClient);

        var result = await client.CreateEmbeddingAsync(
            CreateConfiguration(dimensions: 3),
            "bad request",
            EmbeddingInputRole.RetrievalQuery,
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(1, handler.Count);
        Assert.Contains("HTTP 400", result.Message, StringComparison.Ordinal);
        Assert.True(result.Message.Length < 400, result.Message);
    }

    [Fact]
    public async Task ContextProbe_SendsConfigured8192TokenInputWithTheQueryRole()
    {
        var handler = new RecordingHandler(
            HttpStatusCode.OK,
            "{\"data\":[{\"embedding\":[1.0,0.5,0.25]}]}");
        using var httpClient = new HttpClient(handler);
        var configuration = CreateConfiguration(dimensions: 3);

        var result = await new OpenAiCompatibleEmbeddingClient(httpClient)
            .ProbeConfiguredContextAsync(configuration, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Message);
        using var request = JsonDocument.Parse(handler.Body!);
        var input = request.RootElement.GetProperty("input").GetString()!;
        Assert.StartsWith("search_query: ", input, StringComparison.Ordinal);
        Assert.Equal(8192, input["search_query: ".Length..].Split(' ').Length);
    }

    [Fact]
    public async Task Client_PostsSearchDocumentForStoredContent()
    {
        var handler = new RecordingHandler(
            HttpStatusCode.OK,
            "{\"data\":[{\"embedding\":[1.0,0.5,0.25]}]}");
        using var httpClient = new HttpClient(handler);

        var result = await new OpenAiCompatibleEmbeddingClient(httpClient).CreateEmbeddingAsync(
            CreateConfiguration(dimensions: 3),
            "stored memory",
            EmbeddingInputRole.StoredDocument,
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Message);
        using var request = JsonDocument.Parse(handler.Body!);
        Assert.Equal("search_document: stored memory", request.RootElement.GetProperty("input").GetString());
    }

    private static LocalEmbeddingConfiguration CreateConfiguration(int dimensions)
    {
        Assert.True(LocalEmbeddingConfiguration.TryCreate(
            LocalEmbeddingProviders.LmStudio,
            "http://127.0.0.1:1234/v1/embeddings",
            "text-embedding-nomic-embed-text-v1",
            dimensions,
            LocalEmbeddingProtocolIdentities.OpenAiCompatibleV1,
            8192,
            EmbeddingPromptMode.SearchDocument,
            EmbeddingPromptMode.SearchQuery,
            out var configuration,
            out var failure), failure);
        return configuration!;
    }

    private static string TemporaryRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "Ali.Framework.Tests",
            $"embedding-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class RecordingHandler(HttpStatusCode statusCode, string responseBody) : HttpMessageHandler
    {
        public int Count { get; private set; }

        public HttpMethod? Method { get; private set; }

        public Uri? RequestUri { get; private set; }

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Count++;
            Method = request.Method;
            RequestUri = request.RequestUri;
            Body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            };
        }
    }
}
