using System.Globalization;
using System.Text;
using System.Text.Json;
using Ali.Core.Sources;

namespace Ali.Infrastructure.Sources;

public sealed class LocalVectorLibraryRetriever : ISourceRetriever
{
    private const string LocalDocumentsTopic = "local_documents";
    private const int MaxExcerptCharacters = 1_800;
    private static readonly char[] QuerySeparators =
        [' ', ',', '.', '?', '!', ':', ';', '/', '\\', '-', '_', '(', ')', '[', ']', '"', '\''];
    private static readonly HashSet<string> LocalDocumentTerms = new(StringComparer.OrdinalIgnoreCase)
    {
        "document",
        "documents",
        "doc",
        "file",
        "files",
        "folder",
        "library",
        "manual",
        "rag"
    };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _indexPath;
    private readonly HttpClient _httpClient;
    private readonly LocalVectorLibrarySettings _settings;

    public LocalVectorLibraryRetriever(
        string dataRoot,
        HttpClient httpClient,
        LocalVectorLibrarySettings? settings = null)
    {
        _indexPath = LocalVectorLibrarySettingsStore.GetIndexPath(dataRoot);
        _httpClient = httpClient;
        _settings = settings ?? LocalVectorLibrarySettingsStore.LoadOrDefault(dataRoot);
    }

    public void WriteExample()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_indexPath)!);
            if (_settings.Enabled)
            {
                Directory.CreateDirectory(_settings.RootDirectory);
            }
        }
        catch (IOException)
        {
            // The retriever will report folder problems when a lookup is attempted.
        }
        catch (UnauthorizedAccessException)
        {
            // The retriever will report folder problems when a lookup is attempted.
        }
    }

    public Task<SourceRetrievalResult> RetrieveAsync(string userText, CancellationToken cancellationToken) =>
        RetrieveAsync(
            new SourceQueryPlan(
                true,
                true,
                LocalDocumentsTopic,
                userText,
                Tokenize(userText).ToArray(),
                [LocalDocumentsTopic]),
            cancellationToken);

    public async Task<SourceRetrievalResult> RetrieveAsync(
        SourceQueryPlan plan,
        CancellationToken cancellationToken)
    {
        if (!_settings.Enabled || !ShouldAttempt(plan))
        {
            return SourceRetrievalResult.Empty;
        }

        var warnings = new List<string>();
        var searchText = BuildSearchText(plan);
        var index = LoadIndex(warnings);
        var directPath = TryExtractDirectFilePath(searchText);

        if (!string.IsNullOrWhiteSpace(directPath))
        {
            return await RetrieveDirectDocumentAsync(
                directPath,
                searchText,
                index,
                warnings,
                cancellationToken).ConfigureAwait(false);
        }

        await EnsureScanAsync(index, warnings, cancellationToken).ConfigureAwait(false);

        if (index.Documents.Count == 0)
        {
            warnings.Add($"No local library documents were available in {_settings.RootDirectory}.");
            return new SourceRetrievalResult(Array.Empty<SourceExcerpt>(), warnings, plan.RequiresSourceGrounding);
        }

        return await RetrieveFromIndexedDocumentsAsync(
            index.Documents,
            searchText,
            warnings,
            plan.RequiresSourceGrounding,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<SourceRetrievalResult> RetrieveDirectDocumentAsync(
        string directPath,
        string searchText,
        LocalVectorLibraryIndex index,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        if (!TryResolveApprovedFile(directPath, out var approvedPath, out var warning))
        {
            warnings.Add(warning);
            return new SourceRetrievalResult(Array.Empty<SourceExcerpt>(), warnings);
        }

        var changed = await UpsertDocumentAsync(index, approvedPath, warnings, cancellationToken).ConfigureAwait(false);
        if (changed)
        {
            SaveIndex(index, warnings);
        }

        var document = index.Documents.FirstOrDefault(
            item => string.Equals(item.DocumentPath, approvedPath, StringComparison.OrdinalIgnoreCase));
        if (document is null || document.Chunks.Count == 0)
        {
            warnings.Add($"No usable local document chunks were indexed for {approvedPath}.");
            return new SourceRetrievalResult(Array.Empty<SourceExcerpt>(), warnings);
        }

        return await RetrieveFromIndexedDocumentsAsync(
            [document],
            searchText,
            warnings,
            requiresSourceGrounding: true,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<SourceRetrievalResult> RetrieveFromIndexedDocumentsAsync(
        IReadOnlyList<LocalVectorDocument> documents,
        string searchText,
        List<string> warnings,
        bool requiresSourceGrounding,
        CancellationToken cancellationToken)
    {
        var queryEmbedding = await CreateEmbeddingAsync(searchText, warnings, cancellationToken).ConfigureAwait(false);
        if (queryEmbedding is null)
        {
            return new SourceRetrievalResult(Array.Empty<SourceExcerpt>(), warnings, requiresSourceGrounding);
        }

        var ranked = documents
            .SelectMany(document => document.Chunks.Select(chunk => new
            {
                Document = document,
                Chunk = chunk,
                Score = CosineSimilarity(queryEmbedding, chunk.Embedding)
            }))
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .Take(Math.Max(1, _settings.MaxRetrievedChunks))
            .ToList();

        if (ranked.Count == 0)
        {
            warnings.Add("No local library chunks matched the planned query.");
            return new SourceRetrievalResult(Array.Empty<SourceExcerpt>(), warnings, requiresSourceGrounding);
        }

        var now = DateTimeOffset.UtcNow;
        var excerpts = ranked.Select((item, index) => new SourceExcerpt(
                index + 1,
                LocalDocumentsTopic,
                Path.GetFileName(item.Document.DocumentPath),
                BuildFileUrl(item.Document.DocumentPath),
                now,
                TrimExcerpt(item.Chunk.Text)))
            .ToList();

        return new SourceRetrievalResult(excerpts, warnings, requiresSourceGrounding);
    }

    private async Task EnsureScanAsync(
        LocalVectorLibraryIndex index,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (index.LastScanUtc > DateTimeOffset.MinValue
            && now - index.LastScanUtc < TimeSpan.FromMinutes(Math.Max(1, _settings.ScanIntervalMinutes)))
        {
            return;
        }

        if (!Directory.Exists(_settings.RootDirectory))
        {
            Directory.CreateDirectory(_settings.RootDirectory);
            index.LastScanUtc = now;
            SaveIndex(index, warnings);
            warnings.Add($"Local RAG folder was created at {_settings.RootDirectory}; add supported text documents there.");
            return;
        }

        var changed = false;
        var activePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = Directory.EnumerateFiles(_settings.RootDirectory, "*", SearchOption.AllDirectories)
            .Where(IsAllowedFile)
            .Take(Math.Max(1, _settings.MaxFiles))
            .ToList();

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(file);
            activePaths.Add(fullPath);
            changed = await UpsertDocumentAsync(index, fullPath, warnings, cancellationToken).ConfigureAwait(false) || changed;
        }

        var removed = index.Documents.RemoveAll(document => !activePaths.Contains(document.DocumentPath));
        changed = changed || removed > 0;
        index.LastScanUtc = now;
        SaveIndex(index, warnings);
    }

    private async Task<bool> UpsertDocumentAsync(
        LocalVectorLibraryIndex index,
        string filePath,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        FileInfo fileInfo;
        try
        {
            fileInfo = new FileInfo(filePath);
        }
        catch (IOException ex)
        {
            warnings.Add($"{filePath} could not be inspected: {ex.Message}");
            return false;
        }

        if (!fileInfo.Exists)
        {
            warnings.Add($"{filePath} was not found.");
            return false;
        }

        if (fileInfo.Length > _settings.MaxFileBytes)
        {
            warnings.Add($"{filePath} was skipped because it is larger than {_settings.MaxFileBytes.ToString(CultureInfo.InvariantCulture)} bytes.");
            return false;
        }

        var existing = index.Documents.FirstOrDefault(
            document => string.Equals(document.DocumentPath, filePath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null
            && existing.Length == fileInfo.Length
            && existing.LastWriteUtc == fileInfo.LastWriteTimeUtc)
        {
            return false;
        }

        string text;
        try
        {
            text = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"{filePath} could not be read: {ex.Message}");
            return false;
        }

        var chunks = CreateChunks(text)
            .Take(Math.Max(1, _settings.MaxChunksPerFile))
            .ToList();
        if (chunks.Count == 0)
        {
            warnings.Add($"{filePath} did not contain usable text.");
            return false;
        }

        var embeddedChunks = new List<LocalVectorChunk>();
        for (var i = 0; i < chunks.Count; i++)
        {
            var embedding = await CreateEmbeddingAsync(chunks[i], warnings, cancellationToken).ConfigureAwait(false);
            if (embedding is null)
            {
                return false;
            }

            embeddedChunks.Add(new LocalVectorChunk
            {
                ChunkId = $"{Path.GetFileName(filePath)}:{i + 1}",
                Text = chunks[i],
                Embedding = embedding
            });
        }

        var next = new LocalVectorDocument
        {
            DocumentPath = filePath,
            Length = fileInfo.Length,
            LastWriteUtc = fileInfo.LastWriteTimeUtc,
            Chunks = embeddedChunks
        };

        if (existing is not null)
        {
            index.Documents.Remove(existing);
        }

        index.Documents.Add(next);
        return true;
    }

    private async Task<float[]?> CreateEmbeddingAsync(
        string input,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(_settings.EmbeddingEndpoint, UriKind.Absolute, out var endpoint))
        {
            warnings.Add($"Local RAG embedding endpoint is invalid: {_settings.EmbeddingEndpoint}");
            return null;
        }

        var errors = new List<string>();
        var primary = await TryPostEmbeddingAsync(endpoint, input, useEmbedEndpoint: true, errors, cancellationToken).ConfigureAwait(false);
        if (primary is not null)
        {
            return primary;
        }

        var legacyEndpoint = BuildLegacyEmbeddingEndpoint(endpoint);
        if (legacyEndpoint is not null && !Uri.Compare(endpoint, legacyEndpoint, UriComponents.AbsoluteUri, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase).Equals(0))
        {
            var legacy = await TryPostEmbeddingAsync(legacyEndpoint, input, useEmbedEndpoint: false, errors, cancellationToken).ConfigureAwait(false);
            if (legacy is not null)
            {
                return legacy;
            }
        }

        warnings.Add($"Local RAG embedding request failed for model {_settings.EmbeddingModel}: {string.Join("; ", errors.Take(2))}");
        return null;
    }

    private async Task<float[]?> TryPostEmbeddingAsync(
        Uri endpoint,
        string input,
        bool useEmbedEndpoint,
        List<string> errors,
        CancellationToken cancellationToken)
    {
        try
        {
            var payload = useEmbedEndpoint
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

            var embedding = TryReadEmbedding(body);
            if (embedding is not null)
            {
                return embedding;
            }

            errors.Add($"{endpoint.AbsolutePath} did not return an embedding vector");
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            errors.Add($"{endpoint.AbsolutePath} failed: {ex.Message}");
            return null;
        }
    }

    private LocalVectorLibraryIndex LoadIndex(List<string> warnings)
    {
        if (!File.Exists(_indexPath))
        {
            return new LocalVectorLibraryIndex { EmbeddingModel = _settings.EmbeddingModel };
        }

        try
        {
            using var stream = File.OpenRead(_indexPath);
            var index = JsonSerializer.Deserialize<LocalVectorLibraryIndex>(stream, JsonOptions)
                        ?? new LocalVectorLibraryIndex();
            if (!string.Equals(index.EmbeddingModel, _settings.EmbeddingModel, StringComparison.OrdinalIgnoreCase))
            {
                return new LocalVectorLibraryIndex { EmbeddingModel = _settings.EmbeddingModel };
            }

            index.Documents ??= [];
            return index;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            warnings.Add($"Local RAG index could not be read and will be rebuilt: {ex.Message}");
            return new LocalVectorLibraryIndex { EmbeddingModel = _settings.EmbeddingModel };
        }
    }

    private void SaveIndex(LocalVectorLibraryIndex index, List<string> warnings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_indexPath)!);
            index.EmbeddingModel = _settings.EmbeddingModel;
            using var stream = File.Create(_indexPath);
            JsonSerializer.Serialize(stream, index, JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Local RAG index could not be saved: {ex.Message}");
        }
    }

    private bool ShouldAttempt(SourceQueryPlan plan)
    {
        if (!plan.UseSources)
        {
            return false;
        }

        if (string.Equals(plan.Intent, LocalDocumentsTopic, StringComparison.OrdinalIgnoreCase)
            || string.Equals(plan.Topic, LocalDocumentsTopic, StringComparison.OrdinalIgnoreCase)
            || plan.PreferredSourceTopics.Any(topic => string.Equals(topic, LocalDocumentsTopic, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var text = BuildSearchText(plan);
        if (TryExtractDirectFilePath(text) is not null)
        {
            return true;
        }

        return Tokenize(text).Overlaps(LocalDocumentTerms);
    }

    private bool TryResolveApprovedFile(string directPath, out string approvedPath, out string warning)
    {
        approvedPath = string.Empty;
        warning = string.Empty;
        try
        {
            var fullPath = Path.GetFullPath(directPath.Trim().Trim('"'));
            var root = Path.GetFullPath(_settings.RootDirectory);
            if (!IsInsideDirectory(fullPath, root))
            {
                warning = $"Local document {fullPath} is outside the approved RAG folder {root}. Move it there before Ali reads it.";
                return false;
            }

            if (!File.Exists(fullPath))
            {
                warning = $"Local document {fullPath} was not found.";
                return false;
            }

            if (!IsAllowedFile(fullPath))
            {
                warning = $"Local document {fullPath} uses an unsupported file type.";
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

    private bool IsAllowedFile(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        return _settings.AllowedExtensions.Any(
            allowed => string.Equals(allowed, extension, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsInsideDirectory(string filePath, string rootDirectory)
    {
        var root = rootDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                   + Path.DirectorySeparatorChar;
        return filePath.Equals(rootDirectory, StringComparison.OrdinalIgnoreCase)
               || filePath.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildSearchText(SourceQueryPlan plan) =>
        string.Join(
            ' ',
            new[] { plan.Intent, plan.Topic }
                .Concat(plan.QueryTerms)
                .Concat(plan.PreferredSourceTopics)
                .Where(item => !string.IsNullOrWhiteSpace(item)));

    private static string? TryExtractDirectFilePath(string text)
    {
        foreach (var segment in ExtractQuotedSegments(text))
        {
            if (LooksLikeWindowsPath(segment))
            {
                return segment;
            }
        }

        for (var i = 0; i + 2 < text.Length; i++)
        {
            if (!char.IsLetter(text[i]) || text[i + 1] != ':' || (text[i + 2] != '\\' && text[i + 2] != '/'))
            {
                continue;
            }

            var candidate = text[i..].Trim().TrimEnd('.', ',', ';', '?', '!');
            if (File.Exists(candidate))
            {
                return candidate;
            }

            var parts = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            for (var count = parts.Length; count > 0; count--)
            {
                var shortened = string.Join(' ', parts.Take(count)).TrimEnd('.', ',', ';', '?', '!');
                if (File.Exists(shortened))
                {
                    return shortened;
                }
            }

            return candidate;
        }

        return null;
    }

    private static IEnumerable<string> ExtractQuotedSegments(string text)
    {
        var inQuote = false;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '"')
            {
                continue;
            }

            if (!inQuote)
            {
                inQuote = true;
                start = i + 1;
                continue;
            }

            inQuote = false;
            if (i > start)
            {
                yield return text[start..i].Trim();
            }
        }
    }

    private static bool LooksLikeWindowsPath(string value) =>
        value.Length > 3
        && char.IsLetter(value[0])
        && value[1] == ':'
        && (value[2] == '\\' || value[2] == '/');

    private IEnumerable<string> CreateChunks(string text)
    {
        var normalized = text.Replace("\0", string.Empty).ReplaceLineEndings(Environment.NewLine).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            yield break;
        }

        var chunkSize = Math.Max(400, _settings.ChunkCharacters);
        var overlap = Math.Clamp(_settings.ChunkOverlapCharacters, 0, chunkSize / 2);
        var start = 0;
        while (start < normalized.Length)
        {
            var length = Math.Min(chunkSize, normalized.Length - start);
            yield return normalized.Substring(start, length).Trim();
            if (start + length >= normalized.Length)
            {
                yield break;
            }

            start += Math.Max(1, length - overlap);
        }
    }

    private static HashSet<string> Tokenize(string text) =>
        text.Split(QuerySeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => new string(token.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant())
            .Where(token => token.Length >= 3)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static Uri? BuildLegacyEmbeddingEndpoint(Uri endpoint)
    {
        var builder = new UriBuilder(endpoint);
        if (builder.Path.EndsWith("/api/embed", StringComparison.OrdinalIgnoreCase))
        {
            builder.Path = builder.Path[..^"/api/embed".Length] + "/api/embeddings";
            return builder.Uri;
        }

        return new Uri(endpoint, "/api/embeddings");
    }

    private static float[]? TryReadEmbedding(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("embeddings", out var embeddings)
            && embeddings.ValueKind is JsonValueKind.Array)
        {
            var first = embeddings.EnumerateArray().FirstOrDefault();
            return first.ValueKind is JsonValueKind.Array ? ReadFloatArray(first) : null;
        }

        return root.TryGetProperty("embedding", out var embedding)
               && embedding.ValueKind is JsonValueKind.Array
            ? ReadFloatArray(embedding)
            : null;
    }

    private static float[] ReadFloatArray(JsonElement array) =>
        array.EnumerateArray()
            .Where(item => item.ValueKind is JsonValueKind.Number)
            .Select(item => (float)item.GetDouble())
            .ToArray();

    private static double CosineSimilarity(IReadOnlyList<float> left, IReadOnlyList<float> right)
    {
        var count = Math.Min(left.Count, right.Count);
        if (count == 0)
        {
            return 0;
        }

        double dot = 0;
        double leftNorm = 0;
        double rightNorm = 0;
        for (var i = 0; i < count; i++)
        {
            dot += left[i] * right[i];
            leftNorm += left[i] * left[i];
            rightNorm += right[i] * right[i];
        }

        return leftNorm <= 0 || rightNorm <= 0
            ? 0
            : dot / (Math.Sqrt(leftNorm) * Math.Sqrt(rightNorm));
    }

    private static string TrimExcerpt(string text)
    {
        var normalized = string.Join(
            Environment.NewLine,
            text.ReplaceLineEndings(Environment.NewLine)
                .Split(Environment.NewLine)
                .Select(line => line.TrimEnd()));
        return normalized.Length <= MaxExcerptCharacters
            ? normalized
            : normalized[..MaxExcerptCharacters];
    }

    private static string BuildFileUrl(string filePath) =>
        new Uri(filePath).AbsoluteUri;

    private sealed class LocalVectorLibraryIndex
    {
        public string EmbeddingModel { get; set; } = string.Empty;

        public DateTimeOffset LastScanUtc { get; set; } = DateTimeOffset.MinValue;

        public List<LocalVectorDocument> Documents { get; set; } = [];
    }

    private sealed class LocalVectorDocument
    {
        public string DocumentPath { get; set; } = string.Empty;

        public DateTime LastWriteUtc { get; set; }

        public long Length { get; set; }

        public List<LocalVectorChunk> Chunks { get; set; } = [];
    }

    private sealed class LocalVectorChunk
    {
        public string ChunkId { get; set; } = string.Empty;

        public string Text { get; set; } = string.Empty;

        public float[] Embedding { get; set; } = [];
    }
}
