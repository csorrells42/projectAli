using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Ali.Modules.RAG;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace Ali.Framework.Tests;

public sealed class LocalKnowledgeInfrastructureTests
{
    [Fact]
    public void TreeSitterChunksCSharpByDeclaration()
    {
        const string source = """
            namespace Sample;
            public sealed class Calculator
            {
                public int Add(int left, int right) => left + right;
                public int Subtract(int left, int right) => left - right;
            }
            """;

        var chunks = new StructuredDocumentChunker().Chunk("Calculator.cs", source, 1_400, 200);

        Assert.NotEmpty(chunks);
        Assert.Contains(chunks, chunk => chunk.Parser == "tree-sitter-c-sharp");
        Assert.Contains(chunks, chunk => chunk.Text.Contains("Calculator", StringComparison.Ordinal));
        Assert.All(chunks, chunk => Assert.True(chunk.StartLine > 0 && chunk.EndLine >= chunk.StartLine));
    }

    [Fact]
    public void PlainTextFallbackIsBoundedAndOverlapping()
    {
        var source = string.Join('\n', Enumerable.Range(1, 200).Select(index => $"line {index}: local knowledge content"));

        var chunks = new StructuredDocumentChunker().Chunk("notes.md", source, 400, 50);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk => Assert.Equal("plain-text", chunk.Parser));
        Assert.All(chunks, chunk => Assert.InRange(chunk.Text.Length, 1, 400));
    }

    [Fact]
    public void PointIdsAreStableAndChangeAcrossChunks()
    {
        var path = Path.Combine(Path.GetTempPath(), "ali-local-knowledge", "file.cs");
        var first = LocalVectorLibraryRetriever.CreatePointId(path, 0, 1, 10);
        var repeated = LocalVectorLibraryRetriever.CreatePointId(path, 0, 1, 10);
        var second = LocalVectorLibraryRetriever.CreatePointId(path, 1, 11, 20);

        Assert.Equal(first, repeated);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void EmbeddingSpaceMarker_CoversEveryConfiguredVectorSpaceBoundary()
    {
        var settings = new LocalVectorLibrarySettings
        {
            EmbeddingProvider = "provider-a",
            EmbeddingEndpoint = "http://127.0.0.1:1234/full/path/embeddings",
            EmbeddingModel = "model-a",
            EmbeddingDimensions = 768,
            QdrantHost = "127.0.0.1",
            QdrantHttpPort = 6333,
            QdrantGrpcPort = 6334,
            QdrantUseTls = false,
            QdrantCollectionName = "collection-a"
        };
        var marker = LocalVectorLibraryRetriever.CreateEmbeddingSpaceMarker(settings);
        var changedSettings = new[]
        {
            settings with { EmbeddingProvider = "provider-b" },
            settings with { EmbeddingEndpoint = "http://127.0.0.1:1234/other/embeddings" },
            settings with { EmbeddingModel = "model-b" },
            settings with { EmbeddingDimensions = 1024 },
            settings with { QdrantHost = "localhost" },
            settings with { QdrantHttpPort = 7333 },
              settings with { QdrantGrpcPort = 7334 },
              settings with { QdrantUseTls = true },
              settings with { QdrantCollectionName = "collection-b" },
              settings with { RootDirectory = Path.Combine(Path.GetTempPath(), "other-local-library") }
          };

        Assert.Matches("^[0-9A-F]{64}$", marker);
        Assert.All(changedSettings, changed =>
            Assert.NotEqual(marker, LocalVectorLibraryRetriever.CreateEmbeddingSpaceMarker(changed)));
        Assert.Equal(
              marker,
              LocalVectorLibraryRetriever.CreateEmbeddingSpaceMarker(
                  settings with { MaxFiles = 1 }));
    }

    [Fact]
    public void EmbeddingSpaceGuardDecision_ResetsOnlyWhenMismatchedArtifactsMayExist()
    {
        const string expected = "EXPECTED";

        Assert.Equal(
            EmbeddingSpaceGuardAction.Current,
            LocalVectorLibraryRetriever.DetermineEmbeddingSpaceGuardAction(
                expected, expected, scanStateExists: true, collectionExists: true));
        Assert.Equal(
            EmbeddingSpaceGuardAction.Current,
            LocalVectorLibraryRetriever.DetermineEmbeddingSpaceGuardAction(
                expected, expected, scanStateExists: false, collectionExists: false));
        Assert.Equal(
            EmbeddingSpaceGuardAction.ResetAndReindex,
            LocalVectorLibraryRetriever.DetermineEmbeddingSpaceGuardAction(
                expected, expected, scanStateExists: true, collectionExists: false));
        Assert.Equal(
            EmbeddingSpaceGuardAction.ResetAndReindex,
            LocalVectorLibraryRetriever.DetermineEmbeddingSpaceGuardAction(
                expected, expected, scanStateExists: false, collectionExists: true));
        Assert.Equal(
            EmbeddingSpaceGuardAction.InitializeMarker,
            LocalVectorLibraryRetriever.DetermineEmbeddingSpaceGuardAction(
                expected, observedMarker: null, scanStateExists: false, collectionExists: false));
        Assert.Equal(
            EmbeddingSpaceGuardAction.InitializeMarker,
            LocalVectorLibraryRetriever.DetermineEmbeddingSpaceGuardAction(
                expected, "OLD", scanStateExists: false, collectionExists: false));
        Assert.Equal(
            EmbeddingSpaceGuardAction.ResetAndReindex,
            LocalVectorLibraryRetriever.DetermineEmbeddingSpaceGuardAction(
                expected, observedMarker: null, scanStateExists: true, collectionExists: false));
        Assert.Equal(
            EmbeddingSpaceGuardAction.ResetAndReindex,
            LocalVectorLibraryRetriever.DetermineEmbeddingSpaceGuardAction(
                expected, "OLD", scanStateExists: false, collectionExists: true));
    }

    [Fact]
    public void EmbeddingSpaceMarker_IsStoredOutsideTheScanStateJson()
    {
        var root = Path.Combine(Path.GetTempPath(), "ali-marker-path-test");

        var markerPath = LocalVectorLibrarySettingsStore.GetEmbeddingSpaceMarkerPath(root);
        var scanStatePath = LocalVectorLibrarySettingsStore.GetScanStatePath(root);

        Assert.NotEqual(markerPath, scanStatePath);
        Assert.EndsWith(".sha256", markerPath, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(".json", scanStatePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RipgrepFindsLiteralTextAndHonorsAllowedExtensions()
    {
        var root = Path.Combine(Path.GetTempPath(), "ali-ripgrep-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "included.cs"), "public string Marker => \"alpha[42]\";", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(root, "excluded.bin"), "alpha[42]", TestContext.Current.CancellationToken);
        try
        {
            var results = await new RipgrepSearchService().SearchAsync(
                root, ["alpha[42]"], [".cs"], 5, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            var result = Assert.Single(results);
            Assert.Equal("included.cs:1", result.Name);
            Assert.Contains("alpha[42]", result.Excerpt, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RipgrepStopsAtTheRequestedGlobalResultLimit()
    {
        var root = Path.Combine(Path.GetTempPath(), "ali-ripgrep-limit-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        for (var index = 0; index < 25; index++)
        {
            await File.WriteAllTextAsync(Path.Combine(root, $"match-{index:D2}.txt"), "bounded-result-marker", TestContext.Current.CancellationToken);
        }
        try
        {
            var results = await new RipgrepSearchService().SearchAsync(
                root, ["bounded-result-marker"], [".txt"], 3, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(3, results.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RetrieverCombinesRipgrepAndQdrantWithoutChangingItsContract()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aqh-{Guid.NewGuid():N}"[..12]);
        var dataRoot = Path.Combine(root, "Settings");
        var library = Path.Combine(root, "Library");
        Directory.CreateDirectory(dataRoot);
        Directory.CreateDirectory(library);
        var directFile = Path.Combine(library, "notes.txt");
        await File.WriteAllTextAsync(directFile, "alpha marker explains the local semantic concept", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(library, "second.txt"), "beta marker proves a full forced reindex", TestContext.Current.CancellationToken);
        await using var embeddingServer = new FakeEmbeddingServer();
        var httpPort = GetFreeTcpPort();
        var grpcPort = GetFreeTcpPort();
        while (grpcPort == httpPort) grpcPort = GetFreeTcpPort();
        var settings = new LocalVectorLibrarySettings
        {
            RootDirectory = library,
            EmbeddingEndpoint = embeddingServer.Endpoint,
            EmbeddingDimensions = 3,
            QdrantHttpPort = httpPort,
            QdrantGrpcPort = grpcPort,
            QdrantCollectionName = $"h_{Guid.NewGuid():N}"[..12],
            MaxRetrievedChunks = 4,
            ScanIntervalMinutes = 1
        };

        await using var manager = new QdrantServiceManager(dataRoot);
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var retriever = new LocalVectorLibraryRetriever(dataRoot, httpClient, settings, manager);
        try
        {
            var result = await retriever.RetrieveAsync("alpha marker local document", TestContext.Current.CancellationToken);

            Assert.True(result.HasSources, string.Join(" ", result.Warnings));
            Assert.Contains(result.Excerpts, excerpt => excerpt.Name.StartsWith("notes.txt:1", StringComparison.Ordinal));
            Assert.Contains(result.Excerpts, excerpt => excerpt.Name.StartsWith("notes.txt", StringComparison.Ordinal));

            var markerPath = LocalVectorLibrarySettingsStore.GetEmbeddingSpaceMarkerPath(dataRoot);
            var firstMarker = await File.ReadAllTextAsync(markerPath, TestContext.Current.CancellationToken);
            var siblingCollection = $"s_{Guid.NewGuid():N}"[..12];
            using (var client = manager.CreateClient(settings))
            {
                await client.CreateCollectionAsync(
                    siblingCollection,
                    new VectorParams { Size = 3, Distance = Distance.Cosine },
                    cancellationToken: TestContext.Current.CancellationToken);
            }

            var changedSettings = settings with { EmbeddingModel = "changed-embedding-model" };
            var changedRetriever = new LocalVectorLibraryRetriever(
                dataRoot,
                httpClient,
                changedSettings,
                manager);
            var pending = await changedRetriever.GetStatusAsync(TestContext.Current.CancellationToken);
            Assert.True(pending.ServerReachable);
            Assert.False(pending.CollectionExists);
            Assert.Equal(0ul, pending.ChunkCount);
            Assert.Contains("pending an embedding-space rebuild", pending.Message, StringComparison.OrdinalIgnoreCase);
            using (var client = manager.CreateClient(settings))
            {
                Assert.True(await client.CollectionExistsAsync(
                    settings.QdrantCollectionName,
                    TestContext.Current.CancellationToken));
            }

            var directResult = await changedRetriever.RetrieveAsync(
                $"read local document \"{directFile}\"",
                TestContext.Current.CancellationToken);
            Assert.True(directResult.HasSources, string.Join(" ", directResult.Warnings));
            using (var client = manager.CreateClient(changedSettings))
            {
                Assert.True(await client.CollectionExistsAsync(
                    siblingCollection,
                    TestContext.Current.CancellationToken));
                Assert.Equal(
                    2ul,
                    await client.CountAsync(
                        changedSettings.QdrantCollectionName,
                        exact: true,
                        cancellationToken: TestContext.Current.CancellationToken));
            }

            var secondMarker = await File.ReadAllTextAsync(markerPath, TestContext.Current.CancellationToken);
            Assert.NotEqual(firstMarker, secondMarker);
            Assert.Equal(
                LocalVectorLibraryRetriever.CreateEmbeddingSpaceMarker(changedSettings),
                secondMarker.Trim());
        }
        finally
        {
            await manager.StopAsync(TestContext.Current.CancellationToken);
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task PinnedQdrantPerformsHealthUpsertFilteredQueryAndDelete()
    {
        var executable = FindRepositoryFile(Path.Combine("artifacts", "runtime-assets", "win-x64", "dependencies", "qdrant", "qdrant.exe"));
        Assert.True(File.Exists(executable), $"Pinned Qdrant runtime missing: {executable}");
        var root = Path.Combine(Path.GetTempPath(), "ali-qdrant-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var httpPort = GetFreeTcpPort();
        var grpcPort = GetFreeTcpPort();
        while (grpcPort == httpPort) grpcPort = GetFreeTcpPort();

        using var process = StartQdrant(executable, root, httpPort, grpcPort);
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            using var client = new QdrantClient("127.0.0.1", grpcPort, grpcTimeout: TimeSpan.FromSeconds(10));
            await WaitForHealthAsync(client, process, cancellationToken);
            var collection = $"ali_test_{Guid.NewGuid():N}";
            await client.CreateCollectionAsync(collection, new VectorParams { Size = 3, Distance = Distance.Cosine }, cancellationToken: cancellationToken);
            await client.UpsertAsync(collection,
            [
                new PointStruct { Id = 1, Vectors = new[] { 1f, 0f, 0f }, Payload = { ["document_path"] = "one.cs", ["content"] = "alpha" } },
                new PointStruct { Id = 2, Vectors = new[] { 0f, 1f, 0f }, Payload = { ["document_path"] = "two.cs", ["content"] = "beta" } }
            ], wait: true, cancellationToken: cancellationToken);

            var found = await client.QueryAsync(collection, query: new[] { 0.95f, 0.05f, 0f }, limit: 1, cancellationToken: cancellationToken);
            Assert.Single(found);
            Assert.Equal("alpha", found[0].Payload["content"].StringValue);

            await client.DeleteAsync(collection, Qdrant.Client.Grpc.Conditions.MatchKeyword("document_path", "one.cs"), cancellationToken: cancellationToken);
            Assert.Equal(1ul, await client.CountAsync(collection, exact: true, cancellationToken: cancellationToken));
            await client.DeleteCollectionAsync(collection, cancellationToken: cancellationToken);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task ManagedQdrantStartsAndStopsOnlyItsOwnedProcess()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "ali-qdrant-manager-test", Guid.NewGuid().ToString("N"), "Settings");
        Directory.CreateDirectory(dataRoot);
        var httpPort = GetFreeTcpPort();
        var grpcPort = GetFreeTcpPort();
        while (grpcPort == httpPort) grpcPort = GetFreeTcpPort();
        var settings = new LocalVectorLibrarySettings
        {
            QdrantHttpPort = httpPort,
            QdrantGrpcPort = grpcPort,
            QdrantRequestTimeoutSeconds = 5,
            UseManagedLocalQdrant = true,
            AutoStartQdrant = true
        };

        await using var manager = new QdrantServiceManager(dataRoot);
        var started = await manager.StartAsync(settings, TestContext.Current.CancellationToken);
        Assert.True(started.IsReachable);
        Assert.True(started.IsOwnedProcess);
        var stopped = await manager.StopAsync(TestContext.Current.CancellationToken);
        Assert.False(stopped.IsReachable);
        Assert.True(Directory.Exists(LocalVectorLibrarySettingsStore.GetQdrantDataPath(dataRoot)));
    }

    private static Process StartQdrant(string executable, string root, int httpPort, int grpcPort)
    {
        var info = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        info.Environment["QDRANT__SERVICE__HOST"] = "127.0.0.1";
        info.Environment["QDRANT__SERVICE__HTTP_PORT"] = httpPort.ToString();
        info.Environment["QDRANT__SERVICE__GRPC_PORT"] = grpcPort.ToString();
        info.Environment["QDRANT__STORAGE__STORAGE_PATH"] = Path.Combine(root, "storage");
        info.Environment["QDRANT__STORAGE__SNAPSHOTS_PATH"] = Path.Combine(root, "snapshots");
        info.Environment["QDRANT__TELEMETRY_DISABLED"] = "true";
        return Process.Start(info) ?? throw new InvalidOperationException("Test Qdrant did not start.");
    }

    private static async Task WaitForHealthAsync(QdrantClient client, Process process, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        Exception? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (process.HasExited) throw new InvalidOperationException(await process.StandardError.ReadToEndAsync());
            try { await client.HealthAsync(cancellationToken); return; }
            catch (Exception ex) when (ex is Grpc.Core.RpcException or TimeoutException) { last = ex; await Task.Delay(200, cancellationToken); }
        }
        throw new TimeoutException("Qdrant health test timed out.", last);
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string FindRepositoryFile(string relative)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relative);
            if (File.Exists(candidate)) return candidate;
        }
        return Path.GetFullPath(relative);
    }

    private sealed class FakeEmbeddingServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Task _loop;

        public FakeEmbeddingServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Endpoint = $"http://127.0.0.1:{port}/api/v1/embeddings";
            _loop = RunAsync();
        }

        public string Endpoint { get; }

        private async Task RunAsync()
        {
            while (!_cancellation.IsCancellationRequested)
            {
                try
                {
                    using var client = await _listener.AcceptTcpClientAsync(_cancellation.Token);
                    await using var stream = client.GetStream();
                    var buffer = new byte[16_384];
                    _ = await stream.ReadAsync(buffer, _cancellation.Token);
                    const string body = "{\"data\":[{\"embedding\":[1.0,0.0,0.0]}]}";
                    var bodyBytes = System.Text.Encoding.UTF8.GetBytes(body);
                    var header = System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(header, _cancellation.Token);
                    await stream.WriteAsync(bodyBytes, _cancellation.Token);
                }
                catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
                {
                    break;
                }
                catch (ObjectDisposedException) when (_cancellation.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cancellation.Cancel();
            _listener.Stop();
            try { await _loop; } catch (OperationCanceledException) { }
            _cancellation.Dispose();
        }
    }
}
