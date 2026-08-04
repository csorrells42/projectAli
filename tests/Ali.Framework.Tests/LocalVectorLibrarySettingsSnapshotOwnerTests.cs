using System.Net;
using System.Text;
using Ali.Modules.Internet;
using Ali.Modules.RAG;

namespace Ali.Framework.Tests;

public sealed class LocalVectorLibrarySettingsSnapshotOwnerTests
{
    [Fact]
    public async Task PublishedSave_ImmediatelyChangesLongLivedProductionSearchWithoutRestart()
    {
        var root = TemporaryRoot();
        var library = Path.Combine(root, "Library");
        Directory.CreateDirectory(library);
        var initial = new LocalVectorLibrarySettings
        {
            RootDirectory = library,
            EnableRipgrep = false,
            UseManagedLocalQdrant = false,
            AutoStartQdrant = false,
            EmbeddingEndpoint = "http://127.0.0.1:41001/v1/embeddings",
            EmbeddingModel = "startup-embedding-model",
            EmbeddingDimensions = 3
        };
        LocalVectorLibrarySettingsStore.Save(root, initial);
        var owner = new LocalVectorLibrarySettingsSnapshotOwner(root);
        var handler = new RecordingHandler(
            HttpStatusCode.BadRequest,
            "{\"error\":{\"message\":\"stop before qdrant\"}}");
        using var httpClient = new HttpClient(handler);
        await using var qdrant = new QdrantServiceManager(root);
        var retriever = new LocalVectorLibraryRetriever(root, httpClient, owner, qdrant);

        try
        {
            var published = owner.Save(initial with
            {
                EmbeddingEndpoint = "http://127.0.0.1:42002/custom/embeddings",
                EmbeddingModel = "live-embedding-model"
            });

            var result = await retriever.RetrieveAsync(
                new SourceQueryPlan(
                    true,
                    true,
                    "local_documents",
                    "find the local project notes",
                    ["project", "notes"],
                    ["local_documents"]),
                TestContext.Current.CancellationToken);

            Assert.Equal(2, published.Version);
            Assert.Equal(1, handler.Count);
            Assert.Equal(
                "http://127.0.0.1:42002/custom/embeddings",
                handler.RequestUri?.AbsoluteUri);
            Assert.NotNull(handler.Body);
            Assert.Contains("live-embedding-model", handler.Body, StringComparison.Ordinal);
            Assert.DoesNotContain("startup-embedding-model", handler.Body, StringComparison.Ordinal);
            Assert.Contains(result.Warnings, warning =>
                warning.Contains("stop before qdrant", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void RootDirectoryOnlySave_FromStaleEditorPreservesCurrentConnectionSettings()
    {
        var root = TemporaryRoot();
        var initial = new LocalVectorLibrarySettings
        {
            RootDirectory = Path.Combine(root, "InitialLibrary"),
            EmbeddingEndpoint = "http://127.0.0.1:41001/v1/embeddings",
            EmbeddingModel = "startup-embedding-model",
            QdrantHost = "127.0.0.1",
            QdrantHttpPort = 6333,
            QdrantGrpcPort = 6334
        };
        LocalVectorLibrarySettingsStore.Save(root, initial);
        var owner = new LocalVectorLibrarySettingsSnapshotOwner(root);
        var staleEditorSnapshot = owner.Capture();
        var currentConnection = initial with
        {
            EmbeddingEndpoint = "http://127.0.0.1:42002/custom/embeddings",
            EmbeddingModel = "live-embedding-model",
            QdrantHost = "qdrant.example.test",
            QdrantHttpPort = 7433,
            QdrantGrpcPort = 7434,
            QdrantUseTls = true,
            UseManagedLocalQdrant = false
        };

        try
        {
            owner.Save(currentConnection);
            var newRoot = Path.Combine(root, "NewLibrary");
            var published = owner.SaveRootDirectory(newRoot);
            var persisted = LocalVectorLibrarySettingsStore.LoadOrDefault(root);

            Assert.Equal("startup-embedding-model", staleEditorSnapshot.Settings.EmbeddingModel);
            Assert.Equal(3, published.Version);
            Assert.Equal(Path.GetFullPath(newRoot), published.Settings.RootDirectory);
            Assert.Equal(currentConnection.EmbeddingEndpoint, published.Settings.EmbeddingEndpoint);
            Assert.Equal(currentConnection.EmbeddingModel, published.Settings.EmbeddingModel);
            Assert.Equal(currentConnection.QdrantHost, published.Settings.QdrantHost);
            Assert.Equal(currentConnection.QdrantHttpPort, published.Settings.QdrantHttpPort);
            Assert.Equal(currentConnection.QdrantGrpcPort, published.Settings.QdrantGrpcPort);
            Assert.Equal(currentConnection.QdrantUseTls, published.Settings.QdrantUseTls);
            Assert.Equal(currentConnection.UseManagedLocalQdrant, published.Settings.UseManagedLocalQdrant);
            Assert.Equal(published.Settings.RootDirectory, persisted.RootDirectory);
            Assert.Equal(published.Settings.EmbeddingEndpoint, persisted.EmbeddingEndpoint);
            Assert.Equal(published.Settings.EmbeddingModel, persisted.EmbeddingModel);
            Assert.Equal(published.Settings.QdrantHost, persisted.QdrantHost);
            Assert.Equal(published.Settings.QdrantHttpPort, persisted.QdrantHttpPort);
            Assert.Equal(published.Settings.QdrantGrpcPort, persisted.QdrantGrpcPort);
            Assert.Equal(published.Settings.QdrantUseTls, persisted.QdrantUseTls);
            Assert.Equal(published.Settings.UseManagedLocalQdrant, persisted.UseManagedLocalQdrant);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private static string TemporaryRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "Ali.Framework.Tests",
            $"vector-settings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class RecordingHandler(HttpStatusCode statusCode, string responseBody) : HttpMessageHandler
    {
        public int Count { get; private set; }

        public Uri? RequestUri { get; private set; }

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Count++;
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
