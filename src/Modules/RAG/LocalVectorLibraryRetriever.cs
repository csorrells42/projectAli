using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    private readonly HttpClient _httpClient;
    private readonly LocalVectorLibrarySettings _settings;
    private readonly QdrantServiceManager _qdrant;
    private readonly IDocumentChunker _chunker;
    private readonly RipgrepSearchService _ripgrep;
    private readonly SemaphoreSlim _scanGate = new(1, 1);

    public LocalVectorLibraryRetriever(
        string dataRoot,
        HttpClient httpClient,
        LocalVectorLibrarySettings? settings = null,
        QdrantServiceManager? qdrant = null,
        IDocumentChunker? chunker = null,
        RipgrepSearchService? ripgrep = null)
    {
        _dataRoot = dataRoot;
        _scanStatePath = LocalVectorLibrarySettingsStore.GetScanStatePath(dataRoot);
        _httpClient = httpClient;
        _settings = settings ?? LocalVectorLibrarySettingsStore.LoadOrDefault(dataRoot);
        _qdrant = qdrant ?? new QdrantServiceManager(dataRoot);
        _chunker = chunker ?? new StructuredDocumentChunker();
        _ripgrep = ripgrep ?? new RipgrepSearchService();
    }

    public void WriteExample()
    {
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
        if (!_settings.Enabled || !ShouldAttempt(plan))
        {
            return SourceRetrievalResult.Empty;
        }

        var warnings = new List<string>();
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

            var lexical = await SearchLexicalSafelyAsync(
                approved ?? _settings.RootDirectory,
                plan.QueryTerms.Count > 0 ? plan.QueryTerms : Tokenize(searchText),
                warnings,
                cancellationToken).ConfigureAwait(false);

            var runtime = await _qdrant.EnsureAvailableAsync(_settings, cancellationToken).ConfigureAwait(false);
            if (!runtime.IsReachable)
            {
                warnings.Add(runtime.Message);
                return new(lexical, warnings, plan.RequiresSourceGrounding);
            }

            if (directPath is not null)
            {
                await IndexDocumentAsync(approved!, warnings, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await EnsureScanAsync(warnings, force: false, cancellationToken).ConfigureAwait(false);
            }

            var queryVector = await CreateEmbeddingAsync(searchText, warnings, cancellationToken).ConfigureAwait(false);
            if (queryVector is null)
            {
                return new(lexical, warnings, plan.RequiresSourceGrounding);
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
            return new([], warnings, plan.RequiresSourceGrounding);
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
        var warnings = new List<string>();
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
        var runtime = await _qdrant.ProbeAsync(_settings, cancellationToken).ConfigureAwait(false);
        var state = LoadScanState();
        if (!runtime.IsReachable)
        {
            return new(false, false, 0, state.Files.Count, state.LastScanUtc, runtime.Message);
        }

        using var client = _qdrant.CreateClient(_settings);
        var exists = await client.CollectionExistsAsync(_settings.QdrantCollectionName, cancellationToken).ConfigureAwait(false);
        var count = exists
            ? await client.CountAsync(_settings.QdrantCollectionName, exact: true, cancellationToken: cancellationToken).ConfigureAwait(false)
            : 0;
        return new(true, exists, count, state.Files.Count, state.LastScanUtc,
            exists
                ? $"Qdrant is healthy. {state.Files.Count} document(s), {count} structural/text chunk(s)."
                : "Qdrant is healthy. Scan the approved folder to create the collection.");
    }

    public async Task RebuildAsync(CancellationToken cancellationToken = default)
    {
        var runtime = await _qdrant.EnsureAvailableAsync(_settings, cancellationToken).ConfigureAwait(false);
        if (!runtime.IsReachable)
        {
            throw new InvalidOperationException(runtime.Message);
        }

        using var client = _qdrant.CreateClient(_settings);
        if (await client.CollectionExistsAsync(_settings.QdrantCollectionName, cancellationToken).ConfigureAwait(false))
        {
            await client.DeleteCollectionAsync(_settings.QdrantCollectionName, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        SaveScanState(new ScanState());
        await EnsureScanAsync([], force: true, cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureScanAsync(List<string> warnings, bool force, CancellationToken cancellationToken)
    {
        await _scanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
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
        finally
        {
            _scanGate.Release();
        }
    }

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
            var vector = await CreateEmbeddingAsync(chunk.Text, warnings, cancellationToken).ConfigureAwait(false);
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
                ["embedding_model"] = _settings.EmbeddingModel
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

    private async Task<float[]?> CreateEmbeddingAsync(string input, List<string> warnings, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(_settings.EmbeddingEndpoint, UriKind.Absolute, out var endpoint))
        {
            warnings.Add($"Local knowledge embedding endpoint is invalid: {_settings.EmbeddingEndpoint}");
            return null;
        }

        var errors = new List<string>();
        var primary = await TryPostEmbeddingAsync(endpoint, input, true, errors, cancellationToken).ConfigureAwait(false);
        if (primary is not null)
        {
            return primary;
        }

        var legacy = BuildLegacyEmbeddingEndpoint(endpoint);
        if (legacy is not null && legacy != endpoint)
        {
            var fallback = await TryPostEmbeddingAsync(legacy, input, false, errors, cancellationToken).ConfigureAwait(false);
            if (fallback is not null)
            {
                return fallback;
            }
        }

        warnings.Add($"Embedding request failed for {_settings.EmbeddingModel}: {string.Join("; ", errors.Take(2))}");
        return null;
    }

    private async Task<float[]?> TryPostEmbeddingAsync(Uri endpoint, string input, bool openAi, List<string> errors, CancellationToken cancellationToken)
    {
        try
        {
            var payload = openAi
                ? JsonSerializer.Serialize(new { model = _settings.EmbeddingModel, input }, JsonOptions)
                : JsonSerializer.Serialize(new { model = _settings.EmbeddingModel, prompt = input }, JsonOptions);
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync(endpoint, content, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                errors.Add($"{endpoint.AbsolutePath} returned HTTP {(int)response.StatusCode}");
                return null;
            }

            var vector = TryReadEmbedding(body);
            if (vector is { Length: > 0 })
            {
                return vector;
            }
            errors.Add($"{endpoint.AbsolutePath} returned no vector");
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            errors.Add($"{endpoint.AbsolutePath} failed: {ex.Message}");
            return null;
        }
    }

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
    private static Uri? BuildLegacyEmbeddingEndpoint(Uri endpoint) { var builder = new UriBuilder(endpoint); if (builder.Path.EndsWith("/api/embed", StringComparison.OrdinalIgnoreCase)) builder.Path = builder.Path[..^10] + "/api/embeddings"; else builder.Path = "/api/embeddings"; return builder.Uri; }
    private static float[]? TryReadEmbedding(string json) { using var doc = JsonDocument.Parse(json); var root = doc.RootElement; if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array) { var first = data.EnumerateArray().FirstOrDefault(); if (first.ValueKind == JsonValueKind.Object && first.TryGetProperty("embedding", out var value)) return ReadFloatArray(value); } if (root.TryGetProperty("embeddings", out var values) && values.ValueKind == JsonValueKind.Array) { var first = values.EnumerateArray().FirstOrDefault(); return first.ValueKind == JsonValueKind.Array ? ReadFloatArray(first) : null; } return root.TryGetProperty("embedding", out var embedding) ? ReadFloatArray(embedding) : null; }
    private static float[] ReadFloatArray(JsonElement array) => array.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Number).Select(item => (float)item.GetDouble()).ToArray();

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
