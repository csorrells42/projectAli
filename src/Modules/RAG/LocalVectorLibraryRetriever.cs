using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ali.Modules.Embeddings;
using Ali.Modules.Internet;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using static Qdrant.Client.Grpc.Conditions;

namespace Ali.Modules.RAG;

public sealed record LocalKnowledgeStatus(
    bool ServerReachable,
    bool CollectionExists,
    ulong ChunkCount,
    int DocumentCount,
    DateTimeOffset LastScanUtc,
    string Message);

internal enum EmbeddingSpaceGuardAction
{
    Current,
    InitializeMarker,
    ResetAndReindex
}

public sealed class LocalVectorLibraryRetriever : ISourceRetriever
{
    private const string LocalDocumentsTopic = "local_documents";
    private const int MaxExcerptCharacters = 1_800;
    private static readonly char[] QuerySeparators =
        [' ', ',', '.', '?', '!', ':', ';', '/', '\\', '-', '_', '(', ')', '[', ']', '"', '\''];
    private static readonly HashSet<string> LocalDocumentTerms = new(StringComparer.OrdinalIgnoreCase)
    {
        "document", "documents", "doc", "file", "files", "folder", "library", "manual", "rag"
    };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _dataRoot;
    private readonly string _scanStatePath;
    private readonly string _embeddingSpaceMarkerPath;
    private readonly HttpClient _httpClient;
    private readonly OpenAiCompatibleEmbeddingClient _embeddingClient;
    private readonly LocalVectorLibrarySettings _settings;
    private readonly Func<LocalVectorLibrarySettings>? _settingsProvider;
    private readonly QdrantServiceManager _qdrant;
    private readonly IDocumentChunker _chunker;
    private readonly RipgrepSearchService _ripgrep;
    private readonly SemaphoreSlim _scanGate;

    public LocalVectorLibraryRetriever(
        string dataRoot,
        HttpClient httpClient,
        LocalVectorLibrarySettings? settings = null,
        QdrantServiceManager? qdrant = null,
        IDocumentChunker? chunker = null,
        RipgrepSearchService? ripgrep = null)
        : this(dataRoot, httpClient, settings, qdrant, chunker, ripgrep, scanGate: null)
    {
    }

    private LocalVectorLibraryRetriever(
        string dataRoot,
        HttpClient httpClient,
        LocalVectorLibrarySettings? settings,
        QdrantServiceManager? qdrant,
        IDocumentChunker? chunker,
        RipgrepSearchService? ripgrep,
        SemaphoreSlim? scanGate)
    {
        _dataRoot = dataRoot;
        _scanStatePath = LocalVectorLibrarySettingsStore.GetScanStatePath(dataRoot);
        _embeddingSpaceMarkerPath = LocalVectorLibrarySettingsStore.GetEmbeddingSpaceMarkerPath(dataRoot);
        _httpClient = httpClient;
        _embeddingClient = new OpenAiCompatibleEmbeddingClient(httpClient);
        _settings = settings ?? LocalVectorLibrarySettingsStore.LoadOrDefault(dataRoot);
        _settingsProvider = null;
        _qdrant = qdrant ?? new QdrantServiceManager(dataRoot);
        _chunker = chunker ?? new StructuredDocumentChunker();
        _ripgrep = ripgrep ?? new RipgrepSearchService();
        _scanGate = scanGate ?? new SemaphoreSlim(1, 1);
    }

    public LocalVectorLibraryRetriever(
        string dataRoot,
        HttpClient httpClient,
        LocalVectorLibrarySettingsSnapshotOwner settingsOwner,
        QdrantServiceManager? qdrant = null,
        IDocumentChunker? chunker = null,
        RipgrepSearchService? ripgrep = null)
        : this(
            dataRoot,
            httpClient,
            CaptureSettings(settingsOwner),
            qdrant,
            chunker,
            ripgrep)
    {
        _settingsProvider = () => settingsOwner.Capture().Settings;
    }

    public void WriteExample()
    {
        var operation = BindCurrentSettings();
        if (!ReferenceEquals(operation, this))
        {
            operation.WriteExample();
            return;
        }

        try
        {
            LocalVectorLibrarySettingsStore.WriteExample(_dataRoot);
            if (_settings.Enabled)
            {
                Directory.CreateDirectory(_settings.RootDirectory);
                Directory.CreateDirectory(Path.GetDirectoryName(_scanStatePath)!);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public Task<SourceRetrievalResult> RetrieveAsync(string userText, CancellationToken cancellationToken) =>
        RetrieveAsync(new SourceQueryPlan(
            true, true, LocalDocumentsTopic, userText, Tokenize(userText).ToArray(), [LocalDocumentsTopic]), cancellationToken);

    public async Task<SourceRetrievalResult> RetrieveAsync(SourceQueryPlan plan, CancellationToken cancellationToken)
    {
        var operation = BindCurrentSettings();
        if (!ReferenceEquals(operation, this))
        {
            return await operation.RetrieveAsync(plan, cancellationToken).ConfigureAwait(false);
        }

        if (!_settings.Enabled || !ShouldAttempt(plan))
        {
            return SourceRetrievalResult.Empty;
        }

        var warnings = new List<string>();
        IReadOnlyList<SourceExcerpt> lexical = [];
        try
        {
            var searchText = BuildSearchText(plan);
            var directPath = TryExtractDirectFilePath(searchText);
            string? approved = null;
            if (directPath is not null && !TryResolveApprovedFile(directPath, out approved, out var warning))
            {
                warnings.Add(warning);
                return new([], warnings, plan.RequiresSourceGrounding);
            }

            lexical = await SearchLexicalSafelyAsync(
                approved ?? _settings.RootDirectory,
                plan.QueryTerms.Count > 0 ? plan.QueryTerms : Tokenize(searchText),
                warnings,
                cancellationToken).ConfigureAwait(false);

            var queryVector = await CreateEmbeddingAsync(
                searchText,
                EmbeddingInputRole.RetrievalQuery,
                warnings,
                cancellationToken).ConfigureAwait(false);
            if (queryVector is null)
            {
                return new(lexical, warnings, plan.RequiresSourceGrounding);
            }

            var runtime = await _qdrant.EnsureAvailableAsync(_settings, cancellationToken).ConfigureAwait(false);
            if (!runtime.IsReachable)
            {
                warnings.Add(runtime.Message);
                return new(lexical, warnings, plan.RequiresSourceGrounding);
            }

            if (directPath is not null)
            {
                await IndexDirectDocumentAsync(approved!, warnings, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await EnsureScanAsync(warnings, force: false, cancellationToken).ConfigureAwait(false);
            }

            using var client = _qdrant.CreateClient(_settings);
            if (!await client.CollectionExistsAsync(_settings.QdrantCollectionName, cancellationToken).ConfigureAwait(false))
            {
                warnings.Add($"No local knowledge has been indexed from {_settings.RootDirectory} yet.");
                return new(lexical, warnings, plan.RequiresSourceGrounding);
            }

            var points = directPath is null
                ? await client.QueryAsync(
                    _settings.QdrantCollectionName,
                    query: queryVector,
                    limit: checked((ulong)Math.Max(1, _settings.MaxRetrievedChunks)),
                    cancellationToken: cancellationToken).ConfigureAwait(false)
                : await client.QueryAsync(
                    _settings.QdrantCollectionName,
                    query: queryVector,
                    filter: MatchKeyword("document_path", approved!),
                    limit: checked((ulong)Math.Max(1, _settings.MaxRetrievedChunks)),
                    cancellationToken: cancellationToken).ConfigureAwait(false);

            var now = DateTimeOffset.UtcNow;
            var semantic = points
                .Where(point => ReadString(point.Payload, "content").Length > 0)
                .Select((point, index) => new SourceExcerpt(
                    index + 1,
                    LocalDocumentsTopic,
                    BuildExcerptName(point.Payload),
                    BuildFileUrl(ReadString(point.Payload, "document_path")),
                    now,
                    TrimExcerpt(ReadString(point.Payload, "content"))))
                .ToArray();
            var excerpts = lexical
                .Concat(semantic)
                .DistinctBy(item => $"{item.Url}|{item.Excerpt}", StringComparer.OrdinalIgnoreCase)
                .Take(Math.Max(1, _settings.MaxRetrievedChunks))
                .Select((item, index) => item with { Index = index + 1 })
                .ToArray();
            if (excerpts.Length == 0)
            {
                warnings.Add("No local library chunks matched the planned query.");
            }

            return new(excerpts, warnings, plan.RequiresSourceGrounding);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException
                                   or InvalidOperationException or Grpc.Core.RpcException or TimeoutException)
        {
            warnings.Add($"Local knowledge failed safely: {ex.Message.ReplaceLineEndings(" ").Trim()}");
            return new(lexical, warnings, plan.RequiresSourceGrounding);
        }
    }

    private async Task<IReadOnlyList<SourceExcerpt>> SearchLexicalSafelyAsync(
        string searchRoot,
        IReadOnlyCollection<string> queryTerms,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        if (!_settings.EnableRipgrep)
        {
            return [];
        }

        try
        {
            return await _ripgrep.SearchAsync(
                searchRoot,
                queryTerms,
                _settings.AllowedExtensions,
                _settings.MaxRetrievedChunks,
                TimeSpan.FromSeconds(Math.Clamp(_settings.RipgrepTimeoutSeconds, 1, 30)),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or InvalidOperationException or TimeoutException)
        {
            warnings.Add($"Exact-text search failed safely; semantic search will continue: {ex.Message.ReplaceLineEndings(" ").Trim()}");
            return [];
        }
    }

    public async Task<LocalKnowledgeStatus> ScanAsync(bool force, CancellationToken cancellationToken = default)
    {
        var operation = BindCurrentSettings();
        if (!ReferenceEquals(operation, this))
        {
            return await operation.ScanAsync(force, cancellationToken).ConfigureAwait(false);
        }

        var warnings = new List<string>();
        if (!TryGetEmbeddingConfiguration(out _, out var configurationFailure))
        {
            var state = LoadScanState();
            return new(
                false,
                false,
                0,
                state.Files.Count,
                state.LastScanUtc,
                $"Local knowledge embeddings are unavailable: {configurationFailure}");
        }

        var runtime = await _qdrant.EnsureAvailableAsync(_settings, cancellationToken).ConfigureAwait(false);
        if (!runtime.IsReachable)
        {
            return new(false, false, 0, 0, DateTimeOffset.MinValue, runtime.Message);
        }

        await EnsureScanAsync(warnings, force, cancellationToken).ConfigureAwait(false);
        var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        return warnings.Count == 0 ? status : status with { Message = string.Join(" ", warnings) };
    }

    public async Task<LocalKnowledgeStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var operation = BindCurrentSettings();
        if (!ReferenceEquals(operation, this))
        {
            return await operation.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        }

        var state = LoadScanState();
        if (!TryGetEmbeddingConfiguration(out _, out var configurationFailure))
        {
            return new(
                false,
                false,
                0,
                state.Files.Count,
                state.LastScanUtc,
                $"Local knowledge embeddings are unavailable: {configurationFailure}");
        }

        var runtime = await _qdrant.ProbeAsync(_settings, cancellationToken).ConfigureAwait(false);
        if (!runtime.IsReachable)
        {
            return new(false, false, 0, state.Files.Count, state.LastScanUtc, runtime.Message);
        }

        await _scanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var client = _qdrant.CreateClient(_settings);
            var exists = await client.CollectionExistsAsync(
                _settings.QdrantCollectionName,
                cancellationToken).ConfigureAwait(false);
            string? observedMarker;
            try
            {
                observedMarker = ReadEmbeddingSpaceMarker();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return PendingEmbeddingSpaceRebuildStatus(
                    $"the embedding-space marker could not be read: {ex.Message.ReplaceLineEndings(" ").Trim()}");
            }

            var expectedMarker = CreateEmbeddingSpaceMarker(_settings);
            var action = DetermineEmbeddingSpaceGuardAction(
                expectedMarker,
                observedMarker,
                File.Exists(_scanStatePath),
                exists);
            if (action == EmbeddingSpaceGuardAction.ResetAndReindex)
            {
                return PendingEmbeddingSpaceRebuildStatus(
                    "the configured embedding or Qdrant identity changed");
            }

            if (action == EmbeddingSpaceGuardAction.InitializeMarker)
            {
                try
                {
                    WriteEmbeddingSpaceMarkerAtomically(expectedMarker);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return PendingEmbeddingSpaceRebuildStatus(
                        $"the embedding-space marker could not be initialized: {ex.Message.ReplaceLineEndings(" ").Trim()}");
                }
            }

            state = LoadScanState();
            var count = exists
                ? await client.CountAsync(_settings.QdrantCollectionName, exact: true, cancellationToken: cancellationToken).ConfigureAwait(false)
                : 0;
            return new(true, exists, count, state.Files.Count, state.LastScanUtc,
                exists
                    ? $"Qdrant is healthy. {state.Files.Count} document(s), {count} structural/text chunk(s)."
                    : "Qdrant is healthy. Scan the approved folder to create the collection.");
        }
        finally
        {
            _scanGate.Release();
        }
    }

    public async Task RebuildAsync(CancellationToken cancellationToken = default)
    {
        var operation = BindCurrentSettings();
        if (!ReferenceEquals(operation, this))
        {
            await operation.RebuildAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!TryGetEmbeddingConfiguration(out _, out var configurationFailure))
        {
            throw new InvalidOperationException(
                $"Local knowledge embeddings are unavailable: {configurationFailure}");
        }

        var runtime = await _qdrant.EnsureAvailableAsync(_settings, cancellationToken).ConfigureAwait(false);
        if (!runtime.IsReachable)
        {
            throw new InvalidOperationException(runtime.Message);
        }

        await _scanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var client = _qdrant.CreateClient(_settings);
            if (await client.CollectionExistsAsync(_settings.QdrantCollectionName, cancellationToken).ConfigureAwait(false))
            {
                await client.DeleteCollectionAsync(_settings.QdrantCollectionName, cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            ClearScanState();
            WriteEmbeddingSpaceMarkerAtomically(CreateEmbeddingSpaceMarker(_settings));
            await ScanCoreAsync([], force: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _scanGate.Release();
        }
    }

    private async Task EnsureScanAsync(List<string> warnings, bool force, CancellationToken cancellationToken)
    {
        await _scanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var client = _qdrant.CreateClient(_settings);
            var forceReindex = await ApplyEmbeddingSpaceGuardAsync(client, cancellationToken).ConfigureAwait(false);
            await ScanCoreAsync(warnings, force || forceReindex, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _scanGate.Release();
        }
    }

    private async Task IndexDirectDocumentAsync(
        string filePath,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        await _scanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var client = _qdrant.CreateClient(_settings);
            var forceReindex = await ApplyEmbeddingSpaceGuardAsync(client, cancellationToken).ConfigureAwait(false);
            if (forceReindex)
            {
                await ScanCoreAsync(warnings, force: true, cancellationToken).ConfigureAwait(false);
            }

            await IndexDocumentAsync(filePath, warnings, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _scanGate.Release();
        }
    }

    private async Task ScanCoreAsync(List<string> warnings, bool force, CancellationToken cancellationToken)
    {
        var state = LoadScanState();
        if (!force && state.LastScanUtc > DateTimeOffset.MinValue
            && DateTimeOffset.UtcNow - state.LastScanUtc < TimeSpan.FromMinutes(Math.Max(1, _settings.ScanIntervalMinutes)))
        {
            return;
        }

        Directory.CreateDirectory(_settings.RootDirectory);
        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = Directory.EnumerateFiles(_settings.RootDirectory, "*", SearchOption.AllDirectories)
            .Where(IsAllowedFile)
            .Take(Math.Max(1, _settings.MaxFiles))
            .ToArray();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.GetFullPath(file);
            active.Add(path);
            var info = new FileInfo(path);
            if (!force && state.Files.TryGetValue(path, out var fingerprint)
                       && fingerprint.Length == info.Length
                       && fingerprint.LastWriteTicks == info.LastWriteTimeUtc.Ticks)
            {
                continue;
            }

            if (await IndexDocumentAsync(path, warnings, cancellationToken).ConfigureAwait(false))
            {
                state.Files[path] = new FileFingerprint(info.Length, info.LastWriteTimeUtc.Ticks);
            }
        }

        using var client = _qdrant.CreateClient(_settings);
        if (await client.CollectionExistsAsync(_settings.QdrantCollectionName, cancellationToken).ConfigureAwait(false))
        {
            foreach (var removed in state.Files.Keys.Where(path => !active.Contains(path)).ToArray())
            {
                await client.DeleteAsync(_settings.QdrantCollectionName, MatchKeyword("document_path", removed), cancellationToken: cancellationToken).ConfigureAwait(false);
                state.Files.Remove(removed);
            }
        }

        state.LastScanUtc = DateTimeOffset.UtcNow;
        SaveScanState(state);
    }

    private async Task<bool> ApplyEmbeddingSpaceGuardAsync(
        QdrantClient client,
        CancellationToken cancellationToken)
    {
        var collectionExists = await client.CollectionExistsAsync(
            _settings.QdrantCollectionName,
            cancellationToken).ConfigureAwait(false);
        var expectedMarker = CreateEmbeddingSpaceMarker(_settings);
        var action = DetermineEmbeddingSpaceGuardAction(
            expectedMarker,
            ReadEmbeddingSpaceMarker(),
            File.Exists(_scanStatePath),
            collectionExists);
        if (action == EmbeddingSpaceGuardAction.Current)
        {
            return false;
        }

        if (action == EmbeddingSpaceGuardAction.ResetAndReindex)
        {
            if (collectionExists)
            {
                await client.DeleteCollectionAsync(
                    _settings.QdrantCollectionName,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            ClearScanState();
        }

        WriteEmbeddingSpaceMarkerAtomically(expectedMarker);
        return action == EmbeddingSpaceGuardAction.ResetAndReindex;
    }

    internal static EmbeddingSpaceGuardAction DetermineEmbeddingSpaceGuardAction(
        string expectedMarker,
        string? observedMarker,
        bool scanStateExists,
        bool collectionExists)
    {
        if (string.Equals(expectedMarker, observedMarker, StringComparison.Ordinal))
        {
            return scanStateExists == collectionExists
                ? EmbeddingSpaceGuardAction.Current
                : EmbeddingSpaceGuardAction.ResetAndReindex;
        }

        return scanStateExists || collectionExists
            ? EmbeddingSpaceGuardAction.ResetAndReindex
            : EmbeddingSpaceGuardAction.InitializeMarker;
    }

    internal static string CreateEmbeddingSpaceMarker(LocalVectorLibrarySettings settings)
    {
        var identity = new StringBuilder();
        AppendIdentityValue(identity, settings.EmbeddingProvider);
        AppendIdentityValue(identity, settings.EmbeddingEndpoint);
        AppendIdentityValue(identity, settings.EmbeddingModel);
        AppendIdentityValue(identity, settings.EmbeddingDimensions.ToString(CultureInfo.InvariantCulture));
        AppendIdentityValue(identity, settings.EmbeddingProtocolIdentity);
        AppendIdentityValue(identity, settings.EmbeddingContextTokens.ToString(CultureInfo.InvariantCulture));
        AppendIdentityValue(identity, settings.EmbeddingDocumentPromptMode.ToString());
        AppendIdentityValue(identity, settings.EmbeddingQueryPromptMode.ToString());
        AppendIdentityValue(identity, settings.QdrantHost);
        AppendIdentityValue(identity, settings.QdrantHttpPort.ToString(CultureInfo.InvariantCulture));
        AppendIdentityValue(identity, settings.QdrantGrpcPort.ToString(CultureInfo.InvariantCulture));
        AppendIdentityValue(identity, settings.QdrantUseTls ? "true" : "false");
        AppendIdentityValue(identity, settings.QdrantCollectionName);
        AppendIdentityValue(identity, Path.GetFullPath(settings.RootDirectory));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToString())));
    }

    private static void AppendIdentityValue(StringBuilder identity, string value)
    {
        identity.Append(value.Length.ToString(CultureInfo.InvariantCulture));
        identity.Append(':');
        identity.Append(value);
    }

    private string? ReadEmbeddingSpaceMarker()
    {
        if (!File.Exists(_embeddingSpaceMarkerPath))
        {
            return null;
        }

        return File.ReadAllText(_embeddingSpaceMarkerPath, Encoding.UTF8).Trim();
    }

    private void WriteEmbeddingSpaceMarkerAtomically(string marker)
    {
        var directory = Path.GetDirectoryName(_embeddingSpaceMarkerPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_embeddingSpaceMarkerPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                var bytes = Encoding.UTF8.GetBytes(marker + Environment.NewLine);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _embeddingSpaceMarkerPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private void ClearScanState()
    {
        if (File.Exists(_scanStatePath))
        {
            File.Delete(_scanStatePath);
        }
    }

    private static LocalKnowledgeStatus PendingEmbeddingSpaceRebuildStatus(string reason) =>
        new(
            true,
            false,
            0,
            0,
            DateTimeOffset.MinValue,
            $"Qdrant is healthy. Local knowledge is pending an embedding-space rebuild because {reason}; stored vectors will not be queried.");

    private async Task<bool> IndexDocumentAsync(string filePath, List<string> warnings, CancellationToken cancellationToken)
    {
        var info = new FileInfo(filePath);
        if (!info.Exists)
        {
            warnings.Add($"{filePath} was not found.");
            return false;
        }

        if (info.Length > _settings.MaxFileBytes)
        {
            warnings.Add($"{filePath} was skipped because it exceeds {_settings.MaxFileBytes.ToString(CultureInfo.InvariantCulture)} bytes.");
            return false;
        }

        var text = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
        var chunks = _chunker.Chunk(filePath, text, _settings.ChunkCharacters, _settings.ChunkOverlapCharacters)
            .Take(Math.Max(1, _settings.MaxChunksPerFile))
            .ToArray();
        if (chunks.Length == 0)
        {
            warnings.Add($"{filePath} did not contain usable text.");
            return false;
        }

        var vectors = new List<float[]>(chunks.Length);
        foreach (var chunk in chunks)
        {
            var vector = await CreateEmbeddingAsync(
                chunk.Text,
                EmbeddingInputRole.StoredDocument,
                warnings,
                cancellationToken).ConfigureAwait(false);
            if (vector is null)
            {
                return false;
            }
            vectors.Add(vector);
        }

        using var client = _qdrant.CreateClient(_settings);
        if (!await client.CollectionExistsAsync(_settings.QdrantCollectionName, cancellationToken).ConfigureAwait(false))
        {
            try
            {
                await client.CreateCollectionAsync(
                    _settings.QdrantCollectionName,
                    new VectorParams { Size = checked((ulong)vectors[0].Length), Distance = Distance.Cosine },
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is Grpc.Core.RpcException or InvalidOperationException)
            {
                throw new InvalidOperationException($"Qdrant could not create collection {_settings.QdrantCollectionName}: {ex.Message}", ex);
            }
        }
        else
        {
            try
            {
                await client.DeleteAsync(_settings.QdrantCollectionName, MatchKeyword("document_path", filePath), cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is Grpc.Core.RpcException or InvalidOperationException)
            {
                throw new InvalidOperationException($"Qdrant could not replace the existing document points: {ex.Message}", ex);
            }
        }

        var points = chunks.Select((chunk, index) => new PointStruct
        {
            Id = CreatePointId(filePath, index, chunk.StartLine, chunk.EndLine),
            Vectors = vectors[index],
            Payload =
            {
                ["document_path"] = filePath,
                ["document_name"] = Path.GetFileName(filePath),
                ["extension"] = Path.GetExtension(filePath),
                ["content"] = chunk.Text,
                ["symbol"] = chunk.Symbol,
                ["parser"] = chunk.Parser,
                ["start_line"] = chunk.StartLine,
                ["end_line"] = chunk.EndLine,
                ["chunk_index"] = index,
                ["file_length"] = info.Length,
                ["last_write_ticks"] = info.LastWriteTimeUtc.Ticks,
                 ["embedding_provider"] = _settings.EmbeddingProvider,
                 ["embedding_model"] = _settings.EmbeddingModel,
                 ["embedding_dimensions"] = _settings.EmbeddingDimensions,
                 ["embedding_protocol"] = _settings.EmbeddingProtocolIdentity,
                 ["embedding_context_tokens"] = _settings.EmbeddingContextTokens,
                 ["embedding_prompt_mode"] = _settings.EmbeddingDocumentPromptMode.ToString()
            }
        }).ToArray();
        try
        {
            await client.UpsertAsync(_settings.QdrantCollectionName, points, wait: true, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is Grpc.Core.RpcException or InvalidOperationException)
        {
            throw new InvalidOperationException($"Qdrant could not store document chunks: {ex.Message}", ex);
        }
        return true;
    }

    private async Task<float[]?> CreateEmbeddingAsync(
        string input,
        EmbeddingInputRole role,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        if (!TryGetEmbeddingConfiguration(out var configuration, out var configurationFailure))
        {
            warnings.Add($"Local knowledge embeddings are unavailable: {configurationFailure}");
            return null;
        }

        var result = await _embeddingClient
            .CreateEmbeddingAsync(configuration!, input, role, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Success || result.Vector is null)
        {
            warnings.Add($"Local knowledge embeddings are unavailable: {result.Message}");
            return null;
        }
        return result.Vector;
    }

    private bool TryGetEmbeddingConfiguration(
        out LocalEmbeddingConfiguration? configuration,
        out string failure) =>
        LocalEmbeddingConfiguration.TryCreate(
            _settings.EmbeddingProvider,
            _settings.EmbeddingEndpoint,
            _settings.EmbeddingModel,
            _settings.EmbeddingDimensions,
            _settings.EmbeddingProtocolIdentity,
            _settings.EmbeddingContextTokens,
            _settings.EmbeddingDocumentPromptMode,
            _settings.EmbeddingQueryPromptMode,
            out configuration,
            out failure);

    private bool ShouldAttempt(SourceQueryPlan plan)
    {
        if (!plan.UseSources)
        {
            return false;
        }
        var text = BuildSearchText(plan);
        if (ContainsHttpUrl(text))
        {
            return false;
        }
        return string.Equals(plan.Intent, LocalDocumentsTopic, StringComparison.OrdinalIgnoreCase)
               || string.Equals(plan.Topic, LocalDocumentsTopic, StringComparison.OrdinalIgnoreCase)
               || plan.PreferredSourceTopics.Any(topic => string.Equals(topic, LocalDocumentsTopic, StringComparison.OrdinalIgnoreCase))
               || TryExtractDirectFilePath(text) is not null
               || Tokenize(text).Overlaps(LocalDocumentTerms);
    }

    private bool TryResolveApprovedFile(string directPath, out string? approvedPath, out string warning)
    {
        approvedPath = null;
        warning = string.Empty;
        try
        {
            var fullPath = Path.GetFullPath(directPath.Trim().Trim('"'));
            var root = Path.GetFullPath(_settings.RootDirectory);
            if (!IsInsideDirectory(fullPath, root))
            {
                warning = $"Local document {fullPath} is outside the approved knowledge folder {root}.";
                return false;
            }
            if (!File.Exists(fullPath) || !IsAllowedFile(fullPath))
            {
                warning = $"Local document {fullPath} was not found or uses an unsupported file type.";
                return false;
            }
            approvedPath = fullPath;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            warning = $"Local document path could not be used: {ex.Message}";
            return false;
        }
    }

    private bool IsAllowedFile(string path) => _settings.AllowedExtensions.Any(
        allowed => string.Equals(allowed, Path.GetExtension(path), StringComparison.OrdinalIgnoreCase));

    private LocalVectorLibraryRetriever BindCurrentSettings()
    {
        if (_settingsProvider is null)
        {
            return this;
        }

        return new LocalVectorLibraryRetriever(
            _dataRoot,
            _httpClient,
            _settingsProvider(),
            _qdrant,
            _chunker,
            _ripgrep,
            _scanGate);
    }

    private static LocalVectorLibrarySettings CaptureSettings(
        LocalVectorLibrarySettingsSnapshotOwner settingsOwner)
    {
        ArgumentNullException.ThrowIfNull(settingsOwner);
        return settingsOwner.Capture().Settings;
    }

    private ScanState LoadScanState()
    {
        try
        {
            if (!File.Exists(_scanStatePath)) return new();
            using var stream = File.OpenRead(_scanStatePath);
            return JsonSerializer.Deserialize<ScanState>(stream, JsonOptions) ?? new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    private void SaveScanState(ScanState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_scanStatePath)!);
        using var stream = File.Create(_scanStatePath);
        JsonSerializer.Serialize(stream, state, JsonOptions);
    }

    internal static Guid CreatePointId(string path, int chunkIndex, int startLine, int endLine)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{Path.GetFullPath(path).ToUpperInvariant()}|{chunkIndex}|{startLine}|{endLine}"));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static string BuildExcerptName(IDictionary<string, Qdrant.Client.Grpc.Value> payload)
    {
        var name = ReadString(payload, "document_name");
        var symbol = ReadString(payload, "symbol");
        return string.IsNullOrWhiteSpace(symbol) ? name : $"{name} — {symbol}";
    }
    private static string ReadString(IDictionary<string, Qdrant.Client.Grpc.Value> payload, string key) => payload.TryGetValue(key, out var value) ? value.StringValue : string.Empty;
    private static string BuildSearchText(SourceQueryPlan plan) => string.Join(' ', new[] { plan.Intent, plan.Topic }.Concat(plan.QueryTerms).Concat(plan.PreferredSourceTopics).Where(item => !string.IsNullOrWhiteSpace(item)));
    private static bool ContainsHttpUrl(string text) => text.Contains("https://", StringComparison.OrdinalIgnoreCase) || text.Contains("http://", StringComparison.OrdinalIgnoreCase);
    private static bool IsInsideDirectory(string path, string root) { var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar; return path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase); }
    private static HashSet<string> Tokenize(string text) => text.Split(QuerySeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(token => new string(token.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant()).Where(token => token.Length >= 3).ToHashSet(StringComparer.OrdinalIgnoreCase);
    private static string TrimExcerpt(string text) { var normalized = text.ReplaceLineEndings(Environment.NewLine).Trim(); return normalized.Length <= MaxExcerptCharacters ? normalized : normalized[..MaxExcerptCharacters]; }
    private static string BuildFileUrl(string path) => string.IsNullOrWhiteSpace(path) ? string.Empty : new Uri(path).AbsoluteUri;
    private static string? TryExtractDirectFilePath(string text)
    {
        foreach (var segment in ExtractQuotedSegments(text)) if (LooksLikeWindowsPath(segment)) return segment;
        for (var i = 0; i + 2 < text.Length; i++)
        {
            if ((i > 0 && char.IsLetterOrDigit(text[i - 1])) || !char.IsLetter(text[i]) || text[i + 1] != ':' || (text[i + 2] != '\\' && text[i + 2] != '/')) continue;
            var candidate = text[i..].Trim().TrimEnd('.', ',', ';', '?', '!');
            if (File.Exists(candidate)) return candidate;
            var parts = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            for (var count = parts.Length; count > 0; count--) { var shortened = string.Join(' ', parts.Take(count)).TrimEnd('.', ',', ';', '?', '!'); if (File.Exists(shortened)) return shortened; }
            return candidate;
        }
        return null;
    }
    private static IEnumerable<string> ExtractQuotedSegments(string text) { var quoted = false; var start = 0; for (var i = 0; i < text.Length; i++) { if (text[i] != '"') continue; if (!quoted) { quoted = true; start = i + 1; } else { quoted = false; if (i > start) yield return text[start..i].Trim(); } } }
    private static bool LooksLikeWindowsPath(string value) => value.Length > 3 && char.IsLetter(value[0]) && value[1] == ':' && (value[2] == '\\' || value[2] == '/');

    private sealed class ScanState
    {
        public DateTimeOffset LastScanUtc { get; set; }
        public Dictionary<string, FileFingerprint> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
    private sealed record FileFingerprint(long Length, long LastWriteTicks);
}
